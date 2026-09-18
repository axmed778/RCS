-- description: Final results, decision and closure vocabularies, and the case closure type
--
-- RCS migration 0013 - the decision the case exists to produce (DOMAIN_MODEL.md section 2.16;
-- WORKFLOW.md sections 8, 9; DECISIONS.md ADR-013, ADR-040).
--
-- A final result is a separate entity, never a text column on the case: it carries its own decision type,
-- its approval metadata and its dispatch letter, and it can be superseded or revoked while the earlier one
-- stays fully readable. At most one result is ISSUED at a time - a partial unique index, so a replacement
-- can only ever be issued in the same transaction that supersedes the result it names (ADR-013).
--
-- Approval (ADR-040, closing OB-1): exactly ONE approval is required and it is the Head's. That is an
-- authorization rule, not a schema rule, so no CHECK compares approved_by_user_id with decided_by_user_id -
-- the Head may also be the decision-maker. What the schema does require is that an ISSUED result records
-- both acts and when they happened.

CREATE TABLE rcs.decision_type (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT decision_type_pk PRIMARY KEY (id),
    CONSTRAINT decision_type_code_uq UNIQUE (code),
    CONSTRAINT decision_type_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT decision_type_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT decision_type_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT decision_type_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- The frozen vocabulary of DOMAIN_MODEL.md 2.16.
INSERT INTO rcs.decision_type (id, code, label, sort_order) VALUES
    ('01995c00-000c-7000-8000-000000000001', 'APPROVAL', 'Approval', 10),
    ('01995c00-000c-7000-8000-000000000002', 'PARTIAL_APPROVAL', 'Partial approval', 20),
    ('01995c00-000c-7000-8000-000000000003', 'REFUSAL', 'Refusal', 30),
    ('01995c00-000c-7000-8000-000000000004', 'RETURNED_WITHOUT_REVIEW', 'Returned without review', 40),
    ('01995c00-000c-7000-8000-000000000005', 'TERMINATED', 'Terminated', 50);

-- How a case ended. produces_decision carries closure guard G3 (WORKFLOW.md 9.2): a closure type that does
-- not produce a decision - withdrawn by the requester, duplicate, merged, registered in error - does not
-- require an ISSUED final result. It is a row-local attribute so the guard needs no join at evaluation time.
CREATE TABLE rcs.closure_type (
    id uuid NOT NULL, code text NOT NULL, label text NOT NULL, description text,
    produces_decision boolean NOT NULL,
    is_cancellation boolean NOT NULL DEFAULT false,
    sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT true, valid_from date, valid_to date,
    created_at timestamptz NOT NULL DEFAULT now(), created_by_user_id uuid, updated_at timestamptz, updated_by_user_id uuid,
    row_version integer NOT NULL DEFAULT 1,
    CONSTRAINT closure_type_pk PRIMARY KEY (id),
    CONSTRAINT closure_type_code_uq UNIQUE (code),
    CONSTRAINT closure_type_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$'),
    CONSTRAINT closure_type_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT closure_type_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT closure_type_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

INSERT INTO rcs.closure_type (id, code, label, produces_decision, is_cancellation, sort_order) VALUES
    ('01995c00-000d-7000-8000-000000000001', 'COMPLETED', 'Completed with a decision', true, false, 10),
    ('01995c00-000d-7000-8000-000000000002', 'WITHDRAWN_BY_REQUESTER', 'Withdrawn by the requester', false, true, 20),
    ('01995c00-000d-7000-8000-000000000003', 'DUPLICATE', 'Duplicate dossier', false, true, 30),
    ('01995c00-000d-7000-8000-000000000004', 'MERGED', 'Merged into another case', false, true, 40),
    ('01995c00-000d-7000-8000-000000000005', 'REGISTERED_IN_ERROR', 'Registered in error', false, true, 50);

CREATE TABLE rcs.final_result (
    id                         uuid        NOT NULL,
    case_id                    uuid        NOT NULL,
    result_number              text        NOT NULL,
    decision_type_id           uuid        NOT NULL,
    summary                    text        NOT NULL,
    reasoning                  text,
    decided_at                 timestamptz,
    decided_by_user_id         uuid,
    approved_at                timestamptz,
    approved_by_user_id        uuid,
    issued_at                  timestamptz,
    dispatch_correspondence_id uuid,
    status                     text        NOT NULL,
    supersedes_final_result_id uuid,
    revoked_at                 timestamptz,
    revoked_by_user_id         uuid,
    revocation_reason_note     text,
    void_reason_id             uuid,
    void_note                  text,
    voided_by_user_id          uuid,
    voided_at                  timestamptz,
    created_at                 timestamptz NOT NULL DEFAULT now(),
    created_by_user_id         uuid        NOT NULL,
    updated_at                 timestamptz,
    updated_by_user_id         uuid,
    row_version                integer     NOT NULL DEFAULT 1,
    CONSTRAINT final_result_pk PRIMARY KEY (id),
    CONSTRAINT final_result_result_number_uq UNIQUE (result_number),
    CONSTRAINT final_result_id_case_uq UNIQUE (id, case_id),
    CONSTRAINT final_result_summary_present CHECK (btrim(summary) <> ''),
    CONSTRAINT final_result_status_valid CHECK (status IN ('DRAFT', 'ISSUED', 'SUPERSEDED', 'REVOKED', 'VOID')),
    -- Guards D2 and D3 (WORKFLOW.md 8.3): an issued result records who decided it, who approved it, and when.
    CONSTRAINT final_result_issue_recorded CHECK (
        status IN ('DRAFT', 'VOID')
        OR (decided_at IS NOT NULL AND decided_by_user_id IS NOT NULL
            AND approved_at IS NOT NULL AND approved_by_user_id IS NOT NULL
            AND issued_at IS NOT NULL)),
    CONSTRAINT final_result_revocation_recorded CHECK (
        status <> 'REVOKED' OR (revoked_at IS NOT NULL AND revoked_by_user_id IS NOT NULL AND revocation_reason_note IS NOT NULL)),
    CONSTRAINT final_result_void_recorded CHECK (
        status <> 'VOID' OR (voided_at IS NOT NULL AND voided_by_user_id IS NOT NULL AND void_reason_id IS NOT NULL)),
    CONSTRAINT final_result_not_superseding_itself CHECK (supersedes_final_result_id IS NULL OR supersedes_final_result_id <> id),
    CONSTRAINT final_result_row_version_positive CHECK (row_version > 0),
    CONSTRAINT final_result_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT final_result_decision_type_fk FOREIGN KEY (decision_type_id) REFERENCES rcs.decision_type (id),
    CONSTRAINT final_result_decided_by_fk FOREIGN KEY (decided_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT final_result_approved_by_fk FOREIGN KEY (approved_by_user_id) REFERENCES rcs.app_user (id),
    -- The dispatch letter, when there is one, belongs to the same case.
    CONSTRAINT final_result_dispatch_fk FOREIGN KEY (dispatch_correspondence_id, case_id) REFERENCES rcs.correspondence (id, case_id),
    -- A replacement may only supersede a result of its own case (ADR-013).
    CONSTRAINT final_result_supersedes_fk FOREIGN KEY (supersedes_final_result_id, case_id) REFERENCES rcs.final_result (id, case_id),
    CONSTRAINT final_result_revoked_by_fk FOREIGN KEY (revoked_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT final_result_void_reason_fk FOREIGN KEY (void_reason_id) REFERENCES rcs.void_reason (id),
    CONSTRAINT final_result_voided_by_fk FOREIGN KEY (voided_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT final_result_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT final_result_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- At most one result in force per case; superseded and revoked ones stay, fully readable.
CREATE UNIQUE INDEX final_result_one_issued_per_case_uq ON rcs.final_result (case_id) WHERE status = 'ISSUED';
CREATE INDEX final_result_case_idx ON rcs.final_result (case_id);

-- Closure metadata on the case: the deferred column of migration 0005 arrives with the workflow that uses it.
ALTER TABLE rcs.case_record ADD COLUMN closure_type_id uuid;
ALTER TABLE rcs.case_record ADD CONSTRAINT case_record_closure_type_fk FOREIGN KEY (closure_type_id) REFERENCES rcs.closure_type (id);
ALTER TABLE rcs.case_record DROP CONSTRAINT case_record_closure_recorded;
ALTER TABLE rcs.case_record ADD CONSTRAINT case_record_closure_recorded CHECK (
    lifecycle_state NOT IN ('CLOSED', 'CANCELLED')
    OR (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL AND closure_type_id IS NOT NULL));

GRANT SELECT ON rcs.decision_type, rcs.closure_type TO rcs_app;
GRANT SELECT, INSERT, UPDATE ON rcs.final_result TO rcs_app;
