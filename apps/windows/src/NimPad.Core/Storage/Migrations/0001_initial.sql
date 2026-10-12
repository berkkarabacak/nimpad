-- NimPad local schema, version 1. Applied once inside a transaction by SchemaMigrations; PRAGMA user_version
-- records the applied version. IDs are lowercase hyphenated UUID text; timestamps are ISO-8601 UTC text with
-- milliseconds and a Z suffix (architecture.md, "Identity, time and content contracts").

-- This installation. Exactly one row, created on first run.
CREATE TABLE IF NOT EXISTS device (
    device_id  TEXT NOT NULL PRIMARY KEY,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS workspaces (
    id         TEXT NOT NULL PRIMARY KEY,
    name       TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- Folders and documents. row_id aliases the rowid so it survives VACUUM; notes_fts is keyed by it.
CREATE TABLE IF NOT EXISTS nodes (
    row_id             INTEGER PRIMARY KEY,
    id                 TEXT NOT NULL UNIQUE,
    workspace_id       TEXT NOT NULL REFERENCES workspaces (id),
    parent_id          TEXT REFERENCES nodes (id),
    kind               TEXT NOT NULL CHECK (kind IN ('folder', 'document')),
    name               TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 255),
    position           TEXT NOT NULL,
    is_pinned          INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
    pin_position       TEXT,
    created_at         TEXT NOT NULL,
    updated_at         TEXT NOT NULL,
    content_updated_at TEXT,
    deleted_at         TEXT,
    trash_batch_id     TEXT,
    meta_server_rev    INTEGER,
    content_server_rev INTEGER,
    CHECK (parent_id IS NULL OR parent_id <> id),
    CHECK ((is_pinned = 1) = (pin_position IS NOT NULL)),
    CHECK ((deleted_at IS NULL) = (trash_batch_id IS NULL)),
    CHECK (kind = 'document' OR content_updated_at IS NULL)
);

-- Children of a parent in sibling order (also serves root lookups via parent_id IS NULL).
CREATE INDEX IF NOT EXISTS ix_nodes_parent ON nodes (workspace_id, parent_id, position, id);
-- Subtree walks (recursive CTEs) look children up by parent_id alone.
CREATE INDEX IF NOT EXISTS ix_nodes_parent_id ON nodes (parent_id);
CREATE INDEX IF NOT EXISTS ix_nodes_trash ON nodes (workspace_id, deleted_at) WHERE deleted_at IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_nodes_trash_batch ON nodes (trash_batch_id) WHERE trash_batch_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_nodes_pinned ON nodes (workspace_id, pin_position, id) WHERE is_pinned = 1;
CREATE INDEX IF NOT EXISTS ix_nodes_content_updated ON nodes (workspace_id, content_updated_at)
    WHERE content_updated_at IS NOT NULL;

-- The text of managed documents (canonical LF text). fts_dirty = 1 means notes_fts does not hold this text yet.
CREATE TABLE IF NOT EXISTS document_contents (
    node_id    TEXT NOT NULL PRIMARY KEY REFERENCES nodes (id) ON DELETE CASCADE,
    text       TEXT NOT NULL DEFAULT '',
    format     TEXT NOT NULL CHECK (format IN ('plain', 'markdown', 'json', 'xml', 'yaml', 'python', 'groovy',
                   'javascript', 'typescript', 'csharp', 'sql', 'powershell', 'ini', 'css', 'html')),
    checksum   TEXT NOT NULL,
    char_count INTEGER NOT NULL DEFAULT 0,
    preview    TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL,
    fts_dirty  INTEGER NOT NULL DEFAULT 0 CHECK (fts_dirty IN (0, 1))
);

CREATE INDEX IF NOT EXISTS ix_document_contents_fts_dirty ON document_contents (node_id) WHERE fts_dirty = 1;

-- The last text acknowledged by the server: the base of Phase 2's three-way merge. Empty in Phase 1.
CREATE TABLE IF NOT EXISTS document_sync_base (
    node_id            TEXT NOT NULL PRIMARY KEY REFERENCES nodes (id) ON DELETE CASCADE,
    server_content_rev INTEGER NOT NULL,
    text               TEXT NOT NULL,
    checksum           TEXT NOT NULL
);

-- nimpad://note/{id} references found in a document's text. The target may not exist (a dangling link).
CREATE TABLE IF NOT EXISTS document_links (
    source_id TEXT NOT NULL REFERENCES nodes (id) ON DELETE CASCADE,
    target_id TEXT NOT NULL,
    PRIMARY KEY (source_id, target_id)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS ix_document_links_target ON document_links (target_id);

-- Full-text index over node names and document bodies; rowid = nodes.row_id. A regular (content-storing) FTS5
-- table so snippet() and highlight() work. Trashed nodes stay indexed; searches join nodes to filter them out.
CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5 (
    name,
    body,
    tokenize = 'unicode61 remove_diacritics 2'
);

-- Names follow the nodes table automatically; bodies follow document_contents while fts_dirty = 0.
CREATE TRIGGER IF NOT EXISTS tr_nodes_fts_insert AFTER INSERT ON nodes
BEGIN
    INSERT INTO notes_fts (rowid, name, body) VALUES (new.row_id, new.name, '');
END;

CREATE TRIGGER IF NOT EXISTS tr_nodes_fts_rename AFTER UPDATE OF name ON nodes
WHEN new.name IS NOT old.name
BEGIN
    UPDATE notes_fts SET name = new.name WHERE rowid = new.row_id;
END;

CREATE TRIGGER IF NOT EXISTS tr_nodes_fts_delete AFTER DELETE ON nodes
BEGIN
    DELETE FROM notes_fts WHERE rowid = old.row_id;
END;

CREATE TRIGGER IF NOT EXISTS tr_document_contents_fts_insert AFTER INSERT ON document_contents
WHEN new.fts_dirty = 0
BEGIN
    UPDATE notes_fts SET body = new.text
    WHERE rowid = (SELECT row_id FROM nodes WHERE id = new.node_id);
END;

CREATE TRIGGER IF NOT EXISTS tr_document_contents_fts_update AFTER UPDATE OF text, fts_dirty ON document_contents
WHEN new.fts_dirty = 0 AND (new.text IS NOT old.text OR old.fts_dirty = 1)
BEGIN
    UPDATE notes_fts SET body = new.text
    WHERE rowid = (SELECT row_id FROM nodes WHERE id = new.node_id);
END;

-- The durable local -> server queue (architecture.md, "Pending operation queue"). node_id has no foreign key: a
-- purge outlives the node it deletes.
CREATE TABLE IF NOT EXISTS pending_operations (
    seq              INTEGER PRIMARY KEY AUTOINCREMENT,
    op_id            TEXT NOT NULL UNIQUE,
    workspace_id     TEXT NOT NULL,
    node_id          TEXT NOT NULL,
    kind             TEXT NOT NULL CHECK (kind IN ('create', 'update_content', 'rename', 'move', 'pin', 'delete',
                         'restore', 'purge')),
    payload          TEXT NOT NULL CHECK (json_valid(payload)),
    base_meta_rev    INTEGER,
    base_content_rev INTEGER,
    device_id        TEXT NOT NULL,
    actor_kind       TEXT NOT NULL CHECK (actor_kind IN ('user', 'import', 'system')),
    created_at       TEXT NOT NULL,
    attempts         INTEGER NOT NULL DEFAULT 0,
    last_error       TEXT
);

CREATE INDEX IF NOT EXISTS ix_pending_operations_node ON pending_operations (node_id, kind);
-- At most one not-yet-attempted update_content per node (coalescing replaces it in place).
CREATE UNIQUE INDEX IF NOT EXISTS ux_pending_operations_update_content ON pending_operations (node_id)
    WHERE kind = 'update_content' AND attempts = 0;

-- Open tabs of this device's session. A tab shows either a managed note (node_id) or an external file
-- (external_path).
CREATE TABLE IF NOT EXISTS session_tabs (
    tab_id           TEXT NOT NULL PRIMARY KEY,
    node_id          TEXT REFERENCES nodes (id) ON DELETE CASCADE,
    external_path    TEXT,
    tab_order        INTEGER NOT NULL,
    is_pinned        INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
    is_active        INTEGER NOT NULL DEFAULT 0 CHECK (is_active IN (0, 1)),
    caret_offset     INTEGER NOT NULL DEFAULT 0,
    selection_start  INTEGER NOT NULL DEFAULT 0,
    selection_length INTEGER NOT NULL DEFAULT 0,
    scroll_x         REAL NOT NULL DEFAULT 0,
    scroll_y         REAL NOT NULL DEFAULT 0,
    opened_at        TEXT NOT NULL,
    updated_at       TEXT NOT NULL,
    CHECK ((node_id IS NULL) <> (external_path IS NULL))
);

CREATE INDEX IF NOT EXISTS ix_session_tabs_order ON session_tabs (tab_order);
CREATE INDEX IF NOT EXISTS ix_session_tabs_node ON session_tabs (node_id) WHERE node_id IS NOT NULL;

-- The reopen-closed-tab stack, newest last.
CREATE TABLE IF NOT EXISTS recently_closed (
    seq           INTEGER PRIMARY KEY AUTOINCREMENT,
    node_id       TEXT REFERENCES nodes (id) ON DELETE CASCADE,
    external_path TEXT,
    title         TEXT NOT NULL,
    tab_state     TEXT CHECK (tab_state IS NULL OR json_valid(tab_state)),
    closed_at     TEXT NOT NULL,
    CHECK ((node_id IS NULL) <> (external_path IS NULL))
);

CREATE INDEX IF NOT EXISTS ix_recently_closed_node ON recently_closed (node_id) WHERE node_id IS NOT NULL;

-- Per-device UI state (tree expansion, sidebar width/collapsed, sort order, ...) as JSON values.
CREATE TABLE IF NOT EXISTS ui_state (
    key        TEXT NOT NULL PRIMARY KEY,
    value      TEXT NOT NULL CHECK (json_valid(value)),
    updated_at TEXT NOT NULL
);

-- User settings as JSON values.
CREATE TABLE IF NOT EXISTS settings (
    key        TEXT NOT NULL PRIMARY KEY,
    value      TEXT NOT NULL CHECK (json_valid(value)),
    updated_at TEXT NOT NULL
);

-- Unsaved edits of external files, kept for crash recovery. Never synced.
CREATE TABLE IF NOT EXISTS external_file_buffers (
    path             TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
    text             TEXT NOT NULL,
    encoding         TEXT NOT NULL,
    line_ending      TEXT NOT NULL,
    disk_checksum    TEXT,
    disk_modified_at TEXT,
    updated_at       TEXT NOT NULL
);
