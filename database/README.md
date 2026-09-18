# Database

PostgreSQL is the authoritative structured store, and **hand-written SQL in this directory owns the schema**
(DECISIONS.md ADR-004). No ORM generates, alters or "tidies" it.

| Path | What | Run by |
|---|---|---|
| `roles/roles.sql` | Cluster roles `rcs_migrate`, `rcs_app`, `rcs_backup`. Idempotent, no passwords | an administrator, once per cluster |
| `dev/create_dev_database.sql` | Creates `rcs_dev` — **development only, not the production collation** | an administrator, on a development machine |
| `bootstrap/schema_migration.sql` | Schema `rcs` and the migration history table | the migration runner, before every run |
| `migrations/NNNN_name.sql` | The schema, one ordered step at a time | the migration runner (`Rcs.Web migrate`) |
| `seed/` | Synthetic fixtures only (none yet) | — |

> **PS-1 — production collation is unresolved** (DECISIONS.md). Development and test databases use PostgreSQL's
> builtin `C.UTF-8` locale, which compares by code point and applies no linguistic rules. That is **NOT THE
> PRODUCTION COLLATION DECISION**. No migration may use `COLLATE`, `ICU_LOCALE`, `LC_COLLATE` or similar until
> PS-1 is decided by ADR; a unit test enforces this. Do not create a production database until PS-1 is decided.

## Schema namespace

One schema, `rcs`, owned by `rcs_migrate`. It holds the application tables, the migration history and the
`btree_gist` extension. The `public` schema is unused and closed to `PUBLIC`. Per-module schemas are not used:
the domain is tightly interconnected and cross-schema foreign keys would add friction without isolation.
SQL is written schema-qualified (`rcs.table`); both roles also have `search_path = rcs`.

## Roles

| Role | Login | Owns | May | May not |
|---|---|---|---|---|
| `rcs_migrate` | yes | the database, schema `rcs`, every object in it | run migrations; install approved **trusted** extensions | be used by the running application |
| `rcs_app` | yes | nothing | connect; `SELECT` migration history; later, exactly the table privileges each migration grants | create objects or temp tables, change history, create extensions, be superuser. Will never get `UPDATE`/`DELETE` on the audit table (ADR-018) |
| `rcs_backup` | **no** | nothing | nothing yet — the backup mechanism is not chosen (DEF-01). `roles.sql` documents the grant for each option and never overwrites a later deliberate configuration | — |
| administrators | individual logins | — | interactive administration | be shared, or be held by the application |

No role is superuser, `CREATEDB`, `CREATEROLE`, `REPLICATION` or `BYPASSRLS`. Passwords are set interactively
(`\password rcs_app`) and never stored in this repository.

Migration `0001` closes the database to `PUBLIC` (no `CONNECT`, no `TEMPORARY`), grants `CONNECT` to `rcs_app`,
and revokes the default `EXECUTE`-to-`PUBLIC` on functions the migration role creates. Tables are private by
default; **each table migration grants `rcs_app` exactly what it needs.**

## Setting up a database

Prerequisites, in order — the runner checks them and fails with `MigrationPrerequisiteException` otherwise:

1. `roles/roles.sql` has been run on the cluster.
2. The database exists and is **owned by `rcs_migrate`** (`dev/create_dev_database.sql` for development).
3. Passwords for `rcs_migrate` and `rcs_app` are set and available to the respective processes (pgpass file,
   environment variables, or `RCS_SECRETS_FILE` — see the root README).

Then: `Rcs.Web migrate`.

## Migration files

**Convention:** `NNNN_snake_case_name.sql` — four digits, **contiguous from `0001`**, lowercase letters and digits
separated by single underscores. The leading comment block must contain:

```sql
-- description: What this migration does, in one line
-- transactional: false        (optional; only for the escape hatch below)
```

- **Applied migrations are immutable.** Never edit, rename, renumber or delete one — not even a comment. Fix
  forward with a new migration. A migration that has never been applied anywhere may still be changed.
- **One responsibility per migration.** Do not dump a whole module into one file.
- **Forward-only.** There are no down-migrations; recovery from a bad migration is restore-then-fix-forward
  (ARCHITECTURE.md §7.5).
- **No collation or locale assumptions** until PS-1. **Only approved extensions** (`btree_gist`; anything else needs
  an ADR). Both are enforced by unit tests.
- Migrations are embedded into `Rcs.Infrastructure` at build time, so the runner and the runtime schema check
  both use exactly the files shipped in the release.

## How the runner works

`Rcs.Web migrate` reads `ConnectionStrings:Migration` and then:

