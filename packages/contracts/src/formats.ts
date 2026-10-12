// Document formats and their detection from a note name (docs/architecture.md, "Formats"). The desktop app, the
// API and the MCP server must all pick the same format for the same name; fixtures/formats.json pins it.

import { z } from 'zod';
import { trimWhiteSpace } from './text.ts';

/** Every supported document format. Detected from the name's extension, else `plain`; user-overridable. */
export const DOCUMENT_FORMATS = [
  'plain',
  'markdown',
  'json',
  'xml',
  'yaml',
  'python',
  'groovy',
  'javascript',
  'typescript',
  'csharp',
  'sql',
  'powershell',
  'ini',
  'css',
  'html',
] as const;
export const DocumentFormat = z.enum(DOCUMENT_FORMATS);
export type DocumentFormat = z.infer<typeof DocumentFormat>;

/** Known extensions (lowercase, with the leading dot) and their format. Every other extension means `plain`. */
export const FORMAT_BY_EXTENSION: ReadonlyMap<string, DocumentFormat> = new Map<string, DocumentFormat>([
  ['.md', 'markdown'],
  ['.markdown', 'markdown'],
  ['.json', 'json'],
  ['.jsonc', 'json'],
  ['.xml', 'xml'],
  ['.xaml', 'xml'],
  ['.csproj', 'xml'],
  ['.props', 'xml'],
  ['.targets', 'xml'],
  ['.svg', 'xml'],
  ['.xsd', 'xml'],
  ['.xsl', 'xml'],
  ['.xslt', 'xml'],
  ['.resx', 'xml'],
  ['.config', 'xml'],
  ['.yml', 'yaml'],
  ['.yaml', 'yaml'],
  ['.py', 'python'],
  ['.pyw', 'python'],
  ['.groovy', 'groovy'],
  ['.gradle', 'groovy'],
  ['.js', 'javascript'],
  ['.mjs', 'javascript'],
  ['.cjs', 'javascript'],
  ['.jsx', 'javascript'],
  ['.ts', 'typescript'],
  ['.mts', 'typescript'],
  ['.cts', 'typescript'],
  ['.tsx', 'typescript'],
  ['.cs', 'csharp'],
  ['.csx', 'csharp'],
  ['.sql', 'sql'],
  ['.ps1', 'powershell'],
  ['.psm1', 'powershell'],
  ['.psd1', 'powershell'],
  ['.ini', 'ini'],
  ['.cfg', 'ini'],
  ['.toml', 'ini'],
  ['.conf', 'ini'],
  ['.editorconfig', 'ini'],
  ['.css', 'css'],
  ['.html', 'html'],
  ['.htm', 'html'],
  ['.xhtml', 'html'],
]);

/** Well-known extensionless file names (lowercase) and their format. */
export const FORMAT_BY_FILE_NAME: ReadonlyMap<string, DocumentFormat> = new Map<string, DocumentFormat>([
  ['jenkinsfile', 'groovy'],
]);

// Only A-Z fold. Non-ASCII characters never match a (pure ASCII) table entry, whatever their case mappings: the
// desktop's case-insensitive lookup behaves the same (for example `ſ` does not match `s`).
function asciiLowerCase(value: string): string {
  return value.replace(/[A-Z]/g, (ch) => ch.toLowerCase());
}

/**
 * Detects a document's format from its name. The rules:
 *
 * 1. Trim white space ({@link trimWhiteSpace}); an empty name is `plain`.
 * 2. A well-known file name (`Jenkinsfile`) gives its format.
 * 3. Otherwise the extension is everything from the last `.`, which must not be the last character; a name that
 *    starts with its only dot (`.editorconfig`) is all extension. A known extension gives its format.
 * 4. Everything else is `plain`.
 *
 * Names and extensions compare case-insensitively for ASCII letters only (`CONFIG.JSON` is `json`).
 */
export function detectFormat(name: string): DocumentFormat {
  const trimmed = trimWhiteSpace(name);
  if (trimmed === '') {
    return 'plain';
  }
  const byFileName = FORMAT_BY_FILE_NAME.get(asciiLowerCase(trimmed));
  if (byFileName !== undefined) {
    return byFileName;
  }
  const dot = trimmed.lastIndexOf('.');
  if (dot < 0 || dot === trimmed.length - 1) {
    return 'plain';
  }
  return FORMAT_BY_EXTENSION.get(asciiLowerCase(trimmed.slice(dot))) ?? 'plain';
}
