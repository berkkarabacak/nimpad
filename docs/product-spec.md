# NimPad — Technical Product Specification
**Version:** 1.0  
**Platform:** Windows 10/11  
**Architecture:** Local-first desktop application + cloud backend + AI-agent orchestration layer  
**Status:** Implementation-ready specification

> This file is the product's primary source of truth, reproduced verbatim from the specification supplied by the
> product owner. The approved visual design lives in [`docs/design/`](design/) and its tokens are summarized in
> [`docs/design/tokens.md`](design/tokens.md).

---

## 1. Product Overview

Build NimPad, a fast, lightweight, modern Windows text editor that synchronizes notes between computers and serves as a central workspace for humans and multiple AI agents.

NimPad combines five capabilities:

1. **Fast text editing** — a lightweight alternative to Windows Notepad and Notepad++.
2. **Cloud synchronization** — every note and workspace automatically follows the user between PCs.
3. **Hierarchical organization** — nested folders and pages similar to Windows Explorer and Atlassian Confluence.
4. **Version history** — recover earlier versions and inspect changes made by humans or AI agents.
5. **AI-agent orchestration** — external agents such as Claude can access notes, collaborate on tasks, and maintain shared information through an API and MCP server.

The application must remain primarily a text editor, not become a complicated project-management interface.

**Core philosophy: An extremely fast editor for humans, with a powerful programmatic interface for agents.**

## 2. Primary User Experience

### 2.1 Application Startup

- Start extremely quickly.
- Restore the previous workspace, tabs, and scroll positions.
- Load locally cached notes without waiting for cloud access.
- Synchronize changes asynchronously.
- Keep the user signed in between sessions.
- Never display a blocking synchronization screen during ordinary startup.

### 2.2 Main Interface

Use the previously established NimPad visual design.

**Left Sidebar — Explorer**

- Hierarchical folder and page navigation.
- Unlimited nested folders.
- Expand/collapse nodes.
- Drag-and-drop reordering and moving.
- Right-click context menus.
- Quick access to recent and pinned notes.
- Search across notes.
- Special sections for Daily Notes, Agent Activity, and Tasks.
- Resizable and fully collapsible.

**Top Bar — Tabs**

- Multiple open notes.
- Create, close, pin, rename, and reorder tabs.
- Visually distinguish modified, syncing, and conflicted notes.
- Restore tabs between sessions and across devices.
- Support tab switching with standard shortcuts.

**Main Area — Text Editor**

- Instant typing and scrolling.
- Plain text by default.
- Markdown, JSON, XML, YAML, Python, Groovy, JavaScript, and other common syntax modes.
- Find/replace, line numbers, word wrap, indentation, and standard editing shortcuts.
- Autosave without requiring filenames.
- No rich-text editing toolbar.
- Optional split editor, deferred until core features are stable.

**Right Sidebar — Context Panel**

Hidden by default and shown when requested.

Tabs:
- Version History
- Agent Activity
- Tasks
- Document Information

The editor must remain fully usable when both sidebars are collapsed.

**Bottom Status Bar**

Display line/column, file format, character count, and synchronization status.

### 2.3 Themes

Provide polished light and dark themes following Windows 11 visual conventions.

Avoid heavy animations, large decorative panels, and unnecessary visual effects.

---

## 3. Hierarchical Notes System

NimPad must support a genuine hierarchical information structure.

Example workspace:

- **My Workspace**
  - **Daily Notes**
    - 2026
      - October
        - 2026-10-12
        - 2026-10-13
  - **Projects**
    - NimPad
      - Product Requirements
      - Architecture
      - Development Log
    - Other Projects
      - Ideas
      - Technical Notes
  - **Work**
    - Meetings
    - Technical Documentation
  - **AI Agents**
    - Shared Context
    - Research
    - Agent Reports
  - **Personal**
    - Quick Notes
    - Reading Notes

### Requirements

- Folder nesting must not have an arbitrary shallow limit.
- Support create, rename, move, copy, and soft-delete.
- Allow moving notes without breaking internal links.
- Identify notes by stable IDs, not their folder paths.
- Provide breadcrumbs above the editor.
- Support links between notes.
- Support pinned folders and documents.
- Provide a global search that includes nested pages.
- Preserve tree expansion and ordering between sessions.

### Document Formats

Use a simple text-based document model.

