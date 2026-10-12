-- NimPad local schema, version 3: find the queued creates and moves that name a node as their parent.

-- Purging a never-synced folder keeps its queued operations when a surviving create or move still names it as the
-- parent (PendingOperationQueue.EnqueuePurge). The queue is never drained in Phase 1, so that lookup must not scan it.
CREATE INDEX IF NOT EXISTS ix_pending_operations_parent ON pending_operations (json_extract(payload, '$.parentId'))
    WHERE kind IN ('create', 'move');
