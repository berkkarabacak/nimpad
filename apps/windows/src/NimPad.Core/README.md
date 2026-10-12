# NimPad.Core

The desktop app's domain model, local SQLite storage and services. It targets `net10.0`, references no UI
framework and runs (and is tested) on any OS. The cross-component contracts it implements (IDs, timestamps,
canonical text, checksums, fractional-index ordering, the pending-operation queue, threading) are in
[docs/architecture.md](../../../../docs/architecture.md).

## Composition

`CoreServices.Create(LocalDatabase.Open(paths, clock), clock)` wires every service over one database with a shared
`TimeProvider`, ID generator and change notifier, and starts the background search indexer. The composition root
owns the database. On exit:

1. `await services.Autosave.FlushAllAsync(timeout)`
2. `await services.Backups.CheckpointAndOptimizeAsync()`
3. `services.Dispose()`
4. `database.Dispose()`

Threading rules:
- Every write is one `BEGIN IMMEDIATE` transaction on the single writer thread (`SqliteWriter`), together with its
  pending operation.
- Reads use pooled, query-only connections on the thread pool.
- No method blocks the calling thread on I/O.
- Events (`WorkspaceChangeNotifier.Changed`, the autosave events, `SearchIndexer` events) are raised on background
  threads; marshal them to the UI thread.

## Public API

