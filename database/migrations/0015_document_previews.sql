-- description: Document previews - derived, regenerable preview generations of exact document versions and their artifacts
--
-- RCS migration 0015 - the preview subsystem (DECISIONS.md ADR-044; DOCUMENT_MODEL.md section 9.5; SECURITY.md
-- section 11.5; ARCHITECTURE.md section 14.4).
--
-- A preview is a DERIVED artifact, never a business record:
--   document_preview           one preview generation of one exact document_version: which processor (and which
--                              version of it) produced it from which bytes, and where the generation stands. The row
--                              is also the durable job - PENDING work survives a restart; a PROCESSING row carries a
--                              lease so a crashed worker's job is reclaimed and two workers never run the same job.
--   document_preview_artifact  one inert output file of a READY generation: a rendered page, a re-encoded image, a
--                              thumbnail or a geometry document. Stored in the preview store, never in the
--                              content-addressed object store, and never under an original filename.
--
-- Nothing here changes document, document_version or document_link. The runtime role gets no privilege on those
-- tables that it did not already have; the only change to document_version is a UNIQUE constraint that lets a
-- preview prove, by foreign key, that it was derived from exactly the bytes the version names.

-- (id, content_hash) is trivially unique because id is the primary key; the constraint exists to be referenced.
ALTER TABLE rcs.document_version ADD CONSTRAINT document_version_id_hash_uq UNIQUE (id, content_hash);

