-- description: Temporal assignments with per-scope non-overlapping responsibility
--
-- RCS migration 0010 - assignments (DOMAIN_MODEL.md section 2.7; PERMISSIONS.md sections 21, 22;
-- DECISIONS.md ADR-017, ADR-038).
--
-- Responsibility is a temporal table, never a responsible_user_id column. Reassignment ends a row and
-- inserts another; rows are never updated to change the assignee.
--
-- Non-overlap of RESPONSIBLE assignments (amendment A-7): one EXCLUDE constraint per scope, half-open
-- [valid_from, valid_until) intervals, ENDED rows included and only VOID rows excluded, and no empty
-- ranges. DEF-04, the row-local RESPONSIBLE predicate, is expressed as the fixed id of the seeded
-- assignment_role RESPONSIBLE (migration 0003) - no join, no duplicated column (ADR-038).

CREATE TABLE rcs.assignment (
    id                   uuid        NOT NULL,
    scope                text        NOT NULL,
    case_id              uuid,
    request_id           uuid,
    requirement_id       uuid,
    assignee_user_id     uuid        NOT NULL,
    assignment_role_id   uuid        NOT NULL,
    covers_assignment_id uuid,
    assigned_by_user_id  uuid        NOT NULL,
    valid_from           timestamptz NOT NULL,
    valid_until          timestamptz,
    end_reason_id        uuid,
    reason_note          text,
    ended_by_user_id     uuid,
    status               text        NOT NULL,
    recorded_at          timestamptz NOT NULL DEFAULT now(),
    created_at           timestamptz NOT NULL DEFAULT now(),
    created_by_user_id   uuid        NOT NULL,
    updated_at           timestamptz,
    updated_by_user_id   uuid,
    row_version          integer     NOT NULL DEFAULT 1,
    CONSTRAINT assignment_pk PRIMARY KEY (id),
    CONSTRAINT assignment_scope_valid CHECK (scope IN ('CASE', 'REQUEST', 'REQUIREMENT')),
    CONSTRAINT assignment_exclusive_arc CHECK (num_nonnulls(case_id, request_id, requirement_id) = 1),
    CONSTRAINT assignment_scope_matches_arc CHECK (
        (scope = 'CASE') = (case_id IS NOT NULL)
        AND (scope = 'REQUEST') = (request_id IS NOT NULL)
        AND (scope = 'REQUIREMENT') = (requirement_id IS NOT NULL)),
    CONSTRAINT assignment_window_not_empty CHECK (valid_until IS NULL OR valid_until > valid_from),
    CONSTRAINT assignment_status_valid CHECK (status IN ('ACTIVE', 'ENDED', 'VOID')),
    CONSTRAINT assignment_end_recorded CHECK (status <> 'ENDED' OR (valid_until IS NOT NULL AND end_reason_id IS NOT NULL)),
    CONSTRAINT assignment_not_covering_itself CHECK (covers_assignment_id IS NULL OR covers_assignment_id <> id),
    CONSTRAINT assignment_row_version_positive CHECK (row_version > 0),
    CONSTRAINT assignment_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT assignment_request_fk FOREIGN KEY (request_id) REFERENCES rcs.request (id),
    CONSTRAINT assignment_requirement_fk FOREIGN KEY (requirement_id) REFERENCES rcs.requirement (id),
    CONSTRAINT assignment_assignee_fk FOREIGN KEY (assignee_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT assignment_role_fk FOREIGN KEY (assignment_role_id) REFERENCES rcs.assignment_role (id),
    CONSTRAINT assignment_covers_fk FOREIGN KEY (covers_assignment_id) REFERENCES rcs.assignment (id),
    CONSTRAINT assignment_assigned_by_fk FOREIGN KEY (assigned_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT assignment_end_reason_fk FOREIGN KEY (end_reason_id) REFERENCES rcs.assignment_end_reason (id),
    CONSTRAINT assignment_ended_by_fk FOREIGN KEY (ended_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT assignment_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT assignment_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT assignment_case_responsible_excl EXCLUDE USING gist (
        case_id WITH =, tstzrange(valid_from, valid_until, '[)') WITH &&)
        WHERE (case_id IS NOT NULL AND assignment_role_id = '01995c00-0008-7000-8000-000000000001' AND status <> 'VOID'),
    CONSTRAINT assignment_request_responsible_excl EXCLUDE USING gist (
        request_id WITH =, tstzrange(valid_from, valid_until, '[)') WITH &&)
        WHERE (request_id IS NOT NULL AND assignment_role_id = '01995c00-0008-7000-8000-000000000001' AND status <> 'VOID'),
    CONSTRAINT assignment_requirement_responsible_excl EXCLUDE USING gist (
        requirement_id WITH =, tstzrange(valid_from, valid_until, '[)') WITH &&)
        WHERE (requirement_id IS NOT NULL AND assignment_role_id = '01995c00-0008-7000-8000-000000000001' AND status <> 'VOID')
);

CREATE INDEX assignment_assignee_idx ON rcs.assignment (assignee_user_id);

GRANT SELECT, INSERT, UPDATE ON rcs.assignment TO rcs_app;
