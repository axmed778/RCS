-- ============================================================================================
-- DEVELOPMENT ONLY - NOT THE PRODUCTION COLLATION DECISION.
-- PS-1 (DECISIONS.md) is unresolved. Do not use this script, or its locale, for production.
-- ============================================================================================
--
-- Creates the local development database `rcs_dev`.
--
-- Locale: PostgreSQL's builtin provider with C.UTF-8 (PostgreSQL 17+). It orders and compares by
-- Unicode code point and applies no linguistic rules. It was chosen for development precisely because
-- it cannot be mistaken for an Azerbaijani/Russian collation decision. When PS-1 is decided, drop and
-- recreate development databases with the decided settings.
--
-- Run as an administrator, after database/roles/roles.sql, connected to the "postgres" database:
--     psql -h localhost -U <your-admin-login> -d postgres -f database/dev/create_dev_database.sql
-- Then run the migrations:  dotnet run --project src/Rcs.Web -- migrate

CREATE DATABASE rcs_dev
    OWNER rcs_migrate
    TEMPLATE template0
    ENCODING 'UTF8'
    LOCALE 'C'
    LOCALE_PROVIDER builtin
    BUILTIN_LOCALE 'C.UTF-8';