Supported formats:
- Plain text
- Markdown
- JSON
- XML
- YAML
- Common source-code languages

Markdown remains editable as raw text; rich Markdown rendering can be added later.

### Local Files

Users must also be able to open and edit ordinary Windows files.

Distinguish between:

**Managed NimPad Documents**
- Stored in the workspace.
- Automatically synchronized.
- Versioned and accessible to authorized agents.

**External Local Files**
- Opened directly from Windows.
- Saved to their original location.
- Not automatically uploaded to the cloud.
- Can be explicitly imported into a NimPad workspace.

Never silently upload external files.

---

## 4. Automatic Synchronization

### Core Requirements

- Google SSO authentication.
- Automatic cloud synchronization.
- Local-first SQLite storage.
- Offline editing.
- Background synchronization.
- Conflict detection and recovery.
- Persistent tabs and session state.
- Consistent folder hierarchies across devices.

### Sync Behavior

Every user action should update the local state first.

The background synchronization engine propagates changes to the server and subsequently to other connected computers.

The editor must never pause typing while waiting for a network request.

### Session Synchronization

Synchronize:
- Open documents.
- Tab order.
- Pinned tabs.
- Last active workspace.
- Workspace layout preferences.

Persist cursor and scroll positions per device. On a newly connected computer, restore the last transferable session as a starting point.

Avoid continuously overwriting the active cursor or tab on one PC when the user works on another.

### Offline Mode

- All cached notes remain editable.
- Changes enter a durable local outgoing queue.
- Queued operations upload when connectivity returns.
- The system retries failed requests.
- Duplicate requests must not create duplicate notes or revisions.
- Visible status indicates offline, pending, syncing, synced, or conflict.

### Conflict Resolution

Use document revision numbers and optimistic concurrency.

If two devices or agents edit the same revision:

1. Detect the conflicting changes.
2. Attempt a safe three-way merge where possible.
3. Never silently overwrite either version.
4. Preserve conflicting content.
5. Offer a comparison and resolution interface.

AI agents must submit updates against a specific expected revision. Stale writes must be rejected and retried or proposed for review.

---

## 5. Version History

Version history is a first-class feature.

### Requirements

- Automatically create document revisions.
- Track human edits and AI-agent edits.
- Display date, time, author, and change description.
- Show a visual diff between revisions.
- Restore previous versions.
- Preserve complete document history within configured retention limits.
- Support searching or browsing history by author and date.
- Include version history for agent-generated notes.

### Revision Rules

- Save locally almost immediately.
- Synchronize current content using debounced writes.
- Create historical checkpoints after meaningful edits or a configurable interval.
- Always create identifiable revisions for agent commits and manual restores.
- Restoring an older version creates a new revision; do not erase history.
- Deleting a document moves it to Trash rather than permanently removing it immediately.

Example history:

| Time | Actor | Action |
|---|---|---|
| 09:00 | User | Created document |
| 09:15 | Claude | Added research notes |
| 10:00 | User | Edited summary |
| 11:30 | Research Agent | Updated recommendations |
| 11:45 | User | Restored previous section |

---

## 6. AI-Agent Integration

**This is a major differentiator for NimPad.**

The application must be designed from the beginning so that AI agents are first-class participants in the workspace.

Agents must not depend on the Windows UI to interact with notes.

### 6.1 Model Context Protocol (MCP)

Implement an MCP server that exposes NimPad's information and permitted operations.

The server must support current compatible MCP standards and integrate with clients such as Claude.

Provide:
- Remote HTTPS MCP endpoint for cloud access.
- Secure delegated authentication.
- Tool definitions with structured parameters and outputs.
- Clear error responses.
- Fine-grained access permissions.
- Optional local STDIO MCP adapter for local development and Claude Code.

The remote server must work even when no NimPad desktop application is running.

### 6.2 Agent Capabilities

Authorized agents should be able to:

**Read**
- List workspaces.
- Browse folder hierarchies.
- List notes in a folder.
- Search notes.
- Read document content.
- Read relevant version history.
- Read recent workspace activity.

**Write**
- Create notes.
- Edit existing notes.
- Append text.
- Apply targeted patches.
- Rename notes.
- Create folders.
- Move documents.
- Produce reports.
- Update daily notes.
- Propose changes requiring human review.

