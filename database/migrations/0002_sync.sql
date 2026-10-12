-- 0002_sync: devices, per-device sessions, the per-workspace sync event log and idempotency records.
-- Same conventions as 0001_core (row-level security through nimpad.current_user_id(), minimal grants to
-- nimpad_app, nothing for Supabase's anon/authenticated).

-- An installation of the desktop app signed in as a user. Revoked devices stay revoked.
CREATE TABLE nimpad.devices (
  id uuid PRIMARY KEY DEFAULT nimpad.uuid_generate_v7(),
  user_id uuid NOT NULL REFERENCES nimpad.users (id) ON DELETE CASCADE,
  name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 255 AND name !~ '[\x01-\x1f\x7f]'),
  platform text NOT NULL CHECK (platform ~ '^[a-z][a-z0-9_-]{0,31}$'),
  app_version text NOT NULL CHECK (char_length(app_version) BETWEEN 1 AND 64),
  created_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz,
  -- Target of device_sessions' composite key, which keeps a session's user equal to its device's user.
  CONSTRAINT devices_id_user_id_key UNIQUE (id, user_id)
);

-- The open tabs and layout of one device. `state` is device-specific (caret, scroll, window layout);
-- `transferable` is the part a newly connected device may start from.
CREATE TABLE nimpad.device_sessions (
  device_id uuid PRIMARY KEY,
  user_id uuid NOT NULL,
  workspace_id uuid REFERENCES nimpad.workspaces (id) ON DELETE SET NULL,
  state jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(state) = 'object'),
  transferable jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(transferable) = 'object'),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT device_sessions_device_fkey FOREIGN KEY (device_id, user_id)
    REFERENCES nimpad.devices (id, user_id) ON DELETE CASCADE
);

-- Committed changes per workspace, paged by clients with a (workspace_id, seq) cursor. Rows are written only by
-- nimpad.record_sync_event and never updated.
CREATE TABLE nimpad.sync_events (
  workspace_id uuid NOT NULL REFERENCES nimpad.workspaces (id) ON DELETE CASCADE,
  seq bigint NOT NULL CHECK (seq >= 1),
  -- The client operation (pending_operations.op_id) that caused the event, when there is one.
  op_id uuid,
  event_type text NOT NULL CHECK (event_type ~ '^[a-z][a-z0-9_.]{0,63}$'),
  node_id uuid,
  -- The device whose push caused the event (one of the actor's own devices), so that device can recognise the
  -- event as its own echo when it pulls. No foreign key: events outlive devices and are never updated.
  device_id uuid,
  actor_kind text NOT NULL CHECK (actor_kind IN ('user', 'agent', 'system', 'import')),
  -- 'user' and 'import': the user who made the change; 'agent': the agent; 'system': usually none.
  actor_id uuid,
  payload jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(payload) = 'object'),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (workspace_id, seq)
);

-- The stored outcome of a mutation, keyed by the client's op_id, so a retried request returns the original
-- response instead of applying twice. request_hash detects an op_id reused for a different request.
CREATE TABLE nimpad.idempotency_records (
  user_id uuid NOT NULL REFERENCES nimpad.users (id) ON DELETE CASCADE,
  op_id uuid NOT NULL,
  request_hash text NOT NULL CHECK (char_length(request_hash) BETWEEN 1 AND 200),
  status_code integer NOT NULL CHECK (status_code BETWEEN 100 AND 599),
  response jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL DEFAULT now() + interval '7 days',
  PRIMARY KEY (user_id, op_id),
  CONSTRAINT idempotency_records_expiry_after_creation CHECK (expires_at > created_at)
);

CREATE INDEX devices_user_idx ON nimpad.devices (user_id);
CREATE INDEX device_sessions_user_idx ON nimpad.device_sessions (user_id, updated_at DESC);
CREATE INDEX idempotency_records_expires_idx ON nimpad.idempotency_records (expires_at);

