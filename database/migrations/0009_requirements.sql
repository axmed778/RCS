-- description: Requirements, their evidence, and the child-request causal link
--
-- RCS migration 0009 - requirements (DOMAIN_MODEL.md sections 2.11, 2.12, 6; WORKFLOW.md section 5).
--
-- A requirement is the branch point of the workflow: raised by a response, owning a lifecycle, and
-- spawning child requests (request.source_requirement_id). FULFILLED, WAIVED, VOID and FAILED are four
-- distinct terminal outcomes; none stands in for another, and each carries its own who/why.
-- resolved_at / resolved_by_user_id record when and by whom any terminal state was recorded.
--
-- case_id is the one authorised denormalised scope (DOMAIN_MODEL.md 1.4). "source_response.request.case_id
-- = case_id" spans two hops and is enforced by the application (and tested).
--
-- Deferred: requirement_resolution_correction (Q7, ADR-010) arrives with the correction workflow; the
-- document and internal-record arcs of requirement_evidence arrive with those tables (the arc CHECK below
-- is then replaced by the full exclusive arc).

CREATE TABLE rcs.requirement (
    id                           uuid        NOT NULL,
    case_id                      uuid        NOT NULL,
    requirement_origin_type_id   uuid        NOT NULL,
    source_response_id           uuid,
    raised_by_organization_id    uuid,
    addressed_to_organization_id uuid,
    title                        text        NOT NULL,
    description                  text,
    is_blocking                  boolean     NOT NULL DEFAULT true,
    raised_at                    timestamptz NOT NULL,
    due_at                       timestamptz,
    deadline_basis_id            uuid,
    status                       text        NOT NULL,
    started_at                   timestamptz,
    resolved_at                  timestamptz,
    resolved_by_user_id          uuid,
    resolution_note              text,
    waiver_authorised_by_user_id uuid,
    waiver_reason_id             uuid,
    void_reason_id               uuid,
    voided_by_user_id            uuid,
    void_source_response_id      uuid,
    failure_reason_note          text,
    created_at                   timestamptz NOT NULL DEFAULT now(),
    created_by_user_id           uuid        NOT NULL,
    updated_at                   timestamptz,
    updated_by_user_id           uuid,
    row_version                  integer     NOT NULL DEFAULT 1,
    CONSTRAINT requirement_pk PRIMARY KEY (id),
    CONSTRAINT requirement_id_case_uq UNIQUE (id, case_id),
    CONSTRAINT requirement_title_present CHECK (btrim(title) <> ''),
    CONSTRAINT requirement_status_valid CHECK (status IN ('OPEN', 'IN_PROGRESS', 'FULFILLED', 'WAIVED', 'VOID', 'FAILED')),
    -- origin RESPONSE (fixed lookup id, DECISIONS.md ADR-038) requires the response that imposed it.
    CONSTRAINT requirement_response_origin_has_source CHECK (
        requirement_origin_type_id <> '01995c00-0006-7000-8000-000000000001' OR source_response_id IS NOT NULL),
    CONSTRAINT requirement_started_recorded CHECK (status <> 'IN_PROGRESS' OR started_at IS NOT NULL),
    CONSTRAINT requirement_terminal_resolution_recorded CHECK (
        status IN ('OPEN', 'IN_PROGRESS') OR (resolved_at IS NOT NULL AND resolved_by_user_id IS NOT NULL)),
    CONSTRAINT requirement_open_has_no_resolution CHECK (
        status NOT IN ('OPEN', 'IN_PROGRESS') OR (resolved_at IS NULL AND resolved_by_user_id IS NULL)),
    CONSTRAINT requirement_waiver_recorded CHECK (
        status <> 'WAIVED' OR (waiver_authorised_by_user_id IS NOT NULL AND waiver_reason_id IS NOT NULL AND resolution_note IS NOT NULL)),
    CONSTRAINT requirement_void_recorded CHECK (
        status <> 'VOID' OR (void_reason_id IS NOT NULL AND voided_by_user_id IS NOT NULL AND resolution_note IS NOT NULL)),
    CONSTRAINT requirement_failure_recorded CHECK (status <> 'FAILED' OR failure_reason_note IS NOT NULL),
    CONSTRAINT requirement_row_version_positive CHECK (row_version > 0),
    CONSTRAINT requirement_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT requirement_origin_type_fk FOREIGN KEY (requirement_origin_type_id) REFERENCES rcs.requirement_origin_type (id),
    CONSTRAINT requirement_source_response_fk FOREIGN KEY (source_response_id) REFERENCES rcs.response (id),
    CONSTRAINT requirement_raised_by_fk FOREIGN KEY (raised_by_organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT requirement_addressed_to_fk FOREIGN KEY (addressed_to_organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT requirement_deadline_basis_fk FOREIGN KEY (deadline_basis_id) REFERENCES rcs.deadline_basis (id),
    CONSTRAINT requirement_resolved_by_fk FOREIGN KEY (resolved_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_waiver_authorised_by_fk FOREIGN KEY (waiver_authorised_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_waiver_reason_fk FOREIGN KEY (waiver_reason_id) REFERENCES rcs.waiver_reason (id),
    CONSTRAINT requirement_void_reason_fk FOREIGN KEY (void_reason_id) REFERENCES rcs.void_reason (id),
    CONSTRAINT requirement_voided_by_fk FOREIGN KEY (voided_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_void_source_response_fk FOREIGN KEY (void_source_response_id) REFERENCES rcs.response (id),
    CONSTRAINT requirement_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX requirement_case_idx ON rcs.requirement (case_id);
CREATE INDEX requirement_source_response_idx ON rcs.requirement (source_response_id);
CREATE INDEX requirement_status_idx ON rcs.requirement (status);

-- Child requests: the causal pointer, and proof that the requirement belongs to the request's own case
-- (DOMAIN_MODEL.md 6.4 rule 4).
ALTER TABLE rcs.request
    ADD CONSTRAINT request_source_requirement_fk FOREIGN KEY (source_requirement_id, case_id)
        REFERENCES rcs.requirement (id, case_id);

-- What proves a requirement was met. Retracted, never deleted.
CREATE TABLE rcs.requirement_evidence (
    id                   uuid        NOT NULL,
    requirement_id       uuid        NOT NULL,
    evidence_type        text        NOT NULL,
    response_id          uuid,
    note                 text,
    is_primary           boolean     NOT NULL DEFAULT false,
    recorded_by_user_id  uuid        NOT NULL,
    recorded_at          timestamptz NOT NULL DEFAULT now(),
    status               text        NOT NULL,
    retracted_by_user_id uuid,
    retracted_at         timestamptz,
    retraction_note      text,
    created_at           timestamptz NOT NULL DEFAULT now(),
    created_by_user_id   uuid        NOT NULL,
    updated_at           timestamptz,
    updated_by_user_id   uuid,
    row_version          integer     NOT NULL DEFAULT 1,
    CONSTRAINT requirement_evidence_pk PRIMARY KEY (id),
    CONSTRAINT requirement_evidence_type_valid CHECK (evidence_type IN ('RESPONSE', 'DOCUMENT', 'INTERNAL_ACT')),
    -- Until document and internal_record exist, the only arc is a response.
    CONSTRAINT requirement_evidence_arc CHECK (evidence_type = 'RESPONSE' AND response_id IS NOT NULL),
    CONSTRAINT requirement_evidence_status_valid CHECK (status IN ('ACTIVE', 'RETRACTED')),
    CONSTRAINT requirement_evidence_retraction_recorded CHECK (
        status <> 'RETRACTED' OR (retracted_at IS NOT NULL AND retracted_by_user_id IS NOT NULL AND retraction_note IS NOT NULL)),
    CONSTRAINT requirement_evidence_row_version_positive CHECK (row_version > 0),
    CONSTRAINT requirement_evidence_requirement_fk FOREIGN KEY (requirement_id) REFERENCES rcs.requirement (id),
    CONSTRAINT requirement_evidence_response_fk FOREIGN KEY (response_id) REFERENCES rcs.response (id),
    CONSTRAINT requirement_evidence_recorded_by_fk FOREIGN KEY (recorded_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_evidence_retracted_by_fk FOREIGN KEY (retracted_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_evidence_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT requirement_evidence_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX requirement_evidence_requirement_idx ON rcs.requirement_evidence (requirement_id);
CREATE INDEX requirement_evidence_response_idx ON rcs.requirement_evidence (response_id);

GRANT SELECT, INSERT, UPDATE ON rcs.requirement, rcs.requirement_evidence TO rcs_app;