**Orchestrate**
- Create tasks.
- Assign tasks to agents.
- Claim available tasks.
- Update task status.
- Record progress.
- Add task comments.
- Link tasks to documents.
- Request human approval.
- Hand off work to another agent.
- Record task completion and resulting artifacts.

### 6.3 Suggested MCP Tools

Implement a stable, documented tool interface.

Workspace:
- `list_workspaces`
- `get_workspace_tree`
- `list_folder_contents`

Notes:
- `search_notes`
- `get_note`
- `create_note`
- `update_note`
- `append_to_note`
- `move_note`
- `get_note_history`
- `restore_note_version`

Daily notes:
- `get_or_create_daily_note`

Agent orchestration:
- `list_tasks`
- `create_task`
- `claim_task`
- `update_task`
- `add_task_comment`
- `complete_task`
- `get_recent_activity`

Group or expose tools progressively to avoid overwhelming MCP clients.

For note updates, require stable note IDs, an expected revision, and an operation idempotency key.

Use predictable structured results containing relevant IDs, revision numbers, timestamps, and errors.

### 6.4 Agent Identity

Each connected agent must have a recognizable identity.

Example:
- Claude
- Research Agent
- Coding Agent
- Documentation Agent

Maintain:
- Agent ID.
- Display name.
- Integration type.
- Connection status or last heartbeat.
- Allowed workspace and folder access.
- Read/write permissions.
- Activity history.
- Revocation status.

Agents must have separate identities and credentials rather than sharing a single administrator account.

The application should show which agent made each modification.

### 6.5 Permissions

Support these access levels:
- Read-only.
- Read and write.
- Task management.
- Workspace administration.

Allow restrictions to particular workspaces or folders.

Require explicit human approval for high-impact operations such as bulk deletion, mass restructuring, or destructive restoration when configured.

Agent credentials must be revocable.

Basic security is required even though end-to-end encryption is not a product priority.

---

## 7. Multi-Agent Collaboration and Orchestration

NimPad should function as a shared coordination environment.

It is not sufficient for agents merely to create documents. They must be able to exchange context, assign work, and track progress.

### 7.1 Shared Context

All authorized agents work against the same synchronized knowledge base.

Example:

- User creates a product-research task.
- Research Agent gathers findings and writes a document.
- Research Agent records a handoff for Coding Agent.
- Coding Agent reads the findings and creates technical implementation notes.
- Documentation Agent summarizes the results.
- User reviews the outcome in NimPad.

Every action must be attributed and auditable.

### 7.2 Task System

Create a lightweight task system designed for agents rather than a full Jira replacement.

Each task includes:
- ID and title.
- Description and acceptance criteria.
- Creator.
- Assignee.
- Priority.
- Status.
- Linked notes.
- Dependencies.
- Creation and update timestamps.
- Activity history.
- Completion output.

Supported states:
- Backlog
- Ready
- In Progress
- Blocked
- Needs Review
- Done
- Failed

Agent task claims must be atomic to prevent two workers from independently claiming the same exclusive task.

Use task leases with expiration and retry rules for crashed or disconnected workers.

### 7.3 Agent Communication

Agents communicate through:
- Shared documents.
- Task comments.
- Structured task handoffs.
- Workspace events.
- Optional mentions.

Every handoff should describe what was done, what remains, the relevant document links, and the next expected action.

The system should allow the human user to intervene at any point.

### 7.4 Events and Notifications

Emit events for:
- Document created or updated.
- Folder changed.
- Agent assigned a task.
- Task completed or blocked.
- Human approval requested.
- Agent handoff.
- Sync conflict.

Provide an event stream for the application and authenticated webhook or queue delivery for external agent runners.

Events must be deduplicated and processed safely on retry.

**Important:** MCP provides agents with tools. It does not by itself execute agents, schedule prompts, or keep them running. A separate runner or scheduler is responsible for autonomous execution.

---

## 8. Daily Notes and Automated Agent Workflows

Daily Notes should be a native workspace feature.

### Daily Note Creation

- Create one daily note per workspace, local calendar date, and configured timezone.
- Automatically organize it into year/month folders.
- Allow manual creation and optional automatic generation.
- Repeated generation requests must not create duplicates.

### Daily Note Template

**Daily Summary — YYYY-MM-DD**

- Summary
- Completed Work
- In-Progress Tasks
- Agent Activities
- Important Decisions
- Open Questions
- Tomorrow's Priorities

