# Synchronization

NimPad is local-first. Every edit is committed to the local SQLite database first, and a background engine
propagates it to the cloud and from there to the user's other computers. The editor never waits on the network
(spec §4).

**Status:**
- **Phase 1 (done):** the local half. Edits commit durably on this computer and every change is recorded in the
  durable outgoing queue.
- **Phase 2 (not started):** the network half, which drains that queue and pulls remote changes.

There is no simulated sync anywhere. Until Phase 2 the status bar says **"Local only"**.

## What exists today (Phase 1)

### Local commit path

1. AvalonEdit applies the keystroke immediately.
2. `AutosaveScheduler` saves 400 ms after the last change, and at most 1000 ms after the first unsaved one.
3. `DocumentService` hands the save to the single background writer. One `BEGIN IMMEDIATE` transaction then:
   - updates `document_contents` (canonical LF, storable text, checksum, preview);
   - updates the links and the search index;
   - writes the coalesced `pending_operations` row.
4. SQLite runs in WAL mode with `synchronous=FULL`, so a committed save survives a crash or power loss.

Tree changes go through `NodeService` and follow the same rule: one transaction holds the change and its queued
operation. Those changes are create, rename, move, copy, pin, trash, restore and delete forever.

### The outgoing queue

`pending_operations` is the durable queue the Phase 2 engine will drain in `seq` order. The columns, payload shapes
and coalescing rules are specified in [architecture.md](architecture.md#pending-operation-queue-local--server) and
enforced on both sides by `packages/contracts` and its fixtures. Key properties:

- Every operation carries an `opId`: a UUIDv7 idempotency key generated once and reused for every retry. A retry
  can therefore never create a duplicate note or revision.
- Every operation also records its base revisions (`baseMetaRev`, `baseContentRev`), its `deviceId` and its
  `actorKind`.
- `update_content` carries a checksum, not text. The push reads the current text and verifies it against the
  checksum.
- Coalescing keeps the queue bounded:
  - There is one queued `update_content` per note.
  - Renames and content updates fold into a queued `create`.
  - Purging a never-synced node drops its operations unless a surviving operation still refers to it.
  - Operations that were already attempted are never rewritten or dropped.
- `document_sync_base` is reserved for the last text the server acknowledged, which Phase 2's three-way merge
  uses as the base. It stays empty in Phase 1.

### What is deliberately local and never synced

- External files opened from Windows and their crash-recovery buffers (spec §3: never silently upload).
- Caret, selection and scroll positions, which are kept per device (spec §4).

## Phase 2 design

### Authentication

- Google sign-in through Supabase Auth, using the OAuth authorization-code flow with PKCE from the desktop app.
  The app opens the system browser and receives the callback on a loopback redirect.
- The desktop app receives only public client configuration, never a service-role key or provider secret. Tokens
  are stored with Windows DPAPI.
- On first sign-in the local profile (`profiles/local`) is adopted into the account. The server accepts the
  client-generated UUIDv7 ids, so nothing is renumbered.

### Push

1. The engine reads the oldest operations in order (`seq`) and sends them in small batches to the API.
2. The API runs each operation in its own transaction, inside `withUserTx` so that row-level security applies,
   with a row in `idempotency_records` keyed by `(user_id, op_id)`:
   - A replayed `opId` returns the stored response without applying the operation again.
   - A base revision behind the server's answers `stale_revision`. The operation is never applied blindly.
   - A successful write updates the content or meta revision through the database's triggers, writes a
     `document_revisions` row for content changes, and records a `sync_events` row through
     `nimpad.record_sync_event`, which makes the workspace's event sequence gap-free.
3. On acknowledgement, in one local transaction, the engine:
   - stores the returned revisions on the node;
   - rebases the base revisions of that node's later queued operations;
   - updates `document_sync_base`;
   - deletes the operation.
4. Failures retry with exponential back-off. `attempts` and `last_error` are kept, so the status bar can show
   "Sync paused · Retry" without touching the local copy.

**Server semantics the API must match.** These come from the contract and the desktop queue, and are covered by
the queue's replay tests:
- A `delete` trashes one top-level node and its subtree as one batch. A `restore` brings the whole batch back.
- A `purge` applies to a node that is already in the Trash and removes its whole subtree.
- A never-synced folder can arrive as `create`, `delete`, `purge` when a surviving operation still referred to it.
  This is valid and replayable.
- An attempted `create` or `update_content` whose text was later purged is never dropped, because the server may
  already have applied it. A retry can't re-read the text, so the server must accept the replay by its idempotency
  record.

### Pull

1. Supabase Realtime only signals that a workspace changed (spec §10: "the realtime channel signals changes;
   clients fetch authoritative revisions").
2. The client then pages `sync_events` by `(workspace_id, seq)` after its stored cursor. Because the sequence is
   gap-free in commit order, no change can be skipped.
3. It fetches the authoritative node and content revisions, and applies them locally through the same services.
   Server-origin writes must not enqueue new operations; the actor parameter on the write helpers already allows
   for this.

### Conflicts

- **Optimistic concurrency.** Every update names the revision it was made against.
- **Content conflicts.** When a local queued edit and a remote change touch the same note, the engine attempts a
  three-way merge of `document_sync_base` (base), the local text and the server text.
  - Non-overlapping line edits merge automatically. The merge is an ordinary revision, attributed to the user.
  - Overlapping edits are never merged silently. Both versions are kept, the tab shows the amber conflict dot,
    and the status bar shows "1 conflict".
  - The conflict dialog (design page 11) offers "Keep this computer's version", "Keep Laptop's version",
    "Keep both" and "Decide later". The version not kept stays in Trash for 30 days.
- **Metadata conflicts.** Concurrent renames and moves of different fields don't conflict, because the meta and
  content revisions are separate. For a true conflict on the same field, the later server write wins, and the
  local change is kept in history and shown to the user.
- **Agents.** Agents (Phase 4) write through the same API with an expected revision. A stale agent write is
  rejected with `stale_revision` (spec Scenario 7).

### Session synchronization

- Tab list, tab order, pinned tabs, last active workspace and layout preferences are stored per device in
  `device_sessions`. Only the transferable part is used as the starting session on a new computer.
- One PC never continuously overwrites another's active tab or caret.

### Status bar states (design page 13)

| State | Shown when |
|---|---|
| Local only | Phase 1, or signed out |
| Synced | The queue is empty and the last pull succeeded |
| Syncing… | Only after about 600 ms of pending work, so quick saves never flash |
| Offline · saved locally | No connectivity; edits keep committing locally |
| Back online | Briefly after reconnecting, while the queue drains |
| 1 conflict | A content conflict is waiting for the user |
| Sync paused · Retry | Repeated failures; the local copy is untouched and editable |

### Phase 2 operational items

- Schedule `nimpad.purge_expired_idempotency()` once the API writes idempotency records. Loop until it returns
  fewer rows than the batch size. The worker's database-backed job source arrives in Phase 6, so until then
  Phase 2 owns this, for example as an API-side timer.
- Define the `sync_events.event_type` vocabulary and per-type payload schemas in `packages/contracts` before any
  event is written. Events are immutable, so the vocabulary must be fixed first.
