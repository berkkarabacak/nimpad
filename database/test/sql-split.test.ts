import { describe, expect, it } from 'vitest';
import { splitSqlStatements } from '../src/sql-split.ts';

describe('splitSqlStatements', () => {
  it('splits at top-level semicolons and drops empty statements', () => {
    expect(splitSqlStatements('SELECT 1;\n\nSELECT 2 ;;  \n')).toEqual(['SELECT 1', 'SELECT 2']);
  });

  it('keeps a final statement without a semicolon', () => {
    expect(splitSqlStatements('SELECT 1; SELECT 2')).toEqual(['SELECT 1', 'SELECT 2']);
  });

  it('ignores semicolons inside string literals and quoted identifiers', () => {
    const sql = `INSERT INTO t VALUES ('a;b', 'it''s; fine'); SELECT "odd;name" FROM "x""y;z"`;
    expect(splitSqlStatements(sql)).toEqual([
      `INSERT INTO t VALUES ('a;b', 'it''s; fine')`,
      `SELECT "odd;name" FROM "x""y;z"`,
    ]);
  });

  it('honors backslash escapes only in E strings', () => {
    expect(splitSqlStatements(String.raw`SELECT E'a\';b'; SELECT 'c\'; SELECT 2`)).toEqual([
      String.raw`SELECT E'a\';b'`,
      String.raw`SELECT 'c\'`,
      'SELECT 2',
    ]);
  });

  it('ignores semicolons inside dollar-quoted bodies, with and without tags', () => {
    const fn = `CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $body$
BEGIN
  PERFORM 1; RETURN $$;$$::text::int;
END
$body$`;
    expect(splitSqlStatements(`${fn};\nDO $$ BEGIN PERFORM 1; END $$;`)).toEqual([
      fn,
      'DO $$ BEGIN PERFORM 1; END $$',
    ]);
  });

  it('does not mistake parameters or identifiers containing $ for dollar quotes', () => {
    expect(splitSqlStatements('SELECT $1; SELECT a$b$c FROM t; SELECT 2')).toEqual([
      'SELECT $1',
      'SELECT a$b$c FROM t',
      'SELECT 2',
    ]);
  });

  it('ignores semicolons in line comments and nested block comments, and drops comment-only chunks', () => {
    const sql = `-- nimpad:no-transaction
-- first; still a comment
CREATE INDEX CONCURRENTLY i1 ON t (a); /* outer; /* inner; */ still; */
CREATE INDEX CONCURRENTLY i2 ON t (b);
-- trailing comment;`;
    expect(splitSqlStatements(sql)).toEqual([
      '-- nimpad:no-transaction\n-- first; still a comment\nCREATE INDEX CONCURRENTLY i1 ON t (a)',
      '/* outer; /* inner; */ still; */\nCREATE INDEX CONCURRENTLY i2 ON t (b)',
    ]);
  });

  it('keeps SQL-standard routine bodies (BEGIN ATOMIC ... END) together, as psql does', () => {
    const procedure = `CREATE PROCEDURE insert_two() LANGUAGE sql
BEGIN ATOMIC
  INSERT INTO t VALUES (1);
  INSERT INTO t VALUES (CASE WHEN true THEN 2 ELSE 3 END);
END`;
    const fn = `create or replace function add_one(x int) returns int language sql
begin atomic; select x + 1; end`;
    expect(splitSqlStatements(`${procedure};\nCALL insert_two();\n${fn};\nSELECT add_one(1)`)).toEqual([
      procedure,
      'CALL insert_two()',
      fn,
      'SELECT add_one(1)',
    ]);
  });

  it('counts BEGIN and END only where psql does', () => {
    // A transaction block is not a routine body: BEGIN and COMMIT are statements of their own.
    expect(splitSqlStatements('BEGIN; SELECT 1; END; SELECT 2')).toEqual([
      'BEGIN',
      'SELECT 1',
      'END',
      'SELECT 2',
    ]);
    // Words inside parentheses, quoted identifiers and dollar-quoted bodies do not count.
    const plpgsql = `CREATE FUNCTION f("begin" int, begin_at int) RETURNS int LANGUAGE plpgsql AS $$
BEGIN RETURN 1; END $$`;
    expect(splitSqlStatements(`${plpgsql}; SELECT 3`)).toEqual([plpgsql, 'SELECT 3']);
    // Other CREATE statements are split normally even when they mention BEGIN-like words.
    expect(splitSqlStatements('CREATE TABLE begin_log (case_id int); SELECT 4')).toEqual([
      'CREATE TABLE begin_log (case_id int)',
      'SELECT 4',
    ]);
  });

  it('does not end a statement at a semicolon inside parentheses', () => {
    expect(splitSqlStatements('SELECT (1; 2); SELECT 5')).toEqual(['SELECT (1; 2)', 'SELECT 5']);
  });

  it('rejects unterminated strings, identifiers, comments and dollar quotes', () => {
    expect(() => splitSqlStatements("SELECT 'abc")).toThrow(/Unterminated string literal/);
    expect(() => splitSqlStatements('SELECT "abc')).toThrow(/Unterminated quoted identifier/);
    expect(() => splitSqlStatements('SELECT 1 /* /* */')).toThrow(/Unterminated block comment/);
    expect(() => splitSqlStatements('DO $x$ BEGIN END $y$')).toThrow(
      /Unterminated dollar-quoted string \$x\$/,
    );
  });
});