1. Opens **one unpooled connection** as `rcs_migrate`.
2. Takes the migration lock (below). If it cannot, it fails after the timeout **without touching the schema**.
3. Runs the idempotent bootstrap: schema `rcs`, table `rcs.schema_migration`, and `SELECT` on it for `rcs_app`.
4. Compares the history with the release. **Before anything executes**, it stops with
   `MigrationHistoryMismatchException` if an applied migration was edited or renamed, if history has gaps, or if
   the database holds migrations this release does not know (the database is ahead).
5. Applies pending migrations in order. A **transactional** migration and its history row commit together. On the
   first failure it stops: the failed migration is rolled back and not recorded, and later migrations are not
   attempted (`MigrationExecutionException`). Re-running continues from where it stopped.
6. Releases the lock.

Running it again when nothing is pending changes nothing.

### History and checksums

`rcs.schema_migration` has one row per applied migration: `migration_id`, `name`, `description`,
`checksum_sha256`, `transactional`, `applied_at`, `applied_by` (the effective role), `execution_ms`,
`runner_version`.

The checksum is SHA-256 over the file content after exactly two normalizations — a leading UTF-8 byte-order
mark is removed and CRLF becomes LF — so a Windows checkout does not look like an edit. Any other change,
including whitespace and comments, changes the checksum. `.gitattributes` also keeps `*.sql` as LF.

### Transactions and the escape hatch

Migrations run in a transaction by default. For the rare statement PostgreSQL refuses inside a transaction
block (for example `CREATE INDEX CONCURRENTLY`), declare `-- transactional: false`. Such a file must:

- contain **a single statement** (several statements are sent together and PostgreSQL would still run them in an
  implicit transaction — it fails loudly rather than silently);
- be **safe to re-run** (`IF NOT EXISTS`), because a failure after the statement but before the history row
  leaves it unrecorded.

A transactional migration must not contain `BEGIN;`, `COMMIT`, `ROLLBACK` or `START TRANSACTION`; the file parser
rejects them. PL/pgSQL blocks (`DO $$ BEGIN ... END $$;`) are fine.

### Migration lock

| | |
|---|---|
| Mechanism | PostgreSQL **session** advisory lock, `pg_try_advisory_lock`, polled every 250 ms |
| Key | `0x5243535F4D494752` (the ASCII bytes of `RCS_MIGR`), one bigint. Advisory locks are per database |
| Acquisition | before the bootstrap; waits up to `Rcs:Database:MigrationLockTimeoutSeconds` (default 60) |
| Release | `pg_advisory_unlock` in a `finally` block. The connection is **unpooled**, so closing it releases the lock even if the unlock never runs (crash, network loss, kill) |
| On timeout | `MigrationLockTimeoutException`, exit code 1, schema untouched |

A session lock (not a transaction lock) is used because a run spans several transactions. Case-level locks for
business operations are a different mechanism (below).

## Schema compatibility

The running application **never migrates**. At startup, before the web server starts, it checks with its own
`rcs_app` credentials that the database history equals the release **exactly** — same numbers, names and
checksums — and refuses to start otherwise:

| Status | Meaning | Action |
|---|---|---|
| `Compatible` | exact match | start |
| `NotInitialized` | no history | run `migrate` |
| `DatabaseBehind` | release has unapplied migrations | run `migrate` |
| `DatabaseAhead` | database has migrations unknown to this release | deploy the matching release; never roll back by hand |
| `HistoryMismatch` | applied migration edited or renamed, or gaps | investigate; never "fix" history rows |
| `HistoryUnreadable` | `rcs_app` cannot read history | check grants |
| `UnsafeRuntimeRole` | the runtime connection is a superuser, owns `rcs`, or can create objects in it | use `rcs_app` |

The schema version is the newest migration number. `Rcs:Database:ExpectedSchemaVersion` in the shipped
`appsettings.json` states it too; startup fails if it disagrees with the embedded migrations, and a unit test
keeps them equal. `Rcs.Web check-schema` runs the same check for deployment scripts (exit 0 compatible, 3 not).
`/health/ready` reports it as `Healthy`/`Unhealthy`, without detail.

## Conventions for later migrations

- **Time.** System and audit timestamps are `timestamptz`, written as UTC instants. Dates printed on paper are
  `date`. Business time and recording time stay separate columns (DOMAIN_MODEL.md §1.4). Any SQL that turns an
  instant into a date must state the time zone explicitly — never rely on the session time zone.
- **Identifiers.** `uuid` columns, never text. Values are UUIDv7 generated in the application by `IIdGenerator`
  (no extension, no database default). They give index locality, **not** ordering — audit order is `event_seq`.
- **Closed vocabularies** are `text` + `CHECK`, with codes from `Rcs.Domain.Vocabulary`; open vocabularies are
  lookup tables (DOMAIN_MODEL.md §1.4).