### Claude Integration Example

The user may request:

"Claude, create today's daily note from my recent NimPad activity, summarize completed agent tasks, and list outstanding issues."

Claude should:
1. Read authorized workspace activity.
2. Retrieve relevant tasks and documents.
3. Create or update the correct daily note.
4. Include references to source notes.
5. Save through MCP.
6. Attribute the changes to Claude.

### Scheduled Generation

Allow users to configure workflows such as:

"Every morning, prepare my daily brief."

This requires a separate scheduled agent runtime with an explicitly configured model provider, permissions, and execution policy.

Do not assume connecting Claude through MCP automatically enables unattended model execution.

For autonomous execution, track model API costs and job failures.

Never invent daily activity that is absent from the connected workspace.

---

## 9. Agent Activity Interface

Create an optional dedicated Agent Activity view.

Display:

- Connected agents.
- Last known activity.
- Active tasks.
- Recent document modifications.
- Handoffs.
- Requests awaiting human review.
- Failed operations.

Example:

| Agent | Activity | State |
|---|---|---|
| Claude | Writing Daily Summary | Running |
| Research Agent | Market Research | In Progress |
| Coding Agent | Waiting for Research | Blocked |
| Documentation Agent | Updated Architecture Notes | Completed |

Show "Running" only when an actual connected runtime reports a valid active job. Do not infer that an MCP-connected agent is continuously running.

Clicking an activity opens its associated note, revision, or task.

---

## 10. Recommended Technology Stack

### Windows Desktop

- Language: C#
- Framework: .NET 10
- UI: WPF with modern Windows/Fluent styling
- Editor: AvalonEdit
- Local database: SQLite with FTS5 full-text search
- Architecture: MVVM
- Distribution: Windows installer and portable builds

**Architectural adjustment from the earlier design:** Prefer WPF + AvalonEdit over WinUI 3 + Scintilla for the first implementation. This is a practical, established combination for a fast native Windows text editor, with fewer interoperability concerns.

Maintain a polished Windows 11 appearance without sacrificing responsiveness.

### Backend

- Language: TypeScript
- Runtime: Node.js
- HTTP framework: Fastify
- Primary database: PostgreSQL via Supabase
- Authentication: Supabase Auth + Google SSO
- Realtime updates: Supabase Realtime
- MCP server: Official compatible MCP SDK
- Background jobs: A persistent worker with database-backed scheduling/queueing
- Hosting: A reliable HTTPS service with persistent availability

### Important Architecture Rules

- The Windows application always reads the local cache first.
- Cloud storage is the source of truth for synchronized shared state.
- The backend enforces revision checks and access permissions.
- The realtime channel signals changes; clients fetch authoritative revisions.
- All agent mutations pass through the same application service and validation rules as human changes.
- No agent may bypass versioning, permissions, or audit logging by writing directly to database tables.
- AI model execution is an optional service, separate from basic note synchronization.

### High-Level Architecture

**Windows App**
- WPF User Interface
- AvalonEdit Editor
- Local SQLite Cache
- Sync Engine
- Authentication Client

Communicates with:

**NimPad Cloud**
- REST API
- Google/Supabase Authentication
- PostgreSQL
- Realtime Notifications
- Version History Service
- Task and Activity Service

Connected to:

**AI Integration Layer**
- MCP Server
- Scoped Agent Authorization
- Task Queue
- Webhook/Event Dispatcher
- Optional Scheduled Agent Runner
- Claude and Other Compatible Agents

---

## 11. Data Model

Create schema migrations for the following core entities.

### Users and Workspaces
- `users` — application profile linked to authentication provider.
- `workspaces` — named workspaces with ownership.
- `workspace_members` — user memberships and permissions.
- `devices` — registered device metadata.
- `device_sessions` — tabs, layout preferences, and restore information.

### Hierarchical Documents
- `nodes` — folders and documents, including workspace, parent node, type, name, position, and deletion status.
- `document_contents` — text, format, current revision, and content checksum.
- `document_revisions` — immutable historical snapshots, author, revision number, timestamp, and optional change summary.
- `document_links` — internal references between documents.