-- ---------------------------------------------------------------------------------------------------------
-- Functions and triggers
-- ---------------------------------------------------------------------------------------------------------

-- Appends an event to the workspace's log and returns its sequence number.
--
-- The increment of workspaces.event_seq row-locks the workspace until the caller's transaction ends, so
-- concurrent writers queue up and each gets the next number only after the previous holder committed (or rolled
-- back, which undoes its increment). Sequence numbers are therefore gap-free and in commit order: a client that
-- has read up to seq N never misses an event that later commits with a number <= N. Node moves take the same
-- workspace row lock (nimpad.nodes_check_tree), so a transaction may record events and move nodes in any order:
-- both queue behind one lock instead of two locks taken in opposite orders. Recording late keeps the lock short.
--
-- Attribution: for actor_kind 'user' (the default) and 'import', actor_id is the calling user and may be omitted;
-- naming anyone else is refused. 'agent' requires the agent's id. 'system' records actor_id as given (usually
-- none). device_id, when given, must be a device of the calling user that is not revoked.
--
-- SECURITY DEFINER because nimpad_app may neither update event_seq nor insert into sync_events directly; the
-- caller must be an editor or owner of the workspace.
CREATE FUNCTION nimpad.record_sync_event(
  workspace_id uuid,
  event_type text,
  node_id uuid DEFAULT NULL,
  payload jsonb DEFAULT '{}',
  op_id uuid DEFAULT NULL,
  actor_kind text DEFAULT 'user',
  actor_id uuid DEFAULT NULL,
  device_id uuid DEFAULT NULL
) RETURNS bigint
  LANGUAGE plpgsql VOLATILE SECURITY DEFINER
  SET search_path = ''
  AS $$
DECLARE
  caller uuid := nimpad.current_user_id();
  actor uuid := record_sync_event.actor_id;
  next_seq bigint;
BEGIN
  IF NOT nimpad.is_member(record_sync_event.workspace_id, 'editor') THEN
    RAISE EXCEPTION 'not allowed to record sync events in workspace %', record_sync_event.workspace_id
      USING ERRCODE = 'insufficient_privilege';
  END IF;

  IF record_sync_event.actor_kind IN ('user', 'import') THEN
    IF actor IS NOT NULL AND actor <> caller THEN
      RAISE EXCEPTION 'an event of kind % must be attributed to the calling user', record_sync_event.actor_kind
        USING ERRCODE = 'insufficient_privilege';
    END IF;
    actor := caller;
  ELSIF record_sync_event.actor_kind = 'agent' AND actor IS NULL THEN
    RAISE EXCEPTION 'an agent event must name the agent (actor_id)' USING ERRCODE = 'invalid_parameter_value';
  END IF;

  IF record_sync_event.device_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM nimpad.devices d
    WHERE d.id = record_sync_event.device_id AND d.user_id = caller AND d.revoked_at IS NULL
  ) THEN
    RAISE EXCEPTION 'device % is not an active device of the calling user', record_sync_event.device_id
      USING ERRCODE = 'insufficient_privilege';
  END IF;

  UPDATE nimpad.workspaces w
  SET event_seq = w.event_seq + 1
  WHERE w.id = record_sync_event.workspace_id
  RETURNING w.event_seq INTO next_seq;

  INSERT INTO nimpad.sync_events
    (workspace_id, seq, op_id, event_type, node_id, device_id, actor_kind, actor_id, payload)
  VALUES (
    record_sync_event.workspace_id,
    next_seq,
    record_sync_event.op_id,
    record_sync_event.event_type,
    record_sync_event.node_id,
    record_sync_event.device_id,
    record_sync_event.actor_kind,
    actor,
    COALESCE(record_sync_event.payload, '{}')
  );
  RETURN next_seq;
END
$$;

