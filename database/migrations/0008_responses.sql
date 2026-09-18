-- description: Responses as immutable official acts, and supersession edges between them
--
-- RCS migration 0008 - responses (DOMAIN_MODEL.md sections 2.10, 2.21; WORKFLOW.md section 4;
-- DECISIONS.md ADR-008, ADR-009, ADR-028).
--
-- response_type (what kind of communication arrived) and response_outcome (what it decided) are separate
-- axes, both required. There is no ResponseVersion: a revised letter is a new response plus a supersession
-- edge. Business facts are immutable after insert; only the status transition and its who/when/why are
-- written afterwards. One incoming letter may carry several responses (N:1 to correspondence).

CREATE TABLE rcs.response (
    id                  uuid        NOT NULL,
    request_id          uuid        NOT NULL,
    correspondence_id   uuid        NOT NULL,
    response_type_id    uuid        NOT NULL,
    response_outcome_id uuid        NOT NULL,
    is_conclusive       boolean     NOT NULL,
    summary             text,
    response_date       date,
    received_at         timestamptz,
    recorded_at         timestamptz NOT NULL DEFAULT now(),
    recorded_by_user_id uuid        NOT NULL,
    status              text        NOT NULL,
    void_reason_id      uuid,
    void_note           text,
    voided_by_user_id   uuid,
    voided_at           timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by_user_id  uuid        NOT NULL,
    updated_at          timestamptz,
    updated_by_user_id  uuid,
    row_version         integer     NOT NULL DEFAULT 1,
    CONSTRAINT response_pk PRIMARY KEY (id),
    CONSTRAINT response_id_request_uq UNIQUE (id, request_id),
    CONSTRAINT response_status_valid CHECK (status IN ('ACTIVE', 'SUPERSEDED', 'VOID')),
    CONSTRAINT response_void_recorded CHECK (status <> 'VOID' OR (voided_at IS NOT NULL AND voided_by_user_id IS NOT NULL AND void_reason_id IS NOT NULL)),
    CONSTRAINT response_row_version_positive CHECK (row_version > 0),
    CONSTRAINT response_request_fk FOREIGN KEY (request_id) REFERENCES rcs.request (id),
    CONSTRAINT response_correspondence_fk FOREIGN KEY (correspondence_id) REFERENCES rcs.correspondence (id),
    CONSTRAINT response_type_fk FOREIGN KEY (response_type_id) REFERENCES rcs.response_type (id),
    CONSTRAINT response_outcome_fk FOREIGN KEY (response_outcome_id) REFERENCES rcs.response_outcome (id),
    CONSTRAINT response_recorded_by_fk FOREIGN KEY (recorded_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_void_reason_fk FOREIGN KEY (void_reason_id) REFERENCES rcs.void_reason (id),
    CONSTRAINT response_voided_by_fk FOREIGN KEY (voided_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX response_request_idx ON rcs.response (request_id);
CREATE INDEX response_correspondence_idx ON rcs.response (correspondence_id);

-- One response may supersede several; a response has at most one ACTIVE incoming edge; same request only
-- (composite foreign keys); an edge recorded in error is RETRACTED, never deleted (amendment A-10).
CREATE TABLE rcs.response_supersession (
    id                      uuid        NOT NULL,
    superseding_response_id uuid        NOT NULL,
    superseded_response_id  uuid        NOT NULL,
    request_id              uuid        NOT NULL,
    note                    text,
    recorded_by_user_id     uuid        NOT NULL,
    recorded_at             timestamptz NOT NULL DEFAULT now(),
    status                  text        NOT NULL,
    retracted_by_user_id    uuid,
    retracted_at            timestamptz,
    retraction_note         text,
    created_at              timestamptz NOT NULL DEFAULT now(),
    created_by_user_id      uuid        NOT NULL,
    updated_at              timestamptz,
    updated_by_user_id      uuid,
    row_version             integer     NOT NULL DEFAULT 1,
    CONSTRAINT response_supersession_pk PRIMARY KEY (id),
    CONSTRAINT response_supersession_not_self CHECK (superseding_response_id <> superseded_response_id),
    CONSTRAINT response_supersession_status_valid CHECK (status IN ('ACTIVE', 'RETRACTED')),
    CONSTRAINT response_supersession_retraction_recorded CHECK (status <> 'RETRACTED' OR (retracted_at IS NOT NULL AND retracted_by_user_id IS NOT NULL)),
    CONSTRAINT response_supersession_row_version_positive CHECK (row_version > 0),
    CONSTRAINT response_supersession_superseding_fk FOREIGN KEY (superseding_response_id, request_id) REFERENCES rcs.response (id, request_id),
    CONSTRAINT response_supersession_superseded_fk FOREIGN KEY (superseded_response_id, request_id) REFERENCES rcs.response (id, request_id),
    CONSTRAINT response_supersession_recorded_by_fk FOREIGN KEY (recorded_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_supersession_retracted_by_fk FOREIGN KEY (retracted_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_supersession_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT response_supersession_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE UNIQUE INDEX response_supersession_one_active_incoming_uq ON rcs.response_supersession (superseded_response_id)
    WHERE status = 'ACTIVE';
CREATE INDEX response_supersession_superseding_idx ON rcs.response_supersession (superseding_response_id);
CREATE INDEX response_supersession_request_idx ON rcs.response_supersession (request_id);

GRANT SELECT, INSERT, UPDATE ON rcs.response, rcs.response_supersession TO rcs_app;