CREATE TABLE rcs.document_preview (
    id                    uuid        NOT NULL,
    document_version_id   uuid        NOT NULL,
    -- The bytes this generation was derived from. Always the version's content_hash (enforced by the foreign key).
    source_content_hash   text        NOT NULL,
    preview_type          text        NOT NULL,
    status                text        NOT NULL,
    processor             text        NOT NULL,
    processor_version     text        NOT NULL,
    -- The rendering settings (resolution, limits) that shaped the output. With processor and processor_version it
    -- names one logical preview generation; changing any of the three is a reason to regenerate, never to mutate.
    settings_key          text        NOT NULL,
    requested_at          timestamptz NOT NULL,
    -- NULL when the system requested it (a new upload, a backfill); a person for a retry or a regeneration.
    requested_by_user_id  uuid,
    started_at            timestamptz,
    completed_at          timestamptz,
    failure_code          text,
    -- Sanitized, bounded text. Raw converter output (paths, stderr) is never stored here.
    failure_message       text,
    attempt_count         integer     NOT NULL DEFAULT 0,
    max_attempts          integer     NOT NULL,
    manual_retry_count    integer     NOT NULL DEFAULT 0,
    next_attempt_at       timestamptz NOT NULL,
    lease_token           uuid,
    lease_expires_at      timestamptz,
    page_count            integer,
    pages_rendered        integer,
    superseded_at         timestamptz,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz,
    row_version           integer     NOT NULL DEFAULT 1,
    CONSTRAINT document_preview_pk PRIMARY KEY (id),
    CONSTRAINT document_preview_status_valid CHECK (status IN ('PENDING', 'PROCESSING', 'READY', 'FAILED', 'UNSUPPORTED')),
    CONSTRAINT document_preview_type_valid CHECK (preview_type IN ('PAGES', 'IMAGE', 'GEOMETRY', 'NONE')),
    CONSTRAINT document_preview_hash_format CHECK (source_content_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT document_preview_processor_format CHECK (processor ~ '^[a-z][a-z0-9-]{1,63}$'),
    CONSTRAINT document_preview_processor_version_format CHECK (processor_version ~ '^[A-Za-z0-9][A-Za-z0-9._+;=-]{0,199}$'),
    CONSTRAINT document_preview_settings_key_format CHECK (settings_key ~ '^[A-Za-z0-9][A-Za-z0-9._+;=-]{0,199}$'),
    CONSTRAINT document_preview_failure_code_format CHECK (failure_code IS NULL OR failure_code ~ '^[A-Z][A-Z0-9_]{1,63}$'),
    CONSTRAINT document_preview_failure_message_bounded CHECK (failure_message IS NULL OR char_length(failure_message) <= 500),
    -- An unsupported format has nothing to render and nothing to retry.
    CONSTRAINT document_preview_unsupported_is_none CHECK ((status = 'UNSUPPORTED') = (preview_type = 'NONE')),
    -- A finished generation says when it finished; a failed or unsupported one says why; a ready one carries no failure.
    CONSTRAINT document_preview_completion_recorded CHECK (
        status NOT IN ('READY', 'FAILED', 'UNSUPPORTED') OR completed_at IS NOT NULL),
    CONSTRAINT document_preview_failure_recorded CHECK (
        status NOT IN ('FAILED', 'UNSUPPORTED') OR failure_code IS NOT NULL),
    CONSTRAINT document_preview_ready_clean CHECK (status <> 'READY' OR (failure_code IS NULL AND failure_message IS NULL)),
    -- Exactly the PROCESSING rows hold a lease: a lease is what makes a claim exclusive and a crashed claim reclaimable.
    CONSTRAINT document_preview_lease_consistent CHECK (
        (status = 'PROCESSING') = (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)),
    CONSTRAINT document_preview_attempts_bounded CHECK (
        max_attempts BETWEEN 1 AND 10 AND attempt_count >= 0 AND attempt_count <= max_attempts),
    CONSTRAINT document_preview_manual_retries_valid CHECK (manual_retry_count >= 0),
    CONSTRAINT document_preview_pages_valid CHECK (
        (page_count IS NULL OR page_count > 0) AND (pages_rendered IS NULL OR (pages_rendered > 0 AND (page_count IS NULL OR pages_rendered <= page_count)))),
    -- Only a settled generation is replaced; a running one finishes (or times out) first.
    CONSTRAINT document_preview_supersede_settled CHECK (superseded_at IS NULL OR status NOT IN ('PENDING', 'PROCESSING')),
    CONSTRAINT document_preview_row_version_positive CHECK (row_version > 0),
    -- The preview belongs to one exact version and to that version's exact bytes (ADR-044: version-scoped, derived).
    CONSTRAINT document_preview_version_fk FOREIGN KEY (document_version_id, source_content_hash)
        REFERENCES rcs.document_version (id, content_hash),
    CONSTRAINT document_preview_requested_by_fk FOREIGN KEY (requested_by_user_id) REFERENCES rcs.app_user (id)
);

-- At most one current generation per version. Earlier generations stay as history until a cleanup is decided.
CREATE UNIQUE INDEX document_preview_one_current_uq ON rcs.document_preview (document_version_id) WHERE superseded_at IS NULL;
-- The claim query: due pending work, and expired leases of crashed workers.
CREATE INDEX document_preview_pending_idx ON rcs.document_preview (next_attempt_at) WHERE status = 'PENDING' AND superseded_at IS NULL;
CREATE INDEX document_preview_lease_idx ON rcs.document_preview (lease_expires_at) WHERE status = 'PROCESSING';
CREATE INDEX document_preview_status_idx ON rcs.document_preview (status) WHERE superseded_at IS NULL;

CREATE TABLE rcs.document_preview_artifact (
    id                  uuid        NOT NULL,
    document_preview_id uuid        NOT NULL,
    artifact_kind       text        NOT NULL,
    -- <document_version_id>/<document_preview_id>/<artifact id>.<ext>, relative to the preview store root. Derived
    -- from identifiers alone: no filename, title or case number ever reaches a path.
    storage_key         text        NOT NULL,
    content_type        text        NOT NULL,
    size_bytes          bigint      NOT NULL,
    sha256              text        NOT NULL,
    page_number         integer,
    width               integer,
    height              integer,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_preview_artifact_pk PRIMARY KEY (id),
    CONSTRAINT document_preview_artifact_kind_valid CHECK (artifact_kind IN ('PAGE', 'IMAGE', 'THUMBNAIL', 'GEOMETRY')),
    -- Only inert, re-encoded or RCS-generated formats. Never SVG, HTML, PDF or anything else a browser executes.
    CONSTRAINT document_preview_artifact_content_type_valid CHECK (content_type IN ('image/png', 'image/jpeg', 'application/json')),
    CONSTRAINT document_preview_artifact_kind_type CHECK (
        (artifact_kind = 'GEOMETRY') = (content_type = 'application/json')),
    CONSTRAINT document_preview_artifact_storage_key_format CHECK (
        storage_key ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.(png|jpg|json)$'),
    CONSTRAINT document_preview_artifact_storage_key_uq UNIQUE (storage_key),
    CONSTRAINT document_preview_artifact_size_valid CHECK (size_bytes > 0),
    CONSTRAINT document_preview_artifact_hash_format CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT document_preview_artifact_page_number CHECK ((artifact_kind = 'PAGE') = (page_number IS NOT NULL) AND (page_number IS NULL OR page_number > 0)),
    CONSTRAINT document_preview_artifact_dimensions CHECK ((width IS NULL OR width > 0) AND (height IS NULL OR height > 0)),
    CONSTRAINT document_preview_artifact_preview_fk FOREIGN KEY (document_preview_id) REFERENCES rcs.document_preview (id)
);

CREATE UNIQUE INDEX document_preview_artifact_slot_uq ON rcs.document_preview_artifact (document_preview_id, artifact_kind, page_number) NULLS NOT DISTINCT;

-- Viewing a derived preview is a disclosure of the document's content, audited like a download but named for what it
-- is (ADR-044). The rest of the frozen vocabulary is unchanged.
ALTER TABLE rcs.audit_event DROP CONSTRAINT audit_event_action_code_valid;
ALTER TABLE rcs.audit_event ADD CONSTRAINT audit_event_action_code_valid CHECK (action_code IN (
    'CREATE', 'UPDATE', 'STATE_CHANGE', 'LINK', 'UNLINK', 'UPLOAD', 'DOWNLOAD', 'WITHDRAW', 'VOID',
    'ASSIGN', 'LOGIN', 'LOGOUT', 'EXPORT', 'PRINT', 'PERMISSION_DENIED', 'PREVIEW'));

-- The runtime role creates generations and moves them through their states; the identity of a generation (which
-- version, which bytes, which processor and settings) is written once. preview_type may only turn to NONE when the
-- worker finds the input unrenderable (e.g. binary DXF). Artifacts are written once, never updated.
-- No DELETE: physical cleanup of derived previews is an operational decision not taken yet.
GRANT SELECT, INSERT ON rcs.document_preview TO rcs_app;
GRANT UPDATE (status, preview_type, requested_by_user_id, started_at, completed_at, failure_code, failure_message, attempt_count,
              manual_retry_count, next_attempt_at, lease_token, lease_expires_at, page_count, pages_rendered,
              superseded_at, updated_at, row_version) ON rcs.document_preview TO rcs_app;
GRANT SELECT, INSERT ON rcs.document_preview_artifact TO rcs_app;
