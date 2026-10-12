// Migration command line.
//   node src/cli.ts            apply pending migrations (DATABASE_URL_MIGRATE)
//   node src/cli.ts --status   list applied and pending migrations; exits 1 if an applied file changed
// When NIMPAD_APP_DB_PASSWORD is set (local development), a successful run also lets nimpad_app log in with it; the
// run refuses to start if DATABASE_URL_MIGRATE is not a database on this machine.
// Exit codes: 0 success, 1 failure or drift, 2 usage or configuration error.
import { parseArgs } from 'node:util';
import {
  describeHost,
  isLocalConnectionString,
  MigrationError,
  migrate,
  migrationStatus,
  setAppRolePassword,
} from './migrate.ts';

const USAGE = `Usage: node src/cli.ts [--status] [--dir <migrations dir>]

Environment:
  DATABASE_URL_MIGRATE     connection string of the schema owner (required). A direct or session-mode
                           connection: a transaction-mode pooler (Supabase port 6543) cannot hold the
                           migration lock.
  NIMPAD_APP_DB_PASSWORD   local development only: password that lets the nimpad_app role log in. Refused
                           unless DATABASE_URL_MIGRATE is on this machine (localhost, 127.x.x.x, ::1, socket).
  NIMPAD_ALLOW_REMOTE_DEV_PASSWORD=1
                           also allow NIMPAD_APP_DB_PASSWORD for a development database on another host`;

function parseCommandLine(): { status: boolean; dir: string | undefined; help: boolean } | undefined {
  // `pnpm db:migrate -- --status` forwards the separator itself.
  const args = process.argv.slice(2).filter((arg, index) => !(index === 0 && arg === '--'));
  try {
    const { values } = parseArgs({
      args,
      options: {
        status: { type: 'boolean', default: false },
        dir: { type: 'string' },
        help: { type: 'boolean', short: 'h', default: false },
      },
      strict: true,
    });
    return { status: values.status, dir: values.dir, help: values.help };
  } catch (error) {
    console.error((error as Error).message);
    return undefined;
  }
}

async function main(): Promise<number> {
  const values = parseCommandLine();
  if (values === undefined) {
    console.error(USAGE);
    return 2;
  }
  if (values.help) {
    console.log(USAGE);
    return 0;
  }
  const connectionString = process.env.DATABASE_URL_MIGRATE;
  if (!connectionString) {
    console.error('DATABASE_URL_MIGRATE is not set (see .env.example).');
    console.error(USAGE);
    return 2;
  }
  const dir = values.dir;
  const appPassword = process.env.NIMPAD_APP_DB_PASSWORD;
  const allowRemote = process.env.NIMPAD_ALLOW_REMOTE_DEV_PASSWORD === '1';

  if (values.status) {
    const status = await migrationStatus({ connectionString, dir });
    for (const row of status.applied) {
      const note = status.changed.includes(row.version)
        ? '  CHANGED ON DISK'
        : status.unknown.includes(row.version)
          ? '  (no file)'
          : '';
      console.log(`applied  ${row.version}  ${row.appliedAt.toISOString()}${note}`);
    }
    for (const version of status.pending) console.log(`pending  ${version}`);
    if (status.applied.length === 0 && status.pending.length === 0) console.log('No migrations found.');
    return status.changed.length > 0 ? 1 : 0;
  }

  // Checked before migrating, so a .env meant for local development cannot half-configure a shared database.
  if (appPassword && !allowRemote && !isLocalConnectionString(connectionString)) {
    console.error(
      `NIMPAD_APP_DB_PASSWORD is only for a local database, but DATABASE_URL_MIGRATE points at ` +
        `${describeHost(connectionString)}. A known nimpad_app password there would let anyone act as any user. ` +
        'Unset NIMPAD_APP_DB_PASSWORD for this run, or set NIMPAD_ALLOW_REMOTE_DEV_PASSWORD=1 if this really is a ' +
        'development database on another host.',
    );
    return 2;
  }

  const result = await migrate({ connectionString, dir, log: (message) => console.log(message) });
  for (const version of result.applied) console.log(`applied  ${version}`);

  if (appPassword) {
    await setAppRolePassword(connectionString, appPassword, { allowRemote });
    console.log(
      'nimpad_app can now log in with the password from NIMPAD_APP_DB_PASSWORD (development only).',
    );
  }
  return 0;
}

try {
  process.exitCode = await main();
} catch (error) {
  if (error instanceof MigrationError) {
    console.error(`Migration failed: ${error.message}`);
  } else {
    console.error(error);
  }
  process.exitCode = 1;
}
