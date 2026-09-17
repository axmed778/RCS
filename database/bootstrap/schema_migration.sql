-- RCS migration bootstrap
--
-- Executed by the migration runner (`Rcs.Web migrate`) as the migration role, inside the migration
-- lock and inside a transaction, BEFORE any migration is considered. It must stay idempotent.
--
-- It creates only what the runner needs to read and write migration history. Every application
-- object belongs in a numbered migration under database/migrations. Once released, this file does
-- not change: any later change to the history table is itself a numbered migration.
--
-- Prerequisites (database/README.md): database/roles/roles.sql has been run, and the database is
-- owned by rcs_migrate.

CREATE SCHEMA IF NOT EXISTS rcs;

COMMENT ON SCHEMA rcs IS
    'RCS application schema. Owned by rcs_migrate and changed only by hand-written migrations (DECISIONS.md ADR-004).';

CREATE TABLE IF NOT EXISTS rcs.schema_migration (
    migration_id    integer     NOT NULL,
    name            text        NOT NULL,
    description     text        NOT NULL,
    checksum_sha256 text        NOT NULL,
    transactional   boolean     NOT NULL,
    applied_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_by      text        NOT NULL DEFAULT current_user,
    execution_ms    bigint      NOT NULL,
    runner_version  text        NOT NULL,
    CONSTRAINT schema_migration_pk PRIMARY KEY (migration_id),
    CONSTRAINT schema_migration_id_positive CHECK (migration_id > 0),
    CONSTRAINT schema_migration_name_format CHECK (name ~ '^[a-z0-9]+(_[a-z0-9]+)*$'),
    CONSTRAINT schema_migration_description_present CHECK (description <> ''),
    CONSTRAINT schema_migration_checksum_format CHECK (checksum_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT schema_migration_execution_ms_nonnegative CHECK (execution_ms >= 0)
);

COMMENT ON TABLE rcs.schema_migration IS
    'One row per applied migration. The schema version is the highest contiguous migration_id. Written only by the migration runner; checksums detect edits to applied migrations.';

-- The runtime role reads history to verify schema compatibility at startup. It never writes it.
GRANT USAGE ON SCHEMA rcs TO rcs_app;
GRANT SELECT ON rcs.schema_migration TO rcs_app;
