import { describe, expect, it } from 'vitest';
import {
  DOCUMENT_FORMATS,
  DocumentFormat,
  detectFormat,
  FORMAT_BY_EXTENSION,
  FORMAT_BY_FILE_NAME,
} from '../src/formats.ts';
import { formatsFixture } from './fixtures.ts';

const ch = (codePoint: number): string => String.fromCodePoint(codePoint);

describe('detectFormat', () => {
  it.each([
    ['config.json', 'json'],
    ['README.md', 'markdown'],
    ['notes.markdown', 'markdown'],
    ['build.gradle', 'groovy'],
    ['pyproject.toml', 'ini'],
    ['App.xaml', 'xml'],
    ['Program.cs', 'csharp'],
    ['deploy.ps1', 'powershell'],
    ['index.htm', 'html'],
    ['styles.css', 'css'],
    ['query.sql', 'sql'],
    ['types.d.ts', 'typescript'],
    ['notes.txt', 'plain'],
    ['README', 'plain'],
    ['', 'plain'],
    ['name.', 'plain'],
    ['notes.md.txt', 'plain'],
    ['archive.tar.json', 'json'],
  ] as const)('%s -> %s', (name, format) => {
    expect(detectFormat(name)).toBe(format);
  });

  it('compares ASCII letters case-insensitively', () => {
    expect(detectFormat('CONFIG.JSON')).toBe('json');
    expect(detectFormat('Readme.Md')).toBe('markdown');
    expect(detectFormat('JENKINSFILE')).toBe('groovy');
  });

  it('does not fold non-ASCII letters (the desktop does not either)', () => {
    expect(detectFormat(`query.${ch(0x17f)}ql`)).toBe('plain');
    expect(detectFormat(`settings.${ch(0x131)}ni`)).toBe('plain');
  });

  it('treats a name that starts with its only dot as all extension', () => {
    expect(detectFormat('.editorconfig')).toBe('ini');
    expect(detectFormat('.md')).toBe('markdown');
    expect(detectFormat('.gitignore')).toBe('plain');
  });

  it('trims white space first (the .NET set: U+0085 yes, U+FEFF no)', () => {
    expect(detectFormat(' spaced.md ')).toBe('markdown');
    expect(detectFormat(`next.md${ch(0x85)}`)).toBe('markdown');
    expect(detectFormat(`bom.md${ch(0xfeff)}`)).toBe('plain');
    expect(detectFormat(' Jenkinsfile ')).toBe('groovy');
  });

  it('knows Jenkinsfile only as a whole name', () => {
    expect(detectFormat('Jenkinsfile')).toBe('groovy');
    expect(detectFormat('my Jenkinsfile')).toBe('plain');
    expect(detectFormat('Jenkinsfile.bak')).toBe('plain');
  });

  it('is never confused by Object.prototype keys', () => {
    for (const name of ['constructor', '__proto__', 'toString', 'x.constructor', 'x.__proto__']) {
      expect(detectFormat(name)).toBe('plain');
    }
  });

  it('maps only to supported formats, from lowercase keys', () => {
    for (const [key, format] of [...FORMAT_BY_EXTENSION, ...FORMAT_BY_FILE_NAME]) {
      expect(key).toBe(key.toLowerCase());
      expect(DocumentFormat.safeParse(format).success).toBe(true);
    }
    expect([...FORMAT_BY_EXTENSION.keys()].every((key) => key.startsWith('.'))).toBe(true);
  });
});

describe('formats.json', () => {
  it('lists every format and every known extension', () => {
    expect(formatsFixture.formats).toEqual([...DOCUMENT_FORMATS]);
    const names = new Set(formatsFixture.cases.map((c) => c.name));
    for (const extension of FORMAT_BY_EXTENSION.keys()) {
      expect(names.has(`note${extension}`)).toBe(true);
    }
  });

  it.each(formatsFixture.cases)('$name -> $format', ({ name, format }) => {
    expect(detectFormat(name)).toBe(format);
  });
});
