-- description: Durable operation receipts so a retried command executes at most once
--
-- RCS migration 0012 - operation receipts (DECISIONS.md ADR-020, DEF-03 decided in ADR-039;
-- ARCHITECTURE.md section 12.6).
--
-- A technical table, not a domain entity. The operation id is generated once when a form is prepared and
-- is recorded in the same transaction as the command's effects. A retry presenting a recorded id returns
-- the original result and executes nothing; a retry by a different actor is refused. Retention of rows is
-- not decided; nothing deletes them (the runtime role has no DELETE).

CREATE TABLE rcs.operation_receipt (
    operation_id     uuid        NOT NULL,
    actor_user_id    uuid        NOT NULL,
    operation_kind   text        NOT NULL,
    result_reference text        NOT NULL,
    recorded_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT operation_receipt_pk PRIMARY KEY (operation_id),
    CONSTRAINT operation_receipt_kind_format CHECK (operation_kind ~ '^[a-z][a-z_.]*$'),
    CONSTRAINT operation_receipt_result_present CHECK (result_reference <> ''),
    CONSTRAINT operation_receipt_actor_fk FOREIGN KEY (actor_user_id) REFERENCES rcs.app_user (id)
);

GRANT SELECT, INSERT ON rcs.operation_receipt TO rcs_app;