- **Exclusion constraints** for assignments follow DOMAIN_MODEL.md §2.7 exactly: one per scope, `ENDED` rows
  included, half-open `tstzrange(valid_from, valid_until, '[)')`, `valid_until > valid_from`. An integration test
  already proves `btree_gist` supports this shape.
- **No `ON DELETE CASCADE`**, no `DELETE` grants on business tables (ADR-030).

## The schema so far

Migrations `0002`–`0012` create the first business schema — the vertical slice of the Case workflow:

| Migration | Tables |
|---|---|
| `0002_identity` | `app_user`, `role` (seeded), `user_role` |
| `0003_lookup_vocabularies` | `organization_type`, `correspondence_kind`, `response_type`, `response_outcome`, `requirement_origin_type`, `deadline_basis`, `assignment_role`, `assignment_end_reason`, `void_reason`, `waiver_reason` — each seeded with its frozen codes |
| `0004_organizations` | `organization`, `organization_alias` |
| `0005_cases` | `case_record`, `case_state_change` |
| `0006_correspondence` | `correspondence` |
| `0007_requests` | `request` |
| `0008_responses` | `response`, `response_supersession` |
| `0009_requirements` | `requirement`, `requirement_evidence`, and the child-request foreign key on `request` |
| `0010_assignments` | `assignment`, with the three per-scope `RESPONSIBLE` exclusion constraints |
| `0011_audit_event` | `audit_event` — `INSERT` and `SELECT` only for `rcs_app` |
| `0012_operation_receipts` | `operation_receipt` |

**Names (DECISIONS.md ADR-037).** The domain entities *user* and *case* are PostgreSQL reserved words, so the
tables are **`app_user`** and **`case_record`**; no other entity is renamed and no identifier is ever quoted.
Columns and foreign keys keep the domain names (`case_id`, `*_user_id`), and `audit_event.entity_type` records
**domain** names (`case`, `user`, `request`, …), not table names.

**Seeded lookup identifiers (ADR-038).** Rows seeded by a migration carry fixed UUIDv7 literals, because three
frozen constraints need a row-local predicate naming one code: the per-scope assignment exclusions
(`RESPONSIBLE`), the one-initiating-letter-per-case index (`INITIATING`) and the requirement origin `CHECK`
(`RESPONSE`). Application code resolves lookups **by code**, never by identifier.

**Columns deliberately left for later** — additive, and named in the migration that defers them: `case_type_id`
and `closure_type_id` on `case_record`, `delivery_method_id` and `withdrawal_reason_id` on `correspondence`, and
the document / internal-record arcs of `requirement_evidence`. They arrive with the migrations that build the
workflows using them, so nothing here has to be thrown away.

## Demo data

`Rcs.Web seed-demo` loads **synthetic** organizations, users and three demonstration cases by driving the real
application services, so every consequence and audit event is produced the way the application produces them.
It is **Development-only** (refused elsewhere), deterministic, and safe to re-run: a case already present is
skipped. To start from a clean slate, drop and recreate the development database, migrate, and seed again.

## Deliberately not created yet

These are frozen parts of the model that the first vertical slice does not need. Each arrives with the workflow
that uses it, as a new migration:

- **Documents** — `document`, `document_version`, `document_link` and the content-addressed object store
  (DOCUMENT_MODEL.md). Until then `requirement_evidence` carries the response arc only.
- **`final_result`** and its closure vocabulary (`closure_type`, `decision_type`), with the case-closure guards
  G1–G5 and the `case_record` columns they need.
- **`requirement_resolution_correction`** (ADR-010, transition Q7) and **`case_access_grant`** (restricted-case
  grants — until then a restricted case is visible to its assignees, Chief and Head only).
- **`internal_record`**, `organization` search-normalisation columns (DEF-09, after PS-1), and the background
  job/lock table (ARCHITECTURE.md §14.2).
- **Authentication tables** — credentials and server-side sessions (ADR-033, SECURITY.md §6, §8). The Review
  build has no authentication at all; it acts as one Development-only synthetic user (README.md).

## Case-level serialization (ADR-019)

`ICaseSerializationLock` / `PostgresCaseSerializationLock`: every closure-relevant command calls it first, inside
its unit of work.

- **Mechanism:** `pg_advisory_xact_lock(key)` — transaction-scoped, released at commit, rollback or dispose, so it
  cannot leak through the connection pool. Calling it without an active transaction throws.
- **Key:** first 8 bytes (big-endian, signed) of `SHA-256("rcs:case-serialization:v1:" ‖ case UUID in RFC 9562
  byte order)`. Deterministic everywhere; namespaced away from the migration lock.
- **Several cases:** distinct keys acquired in **ascending key order**, so two commands can never deadlock on
  this lock.
- An in-process lock is not used: it would not be authoritative across processes or connections.
