# RCS — Request Control System

Internal request tracking and workflow management system for processing incoming requests, attachments,
supplier communications, approvals, and related documentation.

**Status: Implementation Phase 1 — repository foundation and database migration infrastructure.** There is no
business functionality and no user interface yet.

The authoritative design is in [`/docs`](docs/). Settled decisions are logged in
[`docs/DECISIONS.md`](docs/DECISIONS.md); do not revisit them in code.

## Solution layout

```text
src/
  Rcs.Domain            closed vocabularies, pure rules. No database, HTTP or filesystem
  Rcs.Application       use-case contracts: unit of work, case serialization lock, id generator, idempotency
  Rcs.Infrastructure    PostgreSQL (Npgsql), migration runner, schema compatibility check, lock implementation
  Rcs.Web               ASP.NET Core host and composition root; health endpoints; `migrate` and `check-schema`
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

# Start the host. It refuses to start unless the schema matches this release exactly.
dotnet run --project src/Rcs.Web
#   http://127.0.0.1:5080/health/live    process is up
#   http://127.0.0.1:5080/health/ready   database reachable and schema compatible
```

Exit codes: `0` success · `1` migration failed · `2` usage or configuration error · `3` schema incompatible ·
`130` cancelled.

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
