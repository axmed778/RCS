# RCS — Request Control System

Internal request tracking and workflow management system for processing incoming requests, attachments,
supplier communications, approvals, and related documentation.

**Status: Review MVP — the first working vertical slice of the Case workflow, with a server-rendered UI.**
It exists to be demonstrated to the product owner for business and UX feedback. **It is not production ready:**
there is no authentication (a Development-only review actor stands in), no document upload, no final result, no
search and no dashboards beyond simple counts. See [Review build](#review-build) below.

The authoritative design is in [`/docs`](docs/). Settled decisions are logged in
[`docs/DECISIONS.md`](docs/DECISIONS.md); do not revisit them in code.

## Solution layout

```text
src/
  Rcs.Domain            closed vocabularies, workflow rules, authorization policy, derived progress. No I/O
  Rcs.Application       use-case contracts and read models; unit of work, case lock, id generator, idempotency
  Rcs.Infrastructure    PostgreSQL (Npgsql, hand-written SQL): services, queries, audit writer, migrations, demo seed
  Rcs.Web               ASP.NET Core host, Razor Pages UI (Azerbaijani), health endpoints; `migrate`, `check-schema`, `seed-demo`
tests/
  Rcs.UnitTests         no database
  Rcs.IntegrationTests  real PostgreSQL
database/               hand-written SQL: roles, bootstrap, migrations, dev database, seed  → database/README.md
deploy/config-templates configuration and secrets templates (no secrets)
docs/                   frozen design documents
```

Dependencies point one way: `Domain ← Application ← Infrastructure ← Web` (a unit test enforces it).

This follows `ARCHITECTURE.md` §21 with two deliberate simplifications requested for Phase 1: SQL lives in
`database/` (not `db/`), and there are two test projects instead of five.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | **10.0** (LTS) | `global.json` accepts 10.0.100 or later feature bands; built with 10.0.401 |
| PostgreSQL | **18** for development; **17 minimum** | 17+ provides the builtin `C.UTF-8` locale, trusted `btree_gist` and `uuid_extract_*`. The production version is pinned later together with OS and ICU packaging (PS-1) |

No Docker is required, and nothing in the repository depends on containers. Install PostgreSQL natively:
on Windows with the official installer; on Linux from the PostgreSQL project's package repositories
(PGDG). A disposable local container is fine for development if you already use one.

> **Windows with Smart App Control enforcing** blocks unsigned, locally built assemblies and unsigned
> PostgreSQL binaries, so nothing built here can run. Develop on a machine or VM whose policy allows development
> tooling — for example WSL, below, which keeps Smart App Control on.

### Linux or WSL quick path

On Windows, first run `wsl --install` in an elevated terminal and reboot. Then, inside Ubuntu:

```sh
# PostgreSQL 18 from the PostgreSQL project's repository (PGDG)
sudo apt update && sudo apt install -y postgresql-common curl
sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh
sudo apt install -y postgresql-18

# .NET 10 SDK with Microsoft's install script (no system package needed)
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
```

Work on a copy of the repository inside the Linux filesystem (for example `~/src/rcs`) rather than under
`/mnt/c` — builds there are much faster.

## Local database

```sh
# 1. roles (once per cluster) — no passwords in the script
psql -h localhost -U <your-admin-login> -d postgres -f database/roles/roles.sql

# 2. passwords, interactively, never committed
psql -h localhost -U <your-admin-login> -d postgres -c "\password rcs_migrate"
psql -h localhost -U <your-admin-login> -d postgres -c "\password rcs_app"

# 3. development database — DEVELOPMENT ONLY, NOT THE PRODUCTION COLLATION (PS-1)
psql -h localhost -U <your-admin-login> -d postgres -f database/dev/create_dev_database.sql
```

`src/Rcs.Web/appsettings.Development.json` points both connection strings at `localhost:5432/rcs_dev` **without
passwords**. Supply them without committing anything, using one of:

- a PostgreSQL password file — `%APPDATA%\postgresql\pgpass.conf` on Windows, `~/.pgpass` on Linux
  (`localhost:5432:rcs_dev:rcs_app:<password>`), which Npgsql reads;
- environment variables `ConnectionStrings__Runtime` and `ConnectionStrings__Migration`;
- `RCS_SECRETS_FILE=<path to a JSON file>` — see `deploy/config-templates/`. It is loaded last and wins.

## Run

Use the Development configuration: `DOTNET_ENVIRONMENT=Development` (PowerShell: `$env:DOTNET_ENVIRONMENT="Development"`).

```sh
dotnet build Rcs.sln

# Apply migrations: an explicit deployment action using the migration role. Safe to repeat.
dotnet run --project src/Rcs.Web -- migrate

# Verify the schema with the runtime role (exit 0 compatible, 3 incompatible).
dotnet run --project src/Rcs.Web -- check-schema

# Load the Development-only synthetic demonstration data. Refused in any other environment; safe to repeat.
dotnet run --project src/Rcs.Web -- seed-demo

# Start the host. It refuses to start unless the schema matches this release exactly.
dotnet run --project src/Rcs.Web
#   http://127.0.0.1:5080/              the Review UI (Development only)
#   http://127.0.0.1:5080/health/live   process is up
#   http://127.0.0.1:5080/health/ready  database reachable and schema compatible
```

Exit codes: `0` success · `1` migration failed · `2` usage or configuration error · `3` schema incompatible ·
`4` document integrity finding · `130` cancelled.

### Document storage

File bytes live on the local filesystem, never in PostgreSQL (`DOCUMENT_MODEL.md` §6, ADR-005). Configure
`Rcs:Storage` (Development defaults to `.local/` at the repository root, which is git-ignored):

| Setting | Meaning |
|---|---|
| `RootPath` | object store root; objects live at `sha256/ab/cd/<full hash>` — no case, filename or organization in the path |
| `TempPath` | temporary upload area. **Must be on the same filesystem volume as `RootPath`**: publishing is an atomic, never-overwriting link/rename, and a cross-volume setup is refused rather than silently copied |
| `VolumeCode` | recorded on every version (`PRIMARY`), so the root can move by configuration |
| `MaxUploadBytes` | 524288000 (500 MB, ADR-042) — enforced while streaming; also set the reverse proxy limit |
| `TemporaryRetentionHours` | interrupted uploads older than this are swept — the only file deletion the system performs |

Published objects are never deleted, moved or rewritten by the application (ADR-043). To check stored bytes
against their metadata (read-only; nothing is repaired):

```bash
dotnet run --project src/Rcs.Web -- verify-documents            # every object exists with its recorded size
dotnet run --project src/Rcs.Web -- verify-documents --rehash   # also re-hash every object (SHA-256)
dotnet run --project src/Rcs.Web -- verify-documents --orphans  # also list objects no version references (retained)
```

## Review build

The Review build exists so the Case workflow can be **demonstrated and reviewed before authentication is
built**. It is enabled by `Rcs:Review:Enabled`, which is `true` only in `appsettings.Development.json`.

| Rule | |
|---|---|
| **Development only** | the host **refuses to start** if the flag is true in any other environment |
| **One synthetic actor** | every request acts as the seeded user `review.demo` ("Nümayiş istifadəçisi", Chief). It is not authentication: no credentials, no session, no sign-in, and no way to act as somebody else |
| **Attribution is real** | every row and every `audit_event` records that user, their roles at the time, and the client host — exactly as a real session would |
| **Authority is real** | every action is checked by the same `can()` policy the final system will use, with roles read from `user_role` at action time |
| **Nothing is exposed otherwise** | with the flag off, the business pages are not mapped at all; only the health endpoints answer |
| **Synthetic data only** | the demo seed is Development-only and refuses to run elsewhere. No production data, ever (SECURITY.md §18.4) |

The banner **RCS Review Build — Development / Synthetic Data** is on every page for the same reason.

Pages: `/` (dashboard) · `/cases` · `/cases/new` · `/cases/{id}` (the case workspace) ·
`/cases/{id}/requests/new` · `/cases/{id}/requests/{requestId}/responses/new` ·
`/cases/{id}/responses/{responseId}/requirements/new` ·
`/cases/{id}/requirements/{requirementId}/{fulfil|waive|void|fail}` · `/cases/{id}/final-result/new` ·
`/cases/{id}/documents/upload` · `/cases/{id}/documents/attach` · `/cases/{id}/documents/{linkId}` (a file,
its versions and its corrections) · `/organizations` · `/organizations/new`. Files are always added from the
context they belong to (a letter, a requirement, a final result), and downloads go through
`/cases/{id}/documents/{linkId}/versions/{versionId}/download`, authorized and audited on every request.

The interface language is Azerbaijani. Every string comes from `src/Rcs.Web/Resources/SharedResource.resx`,
keyed by stable codes (a database vocabulary code, or a `WORKFLOW.md` §11.3 ladder row); a second language is a
second `.resx` and no code change.

How migrations, history, checksums, locking and the compatibility check work: **[database/README.md](database/README.md)**.

## Tests

```sh
dotnet test tests/Rcs.UnitTests          # no database needed
```

Integration tests run against a **real** PostgreSQL 17+ server. Point them at a **disposable development
cluster — never production**. They run `database/roles/roles.sql` (creating the cluster-level roles), and
create and drop a database named `rcs_it_*` per test. The admin login is used only to create databases and to
assume the application roles; every privilege check runs as `rcs_migrate` or `rcs_app`.

```sh
# PowerShell: $env:RCS_TEST_ADMIN_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=..."
export RCS_TEST_ADMIN_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=..."
dotnet test tests/Rcs.IntegrationTests
```

Without `RCS_TEST_ADMIN_CONNECTION` the integration tests **fail** with an explanation rather than silently skip.
Everything works offline once NuGet packages are restored (`packages.lock.json` pins them).

## Rules that apply from the first commit

- **No production data, ever** — no real municipal, company or citizen data, letters or documents in the
  repository, in tests or on developer machines (SECURITY.md §18.4). Fixtures are synthetic.
- **No secrets in source control.** Templates only.
- **PS-1 is unresolved:** do not initialize a production database, and do not write collation-dependent SQL.
- **The schema changes only through new migrations.** Applied migrations are immutable.
- **Runtime never uses the migration credentials**, and never runs as a superuser (startup checks this).
