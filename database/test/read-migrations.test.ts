import { createHash } from 'node:crypto';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  isLocalConnectionString,
  MigrationError,
  migrationChecksum,
  readMigrations,
} from '../src/migrate.ts';

describe('migrationChecksum', () => {
  it('is sha256: plus the lowercase hex digest of the UTF-8 text', () => {
    const sql = 'SELECT 1; -- é\n';
    const expected = createHash('sha256').update(Buffer.from(sql, 'utf8')).digest('hex');
    expect(migrationChecksum(sql)).toBe(`sha256:${expected}`);
  });

  it('ignores CRLF versus LF and a byte-order mark', () => {
    expect(migrationChecksum('﻿SELECT 1;\r\nSELECT 2;\r\n')).toBe(migrationChecksum('SELECT 1;\nSELECT 2;\n'));
  });

  it('changes when the content changes', () => {
    expect(migrationChecksum('SELECT 1;')).not.toBe(migrationChecksum('SELECT 1; '));
  });
});

describe('readMigrations', () => {
  let dir: string;

  beforeEach(async () => {
    dir = await mkdtemp(path.join(tmpdir(), 'nimpad-migrations-'));
  });

  afterEach(async () => {
    await rm(dir, { recursive: true, force: true });
  });

  it('reads the shipped migrations in order', async () => {
    const files = await readMigrations();
    expect(files.map((file) => file.version)).toEqual(['0001_core', '0002_sync']);
    for (const file of files) {
      expect(file.transactional).toBe(true);
      expect(file.checksum).toMatch(/^sha256:[0-9a-f]{64}$/);
    }
  });

  it('sorts by file name, ignores other files and detects the no-transaction marker', async () => {
    await writeFile(
      path.join(dir, '0002_second.sql'),
      '-- nimpad:no-transaction\r\nCREATE INDEX CONCURRENTLY i ON t (a);',
    );
    await writeFile(path.join(dir, '0001_first.sql'), 'CREATE TABLE t (a int);\n-- nimpad:no-transaction\n');
    await writeFile(path.join(dir, 'README.md'), 'not a migration');
    const files = await readMigrations(dir);
    expect(files.map((file) => [file.version, file.transactional])).toEqual([
      ['0001_first', true],
      ['0002_second', false],
    ]);
  });

  it('drops a leading byte-order mark from the SQL but not from anything else', async () => {
    await writeFile(path.join(dir, '0001_bom.sql'), '\ufeff-- nimpad:no-transaction\r\nSELECT 1;\r\n');
    await writeFile(path.join(dir, '0002_plain.sql'), "SELECT '\ufeff';\n");
    const [bom, plain] = await readMigrations(dir);
    expect(bom!.sql).toBe('-- nimpad:no-transaction\r\nSELECT 1;\r\n');
    expect(bom!.transactional).toBe(false);
    expect(bom!.checksum).toBe(migrationChecksum('-- nimpad:no-transaction\nSELECT 1;\n'));
    expect(plain!.sql).toBe("SELECT '\ufeff';\n");
  });

  it('rejects .sql files that do not follow NNNN_name.sql', async () => {
    await writeFile(path.join(dir, '1_core.sql'), 'SELECT 1;');
    await expect(readMigrations(dir)).rejects.toThrow(MigrationError);
    await expect(readMigrations(dir)).rejects.toThrow(/Invalid migration file name "1_core.sql"/);
  });

  it('rejects two files with the same number', async () => {
    await writeFile(path.join(dir, '0001_a.sql'), 'SELECT 1;');
    await writeFile(path.join(dir, '0001_b.sql'), 'SELECT 2;');
    await expect(readMigrations(dir)).rejects.toThrow(/share the number 0001/);
  });
});

describe('isLocalConnectionString', () => {
  it.each([
    'postgres://nimpad:nimpad@127.0.0.1:5432/nimpad',
    'postgresql://nimpad@localhost/nimpad',
    'postgres://nimpad@LOCALHOST:5433/nimpad',
    'postgres://nimpad@127.1.2.3/nimpad',
    'postgres://nimpad@[::1]:5432/nimpad',
    'postgres://nimpad@/nimpad?host=/var/run/postgresql',
    'postgres://nimpad@db.example.com/nimpad?host=/tmp',
  ])('treats %s as this machine', (url) => {
    expect(isLocalConnectionString(url)).toBe(true);
  });

  it.each([
    'postgres://postgres:secret@db.abcdefgh.supabase.co:5432/postgres',
    'postgres://postgres@aws-0-eu-central-1.pooler.supabase.com:6543/postgres',
    'postgres://nimpad@localhost.example.com/nimpad',
    'postgres://nimpad@127.0.0.1.example.com/nimpad',
    'postgres://nimpad@10.0.0.5/nimpad',
    'postgres://nimpad@db/nimpad',
    'postgres://nimpad@localhost/nimpad?host=db.example.com',
    'postgres:///nimpad',
    'mysql://root@localhost/nimpad',
    'not a url',
  ])('treats %s as remote', (url) => {
    expect(isLocalConnectionString(url)).toBe(false);
  });
});