-- Deletes up to batch_size expired idempotency records (oldest first) and returns how many were deleted. Run
-- repeatedly by the worker until it returns less than batch_size.
CREATE FUNCTION nimpad.purge_expired_idempotency(batch_size integer DEFAULT 10000) RETURNS integer
  LANGUAGE plpgsql VOLATILE SECURITY DEFINER
  SET search_path = ''
  AS $$
DECLARE
  deleted integer;
BEGIN
  IF batch_size IS NULL OR batch_size < 1 THEN
    RAISE EXCEPTION 'batch_size must be positive' USING ERRCODE = 'invalid_parameter_value';
  END IF;
  DELETE FROM nimpad.idempotency_records r
  USING (
    SELECT e.user_id, e.op_id
    FROM nimpad.idempotency_records e
    WHERE e.expires_at <= pg_catalog.now()
    ORDER BY e.expires_at
    LIMIT batch_size
    FOR UPDATE SKIP LOCKED
  ) expired
  WHERE r.user_id = expired.user_id AND r.op_id = expired.op_id;
  GET DIAGNOSTICS deleted = ROW_COUNT;
  RETURN deleted;
END
$$;

-- A member who leaves or is removed can no longer read the workspace, so their device sessions stop pointing at it
-- (a hard-deleted workspace is handled by the ON DELETE SET NULL foreign key). Otherwise every later save of such
-- a session would fail the membership check of device_sessions_update_own. SECURITY DEFINER: the owner removing a
-- member cannot see that member's sessions.
CREATE FUNCTION nimpad.workspace_members_release_sessions() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER
  SET search_path = ''
  AS $$
BEGIN
  UPDATE nimpad.device_sessions s
  SET workspace_id = NULL
  WHERE s.user_id = OLD.user_id AND s.workspace_id = OLD.workspace_id;
  RETURN NULL;
END
$$;

CREATE FUNCTION nimpad.devices_keep_revoked() RETURNS trigger
  LANGUAGE plpgsql
  SET search_path = ''
  AS $$
BEGIN
  IF OLD.revoked_at IS NOT NULL AND NEW.revoked_at IS DISTINCT FROM OLD.revoked_at THEN
    RAISE EXCEPTION 'device % is revoked', OLD.id
      USING ERRCODE = 'check_violation', CONSTRAINT = 'devices_revocation_final', SCHEMA = 'nimpad',
            TABLE = 'devices';
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER workspace_members_release_sessions AFTER DELETE ON nimpad.workspace_members
  FOR EACH ROW EXECUTE FUNCTION nimpad.workspace_members_release_sessions();
CREATE TRIGGER devices_keep_revoked BEFORE UPDATE OF revoked_at ON nimpad.devices
  FOR EACH ROW EXECUTE FUNCTION nimpad.devices_keep_revoked();
CREATE TRIGGER device_sessions_set_updated_at BEFORE UPDATE ON nimpad.device_sessions
  FOR EACH ROW EXECUTE FUNCTION nimpad.set_updated_at();
CREATE TRIGGER sync_events_immutable BEFORE UPDATE ON nimpad.sync_events
  FOR EACH ROW EXECUTE FUNCTION nimpad.reject_change();

-- ---------------------------------------------------------------------------------------------------------
-- Row-level security
-- ---------------------------------------------------------------------------------------------------------

ALTER TABLE nimpad.devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE nimpad.devices FORCE ROW LEVEL SECURITY;
ALTER TABLE nimpad.device_sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE nimpad.device_sessions FORCE ROW LEVEL SECURITY;
ALTER TABLE nimpad.sync_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE nimpad.sync_events FORCE ROW LEVEL SECURITY;
ALTER TABLE nimpad.idempotency_records ENABLE ROW LEVEL SECURITY;
ALTER TABLE nimpad.idempotency_records FORCE ROW LEVEL SECURITY;

CREATE POLICY devices_select_own ON nimpad.devices FOR SELECT TO nimpad_app
  USING (user_id = (SELECT nimpad.current_user_id()));
