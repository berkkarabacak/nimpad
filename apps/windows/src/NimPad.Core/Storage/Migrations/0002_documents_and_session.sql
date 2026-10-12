-- NimPad local schema, version 2: document versions, tab titles and the BOM flag of recovery buffers.

-- A local counter that every committed change of a document's text or format increments (DocumentService). Rows
-- that existed before this migration start at 1, like new documents.
ALTER TABLE document_contents ADD COLUMN version INTEGER NOT NULL DEFAULT 1;

-- The title a restored tab shows before its note or file has loaded.
ALTER TABLE session_tabs ADD COLUMN title TEXT NOT NULL DEFAULT '';

-- Whether the external file was read with (and is saved with) a byte order mark; encoding holds its web name.
ALTER TABLE external_file_buffers ADD COLUMN has_bom INTEGER NOT NULL DEFAULT 0 CHECK (has_bom IN (0, 1));
