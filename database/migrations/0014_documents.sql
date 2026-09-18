-- description: Documents, immutable versions, placement links, and document evidence for requirements
--
-- RCS migration 0014 - the document model (DOMAIN_MODEL.md sections 2.12-2.15; DOCUMENT_MODEL.md sections 2-5,
-- 10-13; DECISIONS.md ADR-005, ADR-014, ADR-015, ADR-016, ADR-026, ADR-027, ADR-036, ADR-042, ADR-043).
--
-- Three entities answer three different questions and are never collapsed into one attachment table (R1):
--   document          what the file is, as a business object - title, kind, issuer. No bytes, no case, no letter.
--   document_version  the exact bytes: one immutable, content-addressed object on the local filesystem. The
--                     database holds metadata only - there is no bytea, no large object, no base64 column.
--   document_link     why the file is here: one placement of a document (optionally pinned to one version) in one
--                     business context, in one role.
--
-- Immutability is enforced by privilege, not by convention: the runtime role may UPDATE only the status transition
-- and integrity columns of document_version, and only the removal/freeze columns of document_link. Nothing is ever
-- deleted - no business table grants DELETE, and the object store has no delete path for published objects
-- (ADR-043: retention is indefinite).
--
-- Deferred: internal_record does not exist yet, so the internal_record arc of document_link and of
-- requirement_evidence arrives with that table (the arc CHECKs below are then widened, as 0009's was here).

CREATE TABLE rcs.document_kind (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT document_kind_pk PRIMARY KEY (id),
    CONSTRAINT document_kind_code_uq UNIQUE (code),
    CONSTRAINT document_kind_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT document_kind_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT document_kind_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_kind_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- What the file IS (DOMAIN_MODEL.md 2.13). A map is always a map, whatever context it is placed in (DOCUMENT_MODEL.md 4.1).
INSERT INTO rcs.document_kind (id, code, label, sort_order) VALUES
    ('01995c00-000e-7000-8000-000000000001', 'LETTER_BODY', 'Letter', 10),
    ('01995c00-000e-7000-8000-000000000002', 'MAP', 'Map', 20),
    ('01995c00-000e-7000-8000-000000000003', 'DRAWING', 'Drawing', 30),
    ('01995c00-000e-7000-8000-000000000004', 'SPREADSHEET', 'Spreadsheet', 40),
    ('01995c00-000e-7000-8000-000000000005', 'PHOTO', 'Photo', 50),
    ('01995c00-000e-7000-8000-000000000006', 'CERTIFICATE', 'Certificate', 60),
    ('01995c00-000e-7000-8000-000000000007', 'PERMIT', 'Permit', 70),
    ('01995c00-000e-7000-8000-000000000008', 'OTHER', 'Other', 90);

CREATE TABLE rcs.document_link_role (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT document_link_role_pk PRIMARY KEY (id),
    CONSTRAINT document_link_role_code_uq UNIQUE (code),
    CONSTRAINT document_link_role_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT document_link_role_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT document_link_role_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_link_role_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- Why the file is HERE (DOMAIN_MODEL.md 2.15). Every row is named by a constraint below (ADR-038), so none of these
-- rows is ever deleted.
INSERT INTO rcs.document_link_role (id, code, label, sort_order) VALUES
    ('01995c00-000f-7000-8000-000000000001', 'PRIMARY_LETTER', 'Primary letter', 10),
    ('01995c00-000f-7000-8000-000000000002', 'ATTACHMENT', 'Attachment', 20),
    ('01995c00-000f-7000-8000-000000000003', 'ANNEX', 'Annex', 30),
    ('01995c00-000f-7000-8000-000000000004', 'REQUIREMENT_EVIDENCE', 'Requirement evidence', 40),
    ('01995c00-000f-7000-8000-000000000005', 'FINAL_RESULT_DOCUMENT', 'Final result document', 50),
    ('01995c00-000f-7000-8000-000000000006', 'SUPPORTING', 'Supporting', 60),
    ('01995c00-000f-7000-8000-000000000007', 'WORKING_COPY', 'Working copy', 70);

CREATE TABLE rcs.withdrawal_reason (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT withdrawal_reason_pk PRIMARY KEY (id),
    CONSTRAINT withdrawal_reason_code_uq UNIQUE (code),
    CONSTRAINT withdrawal_reason_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT withdrawal_reason_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT withdrawal_reason_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT withdrawal_reason_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- PROVISIONAL. The frozen documents name the lookup (DOMAIN_MODEL.md 2.19) and the DUPLICATE case (DOCUMENT_MODEL.md
-- 8.2 #4) but no full code list. A starting vocabulary for product-owner review; lookup vocabularies are refined
-- without reopening the model (DOMAIN_MODEL.md 12.6). Unsuitable rows are deactivated, never deleted.
INSERT INTO rcs.withdrawal_reason (id, code, label, sort_order) VALUES
    ('01995c00-0010-7000-8000-000000000001', 'UPLOADED_IN_ERROR', 'Wrong file uploaded', 10),
    ('01995c00-0010-7000-8000-000000000002', 'DUPLICATE', 'Duplicate', 20),
    ('01995c00-0010-7000-8000-000000000003', 'RECALLED_BY_ISSUER', 'Recalled by the issuing organization', 30),
    ('01995c00-0010-7000-8000-000000000004', 'OTHER', 'Other (explained in the note)', 90);

-- The stable business identity of a file (DOMAIN_MODEL.md 2.13). No case_id and no correspondence_id - by design:
-- all business context comes from document_link (ADR-036).
CREATE TABLE rcs.document (
    id                      uuid        NOT NULL,
    document_kind_id        uuid        NOT NULL,
    title                   text        NOT NULL,
    description             text,
    issuing_organization_id uuid,
    document_reference      text,
    status                  text        NOT NULL,
    withdrawn_at            timestamptz,
    withdrawn_by_user_id    uuid,
    withdrawal_reason_id    uuid,
    withdrawal_note         text,
    void_reason_id          uuid,
    void_note               text,
    voided_by_user_id       uuid,
    voided_at               timestamptz,
    created_at              timestamptz NOT NULL DEFAULT now(),
    created_by_user_id      uuid        NOT NULL,
    updated_at              timestamptz,
    updated_by_user_id      uuid,
    row_version             integer     NOT NULL DEFAULT 1,
    CONSTRAINT document_pk PRIMARY KEY (id),
    CONSTRAINT document_title_present CHECK (btrim(title) <> ''),
    CONSTRAINT document_status_valid CHECK (status IN ('ACTIVE', 'WITHDRAWN', 'VOID')),
    CONSTRAINT document_withdrawal_recorded CHECK (
        status <> 'WITHDRAWN' OR (withdrawn_at IS NOT NULL AND withdrawn_by_user_id IS NOT NULL AND withdrawal_reason_id IS NOT NULL)),
    CONSTRAINT document_void_recorded CHECK (
        status <> 'VOID' OR (voided_at IS NOT NULL AND voided_by_user_id IS NOT NULL AND void_reason_id IS NOT NULL)),
    CONSTRAINT document_row_version_positive CHECK (row_version > 0),
    CONSTRAINT document_kind_fk FOREIGN KEY (document_kind_id) REFERENCES rcs.document_kind (id),
    CONSTRAINT document_issuing_organization_fk FOREIGN KEY (issuing_organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT document_withdrawn_by_fk FOREIGN KEY (withdrawn_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_withdrawal_reason_fk FOREIGN KEY (withdrawal_reason_id) REFERENCES rcs.withdrawal_reason (id),
    CONSTRAINT document_void_reason_fk FOREIGN KEY (void_reason_id) REFERENCES rcs.void_reason (id),
    CONSTRAINT document_voided_by_fk FOREIGN KEY (voided_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- One immutable stored file (DOMAIN_MODEL.md 2.14). content_hash is the storage address; the path is derived from
-- it alone and is stored so a future layout change never rewrites history (DOCUMENT_MODEL.md 6.2).
CREATE TABLE rcs.document_version (
    id                    uuid        NOT NULL,
    document_id           uuid        NOT NULL,
    version_no            integer     NOT NULL,
    original_filename     text        NOT NULL,
    content_hash          text        NOT NULL,
    hash_algorithm        text        NOT NULL DEFAULT 'SHA256',
    storage_volume_code   text        NOT NULL,
    stored_relative_path  text        NOT NULL,
    byte_size             bigint      NOT NULL,
    mime_type             text        NOT NULL,
    page_count            integer,
    document_date         date,
    uploaded_at           timestamptz NOT NULL,
    uploaded_by_user_id   uuid        NOT NULL,
    supersedes_version_id uuid,
    status                text        NOT NULL,
    withdrawn_at          timestamptz,
    withdrawn_by_user_id  uuid,
    withdrawal_reason_id  uuid,
    withdrawal_note       text,
    integrity_checked_at  timestamptz,
    created_at            timestamptz NOT NULL DEFAULT now(),
    created_by_user_id    uuid        NOT NULL,
    updated_at            timestamptz,
    updated_by_user_id    uuid,
    row_version           integer     NOT NULL DEFAULT 1,
    CONSTRAINT document_version_pk PRIMARY KEY (id),
    -- The target of the composite foreign keys that prove "this version belongs to that document" (L5).
    CONSTRAINT document_version_id_document_uq UNIQUE (id, document_id),
    CONSTRAINT document_version_number_uq UNIQUE (document_id, version_no),
    -- Retried or repeated uploads onto the same document converge on one row: no phantom versions. Scoped to the
    -- document on purpose - two documents may share bytes (DOCUMENT_MODEL.md 6.3).
    CONSTRAINT document_version_content_uq UNIQUE (document_id, content_hash),
    CONSTRAINT document_version_number_positive CHECK (version_no > 0),
    CONSTRAINT document_version_filename_present CHECK (original_filename <> ''),
    CONSTRAINT document_version_hash_algorithm_valid CHECK (hash_algorithm = 'SHA256'),
    CONSTRAINT document_version_hash_format CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT document_version_volume_code_format CHECK (storage_volume_code ~ '^[A-Z][A-Z0-9_]*$'),
    -- sha256/ab/cd/<full hash>: nothing but the content decides where the bytes live.
    CONSTRAINT document_version_path_derived CHECK (
        stored_relative_path = 'sha256/' || substr(content_hash, 1, 2) || '/' || substr(content_hash, 3, 2) || '/' || content_hash),
    CONSTRAINT document_version_size_valid CHECK (byte_size >= 0),
    CONSTRAINT document_version_mime_format CHECK (mime_type ~* '^[a-z0-9][a-z0-9.+-]*/[a-z0-9][a-z0-9.+-]*$'),
    CONSTRAINT document_version_page_count_valid CHECK (page_count IS NULL OR page_count > 0),
    CONSTRAINT document_version_status_valid CHECK (status IN ('ACTIVE', 'SUPERSEDED', 'WITHDRAWN')),
    CONSTRAINT document_version_withdrawal_recorded CHECK (
        status <> 'WITHDRAWN' OR (withdrawn_at IS NOT NULL AND withdrawn_by_user_id IS NOT NULL AND withdrawal_reason_id IS NOT NULL)),
    CONSTRAINT document_version_not_superseding_itself CHECK (supersedes_version_id IS NULL OR supersedes_version_id <> id),
    CONSTRAINT document_version_row_version_positive CHECK (row_version > 0),
    CONSTRAINT document_version_document_fk FOREIGN KEY (document_id) REFERENCES rcs.document (id),
    -- A version supersedes a version of its own document.
    CONSTRAINT document_version_supersedes_fk FOREIGN KEY (supersedes_version_id, document_id) REFERENCES rcs.document_version (id, document_id),
    CONSTRAINT document_version_uploaded_by_fk FOREIGN KEY (uploaded_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_version_withdrawn_by_fk FOREIGN KEY (withdrawn_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_version_withdrawal_reason_fk FOREIGN KEY (withdrawal_reason_id) REFERENCES rcs.withdrawal_reason (id),
    CONSTRAINT document_version_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_version_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- "Current version" means the one ACTIVE version; there is no pointer column (ADR-027). This index is the backstop
-- behind the per-document serialization of upload, withdrawal and reinstatement.
CREATE UNIQUE INDEX document_version_one_active_uq ON rcs.document_version (document_id) WHERE status = 'ACTIVE';
CREATE INDEX document_version_content_hash_idx ON rcs.document_version (content_hash);

-- One placement of a document in one business context (DOMAIN_MODEL.md 2.15). Exclusive arc, real foreign keys
-- (DOCUMENT_MODEL.md 4.3, ADR-036). document_version_id set = pinned to exactly that version; NULL = floating.
CREATE TABLE rcs.document_link (
    id                    uuid        NOT NULL,
    document_id           uuid        NOT NULL,
    document_version_id   uuid,
    correspondence_id     uuid,
    requirement_id        uuid,
    request_id            uuid,
    response_id           uuid,
    final_result_id       uuid,
    case_id               uuid,
    document_link_role_id uuid        NOT NULL,
    is_origin             boolean     NOT NULL DEFAULT false,
    ordinal               smallint,
    linked_by_user_id     uuid        NOT NULL,
    linked_at             timestamptz NOT NULL,
    status                text        NOT NULL,
    removed_by_user_id    uuid,
    removed_at            timestamptz,
    removal_reason_note   text,
    created_at            timestamptz NOT NULL DEFAULT now(),
    created_by_user_id    uuid        NOT NULL,
    updated_at            timestamptz,
    updated_by_user_id    uuid,
    row_version           integer     NOT NULL DEFAULT 1,
    CONSTRAINT document_link_pk PRIMARY KEY (id),
    -- L1: exactly one business context.
    CONSTRAINT document_link_one_target CHECK (
        num_nonnulls(correspondence_id, requirement_id, request_id, response_id, final_result_id, case_id) = 1),
    -- L2: a case is a target only for material that names itself supporting (SUPPORTING 06, WORKING_COPY 07).
    CONSTRAINT document_link_case_role CHECK (
        case_id IS NULL OR document_link_role_id IN ('01995c00-000f-7000-8000-000000000006', '01995c00-000f-7000-8000-000000000007')),
    -- L8 (ADR-014): every file of a registered letter is pinned, whatever its role, so a letter's composition can
    -- never change after the fact.
    CONSTRAINT document_link_correspondence_pinned CHECK (correspondence_id IS NULL OR document_version_id IS NOT NULL),
    -- A letter holds its primary letter, attachments, annexes and filed supporting material (DOCUMENT_MODEL.md 4.2);
    -- PRIMARY_LETTER (01) and ATTACHMENT (02) exist only on a letter.
    CONSTRAINT document_link_correspondence_role CHECK (
        correspondence_id IS NULL OR document_link_role_id IN (
            '01995c00-000f-7000-8000-000000000001', '01995c00-000f-7000-8000-000000000002',
            '01995c00-000f-7000-8000-000000000003', '01995c00-000f-7000-8000-000000000006')),
    CONSTRAINT document_link_letter_roles_on_letter CHECK (
        document_link_role_id NOT IN ('01995c00-000f-7000-8000-000000000001', '01995c00-000f-7000-8000-000000000002')
        OR correspondence_id IS NOT NULL),
    -- L9: REQUIREMENT_EVIDENCE (04) is pinned, always, and only on a requirement (DOCUMENT_MODEL.md 4.6).
    CONSTRAINT document_link_evidence_pinned CHECK (
        document_link_role_id <> '01995c00-000f-7000-8000-000000000004'
        OR (requirement_id IS NOT NULL AND document_version_id IS NOT NULL)),
    -- L9, applied strictly: every placement on a final result is pinned from creation, so what a decision relied on
    -- can never float (DOCUMENT_MODEL.md 4.7 permits pinning at issue at the latest; this build pins at once).
    -- FINAL_RESULT_DOCUMENT (05) exists only on a final result.
    CONSTRAINT document_link_final_result_pinned CHECK (final_result_id IS NULL OR document_version_id IS NOT NULL),
    CONSTRAINT document_link_final_result_role CHECK (
        document_link_role_id <> '01995c00-000f-7000-8000-000000000005' OR final_result_id IS NOT NULL),
    CONSTRAINT document_link_status_valid CHECK (status IN ('ACTIVE', 'REMOVED')),
    CONSTRAINT document_link_removal_recorded CHECK (
        status <> 'REMOVED' OR (removed_at IS NOT NULL AND removed_by_user_id IS NOT NULL AND removal_reason_note IS NOT NULL)),
    CONSTRAINT document_link_ordinal_valid CHECK (ordinal IS NULL OR ordinal >= 0),
    CONSTRAINT document_link_row_version_positive CHECK (row_version > 0),
    -- L7: every business foreign key restricts; nothing cascades.
    CONSTRAINT document_link_document_fk FOREIGN KEY (document_id) REFERENCES rcs.document (id),
    -- L5: a pinned version belongs to the linked document.
    CONSTRAINT document_link_version_fk FOREIGN KEY (document_version_id, document_id) REFERENCES rcs.document_version (id, document_id),
    CONSTRAINT document_link_correspondence_fk FOREIGN KEY (correspondence_id) REFERENCES rcs.correspondence (id),
    CONSTRAINT document_link_requirement_fk FOREIGN KEY (requirement_id) REFERENCES rcs.requirement (id),
    CONSTRAINT document_link_request_fk FOREIGN KEY (request_id) REFERENCES rcs.request (id),
    CONSTRAINT document_link_response_fk FOREIGN KEY (response_id) REFERENCES rcs.response (id),
    CONSTRAINT document_link_final_result_fk FOREIGN KEY (final_result_id) REFERENCES rcs.final_result (id),
    CONSTRAINT document_link_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT document_link_role_fk FOREIGN KEY (document_link_role_id) REFERENCES rcs.document_link_role (id),
    CONSTRAINT document_link_linked_by_fk FOREIGN KEY (linked_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_link_removed_by_fk FOREIGN KEY (removed_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_link_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT document_link_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- L3: at most one ACTIVE primary letter per letter.
CREATE UNIQUE INDEX document_link_one_primary_letter_uq ON rcs.document_link (correspondence_id)
    WHERE status = 'ACTIVE' AND document_link_role_id = '01995c00-000f-7000-8000-000000000001';
-- L4: at most one ACTIVE origin per document - the placement where the file entered the system, which fixes the
-- document's home case (DOCUMENT_MODEL.md 4.4).
CREATE UNIQUE INDEX document_link_one_origin_uq ON rcs.document_link (document_id) WHERE status = 'ACTIVE' AND is_origin;

CREATE INDEX document_link_document_idx ON rcs.document_link (document_id);
CREATE INDEX document_link_version_idx ON rcs.document_link (document_version_id);
CREATE INDEX document_link_correspondence_idx ON rcs.document_link (correspondence_id);
CREATE INDEX document_link_requirement_idx ON rcs.document_link (requirement_id);
CREATE INDEX document_link_request_idx ON rcs.document_link (request_id);
CREATE INDEX document_link_response_idx ON rcs.document_link (response_id);
CREATE INDEX document_link_final_result_idx ON rcs.document_link (final_result_id);
CREATE INDEX document_link_case_idx ON rcs.document_link (case_id);

-- The case each placement lies in: the single definition authorization, the home case and every document read
-- resolve through (DOCUMENT_MODEL.md 10.1). A response is placed in its request's case - never in the case of the
-- letter that carried it, which stays governed by its own case (A-6, ADR-016).
CREATE VIEW rcs.document_link_context AS
SELECT l.id AS document_link_id,
       COALESCE(l.case_id, co.case_id, rq.case_id, rs.case_id, rp_request.case_id, fr.case_id) AS context_case_id
FROM rcs.document_link AS l
LEFT JOIN rcs.correspondence AS co ON co.id = l.correspondence_id
LEFT JOIN rcs.requirement AS rq ON rq.id = l.requirement_id
LEFT JOIN rcs.request AS rs ON rs.id = l.request_id
LEFT JOIN rcs.response AS rp ON rp.id = l.response_id
LEFT JOIN rcs.request AS rp_request ON rp_request.id = rp.request_id
LEFT JOIN rcs.final_result AS fr ON fr.id = l.final_result_id;

-- Document evidence for requirements: the document arc of requirement_evidence (DOMAIN_MODEL.md 2.12, amendment A-5).
-- Evidence that is a document always pins the exact version accepted as proof, and the version belongs to that
-- document. The matching REQUIREMENT_EVIDENCE document_link is written in the same transaction (DOCUMENT_MODEL.md 4.6).
ALTER TABLE rcs.requirement_evidence ADD COLUMN document_id uuid;
ALTER TABLE rcs.requirement_evidence ADD COLUMN document_version_id uuid;
ALTER TABLE rcs.requirement_evidence DROP CONSTRAINT requirement_evidence_arc;
ALTER TABLE rcs.requirement_evidence ADD CONSTRAINT requirement_evidence_arc CHECK (
    num_nonnulls(response_id, document_id) = 1
    AND (evidence_type <> 'RESPONSE' OR response_id IS NOT NULL)
    AND (evidence_type <> 'DOCUMENT' OR (document_id IS NOT NULL AND document_version_id IS NOT NULL))
    -- INTERNAL_ACT arrives with internal_record.
    AND evidence_type <> 'INTERNAL_ACT'
    AND (document_version_id IS NULL OR document_id IS NOT NULL));
ALTER TABLE rcs.requirement_evidence ADD CONSTRAINT requirement_evidence_document_fk
    FOREIGN KEY (document_id) REFERENCES rcs.document (id);
ALTER TABLE rcs.requirement_evidence ADD CONSTRAINT requirement_evidence_document_version_fk
    FOREIGN KEY (document_version_id, document_id) REFERENCES rcs.document_version (id, document_id);
CREATE INDEX requirement_evidence_document_version_idx ON rcs.requirement_evidence (document_version_id);

GRANT SELECT ON rcs.document_kind, rcs.document_link_role, rcs.withdrawal_reason, rcs.document_link_context TO rcs_app;
GRANT SELECT, INSERT, UPDATE ON rcs.document TO rcs_app;

-- A version's bytes and creation facts are written once (DOMAIN_MODEL.md 2.14): the runtime role may change only the
-- status transition, its who/when/why, and the integrity timestamp.
GRANT SELECT, INSERT ON rcs.document_version TO rcs_app;
GRANT UPDATE (status, withdrawn_at, withdrawn_by_user_id, withdrawal_reason_id, withdrawal_note, integrity_checked_at,
              updated_at, updated_by_user_id, row_version) ON rcs.document_version TO rcs_app;

-- A link is removed by status, never deleted, and a floating link may be frozen once (DOCUMENT_MODEL.md 4.4). Its
-- target, role, origin flag and provenance are never rewritten.
GRANT SELECT, INSERT ON rcs.document_link TO rcs_app;
GRANT UPDATE (document_version_id, status, removed_by_user_id, removed_at, removal_reason_note,
              updated_at, updated_by_user_id, row_version) ON rcs.document_link TO rcs_app;