CREATE POLICY devices_insert_own ON nimpad.devices FOR INSERT TO nimpad_app
  WITH CHECK (user_id = (SELECT nimpad.current_user_id()));
CREATE POLICY devices_update_own ON nimpad.devices FOR UPDATE TO nimpad_app
  USING (user_id = (SELECT nimpad.current_user_id()))
  WITH CHECK (user_id = (SELECT nimpad.current_user_id()));

-- A session may point only at a workspace the user can read (removal from a workspace clears the pointer, see
-- workspace_members_release_sessions).
CREATE POLICY device_sessions_select_own ON nimpad.device_sessions FOR SELECT TO nimpad_app
  USING (user_id = (SELECT nimpad.current_user_id()));
CREATE POLICY device_sessions_insert_own ON nimpad.device_sessions FOR INSERT TO nimpad_app
  WITH CHECK (
    user_id = (SELECT nimpad.current_user_id())
    AND (workspace_id IS NULL OR workspace_id IN (SELECT nimpad.member_workspace_ids('viewer')))
  );
CREATE POLICY device_sessions_update_own ON nimpad.device_sessions FOR UPDATE TO nimpad_app
  USING (user_id = (SELECT nimpad.current_user_id()))
  WITH CHECK (
    user_id = (SELECT nimpad.current_user_id())
    AND (workspace_id IS NULL OR workspace_id IN (SELECT nimpad.member_workspace_ids('viewer')))
  );
CREATE POLICY device_sessions_delete_own ON nimpad.device_sessions FOR DELETE TO nimpad_app
  USING (user_id = (SELECT nimpad.current_user_id()));

CREATE POLICY sync_events_select ON nimpad.sync_events FOR SELECT TO nimpad_app
  USING (workspace_id IN (SELECT nimpad.member_workspace_ids('viewer')));

CREATE POLICY idempotency_records_select_own ON nimpad.idempotency_records FOR SELECT TO nimpad_app
  USING (user_id = (SELECT nimpad.current_user_id()));
CREATE POLICY idempotency_records_insert_own ON nimpad.idempotency_records FOR INSERT TO nimpad_app
  WITH CHECK (user_id = (SELECT nimpad.current_user_id()));
CREATE POLICY idempotency_records_update_own ON nimpad.idempotency_records FOR UPDATE TO nimpad_app
  USING (user_id = (SELECT nimpad.current_user_id()))
  WITH CHECK (user_id = (SELECT nimpad.current_user_id()));

-- ---------------------------------------------------------------------------------------------------------
-- Privileges
-- ---------------------------------------------------------------------------------------------------------

GRANT SELECT, INSERT ON nimpad.devices TO nimpad_app;
GRANT UPDATE (name, app_version, last_seen_at, revoked_at) ON nimpad.devices TO nimpad_app;

GRANT SELECT, INSERT, DELETE ON nimpad.device_sessions TO nimpad_app;
GRANT UPDATE (workspace_id, state, transferable) ON nimpad.device_sessions TO nimpad_app;

-- Written only through nimpad.record_sync_event.
GRANT SELECT ON nimpad.sync_events TO nimpad_app;

GRANT SELECT, INSERT ON nimpad.idempotency_records TO nimpad_app;
GRANT UPDATE (status_code, response, expires_at) ON nimpad.idempotency_records TO nimpad_app;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA nimpad FROM PUBLIC;
GRANT EXECUTE ON FUNCTION
  nimpad.record_sync_event(uuid, text, uuid, jsonb, uuid, text, uuid, uuid),
  nimpad.purge_expired_idempotency(integer)
TO nimpad_app;

DO $$
DECLARE
  r text;
BEGIN
  FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = r) THEN
      EXECUTE format('REVOKE ALL ON SCHEMA nimpad FROM %I', r);
      EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA nimpad FROM %I', r);
      EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA nimpad FROM %I', r);
      EXECUTE format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA nimpad FROM %I', r);
    END IF;
  END LOOP;
END
$$;