### Agents and Orchestration
- `agents` — identity, owner, and permissions.
- `agent_credentials` — hashed/revocable credentials or delegated authorization references.
- `tasks` — tasks, states, assignees, dependencies, and linked documents.
- `task_events` — task progress and handoffs.
- `agent_runs` — actual scheduled or connected agent execution records.
- `activity_events` — workspace activity and actor attribution.
- `approvals` — human approval requests and decisions.
- `webhook_subscriptions` — authenticated event destinations.

### Synchronization and Recovery
- `sync_events` — committed change events.
- `idempotency_records` — protection against repeated operations.
- `audit_log` — immutable security and agent operation records.

The local SQLite schema must also contain a durable pending-operation queue.

### Database Rules

- Stable UUID identifiers.
- Foreign-key integrity.
- Folder-cycle prevention.
- Atomic document revision updates.
- Atomic task claiming.
- Soft deletion with recovery.
- Pagination for large folders.
- Indexes for workspace membership, parent nodes, revisions, tasks, and timestamps.
- User/agent authorization enforced at the service layer and supported by PostgreSQL row-level security.

---

# 12. Implementation Plan

Build the product incrementally. Each phase must produce a usable, testable vertical slice.

## Phase 0 — Repository and Project Foundation

**Goal:** Establish a reliable development environment.

Tasks:
- Create a monorepo.
- Configure Windows desktop project.
- Configure backend and MCP projects.
- Configure database migrations.
- Configure automated tests.
- Configure GitHub Actions.
- Create local development environment instructions.
- Define shared API contracts.
- Establish linting, formatting, and build commands.

Suggested repository organization:

- `/apps/windows` — Windows application
- `/services/api` — backend application
- `/services/mcp` — MCP adapter/server
- `/services/worker` — scheduler and background job runner
- `/packages/contracts` — shared API schemas
- `/database/migrations` — database schema
- `/tests` — integration and end-to-end tests
- `/docs` — architecture, deployment, and operating instructions

**Acceptance:** A developer can clone the repository, run automated setup steps, and launch the desktop application and local backend successfully.

## Phase 1 — Native Editor and Hierarchical Workspace

**Goal:** Deliver a genuinely usable offline text editor.

Implement:
- Modern main window.
- Fast text editing using AvalonEdit.
- Tabs and keyboard shortcuts.
- Light/dark themes.
- SQLite persistence.
- Autosave.
- Crash recovery.
- Nested folder tree.
- Drag-and-drop note organization.
- Local search.
- Document creation, editing, moving, and deletion.
- External file opening.

**Acceptance:** A user can create hundreds of notes, organize them in nested folders, close the application, reopen it, and recover everything without using the internet.

Do not proceed until ordinary editing is stable and responsive.

## Phase 2 — Google Authentication and Cloud Sync

**Goal:** Make workspaces available across computers.

Implement:
- Google OAuth login using an appropriate desktop PKCE flow.
- Secure local session handling.
- Workspace ownership.
- Cloud document storage.
- Background synchronization.
- Realtime change notifications.
- Offline mutation queue.
- Retry and idempotency logic.
- Session synchronization.
- Conflict handling.

**Acceptance:** Two independently installed Windows clients can sign in to the same account, synchronize notes and folder hierarchies, recover from disconnection, and preserve concurrent edits without silent data loss.

## Phase 3 — Version History

**Goal:** Provide robust recovery and accountability.

Implement:
- Automatic revisions.
- Actor attribution.
- Version-history panel.
- Text comparison UI.
- Restore functionality.
- Trash and recovery.
- Revision retention policy.

**Acceptance:** A user can view revisions, identify changes made by an agent, compare versions, restore an older version, and retain the complete restore audit trail.

## Phase 4 — MCP Server and Claude Integration

**Goal:** Give Claude real read/write access to the workspace.

Implement:
- Secure remote MCP server.
- MCP-compatible authentication and authorization.
- Workspace and note tools.
- Search tools.
- Revision-aware update tools.
- Agent identity.
- Scoped permissions.
- Activity attribution.
- Local MCP development option.

**Acceptance:** From Claude, a user can request:

"Find the NimPad project, read its architecture notes, and create an updated development summary."

Claude must successfully find the documents, read them, create the summary in the correct folder, and return the resulting note reference.

The created note must appear in the Windows app without restarting it.

## Phase 5 — Multi-Agent Task Orchestration

**Goal:** Support multiple agents working through shared tasks.

