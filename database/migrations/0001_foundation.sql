-- description: Database privilege baseline and the btree_gist extension
--
-- RCS migration 0001 - foundation. Responsibilities, and nothing else:
--   1. close the database to PUBLIC; let the runtime role connect;
--   2. keep the unused public schema closed;
--   3. keep functions created by later migrations private until a migration grants them;
--   4. install btree_gist, the one approved extension (DECISIONS.md ADR-017).
--
-- No business table is created here. No collation or locale is assumed: production collation is
-- unresolved until PS-1 (DECISIONS.md). Applied migrations are immutable - never edit this file
-- after it has been applied anywhere; write migration 0002 instead.

-- 1. Database access. The migration role owns the database, so it may change these grants.
DO $$
BEGIN
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO rcs_app', current_database());
END
$$;

-- 2. RCS does not use the public schema.
REVOKE ALL ON SCHEMA public FROM PUBLIC;

-- 3. PostgreSQL grants EXECUTE on new functions to PUBLIC by default. RCS grants explicitly instead.
--    Tables and sequences are already private by default: each table migration grants rcs_app exactly
--    what it needs, and never UPDATE or DELETE on the audit table (DECISIONS.md ADR-018).
ALTER DEFAULT PRIVILEGES FOR ROLE rcs_migrate REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- 4. btree_gist supports the per-scope assignment exclusion constraints (DOMAIN_MODEL.md section 2.7).
--    It is a trusted extension, so the migration role installs it without superuser rights.
--    pg_trgm is deliberately not installed (DECISIONS.md ADR-024).
CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA rcs;
