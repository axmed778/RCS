-- description: Append-only audit log, INSERT and SELECT only for the runtime role
--
-- RCS migration 0011 - audit (DOMAIN_MODEL.md section 2.18; WORKFLOW.md section 13; SECURITY.md section 9;
-- DECISIONS.md ADR-018).
--
-- event_seq is the ordering authority; gaps are normal (identity values are consumed by rolled-back
-- transactions) and are not evidence of tampering. actor_user_id is the initiating person and is empty
-- only for JOB events; actor_kind records how the change was executed (amendment A-8).
--
-- entity_type + entity_id is the one documented exception to "no generic polymorphic reference". It holds
-- DOMAIN entity names ('case', 'user', 'request', ...), not physical table names (ADR-037).
--
-- The runtime role receives INSERT and SELECT only - never UPDATE, DELETE or TRUNCATE. This is the
-- database guarantee that ordinary users, and a compromised application, cannot rewrite history.

CREATE TABLE rcs.audit_event (
    event_seq                   bigint      GENERATED ALWAYS AS IDENTITY,
    id                          uuid        NOT NULL,
    occurred_at                 timestamptz NOT NULL,
    recorded_at                 timestamptz NOT NULL DEFAULT clock_timestamp(),
    actor_user_id               uuid,
    actor_kind                  text        NOT NULL,
    actor_username_snapshot     text,
    actor_display_name_snapshot text,
    actor_roles_snapshot        text[],
    session_id                  text,
    client_host                 text,
    action_code                 text        NOT NULL,
    entity_type                 text        NOT NULL,
    entity_id                   uuid        NOT NULL,
    entity_version              integer,
    case_id                     uuid,
    before_state                jsonb,
    after_state                 jsonb,
    changed_fields              text[],
    document_hash               text,
    reason_note                 text,
    correlation_id              uuid        NOT NULL,
    CONSTRAINT audit_event_pk PRIMARY KEY (event_seq),
    CONSTRAINT audit_event_id_uq UNIQUE (id),
    CONSTRAINT audit_event_actor_kind_valid CHECK (actor_kind IN ('USER', 'SYSTEM', 'JOB')),
    CONSTRAINT audit_event_actor_present_unless_job CHECK ((actor_kind = 'JOB') = (actor_user_id IS NULL)),
    CONSTRAINT audit_event_action_code_valid CHECK (action_code IN (
        'CREATE', 'UPDATE', 'STATE_CHANGE', 'LINK', 'UNLINK', 'UPLOAD', 'DOWNLOAD', 'WITHDRAW', 'VOID',
        'ASSIGN', 'LOGIN', 'LOGOUT', 'EXPORT', 'PRINT', 'PERMISSION_DENIED')),
    CONSTRAINT audit_event_entity_type_format CHECK (entity_type ~ '^[a-z][a-z_]*$'),
    CONSTRAINT audit_event_document_hash_format CHECK (document_hash IS NULL OR document_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT audit_event_actor_fk FOREIGN KEY (actor_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT audit_event_case_fk FOREIGN KEY (case_id) REFERENCES rcs.case_record (id)
);

CREATE INDEX audit_event_case_idx ON rcs.audit_event (case_id, event_seq);
CREATE INDEX audit_event_entity_idx ON rcs.audit_event (entity_type, entity_id);
CREATE INDEX audit_event_correlation_idx ON rcs.audit_event (correlation_id);

GRANT SELECT, INSERT ON rcs.audit_event TO rcs_app;