| Area | Types | What they do |
|---|---|---|
| Ids, time, ordering | `NodeId`, `WorkspaceId`, `TrashBatchId`, `IIdGenerator`/`UuidV7Generator`, `Timestamps`, `FractionalIndex` | UUIDv7 IDs as lowercase text, millisecond UTC timestamps, sibling keys. |
| Text | `ContentCanonicalizer`, `DocumentPreview`, `LinkParser`, `NoteLink` | LF canonicalization and `sha256:` checksums. `IsStorable` / `ToStorableText`: U+0000 and unpaired surrogates (which the server cannot store) become U+FFFD; every managed-note save and import applies it. The 120-character preview line. `nimpad://note/{id}` links (strict 8-4-4-4-12 hex UUID, prefix case folded for ASCII only): find, extract, `CreateUri`, `CreateLinkText` (Markdown or bare URI). |
| Domain | `NodeInfo`, `NodeKind`, `DocumentFormat`/`DocumentFormats`, `DocumentContent`, `DocumentSaveResult`, `TrashItem`, `Workspace`, `InsertPosition`, `NodeNames` | Node metadata and document content records. `DocumentFormats.Detect` picks the format from the extension, or from the content when there is none. |
| Session domain | `SessionTab`, `SessionTabKind`, `RecentlyClosedTab`, `UiState`, `ExplorerSortOrder`, `AppSettings`, `ThemePreference` | Device-local session and settings records. |
| Storage | `AppPaths`, `LocalDatabase`, `SqliteWriter`, `WriteContext`, `ReadConnections`, `BackupService`, `StartupRecovery` | Paths, open/check/migrate, the single writer, readers, daily backups. Schema migrations are `Storage/Migrations/NNNN_*.sql`; version 2 adds `document_contents.version`, `session_tabs.title` and `external_file_buffers.has_bom`; version 3 indexes the `parentId` of queued creates and moves. A locked database fails to open (`SqliteException`) and is left alone; only damage triggers recovery. |
| Tree | `NodeService`, `WorkspaceService`, `WorkspaceChangeNotifier` | Create, rename, move, copy, pin, Trash, restore, purge. Change events. See "Trash" below. `DiscardIfPristineAsync` hard-deletes an untouched `Untitled N` note when its tab closes. |
| Documents | `DocumentService` (implements `IDocumentContentSaver`), `DocumentConflictException` | `GetContentAsync` returns text, format, checksum, `UpdatedAt` and `Version`. `SaveContentAsync(id, text, expectedChecksum)` stores the text in its storable, LF form, then writes nothing when the checksum is unchanged; otherwise one transaction updates the content, preview, `content_updated_at`, links, search index and the coalesced `update_content`. With `expectedChecksum` (the stored text the editor's text is based on) a save over text that changed elsewhere fails with `DocumentConflictException` and writes nothing. Saves of one note commit in call order. Also `SetFormatAsync`, `GetBacklinksAsync`, `GetOutgoingLinksAsync`. Saves to notes in the Trash are accepted so last edits are never lost. |
| Autosave | `AutosaveScheduler`, `IAutosaveSource`, `AutosaveState`, `AutosaveSucceededEventArgs`, `AutosaveFailedEventArgs` | `NotifyChanged(nodeId, source)` on every edit. Saves 400 ms after the last change, at most 1000 ms after the first unsaved one. Dirty state follows source versions; saves of one note never overlap. Failures retry after 1, 2, 4 … 30 s. `Track(nodeId, source, baseChecksum)` (pass the loaded `DocumentContent.Checksum`; tracking a tracked note again never marks its edits saved), `FlushAsync` (snapshot on the calling thread), `FlushAllAsync(timeout)`, `Remove`, `IsDirty`, `GetState`. Each save names the stored text it replaces; a conflict or a note deleted forever halts automatic saving (`AutosaveFailedEventArgs.WillRetry` is false, the note stays dirty) until the App calls `Rebase` (keep the editor's text) or `Remove`. |
| Search | `SearchService`, `SearchResult`, `TextRange`, `SearchIndexer` | `SearchAsync(query, limit, ct)`; see below. `SearchIndexer` indexes documents over 256 K characters (`fts_dirty`) once writes have been quiet for 2 s, and at startup. |
| Session | `SessionService`, `SettingsService` | Described below. |
| External files | `ExternalFileService`, `ExternalFileContent`, `TextEncodingInfo`, `LineEndingInfo`, `LineEndingStyle`, `RecoveryBuffer`, `ExternalFileTooLargeException`, `UnrepresentableCharactersException` | Described below. |
| Sync | `PendingOperationQueue`, `PendingOperation`, `PendingOperationKind`, `LocalActorKind` | Read API of the local → server queue. Phase 1 never drains it. Debug builds check every queued operation against the contract (internal `PendingOperationContract`, which also passes the shared `pending-operations.json` fixture). |

### Trash and the queue

- `TrashAsync(ids)` makes each top-level node its own Trash batch (one Trash item, one `delete`), and returns the
  batches. A batch is what a restore brings back, as the contract's `restore` does: `RestoreAsync(id)` restores the
  batch that contains the node (one `restore`), and `RestoreBatchAsync(batches)` undoes a multi-item trash.
- Deleting forever keeps the queue replayable on the server: a node the server cannot know (never acknowledged,
  nothing of it attempted) has its operations dropped, unless a surviving queued create or move still names it as
  the parent; then they stay. A kept root gets one `purge` after its operations. Queued `update_content`s of purged
  documents are dropped (the push reads the text, which is gone). Attempted operations are never dropped.

### Search (`SearchAsync`)

- Each whitespace-separated piece of input becomes a quoted FTS5 prefix term (`"piece"*`, with `"` doubled), and the
  terms are combined with AND. User input never reaches FTS5 as syntax.
- Blank input, or input with no letters or digits, returns no results. The method never throws because of what was
  typed.
- Results are ranked by bm25, with name matches weighted 10×. Items in the Trash are excluded.
- Each result carries the ancestor path, a snippet of at most 120 characters (the line of the first match, cut with
  `…`), highlight ranges for the snippet and the name, and `ContentUpdatedAt`.
- Cancelling the token interrupts a running query.
- Benchmark (`SearchBenchmarkTests`): 20 typical queries over 10,000 notes of 1–2 KB each have a median of about
  12 ms on the dev box. The target is 200 ms.

### Session and settings (device-local, never queued)

- **`SessionService`**
  - `LoadTabsAsync` / `SaveTabsAsync`: the whole tab list, replaced atomically. Each tab has its kind, node or path,
    title, pinned flag, order, active flag, caret, selection, and vertical and horizontal scroll offsets.
  - Reopen stack: `PushRecentlyClosedAsync`, `PopRecentlyClosedAsync`, `GetRecentlyClosedAsync` and
    `ClearRecentlyClosedAsync`. It holds 20 entries, last in first out, and is persisted.
  - `LoadUiStateAsync` / `SaveUiStateAsync`: expanded folders, sidebar width and collapsed state, sort order,
    selected node, and context panel visibility.
- **`SettingsService.LoadAsync` / `SaveAsync`**: an `AppSettings` record covering theme, editor font and size, word
  wrap, line numbers, tab size (2, 4 or 8), restore tabs, start with Windows, global hotkey and zoom.
- **Storage**: settings and UI state are stored one key per row (`SettingsService.Keys`). A missing or unreadable key
  takes its default, and keys written by a newer version are left untouched.

### External files (`ExternalFileService`)

- **`OpenAsync(path, progress, ct)`**
  - Encoding comes from the BOM (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE). Without a BOM, text whose every other byte is
    mostly zero is read as UTF-16 (and saved again without a BOM), valid UTF-8 as UTF-8, and anything else as
    Windows-1252.
  - The text is kept verbatim; `LineEndingInfo` describes its line endings (CRLF, LF, CR or Mixed, plus the dominant
    style).
  - Progress is reported for files over 5 MB. Files over 256 MB are refused with `ExternalFileTooLargeException`.
- **`SaveAsync(path, text, encoding)`**: writes a temporary file (a fixed-length name in the same directory) and
  flushes it to disk, then swaps it in with `File.Replace`, keeping the original under a backup name until the new
  file is in place. Where replacing is unsupported (such as some network shares), it moves the file over the target
  instead. A failure leaves the original in place (put back if a replace failed halfway); only if it cannot be put
  back are both copies kept and named in the error.
  - The BOM choice is kept, and symbolic links are saved through to their target.
  - Characters the encoding can't represent throw `UnrepresentableCharactersException` and leave the file untouched.
  - It returns the new last write time, so the App can ignore its own `FileSystemWatcher` event.
- **Recovery buffers**: `SaveRecoveryBufferAsync`, `ClearRecoveryBufferAsync` and `GetRecoveryBuffersAsync`. They are
  stored in `external_file_buffers` and are never part of `pending_operations`.
- **`ImportAsync(path, parentFolderId)`** creates a managed note named after the file, with LF, storable text and a
  detected format, queued with actor `import`.
- **`ExportAsync(nodeId, path)`** (Ctrl+S on a managed note) writes UTF-8 without a BOM and keeps the note's LF line
  endings. The exported bytes are exactly the canonical text: the checksum matches, a re-import is identical, and
  every current Windows editor reads LF.

## Tests

Run `dotnet test --project apps/windows/tests/NimPad.Core.Tests`. Notable suites:
- `ContractFixtureTests` (with `FractionalIndexTests` and `ContentCanonicalizerTests`) runs every shared fixture in
  `packages/contracts/fixtures`.
- `PendingOperationQueueTests` replays the queue on a model of the server (`Support/ServerReplica`) and checks that
  every operation is valid and applicable in order, and that the result equals the local tree.
- `Phase1AcceptanceTests`:
  - 500 notes in a six-level tree, with pins, Trash and restore, moves, a session and settings, are closed and
    reopened; everything compares identical, matches an independent record of what the test did, and the whole
    queue replays to the same tree on an empty server.
  - A crash copy of the database and its WAL, taken without any shutdown, keeps all committed work.
- `AutosaveSchedulerTests` uses a fake clock.
- `SearchBenchmarkTests` runs alone, in the performance collection.
- `ExternalFileServiceTests` has byte fixtures for every supported encoding.
