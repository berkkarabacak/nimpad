/**
 * Splits a PostgreSQL script into individual statements at top-level semicolons.
 *
 * Only `-- nimpad:no-transaction` migrations need this: PostgreSQL runs a multi-statement simple query as one
 * implicit transaction, so statements such as `CREATE INDEX CONCURRENTLY` must be sent one at a time.
 *
 * Understands everything that can hide a semicolon: line comments, nested block comments, single-quoted strings
 * (including `E'...'` escape strings), quoted identifiers, dollar-quoted bodies (`$$...$$`, `$tag$...$tag$`),
 * parentheses, and SQL-standard routine bodies (`CREATE [OR REPLACE] FUNCTION|PROCEDURE ... BEGIN ATOMIC ...; ...;
 * END`). For the last two it follows psql's own heuristic: a semicolon ends a statement only outside parentheses,
 * and, in a statement that starts with CREATE [OR REPLACE] FUNCTION or PROCEDURE, only outside BEGIN ... END
 * (counting CASE ... END inside it). Statements that contain nothing but whitespace and comments are dropped.
 * Comments are kept with the statement they precede, so error positions stay meaningful.
 */
export function splitSqlStatements(sql: string): string[] {
  const statements: string[] = [];
  let start = 0;
  let hasContent = false;
  let i = 0;
  // State of the current statement for psql's heuristic.
  let leadingWords: string[] = [];
  let parenDepth = 0;
  let beginDepth = 0;

  const flush = (end: number): void => {
    if (hasContent) statements.push(sql.slice(start, end).trim());
    start = end + 1;
    hasContent = false;
    leadingWords = [];
    parenDepth = 0;
    beginDepth = 0;
  };

  const trackWord = (word: string): void => {
    const lower = word.toLowerCase();
    if (leadingWords.length < 4) leadingWords.push(lower);
    if (parenDepth > 0 || !isCreateRoutine(leadingWords)) return;
    if (lower === 'begin') {
      beginDepth += 1;
    } else if (lower === 'case') {
      // CASE also ends with END; it only matters inside a BEGIN block.
      if (beginDepth > 0) beginDepth += 1;
    } else if (lower === 'end' && beginDepth > 0) {
      beginDepth -= 1;
    }
  };

  while (i < sql.length) {
    const c = sql[i]!;
    const next = sql[i + 1];

    if (c === '-' && next === '-') {
      const newline = sql.indexOf('\n', i + 2);
      i = newline === -1 ? sql.length : newline + 1;
      continue;
    }
    if (c === '/' && next === '*') {
      i = skipBlockComment(sql, i);
      continue;
    }
    if (c === ';' && parenDepth === 0 && beginDepth === 0) {
      flush(i);
      i += 1;
      continue;
    }
    if (/\s/.test(c)) {
      i += 1;
      continue;
    }

    hasContent = true;
    if (c === "'") {
      i = skipQuoted(sql, i, "'", isEscapeStringStart(sql, i));
    } else if (c === '"') {
      i = skipQuoted(sql, i, '"', false);
    } else if (c === '$' && !isIdentifierChar(sql[i - 1])) {
      i = skipDollarQuoted(sql, i);
    } else if (isIdentifierStart(c)) {
      let end = i + 1;
      while (end < sql.length && isIdentifierChar(sql[end])) end += 1;
      trackWord(sql.slice(i, end));
      i = end;
    } else {
      if (c === '(') parenDepth += 1;
      else if (c === ')' && parenDepth > 0) parenDepth -= 1;
      i += 1;
    }
  }
  flush(sql.length);
  return statements;
}

/** CREATE [OR REPLACE] FUNCTION|PROCEDURE, judged by the statement's first words. */
function isCreateRoutine(words: string[]): boolean {
  const routine = (word: string | undefined) => word === 'function' || word === 'procedure';
  return (
    words[0] === 'create' &&
    (routine(words[1]) || (words[1] === 'or' && words[2] === 'replace' && routine(words[3])))
  );
}

function isIdentifierStart(c: string): boolean {
  return /[\p{L}_]/u.test(c);
}

function isIdentifierChar(c: string | undefined): boolean {
  return c !== undefined && /[\p{L}\p{N}_$]/u.test(c);
}

/** True when the quote at `quoteIndex` opens an `E'...'` string, where backslash escapes apply. */
function isEscapeStringStart(sql: string, quoteIndex: number): boolean {
  const prefix = sql[quoteIndex - 1];
  return (prefix === 'E' || prefix === 'e') && !isIdentifierChar(sql[quoteIndex - 2]);
}

/** Returns the index just past the closing quote. A doubled quote is an escaped quote. */
function skipQuoted(sql: string, open: number, quote: string, backslashEscapes: boolean): number {
  let i = open + 1;
  while (i < sql.length) {
    const c = sql[i];
    if (backslashEscapes && c === '\\') {
      i += 2;
    } else if (c === quote) {
      if (sql[i + 1] === quote) {
        i += 2;
      } else {
        return i + 1;
      }
    } else {
      i += 1;
    }
  }
  throw new Error(`Unterminated ${quote === '"' ? 'quoted identifier' : 'string literal'} at offset ${open}`);
}

function skipBlockComment(sql: string, open: number): number {
  let depth = 1;
  let i = open + 2;
  while (i < sql.length) {
    if (sql[i] === '/' && sql[i + 1] === '*') {
      depth += 1;
      i += 2;
    } else if (sql[i] === '*' && sql[i + 1] === '/') {
      depth -= 1;
      i += 2;
      if (depth === 0) return i;
    } else {
      i += 1;
    }
  }
  throw new Error(`Unterminated block comment at offset ${open}`);
}

const DOLLAR_TAG = /\$(?:[\p{L}_][\p{L}\p{N}_]*)?\$/uy;

/** Skips a dollar-quoted string if one starts at `open`; otherwise (e.g. a `$1` parameter) skips the `$`. */
function skipDollarQuoted(sql: string, open: number): number {
  DOLLAR_TAG.lastIndex = open;
  const match = DOLLAR_TAG.exec(sql);
  if (!match) return open + 1;
  const tag = match[0];
  const close = sql.indexOf(tag, open + tag.length);
  if (close === -1) throw new Error(`Unterminated dollar-quoted string ${tag} at offset ${open}`);
  return close + tag.length;
}
