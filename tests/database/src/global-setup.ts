// Runs once per vitest run. Makes sure the Supabase Data API roles exist so every test database can simulate a
// Supabase project, and removes the ones it created afterwards (roles are cluster-wide).
import pg from 'pg';
import { ADMIN_URL, databaseUnavailableReason, SUPABASE_ROLES } from './harness.ts';

export default async function setup(): Promise<() => Promise<void>> {
  const unavailable = await databaseUnavailableReason();
  if (unavailable !== undefined) {
    console.warn(`[database tests] skipping: ${unavailable}`);
    return async () => {};
  }

  const created: string[] = [];
  const client = new pg.Client({ connectionString: ADMIN_URL });
  await client.connect();
  try {
    for (const role of SUPABASE_ROLES) {
      const exists = await client.query('SELECT 1 FROM pg_roles WHERE rolname = $1', [role]);
      if (exists.rowCount !== 0) continue;
      try {
        await client.query(`CREATE ROLE ${pg.escapeIdentifier(role)} NOLOGIN NOINHERIT`);
        created.push(role);
      } catch (error) {
        // Created concurrently by another run: use it, but leave it alone afterwards.
        if (!(error instanceof pg.DatabaseError && (error.code === '42710' || error.code === '23505')))
          throw error;
      }
    }
  } finally {
    await client.end();
  }

  return async () => {
    if (created.length === 0) return;
    const cleanup = new pg.Client({ connectionString: ADMIN_URL });
    await cleanup.connect();
    try {
      for (const role of created) {
        try {
          await cleanup.query(`DROP ROLE IF EXISTS ${pg.escapeIdentifier(role)}`);
        } catch (error) {
          console.warn(`[database tests] could not drop role ${role}: ${(error as Error).message}`);
        }
      }
    } finally {
      await cleanup.end();
    }
  };
}
