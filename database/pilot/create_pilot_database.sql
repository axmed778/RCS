-- RCS pilot database
--
-- Run by an administrator on the pilot server, ONCE, before the first migration. It creates an EMPTY database owned by
-- rcs_migrate; every table, lookup row and vocabulary then arrives through the real migration runner
-- (Rcs.Web migrate), exactly as it will in production. Nothing is copied from rcs_dev: the pilot must not inherit demo
-- cases, synthetic organizations or review users.
--
-- Prerequisites: database/roles/roles.sql has been run on this cluster and both role passwords are set.
--
--   psql -U postgres -f database/pilot/create_pilot_database.sql
--   Rcs.Web migrate          (as the deployment user, with ConnectionStrings:Migration)
--   Rcs.Web check-schema     (with ConnectionStrings:Runtime; exit 0 means compatible)
--   Rcs.Web user bootstrap-admin --username=... --full-name="..."   (PERMISSIONS.md 25.2)
--
-- PS-1 WARNING (DECISIONS.md): the production collation decision is still open, so this script uses the same builtin
-- C.UTF-8 locale as development. It compares by code point and applies no Azerbaijani linguistic rules. That is
-- acceptable for a 10-day single-user pilot with a known small data set, and it is recorded as a known limitation in
-- docs/PILOT_READINESS.md. A production database must wait for PS-1.

\set ON_ERROR_STOP on

CREATE DATABASE rcs_pilot
    OWNER rcs_migrate
    TEMPLATE template0
    ENCODING 'UTF8'
    LOCALE_PROVIDER builtin
    BUILTIN_LOCALE 'C.UTF-8';

COMMENT ON DATABASE rcs_pilot IS
    'RCS controlled pilot database. Real operational data. Not a development database; never reseeded with demo data.';
