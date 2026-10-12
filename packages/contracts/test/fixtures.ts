// Loads the shared JSON fixtures the same way any consumer would: as plain JSON files on disk.

import { readFileSync } from 'node:fs';
import type {
  ChecksumFixture,
  FormatsFixture,
  FractionalIndexFixture,
  NodeNamesFixture,
  NoteLinksFixture,
  NoteTextFixture,
} from '../scripts/fixture-builders.ts';

export interface PendingOperationExample {
  description: string;
  operation: Record<string, unknown>;
}

export interface PendingOperationsFixture {
  description: string;
  valid: PendingOperationExample[];
  invalid: PendingOperationExample[];
  needsRebase: PendingOperationExample[];
}

/** Every fixture file, generated or hand-written. */
export const FIXTURE_FILES = [
  'fractional-index.json',
  'checksum.json',
  'note-text.json',
  'node-names.json',
  'formats.json',
  'note-links.json',
  'pending-operations.json',
] as const;

const fixturesDir = new URL('../fixtures/', import.meta.url);

export function readFixtureText(name: string): string {
  return readFileSync(new URL(name, fixturesDir), 'utf8');
}

function readFixture<T>(name: string): T {
  return JSON.parse(readFixtureText(name)) as T;
}

export const fractionalIndexFixture = readFixture<FractionalIndexFixture>('fractional-index.json');
export const checksumFixture = readFixture<ChecksumFixture>('checksum.json');
export const noteTextFixture = readFixture<NoteTextFixture>('note-text.json');
export const nodeNamesFixture = readFixture<NodeNamesFixture>('node-names.json');
export const formatsFixture = readFixture<FormatsFixture>('formats.json');
export const noteLinksFixture = readFixture<NoteLinksFixture>('note-links.json');
export const pendingOperationsFixture = readFixture<PendingOperationsFixture>('pending-operations.json');
