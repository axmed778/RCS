-- RCS PostgreSQL roles (cluster level)
--
-- Run by a database administrator (superuser, or a role with CREATEROLE) once per PostgreSQL cluster,
-- before the first migration. Idempotent: safe to re-run. Sources: ARCHITECTURE.md section 7.2,
-- SECURITY.md section 12.2, DECISIONS.md ADR-004 and ADR-018.
--
-- This script sets NO passwords and contains no secrets. Set passwords interactively, outside source
-- control, for example in psql:
--     \password rcs_migrate
--     \password rcs_app
--
-- Administrators: no shared "rcs_admin" login is created. Each administrator uses an individually named
-- superuser login created at installation (SECURITY.md P9 - every user is an individual). The application
-- never holds administrator credentials.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'rcs_migrate') THEN
        CREATE ROLE rcs_migrate LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'rcs_app') THEN
        CREATE ROLE rcs_app LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'rcs_backup') THEN
        CREATE ROLE rcs_backup NOLOGIN;
    END IF;
END
$$;

-- Migration / deployment role. Owns the application database and the rcs schema, runs migrations,
-- installs approved trusted extensions. Never used by the running application.
ALTER ROLE rcs_migrate WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE rcs_migrate SET search_path = rcs;

-- Runtime role. Owns nothing, creates nothing; receives only the table privileges each migration grants.
ALTER ROLE rcs_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE rcs_app SET search_path = rcs;

-- The runtime role must never inherit migration privileges.
REVOKE rcs_migrate FROM rcs_app;

-- Backup role. Created without login and without privileges, because the backup mechanism is not
-- chosen yet (DECISIONS.md DEF-01). When it is chosen, grant exactly what that mechanism needs:
--   physical base backup + WAL archive (ADR-021 option B):
--       ALTER ROLE rcs_backup LOGIN REPLICATION;   plus a "replication" entry in pg_hba.conf
--   consistent logical snapshot (ADR-021 option A):
--       ALTER ROLE rcs_backup LOGIN;  GRANT pg_read_all_data TO rcs_backup;  GRANT CONNECT ON DATABASE ...
-- Never grant both "just in case". This script deliberately does not re-assert rcs_backup's attributes,
-- so re-running it never undoes that later, deliberate configuration.
