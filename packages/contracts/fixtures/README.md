# Shared contract fixtures

These JSON files are the executable form of the "Identity, time and content contracts" and "Pending operation
queue" sections of [docs/architecture.md](../../../docs/architecture.md). Every file is UTF-8 without a BOM, uses LF
line endings, and has a top-level `description` string that consumers ignore.

Every implementation of a contract must pass its fixture: the TypeScript one (`packages/contracts`, used by the
API, MCP server and worker) and the desktop's C# one (`apps/windows/src/NimPad.Core`). The TypeScript tests
(`packages/contracts/test`) read every file. The C# tests (`apps/windows/tests/NimPad.Core.Tests`, which copy
`*.json` from here next to the test assembly) currently read only the files marked "yes" below; the others still
need a C# test.

| File | Produced by | Contract | Read by the C# tests |
|---|---|---|---|
| `fractional-index.json` | generated | `generateKeyBetween` / `generateNKeysBetween` | yes |
| `checksum.json` | generated | `canonicalizeText` / `contentChecksum` | yes |
| `note-text.json` | generated | `isStorableText` / `toStorableText` / checksum of unstorable text | not yet |
| `node-names.json` | generated | `NodeName` / `normalizeNodeName` (C#: `NodeNames.Normalize`) | not yet |
| `formats.json` | generated | `detectFormat` (C#: `DocumentFormats.FromName`) | not yet |
| `note-links.json` | generated | `findNoteLinks` (C#: `LinkParser.FindLinks`) | not yet |
| `pending-operations.json` | hand-written | `PendingOperation` / `PushOperation` shapes and validation | not yet |

Generated files are rebuilt by `pnpm --filter @nimpad/contracts fixtures` from the inputs in
`scripts/fixture-builders.ts`; never edit them by hand. A test fails if a committed generated file differs from
what the implementation produces. After regenerating, run `pnpm format` (Biome) and review the diff: a changed
expected value is a breaking contract change for every client and for data already stored.

Invisible or easily confused characters (white space other than the ASCII space, DEL, C1 controls, zero-width
and bidi characters, U+FEFF, U+FFFD) are written as `\u` escapes so that no editor can strip them.

**Strings that are not well-formed UTF-16.** A string containing an unpaired surrogate cannot be a JSON string
for strict readers (System.Text.Json throws when decoding an escaped unpaired surrogate). Where a fixture needs
one, the case has a `utf16` array of UTF-16 code units (numbers) instead of its string field; build the string
from those units. Every other string is an ordinary JSON string.

## `fractional-index.json`

```jsonc
{
  "description": "…",
  "digits": "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz",
  "between": [{ "a": "a0", "b": null, "expected": "a1" }],          // generateKeyBetween(a, b) === expected
  "nKeys":   [{ "a": null, "b": "a0", "n": 5, "expected": ["Zv", "Zw", "Zx", "Zy", "Zz"] }],
                                                                     // generateNKeysBetween(a, b, n) === expected
  "invalid": [{ "a": "a1", "b": "a0", "error": true, "message": "a1 >= a0" }]
                                                                     // generateKeyBetween(a, b) throws
}
```

- `a` and `b` are a key or `null` (`null` means "start" for `a` and "end" for `b`).
- `invalid[].message` is the message thrown by the reference implementation (rocicorp/fractional-indexing 3.2.0).
  A port must throw for every invalid case; it should keep the message text, except that where JavaScript prints
  `undefined` for a missing head character (the empty key) a port may say so in its own words.
- Porting notes, both exercised by the cases:
  - The middle digit is `Math.round(0.5 * (digitA + digitB))`, which rounds halves **up**. In C# use
    `(int)Math.Round(x, MidpointRounding.AwayFromZero)` (or `(digitA + digitB + 1) / 2`); the default banker's
    rounding produces different keys (for example between `a0` and `a05`).
  - Every comparison (`a >= b`, `i < b`, `ib < b`) is ordinal on UTF-16 code units: `string.CompareOrdinal` in
    C#, never culture-aware comparison.
- Siblings sort by `(position, id)`, both compared ordinally.

## `checksum.json`

```jsonc
{
  "description": "…",
  "cases": [{ "name": "trailing CRLF", "input": "hello\r\n", "canonical": "hello\n", "checksum": "sha256:5891…" }]
}
```

For every case: `canonicalizeText(input) === canonical`, `contentChecksum(input) === checksum`, and
`contentChecksum(canonical) === checksum`. The checksum is `sha256:` followed by the lowercase hex SHA-256 of the
canonical text's UTF-8 bytes. Canonicalization changes only line endings: CRLF and lone CR become LF. It does not
apply Unicode normalization, strip a leading U+FEFF, trim whitespace, or treat U+2028/U+0085 as line breaks.
The checksum is defined for every string, including text that is not storable (see `note-text.json`): U+0000 is
hashed as the byte 0x00 (one case here), and an unpaired surrogate as U+FFFD, the bytes every UTF-8 encoder writes
for it (cases in `note-text.json`, which can carry such strings).

## `note-text.json`

```jsonc
{
  "description": "…",
  "cases": [
    { "description": "U+0000 inside", "text": "a\u0000b", "storable": false, "storableText": "a\ufffdb",
      "checksum": "sha256:…" },
    { "description": "unpaired high surrogate", "utf16": [120, 55296, 121], "storable": false,
      "storableText": "x\ufffdy", "checksum": "sha256:…" }
  ]
}
```

For every case, with the input built from `text` or `utf16`: `isStorableText(input) === storable`,
`toStorableText(input) === storableText`, and `contentChecksum(input) === checksum`.

**Storable text** is text PostgreSQL `text` keeps exactly: well-formed UTF-16 (no unpaired surrogate) and no
U+0000. PostgreSQL rejects U+0000, and every UTF-8 encoder (Node, .NET, pg) silently turns an unpaired surrogate
into U+FFFD, so the server would store a different string under the same checksum. Managed note text and node
names must be storable: `DocumentContent` and `NodeName` reject anything else, and so does the server. A client
that receives such text from a paste or an import applies `toStorableText` (each U+0000 and each unpaired surrogate
becomes U+FFFD, nothing else changes) before saving, and computes the checksum on the result. Canonicalization
(line endings) and storability are independent; the checksum function is unchanged.

## `node-names.json`

```jsonc
{
  "description": "…",
  "whiteSpace": ["\t", "\n", "…"],           // the 25 white-space characters, in code-point order
  "cases": [
    { "description": "leading space is trimmed", "text": " leading", "valid": false, "normalized": "leading" },
    { "description": "U+FEFF alone is not white space", "text": "\ufeff", "valid": true, "normalized": "\ufeff" },
    { "description": "unpaired low surrogate alone", "utf16": [56320], "valid": false, "normalized": null }
  ]
}
```

- `whiteSpace` is the white-space set used by every name and format rule: Unicode White_Space, which is exactly
  .NET's `char.IsWhiteSpace` (so `string.Trim()`). It is **not** JavaScript's `String.prototype.trim` set, which
  also strips U+FEFF and keeps U+0085.
- `valid`: `NodeName` accepts the string as it is. That is the stored and wire form: 1–255 UTF-16 code units, no
  C0 control character or DEL, well-formed UTF-16, not blank, and neither starting nor ending with white space.
  U+0085 and other C1 controls are allowed inside a name.
- `normalized`: `normalizeNodeName(input)` (C#: `NodeNames.Normalize`, or null where it throws): white space trimmed
  from both ends, then validated as above; null when the trimmed name is still invalid. `valid` is true exactly
  when `normalized` equals the input.

## `formats.json`

```jsonc
{
  "description": "…",
  "formats": ["plain", "markdown", "…"],        // every supported format
  "cases": [{ "name": "config.json", "format": "json" }, { "name": "Jenkinsfile", "format": "groovy" }]
}
```

`detectFormat(name) === format` for every case. The rule: trim white space (as above); an empty name is `plain`; a
well-known file name (`Jenkinsfile`) gives its format; otherwise the extension is everything from the last `.`,
which must not be the last character (`.editorconfig` is all extension), and a known extension gives its format;
anything else is `plain`. Names and extensions compare case-insensitively **for ASCII letters only**: `ſ` (U+017F)
does not match `s`, `ı` (U+0131) does not match `i`. The cases include every known extension in lower and upper
case.

## `note-links.json`

```jsonc
{
  "description": "…",
  "prefix": "nimpad://note/",
  "cases": [
    { "description": "Markdown link", "text": "[Plan](nimpad://note/0192f0c4-…)",
      "links": [{ "start": 7, "length": 50, "target": "0192f0c4-…" }] }
  ]
}
```

`findNoteLinks(text)` returns exactly `links`, in order. `start` and `length` count UTF-16 code units; `target` is
the lowercase UUID. A link is `nimpad://note/` (ASCII letters in any case) directly followed by a hyphenated
8-4-4-4-12 UUID whose hex digits may be in any case, and nothing else: no braces, no `+` or `0x` inside a group
(.NET's `Guid.TryParseExact(…, "D")` accepts both, so it must not be used alone). The character before the prefix,
if any, must not be a letter or decimal digit (Unicode categories L* and Nd, judged per UTF-16 code unit, so a
surrogate half is neither), and the character after the UUID, if any, must not be a letter, digit, `-` or `_`.
After a link, scanning resumes at its end; after a rejected candidate, one character after the candidate's start.

## `pending-operations.json`

```jsonc
{
  "description": "…",
  "valid":       [{ "description": "rename", "operation": { /* a PendingOperation that is also a PushOperation */ } }],
  "invalid":     [{ "description": "rename to a blank name", "operation": { /* rejected */ } }],
  "needsRebase": [{ "description": "…", "operation": { /* a valid PendingOperation that is not a PushOperation */ } }]
}
```

- Every `valid[].operation` must be accepted by both `PendingOperation` and `PushOperation`, and serializing the
  parsed value must give back the same JSON object (same keys and values; key order is not significant). There is
  at least one valid example for each `kind`.
- Every `invalid[].operation` must be rejected. `description` says why.
- Every `needsRebase[].operation` is a valid queue entry (`PendingOperation` accepts it) that must not be pushed
  as it is (`PushOperation` rejects it): its base revisions must be rebased first (see below).

Operation fields are camelCase. `kind` is one of `create`, `update_content`, `rename`, `move`, `pin`, `delete`,
`restore`, `purge`, and selects the `payload` shape. Objects are strict: unknown keys are rejected, so the local
bookkeeping columns (`seq`, `attempts`, `last_error`) never appear. The authoritative definitions are the zod
schemas in [`src/pending-operations.ts`](../src/pending-operations.ts).

| `kind` | `payload` |
|---|---|
| `create` | folder: `{ nodeKind: "folder", parentId, name, position }`; document: the same with `nodeKind: "document"` plus `format` and `checksum` |
| `update_content` | `{ checksum, format }` (never the text; the push reads the current text) |
| `rename` | `{ name }` |
| `move` | `{ parentId, position }` (`parentId` null means the workspace root) |
| `pin` | `{ pinned: true, pinPosition }` or `{ pinned: false, pinPosition: null }` |
| `delete` | `{ trashBatchId, deletedAt }` |
| `restore` | `{ trashBatchId }` |
| `purge` | `{}` |

Other rules:

- IDs are lowercase hyphenated UUIDs. IDs NimPad generates are version 7: `opId`, `deviceId`, the `nodeId` of a
  `create` and the `trashBatchId` of a `delete`. IDs that refer to something existing (`workspaceId`, the
  `nodeId` of other kinds, `parentId`, the `trashBatchId` of a `restore`) may be any version.
- Timestamps are UTC with milliseconds and `Z`.
- Revisions are integers starting at 1 (the server's first revision), or `null`.
- `actorKind` is `user`, `import` or `system` (agents act only through the server).
- A node cannot be its own parent.
- Names follow `node-names.json` (`valid`): already trimmed, 1–255 UTF-16 code units, and so on.
- A `create` folds later renames and content updates (text or format) of its node while it has not been
  attempted yet; moves and pins are always queued as their own operations.

**Base revisions.** `baseMetaRev`/`baseContentRev` are the server revisions a change was made against, or `null`
while the node has not been acknowledged by the server (its `create` is still queued or unacknowledged). A `create`
always has both `null`. A `null` never means "apply unconditionally". When the server acknowledges an operation,
the sync engine rewrites the base revisions of the same node's later queued operations to the revisions the server
returned, in the same local transaction. So a pushed operation (`PushOperation`) always has a `baseMetaRev` unless
it is a `create`, and an `update_content` also has a `baseContentRev` (other kinds may leave it `null`: folders
have no content). The server answers `bad_request` to an operation that breaks this rule, and `stale_revision`
when a base revision is behind its own.