Implement:
- Task database.
- Tasks panel.
- Agent identities.
- Atomic task claiming.
- Status transitions.
- Task comments and handoffs.
- Task-linked notes.
- Human review and approvals.
- Workspace activity feed.
- Agent event and webhook delivery.

**Acceptance:** Two different agents can cooperate on one workflow, pass work between each other, and report completion while the user can inspect the full activity history.

The orchestration service must prevent duplicate exclusive task claims.

## Phase 6 — Automated Daily Notes and Scheduled Workflows

**Goal:** Enable repeatable agent activity without requiring the Windows application to remain open.

Implement:
- Daily notes hierarchy.
- Daily note templates.
- Idempotent daily note creation.
- Configurable scheduler.
- Agent runner interface.
- Claude-compatible model-provider integration.
- Scheduled jobs with retry and failure handling.
- Source-linked activity summaries.
- Usage and cost tracking for executed model calls.

**Acceptance:** A configured scheduled job generates exactly one daily note for the appropriate date, uses available source information, records the agent's execution, and synchronizes the result to the user's PCs.

## Phase 7 — Performance, Reliability, and Release

Implement:
- Startup optimization.
- Memory profiling.
- Large-document handling.
- Efficient tree virtualization.
- Background search indexing.
- Sync stress testing.
- Multi-device concurrency testing.
- Authentication and permission testing.
- Installer packaging.
- Application updates.
- Logging and diagnostics.
- Recovery and backup procedures.

**Acceptance:** The completed application passes all functional, performance, synchronization, and security regression tests and can be installed cleanly on a new Windows machine.

---

## 13. Performance Targets

Treat these as engineering targets to benchmark, not guaranteed results.

| Area | Target |
|---|---|
| Warm startup | Under 500 ms |
| Cold startup | Under 2 seconds |
| Typing responsiveness | No perceptible input lag |
| Local autosave | Durable within 1 second under normal operation |
| Online synchronization | Typically within 2 seconds after an edit settles |
| Search | Under 200 ms for common queries over 10,000 locally indexed notes |
| Idle memory | Aim below 200 MB for the core application |
| Offline mode | Fully functional for cached documents |
| Large folders | Smooth navigation through virtualized tree controls |

The app must remain responsive when cloud services are unavailable or an agent produces many updates.

---

## 14. Required BDD Acceptance Scenarios

### Scenario 1 — Instant Local Editing

**Given** NimPad is installed and the user has a cached workspace  
**When** the user launches the application without internet access  
**Then** the editor opens using local data  
**And** the user can edit existing documents immediately  
**And** all changes are saved locally.

### Scenario 2 — Synchronization Across PCs

**Given** the user has NimPad installed on two computers  
**When** a note is created on Computer A  
**Then** it synchronizes to Computer B  
**And** its folder location and contents are preserved  
**And** both computers can reopen it offline after synchronization.

### Scenario 3 — Hierarchical Organization

**Given** a workspace has multiple nested folders  
**When** the user moves a note from one folder to another  
**Then** the change synchronizes  
**And** the note keeps its stable identity and version history  
**And** existing internal links remain valid.

### Scenario 4 — Version Recovery

**Given** Claude has modified a note  
**When** the user opens its version history  
**Then** the user can identify Claude's modification  
**And** compare it with the previous revision  
**And** restore the earlier content without destroying history.

### Scenario 5 — Claude MCP Integration

**Given** Claude has an authorized NimPad MCP connection  
**When** the user asks Claude to create a project document  
**Then** Claude creates it through MCP  
**And** NimPad attributes the change to Claude  
**And** the document appears in the correct workspace.

### Scenario 6 — Multi-Agent Handoff

**Given** Research Agent and Coding Agent are connected  
**When** Research Agent completes a research task  
**And** assigns a follow-up task to Coding Agent  
**Then** Coding Agent can retrieve the task and linked notes  
**And** work can proceed using the shared context  
**And** the user can inspect the handoff history.

### Scenario 7 — Concurrent Edits

**Given** an agent and the user are editing the same note  
**When** the agent attempts to save a stale revision  
**Then** the server rejects the stale operation  
**And** the user's changes are preserved  
**And** the agent receives a conflict response  
**And** both versions remain recoverable.

### Scenario 8 — Offline Recovery

**Given** the user is editing while disconnected  
**When** NimPad closes unexpectedly and is reopened  
**Then** the unsynchronized changes are recovered  
**And** the changes synchronize safely when connectivity returns.

