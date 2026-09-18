-- description: Open vocabularies as lookup tables with their frozen seed codes
--
-- RCS migration 0003 - lookup vocabularies (DOMAIN_MODEL.md sections 1.4, 2.19).
--
-- Every lookup has the same shape: id, code (unique, stable, uppercase), label, description, sort_order,
-- is_active, valid_from, valid_to, plus the common columns. Rows are deactivated, never deleted.
--
-- Labels are English reference text. User interfaces translate by CODE (the Review build shows
-- Azerbaijani); business logic never reads a label.
--
-- Fixed identifiers (DECISIONS.md ADR-038): seeded rows have fixed UUIDs of the form
--   01995c00-00TT-7000-8000-0000000000RR   (TT = lookup table, RR = row)
-- so that constraints can express a row-local predicate on a seeded code - for example the RESPONSIBLE
-- assignment exclusion (DEF-04) - without a join. Those ids never change and those rows are never deleted.
--
-- Only the lookups the first vertical slice references are created here. The others (case_type,
-- closure_type, delivery_method, document_kind, document_link_role, decision_type,
-- internal_record_type, withdrawal_reason) arrive with the migrations that introduce their columns.

CREATE TABLE rcs.organization_type (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT organization_type_pk PRIMARY KEY (id),
    CONSTRAINT organization_type_code_uq UNIQUE (code),
    CONSTRAINT organization_type_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT organization_type_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT organization_type_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT organization_type_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- DOMAIN_MODEL.md 2.1 names the kinds of party. INDIVIDUAL is the OQ-1 placeholder and is inactive:
-- V1 models parties as organizations only.
INSERT INTO rcs.organization_type (id, code, label, sort_order, is_active) VALUES
    ('01995c00-0002-7000-8000-000000000001', 'STATE_AUTHORITY', 'State authority', 10, true),
    ('01995c00-0002-7000-8000-000000000002', 'MUNICIPAL_DEPARTMENT', 'Municipal department', 20, true),
    ('01995c00-0002-7000-8000-000000000003', 'UTILITY_COMPANY', 'Utility company', 30, true),
    ('01995c00-0002-7000-8000-000000000004', 'LEGAL_ENTITY', 'Legal entity', 40, true),
    ('01995c00-0002-7000-8000-000000000005', 'INDIVIDUAL', 'Individual (placeholder, OQ-1)', 90, false);

CREATE TABLE rcs.correspondence_kind (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT correspondence_kind_pk PRIMARY KEY (id),
    CONSTRAINT correspondence_kind_code_uq UNIQUE (code),
    CONSTRAINT correspondence_kind_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT correspondence_kind_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT correspondence_kind_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT correspondence_kind_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- DOMAIN_MODEL.md 2.8. INITIATING (row 01) is referenced by a partial unique index (migration 0006).
INSERT INTO rcs.correspondence_kind (id, code, label, sort_order) VALUES
    ('01995c00-0003-7000-8000-000000000001', 'INITIATING', 'Initiating incoming request', 10),
    ('01995c00-0003-7000-8000-000000000002', 'OUTGOING_REQUEST', 'Outgoing request', 20),
    ('01995c00-0003-7000-8000-000000000003', 'INCOMING_RESPONSE', 'Incoming response', 30),
    ('01995c00-0003-7000-8000-000000000004', 'REMINDER', 'Reminder', 40),
    ('01995c00-0003-7000-8000-000000000005', 'WITHDRAWAL', 'Withdrawal', 50),
    ('01995c00-0003-7000-8000-000000000006', 'FINAL_RESULT_DISPATCH', 'Final result dispatch', 60),
    ('01995c00-0003-7000-8000-000000000007', 'INFORMATIONAL', 'Informational', 70),
    ('01995c00-0003-7000-8000-000000000008', 'INTERNAL_MEMO', 'Internal memo', 80);

CREATE TABLE rcs.response_type (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    is_conclusive_default boolean NOT NULL,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT response_type_pk PRIMARY KEY (id),
    CONSTRAINT response_type_code_uq UNIQUE (code),
    CONSTRAINT response_type_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT response_type_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT response_type_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_type_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- What kind of communication arrived (DOMAIN_MODEL.md 2.10, decision C-2). The conclusive default is a
-- suggestion the clerk confirms; it follows the typical rows of WORKFLOW.md 4.2.
INSERT INTO rcs.response_type (id, code, label, is_conclusive_default, sort_order) VALUES
    ('01995c00-0004-7000-8000-000000000001', 'OPINION', 'Opinion', true, 10),
    ('01995c00-0004-7000-8000-000000000002', 'CLARIFICATION', 'Clarification', false, 20),
    ('01995c00-0004-7000-8000-000000000003', 'INFORMATION_REQUEST', 'Information request', false, 30),
    ('01995c00-0004-7000-8000-000000000004', 'ADDITIONAL_REQUIREMENT', 'Additional requirement', false, 40),
    ('01995c00-0004-7000-8000-000000000005', 'INFORMATION', 'Information', true, 50),
    ('01995c00-0004-7000-8000-000000000006', 'OTHER', 'Other', false, 60),
    ('01995c00-0004-7000-8000-000000000007', 'ACKNOWLEDGEMENT', 'Acknowledgement', false, 70),
    ('01995c00-0004-7000-8000-000000000008', 'ADDITIONAL_DOCUMENT', 'Additional document', false, 80),
    ('01995c00-0004-7000-8000-000000000009', 'DEADLINE_EXTENSION', 'Deadline extension', false, 90);

CREATE TABLE rcs.response_outcome (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    is_verdict boolean NOT NULL,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT response_outcome_pk PRIMARY KEY (id),
    CONSTRAINT response_outcome_code_uq UNIQUE (code),
    CONSTRAINT response_outcome_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT response_outcome_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT response_outcome_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_outcome_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- What the communication decided - a separate axis (decision C-2). NOT_APPLICABLE and UNDETERMINED are
-- explicit non-verdicts with different meanings.
INSERT INTO rcs.response_outcome (id, code, label, is_verdict, sort_order) VALUES
    ('01995c00-0005-7000-8000-000000000001', 'APPROVED', 'Approved', true, 10),
    ('01995c00-0005-7000-8000-000000000002', 'REJECTED', 'Rejected', true, 20),
    ('01995c00-0005-7000-8000-000000000003', 'CONDITIONAL', 'Conditional', true, 30),
    ('01995c00-0005-7000-8000-000000000004', 'NOT_APPLICABLE', 'Not applicable', false, 40),
    ('01995c00-0005-7000-8000-000000000005', 'UNDETERMINED', 'Undetermined', false, 50);

CREATE TABLE rcs.requirement_origin_type (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT requirement_origin_type_pk PRIMARY KEY (id),
    CONSTRAINT requirement_origin_type_code_uq UNIQUE (code),
    CONSTRAINT requirement_origin_type_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT requirement_origin_type_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT requirement_origin_type_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_origin_type_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- DOMAIN_MODEL.md 2.11. RESPONSE (row 01) is referenced by a CHECK in migration 0009.
INSERT INTO rcs.requirement_origin_type (id, code, label, sort_order) VALUES
    ('01995c00-0006-7000-8000-000000000001', 'RESPONSE', 'Imposed by a response', 10),
    ('01995c00-0006-7000-8000-000000000002', 'INCOMING_REQUEST', 'Stated in the incoming request', 20),
    ('01995c00-0006-7000-8000-000000000003', 'INTERNAL', 'Identified internally', 30),
    ('01995c00-0006-7000-8000-000000000004', 'REGULATION', 'Standing regulation', 40);

CREATE TABLE rcs.deadline_basis (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT deadline_basis_pk PRIMARY KEY (id),
    CONSTRAINT deadline_basis_code_uq UNIQUE (code),
    CONSTRAINT deadline_basis_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT deadline_basis_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT deadline_basis_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT deadline_basis_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

INSERT INTO rcs.deadline_basis (id, code, label, sort_order) VALUES
    ('01995c00-0007-7000-8000-000000000001', 'STATUTORY', 'Statutory', 10),
    ('01995c00-0007-7000-8000-000000000002', 'INTERNAL', 'Internal', 20),
    ('01995c00-0007-7000-8000-000000000003', 'AGREED', 'Agreed', 30);

CREATE TABLE rcs.assignment_role (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT assignment_role_pk PRIMARY KEY (id),
    CONSTRAINT assignment_role_code_uq UNIQUE (code),
    CONSTRAINT assignment_role_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT assignment_role_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT assignment_role_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT assignment_role_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- DOMAIN_MODEL.md 2.7. RESPONSIBLE (row 01) is the predicate of the assignment exclusions (migration 0010).
INSERT INTO rcs.assignment_role (id, code, label, sort_order) VALUES
    ('01995c00-0008-7000-8000-000000000001', 'RESPONSIBLE', 'Responsible', 10),
    ('01995c00-0008-7000-8000-000000000002', 'CO_WORKER', 'Co-worker', 20),
    ('01995c00-0008-7000-8000-000000000003', 'SUPERVISOR', 'Supervisor', 30),
    ('01995c00-0008-7000-8000-000000000004', 'TEMPORARY_COVER', 'Temporary cover', 40),
    ('01995c00-0008-7000-8000-000000000005', 'OBSERVER', 'Observer', 50);

CREATE TABLE rcs.assignment_end_reason (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT assignment_end_reason_pk PRIMARY KEY (id),
    CONSTRAINT assignment_end_reason_code_uq UNIQUE (code),
    CONSTRAINT assignment_end_reason_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT assignment_end_reason_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT assignment_end_reason_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT assignment_end_reason_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

INSERT INTO rcs.assignment_end_reason (id, code, label, sort_order) VALUES
    ('01995c00-0009-7000-8000-000000000001', 'REASSIGNED', 'Reassigned', 10),
    ('01995c00-0009-7000-8000-000000000002', 'ABSENCE_COVER_ENDED', 'Absence cover ended', 20),
    ('01995c00-0009-7000-8000-000000000003', 'CASE_CLOSED', 'Case closed', 30),
    ('01995c00-0009-7000-8000-000000000004', 'USER_DEACTIVATED', 'User deactivated', 40),
    ('01995c00-0009-7000-8000-000000000005', 'CORRECTION', 'Correction', 50);

CREATE TABLE rcs.void_reason (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    counts_as_business_outcome boolean NOT NULL,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT void_reason_pk PRIMARY KEY (id),
    CONSTRAINT void_reason_code_uq UNIQUE (code),
    CONSTRAINT void_reason_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT void_reason_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT void_reason_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT void_reason_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- DOMAIN_MODEL.md 2.11, 2.19: reporting separates real outcomes from corrections by this flag, never by
-- the state alone.
INSERT INTO rcs.void_reason (id, code, label, counts_as_business_outcome, sort_order) VALUES
    ('01995c00-000a-7000-8000-000000000001', 'NO_LONGER_REQUIRED', 'No longer required', true, 10),
    ('01995c00-000a-7000-8000-000000000002', 'SUPERSEDED_BY_RESPONSE', 'Superseded by a later response', true, 20),
    ('01995c00-000a-7000-8000-000000000003', 'BRANCH_REMOVED', 'Workflow branch removed', true, 30),
    ('01995c00-000a-7000-8000-000000000004', 'DATA_ENTRY_ERROR', 'Data entry error', false, 40),
    ('01995c00-000a-7000-8000-000000000005', 'DUPLICATE', 'Duplicate', false, 50);

CREATE TABLE rcs.waiver_reason (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT waiver_reason_pk PRIMARY KEY (id),
    CONSTRAINT waiver_reason_code_uq UNIQUE (code),
    CONSTRAINT waiver_reason_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT waiver_reason_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT waiver_reason_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT waiver_reason_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- PROVISIONAL. The frozen documents require a waiver reason (WORKFLOW.md 5.2 Q4) but name no codes.
-- These are a starting vocabulary for product-owner review; lookup vocabularies are refined without
-- reopening the model (DOMAIN_MODEL.md 12.6). Unsuitable rows are deactivated, never deleted.
INSERT INTO rcs.waiver_reason (id, code, label, sort_order) VALUES
    ('01995c00-000b-7000-8000-000000000001', 'AUTHORISED_TO_PROCEED', 'Authorised decision to proceed without it', 10),
    ('01995c00-000b-7000-8000-000000000002', 'DISPROPORTIONATE', 'Obtaining it is disproportionate to the case', 20),
    ('01995c00-000b-7000-8000-000000000003', 'OTHER', 'Other (explained in the note)', 90);

GRANT SELECT ON rcs.organization_type, rcs.correspondence_kind, rcs.response_type, rcs.response_outcome,
    rcs.requirement_origin_type, rcs.deadline_basis, rcs.assignment_role, rcs.assignment_end_reason,
    rcs.void_reason, rcs.waiver_reason TO rcs_app;
