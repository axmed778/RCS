-- description: Case dossier header and its business lifecycle history
--
-- RCS migration 0005 - cases (DOMAIN_MODEL.md sections 2.5, 2.6; WORKFLOW.md section 1).
--
-- Physical name: the domain entity "case" is the table case_record, because CASE is a reserved word in
-- PostgreSQL and quoted identifiers are not used (DECISIONS.md ADR-037). Foreign keys keep case_id.
--
-- The case is a thin header. Responsible employee (assignment), original letter (correspondence
-- INITIATING), final result and progress are relationships or derivations - never columns
-- (decision C-3, ADR-029).
--
-- Deferred columns, added by the migration that implements their workflow (additive, no rework):
-- case_type_id, closure_type_id (and the CLOSED/CANCELLED closure-type CHECKs that depend on it).

CREATE TABLE rcs.case_record (
    id                         uuid        NOT NULL,
    case_number                text        NOT NULL,
    title                      text        NOT NULL,
    subject                    text,
    requesting_organization_id uuid        NOT NULL,
    applicant_reference        text,
    lifecycle_state            text        NOT NULL,
    hold_reason_note           text,
    hold_until                 date,
    registered_at              timestamptz NOT NULL,
    statutory_due_at           timestamptz,
    is_restricted              boolean     NOT NULL DEFAULT false,
    restriction_reason_note    text,
    restricted_by_user_id      uuid,
    restricted_at              timestamptz,
    restriction_lifted_at      timestamptz,
    closed_at                  timestamptz,
    closed_by_user_id          uuid,
    closure_note               text,
    notes                      text,
    last_activity_at           timestamptz,
    merged_into_case_id        uuid,
    created_at                 timestamptz NOT NULL DEFAULT now(),
    created_by_user_id         uuid        NOT NULL,
    updated_at                 timestamptz,
    updated_by_user_id         uuid,
    row_version                integer     NOT NULL DEFAULT 1,
    CONSTRAINT case_record_pk PRIMARY KEY (id),
    CONSTRAINT case_record_case_number_uq UNIQUE (case_number),
    CONSTRAINT case_record_title_present CHECK (btrim(title) <> ''),
    CONSTRAINT case_record_lifecycle_state_valid CHECK (lifecycle_state IN ('REGISTERED', 'ACTIVE', 'ON_HOLD', 'CLOSED', 'CANCELLED')),
    CONSTRAINT case_record_hold_reason_recorded CHECK (lifecycle_state <> 'ON_HOLD' OR hold_reason_note IS NOT NULL),
    CONSTRAINT case_record_closure_recorded CHECK (lifecycle_state NOT IN ('CLOSED', 'CANCELLED') OR (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL)),
    CONSTRAINT case_record_restriction_recorded CHECK (NOT is_restricted OR (restricted_at IS NOT NULL AND restricted_by_user_id IS NOT NULL)),
    CONSTRAINT case_record_not_merged_into_itself CHECK (merged_into_case_id IS NULL OR merged_into_case_id <> id),
    CONSTRAINT case_record_row_version_positive CHECK (row_version > 0),
    CONSTRAINT case_record_requesting_organization_fk FOREIGN KEY (requesting_organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT case_record_restricted_by_fk FOREIGN KEY (restricted_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT case_record_closed_by_fk FOREIGN KEY (closed_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT case_record_merged_into_fk FOREIGN KEY (merged_into_case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT case_record_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT case_record_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX case_record_requesting_organization_idx ON rcs.case_record (requesting_organization_id);
CREATE INDEX case_record_lifecycle_state_idx ON rcs.case_record (lifecycle_state);
CREATE INDEX case_record_registered_at_idx ON rcs.case_record (registered_at);

COMMENT ON TABLE rcs.case_record IS
    'Domain entity "case" (DOMAIN_MODEL.md 2.5). last_activity_at is a rebuildable cache, not business history (OQ-13).';

-- Business-readable lifecycle history. Append-only: lifecycle_state is a cache of the newest to_state.
-- Registration (T1) is the row with no from_state.
CREATE TABLE rcs.case_state_change (
    id            uuid        NOT NULL,
    case_id       uuid        NOT NULL,
    from_state    text,
    to_state      text        NOT NULL,
    reason_code   text,
    note          text,
    occurred_at   timestamptz NOT NULL,
    recorded_at   timestamptz NOT NULL DEFAULT now(),
    actor_user_id uuid        NOT NULL,
    CONSTRAINT case_state_change_pk PRIMARY KEY (id),
    CONSTRAINT case_state_change_from_state_valid CHECK (from_state IS NULL OR from_state IN ('REGISTERED', 'ACTIVE', 'ON_HOLD', 'CLOSED', 'CANCELLED')),
    CONSTRAINT case_state_change_to_state_valid CHECK (to_state IN ('REGISTERED', 'ACTIVE', 'ON_HOLD', 'CLOSED', 'CANCELLED')),
    CONSTRAINT case_state_change_is_a_change CHECK (from_state IS DISTINCT FROM to_state),
    CONSTRAINT case_state_change_registration_shape CHECK ((from_state IS NULL) = (to_state = 'REGISTERED')),
    CONSTRAINT case_state_change_reason_code_format CHECK (reason_code IS NULL OR reason_code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT case_state_change_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT case_state_change_actor_fk FOREIGN KEY (actor_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX case_state_change_case_idx ON rcs.case_state_change (case_id, occurred_at);

GRANT SELECT, INSERT, UPDATE ON rcs.case_record TO rcs_app;
GRANT SELECT, INSERT ON rcs.case_state_change TO rcs_app;
