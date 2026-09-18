-- description: Requests - obligations tracked against external organizations
--
-- RCS migration 0007 - requests (DOMAIN_MODEL.md section 2.9; WORKFLOW.md section 3).
--
-- A request is a work item carried by an outgoing letter, not the letter (decision C-1): several
-- requests may share one dispatch correspondence. SENT means "officially issued in the external system",
-- established by registering that letter - never by a send action (ADR-006). There is no sent_at column:
-- the sent date is the dispatch correspondence's sent_at. There is no responsible-user column: that is
-- an assignment. OVERDUE is never stored.
--
-- Causality: source_requirement_id NULL = top-level request; set = child request created to satisfy that
-- requirement. Its foreign key - which also proves the requirement is in the same case - is added with the
-- requirement table (migration 0009). The pointer is write-once (DOMAIN_MODEL.md 6.4).

CREATE TABLE rcs.request (
    id                         uuid        NOT NULL,
    case_id                    uuid        NOT NULL,
    request_number             text        NOT NULL,
    target_organization_id     uuid        NOT NULL,
    source_requirement_id      uuid,
    dispatch_correspondence_id uuid,
    subject                    text        NOT NULL,
    requested_items_note       text,
    due_at                     timestamptz,
    original_due_at            timestamptz,
    deadline_basis_id          uuid,
    deadline_note              text,
    status                     text        NOT NULL,
    closed_at                  timestamptz,
    closed_by_user_id          uuid,
    closure_note               text,
    withdrawn_at               timestamptz,
    withdrawn_by_user_id       uuid,
    withdrawal_note            text,
    void_reason_id             uuid,
    void_note                  text,
    voided_by_user_id          uuid,
    voided_at                  timestamptz,
    created_at                 timestamptz NOT NULL DEFAULT now(),
    created_by_user_id         uuid        NOT NULL,
    updated_at                 timestamptz,
    updated_by_user_id         uuid,
    row_version                integer     NOT NULL DEFAULT 1,
    CONSTRAINT request_pk PRIMARY KEY (id),
    CONSTRAINT request_request_number_uq UNIQUE (request_number),
    CONSTRAINT request_id_case_uq UNIQUE (id, case_id),
    CONSTRAINT request_subject_present CHECK (btrim(subject) <> ''),
    CONSTRAINT request_status_valid CHECK (status IN ('DRAFT', 'SENT', 'ANSWERED', 'CLOSED', 'WITHDRAWN', 'VOID')),
    -- Anything beyond DRAFT that was issued has its registered dispatch letter.
    CONSTRAINT request_issued_has_dispatch CHECK (status IN ('DRAFT', 'WITHDRAWN', 'VOID') OR dispatch_correspondence_id IS NOT NULL),
    CONSTRAINT request_closure_recorded CHECK (status <> 'CLOSED' OR (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL)),
    CONSTRAINT request_withdrawal_recorded CHECK (status <> 'WITHDRAWN' OR (withdrawn_at IS NOT NULL AND withdrawn_by_user_id IS NOT NULL AND withdrawal_note IS NOT NULL)),
    CONSTRAINT request_void_recorded CHECK (status <> 'VOID' OR (voided_at IS NOT NULL AND voided_by_user_id IS NOT NULL AND void_reason_id IS NOT NULL)),
    CONSTRAINT request_original_due_kept CHECK (original_due_at IS NULL OR due_at IS NOT NULL),
    CONSTRAINT request_row_version_positive CHECK (row_version > 0),
    CONSTRAINT request_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT request_target_organization_fk FOREIGN KEY (target_organization_id) REFERENCES rcs.organization (id),
    -- The dispatch letter belongs to the same case and is addressed to the request's target organization.
    CONSTRAINT request_dispatch_correspondence_fk FOREIGN KEY (dispatch_correspondence_id, case_id, target_organization_id)
        REFERENCES rcs.correspondence (id, case_id, recipient_organization_id),
    CONSTRAINT request_deadline_basis_fk FOREIGN KEY (deadline_basis_id) REFERENCES rcs.deadline_basis (id),
    CONSTRAINT request_closed_by_fk FOREIGN KEY (closed_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT request_withdrawn_by_fk FOREIGN KEY (withdrawn_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT request_void_reason_fk FOREIGN KEY (void_reason_id) REFERENCES rcs.void_reason (id),
    CONSTRAINT request_voided_by_fk FOREIGN KEY (voided_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT request_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT request_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX request_case_idx ON rcs.request (case_id);
CREATE INDEX request_target_organization_idx ON rcs.request (target_organization_id);
CREATE INDEX request_source_requirement_idx ON rcs.request (source_requirement_id);
CREATE INDEX request_dispatch_correspondence_idx ON rcs.request (dispatch_correspondence_id);

GRANT SELECT, INSERT, UPDATE ON rcs.request TO rcs_app;