### Scenario 9 — Agent Permissions

**Given** an agent has read-only access to one folder  
**When** it tries to modify a note or access an unauthorized folder  
**Then** the request is denied  
**And** the attempted action is recorded appropriately.

### Scenario 10 — Automated Daily Note

**Given** a daily-note automation is configured  
**When** its scheduled execution runs  
**Then** the agent creates or updates the correct dated note  
**And** includes references to available source information  
**And** records its execution  
**And** retries do not produce duplicate daily notes.

---

## 15. Security and Reliability Requirements

End-to-end encryption is not necessary for the initial product.

However, the application must implement essential safeguards:

- HTTPS/TLS for network communication.
- Secure Google authentication.
- PKCE for public desktop authentication clients.
- Protected local credential storage.
- Strict backend access controls.
- Agent-specific permissions.
- Token expiration and revocation.
- OAuth-compatible authorization for remote MCP clients.
- No service-role or provider secrets embedded in the Windows application.
- Validation of all inputs and agent operations.
- Rate limits and payload size limits.
- Safe conflict and restore behavior.
- Durable backups and documented recovery procedures.
- Protection against malicious or untrusted instructions embedded in note content.

An AI agent must never gain higher privileges simply because another document instructs it to do so.

---

# 16. Instructions for the Implementing AI Agent

You are the lead software architect, Windows desktop engineer, and backend engineer responsible for implementing NimPad.

Treat this specification as the product's primary source of truth.

Your objective is to deliver a working, tested, installable Windows application and the supporting cloud and MCP services.

## Engineering Rules

1. Prioritize application speed, reliability, and simplicity.
2. Follow the approved UI design. Do not invent a complicated alternative interface.
3. Build complete vertical slices, not disconnected mockups.
4. Never substitute fake synchronization or simulated AI actions for real implementations.
5. Write automated tests for significant features.
6. Run existing tests before and after changes.
7. Keep database migrations repeatable and version-controlled.
8. Document important technical decisions.
9. Avoid unnecessary dependencies and frameworks.
10. Do not silently change the product architecture.
11. Use the local-first model for all document editing.
12. Make APIs usable by both the Windows application and external AI agents.
13. Preserve revision history and actor attribution on every committed document update.
14. Never expose credentials in source code, examples, logs, or commits.
15. Implement explicit error states instead of silently ignoring failures.
16. Do not claim a feature works unless it has been tested.
17. Use current stable, compatible libraries and consult their official documentation.
18. Make reasonable engineering decisions independently; document assumptions and proceed.

## Execution Workflow

For every phase:

1. Inspect the existing code and documentation.
2. Identify completed and missing requirements.
3. Prepare a small implementation checklist.
4. Implement the next coherent set of functionality.
5. Build the affected projects.
6. Execute relevant automated tests.
7. Manually verify the critical user flows when the environment supports it.
8. Fix failures.
9. Update documentation and implementation status.
10. Commit the completed, tested work.

Do not repeatedly rebuild features that are already implemented correctly.

If authentication credentials, hosting configuration, certificates, or other external secrets are unavailable, create documented environment-variable placeholders and continue implementing all independently testable functionality.

Never fabricate successful cloud integration tests when credentials or a deployment are missing.

## Required Documentation

Maintain:
- `README.md`
- `docs/architecture.md`
- `docs/implementation-plan.md`
- `docs/product-spec.md`
- `docs/mcp-integration.md`
- `docs/synchronization.md`
- `docs/deployment.md`
- `docs/testing.md`
- `docs/implementation-status.md`

Provide a clear local startup command, environment-variable template, database setup instructions, and steps for connecting Claude to the MCP server.

## Definition of Done

The project is complete when:

- The Windows app launches and operates reliably.
- Users can create and organize notes hierarchically.
- Notes persist locally and work offline.
- Google login works.
- Multiple PCs synchronize safely.
- Version history and restoration work.
- Claude can access NimPad through a real MCP connection.
- Multiple agent identities can read, write, and coordinate through tasks.
- Scheduled daily-note generation works when configured.
- Critical acceptance scenarios pass.
- The Windows installer and deployment documentation are available.

**Start by implementing Phase 0, then Phase 1. Preserve the overall architecture so the MCP, synchronization, and multi-agent features can be added without rewriting the foundation.**
