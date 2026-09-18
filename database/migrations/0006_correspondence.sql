-- description: Official correspondence records (letters sent and received externally)
--
-- RCS migration 0006 - correspondence (DOMAIN_MODEL.md section 2.8; WORKFLOW.md sections 0, 2, 3.1, 4.1).
--
-- This application never sends or receives letters (DECISIONS.md ADR-006). A correspondence row records
-- an official communication that already happened in the external government system: its numbers and
-- dates are recorded, never generated. Business time (letter_date, sent_at, received_at) and system time
-- (registered_at, created_at) are separate columns.
--
-- Request and response link to their letter (decision C-1); there is no related_request_id here and none
-- may be added. One owning case per letter (OQ-10: case_id NOT NULL in V1).
--
-- Numbers (OQ-7, provisional): letter_number is the counterparty's number printed on the letter and is not
-- unique. registry_number is the number the external registry assigned; it is assumed unique per direction
-- (incoming and outgoing registries may overlap) and is never reused, including by voided letters.
--
-- Deferred columns: delivery_method_id, withdrawal_reason_id (with their lookups).

CREATE TABLE rcs.correspondence (
    id                           uuid        NOT NULL,
    case_id                      uuid        NOT NULL,
    direction                    text        NOT NULL,
    correspondence_kind_id       uuid        NOT NULL,
    sender_organization_id       uuid        NOT NULL,
    recipient_organization_id    uuid        NOT NULL,
    letter_number                text,
    letter_date                  date,
    registry_number              text,
    registered_at                timestamptz NOT NULL DEFAULT now(),
    registered_by_user_id        uuid        NOT NULL,
    sent_at                      timestamptz,
    received_at                  timestamptz,
    subject                      text,
    summary                      text,
    parent_correspondence_id     uuid,
    status                       text        NOT NULL,
    supersedes_correspondence_id uuid,
    withdrawn_at                 timestamptz,
    withdrawn_by_user_id         uuid,
    withdrawal_note              text,
    void_reason_id               uuid,
    void_note                    text,
    voided_by_user_id            uuid,
    voided_at                    timestamptz,
    created_at                   timestamptz NOT NULL DEFAULT now(),
    created_by_user_id           uuid        NOT NULL,
    updated_at                   timestamptz,
    updated_by_user_id           uuid,
    row_version                  integer     NOT NULL DEFAULT 1,
    CONSTRAINT correspondence_pk PRIMARY KEY (id),
    CONSTRAINT correspondence_id_case_uq UNIQUE (id, case_id),
    CONSTRAINT correspondence_id_case_recipient_uq UNIQUE (id, case_id, recipient_organization_id),
    CONSTRAINT correspondence_direction_valid CHECK (direction IN ('IN', 'OUT')),
    CONSTRAINT correspondence_status_valid CHECK (status IN ('DRAFT', 'REGISTERED', 'SENT', 'RECEIVED', 'WITHDRAWN', 'SUPERSEDED', 'VOID')),
    CONSTRAINT correspondence_parties_differ CHECK (sender_organization_id <> recipient_organization_id),
    CONSTRAINT correspondence_sent_recorded CHECK (status <> 'SENT' OR (direction = 'OUT' AND sent_at IS NOT NULL)),
    CONSTRAINT correspondence_received_recorded CHECK (status <> 'RECEIVED' OR (direction = 'IN' AND received_at IS NOT NULL)),
    CONSTRAINT correspondence_withdrawal_recorded CHECK (status <> 'WITHDRAWN' OR (withdrawn_at IS NOT NULL AND withdrawn_by_user_id IS NOT NULL AND withdrawal_note IS NOT NULL)),
    CONSTRAINT correspondence_void_recorded CHECK (status <> 'VOID' OR (voided_at IS NOT NULL AND voided_by_user_id IS NOT NULL AND void_reason_id IS NOT NULL)),
    CONSTRAINT correspondence_not_own_parent CHECK (parent_correspondence_id IS NULL OR parent_correspondence_id <> id),
    CONSTRAINT correspondence_not_superseding_itself CHECK (supersedes_correspondence_id IS NULL OR supersedes_correspondence_id <> id),
    CONSTRAINT correspondence_row_version_positive CHECK (row_version > 0),
    CONSTRAINT correspondence_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id),
    CONSTRAINT correspondence_kind_fk FOREIGN KEY (correspondence_kind_id) REFERENCES rcs.correspondence_kind (id),
    CONSTRAINT correspondence_sender_fk FOREIGN KEY (sender_organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT correspondence_recipient_fk FOREIGN KEY (recipient_organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT correspondence_registered_by_fk FOREIGN KEY (registered_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT correspondence_parent_fk FOREIGN KEY (parent_correspondence_id) REFERENCES rcs.correspondence (id),
    CONSTRAINT correspondence_supersedes_fk FOREIGN KEY (supersedes_correspondence_id) REFERENCES rcs.correspondence (id),
    CONSTRAINT correspondence_withdrawn_by_fk FOREIGN KEY (withdrawn_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT correspondence_void_reason_fk FOREIGN KEY (void_reason_id) REFERENCES rcs.void_reason (id),
    CONSTRAINT correspondence_voided_by_fk FOREIGN KEY (voided_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT correspondence_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT correspondence_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- At most one initiating letter per case (DOMAIN_MODEL.md 2.5) - a partial unique index instead of a
-- circular case <-> correspondence foreign key. The predicate is the fixed id of correspondence_kind
-- INITIATING (migration 0003, DECISIONS.md ADR-038).
CREATE UNIQUE INDEX correspondence_one_initiating_per_case_uq ON rcs.correspondence (case_id)
    WHERE correspondence_kind_id = '01995c00-0003-7000-8000-000000000001';

CREATE UNIQUE INDEX correspondence_registry_number_uq ON rcs.correspondence (direction, registry_number)
    WHERE registry_number IS NOT NULL;

CREATE INDEX correspondence_case_idx ON rcs.correspondence (case_id);
CREATE INDEX correspondence_letter_number_idx ON rcs.correspondence (letter_number);
CREATE INDEX correspondence_parent_idx ON rcs.correspondence (parent_correspondence_id);

GRANT SELECT, INSERT, UPDATE ON rcs.correspondence TO rcs_app;
