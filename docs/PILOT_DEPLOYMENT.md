# RCS pilot deployment

**What this describes:** a controlled 10-day, single-user pilot on one Linux server on the department LAN, with real
operational documents. It is **not** a department rollout and **not** a production deployment: several production
prerequisites are deliberately still open (PS-1 collation, AD, second machine, backup software choice).

The deployed application runs offline. Prepare OS packages, the .NET runtime or a self-contained release,
and NuGet packages in advance; installing missing dependencies can require a connected preparation machine.

---

## 1. What the pilot server needs

| | |
|---|---|
| OS | Linux (tested on Ubuntu 24.04). Windows Server is not broken by this work but is not the pilot target (ADR-023) |
| PostgreSQL | 17+, listening on a **Unix socket only** |
| Packages for previews | `poppler-utils`, `libvips-tools`, `libreoffice-writer-nogui`, `libreoffice-calc-nogui`, `bubblewrap`, `util-linux`, and the fonts your documents need (`fonts-liberation2`, `fonts-crosextra-carlito`, `fonts-crosextra-caladea`) |
| Reverse proxy | `nginx` |
| Accounts | `rcs-app` (service), `rcs-backup` (backups). Neither is a login account for people |

```sh
sudo adduser --system --group --home /opt/rcs --shell /usr/sbin/nologin rcs-app
sudo adduser --system --group rcs-backup
```

## 2. Storage outside the repository

Real business bytes never live inside the release directory — a deployment replaces that directory. The application
**refuses to start** outside Development if they do (`DeploymentSafetyGate`).

```sh
# New installation only; inspect existing directories before changing any ownership.
sudo usermod -aG rcs-app rcs-backup
sudo install -d -o rcs-app -g rcs-app -m 0750 /srv/rcs-pilot /srv/rcs-pilot/objects
sudo install -d -o rcs-app -g rcs-app -m 0700 /srv/rcs-pilot/tmp-uploads /srv/rcs-pilot/previews /srv/rcs-pilot/preview-tmp /srv/rcs-pilot/keys
sudo install -d -o rcs-backup -g rcs-backup -m 0700 /srv/rcs-pilot/backups
```

| Path | Holds | Notes |
|---|---|---|
| `/srv/rcs-pilot/objects` | original document bytes | content-addressed, append-only, **the evidence**; back up |
| `/srv/rcs-pilot/tmp-uploads` | uploads in flight | same filesystem as `objects` (publish is an atomic link) |
| `/srv/rcs-pilot/previews` | derived preview artifacts | regenerable; not backed up |
| `/srv/rcs-pilot/preview-tmp` | sandbox staging per job | regenerable; emptied by the service |
| `/srv/rcs-pilot/backups` | recovery points | owned by `rcs-backup` |
| `/srv/rcs-pilot/keys` | cookie-protection keys | service-only, outside releases; loss requires users to log in again |
| journal (`journalctl -u rcs`) | application logs | no log file to rotate |

**These directories are never shared.** No SMB, no NFS, no read-only export "just for convenience". Documents reach
people only through RCS, which is what makes the audit trail meaningful (`SECURITY.md` §5.3).

## 3. Clean pilot database

`rcs_dev` is left completely alone. The pilot gets its own database, built only by the real migration runner.

```sh
sudo -u postgres psql -X -v ON_ERROR_STOP=1 -f database/roles/roles.sql
sudo -u postgres psql -X -v ON_ERROR_STOP=1 -f database/pilot/create_pilot_database.sql
# Prepare the release and configuration in section 5 first. The migration secret belongs to the deployment operator.
DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/secure/operator/rcs-migration.secrets.json /opt/rcs/Rcs.Web migrate
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web check-schema
```

No demo data: `seed-demo` is Development-only and refuses to run here. Lookup and vocabulary rows arrive with the
migrations themselves, so the database has everything the workflow needs and nothing synthetic.

> **PS-1 is still open.** The pilot database uses the builtin `C.UTF-8` locale, like development. Azerbaijani sorting
> and case-insensitive matching are therefore not linguistically correct yet. Acceptable for one user over ten days;
> **not** acceptable for production (`DECISIONS.md` PS-1).

The shipped backup role starts NOLOGIN with no privileges. For this logical pilot backup only, an administrator
enables `LOGIN`, grants `CONNECT ON DATABASE rcs_pilot`, `USAGE ON SCHEMA rcs`, and `SELECT ON ALL TABLES IN SCHEMA rcs`
to `rcs_backup`; grant SELECT on future tables through `ALTER DEFAULT PRIVILEGES FOR ROLE rcs_migrate IN SCHEMA rcs`.
Do not grant replication, schema ownership or write privileges. Use a protected pgpass or an explicit peer map.

PostgreSQL's `listen_addresses = ''` must be set and verified on the pilot server; a socket connection string alone
does not disable TCP. With peer authentication, map OS `rcs-app` to database `rcs_app` and OS `rcs-backup` to database
`rcs_backup` in `pg_ident.conf`, and add matching `local` entries in `pg_hba.conf` before generic rules. Without that
mapping the differently named identities will not authenticate. Migration credentials remain operator-only.

## 4. The first accounts

A new database has nobody who can sign in, so the first two acts happen on the server console. Both are audited
exactly like any other account action (`PERMISSIONS.md` §25.2). Every command below needs Pilot configuration;
run from `/opt/rcs`. `bootstrap-admin` refuses every invocation after the first TECH_ADMIN grant. The first Head
exception permits HEAD only and never reopens after suspension or revocation.

```sh
# 1. The technical administrator (creates accounts and credentials; holds NO business authority).
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web user bootstrap-admin --username=tech.admin --full-name="Ad Soyad"
#    → prints a temporary password once. Hand it over in person.

# 2. The pilot employee's own account.
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web user create --username=aad --full-name="Ad Soyad" --job-title="Baş mütəxəssis" --by=tech.admin

# 3. The roles for the ten days. The FIRST Head grant is the documented installation exception; after that only a
#    Head may grant anything, from the console or the screen.
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web user grant --username=aad --role=HEAD  --by=tech.admin
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web user grant --username=aad --role=CHIEF --by=aad
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web user grant --username=aad --role=WORKER --by=aad
```

The employee holds **Worker + Chief + Head** so they can exercise normal work, approvals, closure and reopening. They
pass the ordinary authorization checks because they genuinely hold the roles — there is no pilot exception anywhere in
the policy. TechAdmin stays a separate account and is **not** given to the employee.

After the pilot, a Head revokes what is no longer appropriate (`/admin/users`), and the grant rows stay as history.

To set a password without generating one (read from stdin, never from the command line):

```sh
# Do not type a password literal into shell history. Prefer the default generated temporary credential.
read -rs -p 'Temporary password: ' pilot_password; printf '\n'
printf '%s' "$pilot_password" | sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web user set-password --username=aad --by=tech.admin --password-stdin
unset pilot_password
```

Record the department before creating any Case (no demo seed):

```sh
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web user set-department --name="Department official name" --type=MUNICIPAL_DEPARTMENT --by=tech.admin
```

## 5. Configuration and release

Build with `dotnet restore Rcs.sln --locked-mode`, `dotnet build Rcs.sln --no-restore`, and publish with
`dotnet publish src/Rcs.Web -c Release --no-restore -o <staging-directory>`. This is framework-dependent: install
the .NET 10 ASP.NET runtime on the server. Alternatively prepare and verify a self-contained Linux release separately.
Preserve the `preview-worker/` publish subdirectory. Copy `deploy/` and relevant `docs/` alongside the release,
and write a `RELEASE` file with the final git commit. Keep runtime configuration outside version control.

Copy `deploy/config-templates/appsettings.Pilot.example.json` to `/opt/rcs/appsettings.Pilot.json` and edit it. It
contains **no secrets**: connection strings with passwords go in the secrets file.

```sh
sudo install -d -o rcs-app -g rcs-app -m 0750 /etc/rcs
sudo install -o rcs-app -g rcs-app -m 0600 rcs-runtime.secrets.json /etc/rcs/
```

`RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json` is set by the service unit. Peer authentication on the Unix
socket means the pilot may not need a password at all.

## 6. Service

```sh
sudo install -m 0644 deploy/systemd/rcs.service /etc/systemd/system/rcs.service
sudo systemctl daemon-reload
sudo systemctl enable --now rcs
systemctl status rcs
journalctl -u rcs -f
```

The unit starts on boot, restarts on failure, keeps the service's whole process tree under `MemoryMax=3G` and
`TasksMax=512` (which is where the preview converters live), and writes to the journal. The preview worker is part of
the application: it is started per job by the service, sandboxed with bubblewrap — there is no second service to run.

Health: `/health/live` (process) and `/health/ready` (schema). Both answer without a session.

## 7. LAN access

| | |
|---|---|
| Application | Kestrel binds to **127.0.0.1:5080** only. Nothing but nginx can reach it |
| nginx | listens on 443 (and 80, which only redirects) on the LAN interface |
| PostgreSQL | **Unix socket only.** No workstation can reach the database, by design |
| Firewall | allow 443/tcp (and 80/tcp for the redirect) from the LAN; deny everything else inbound |
| Internet | none. No outbound dependency at runtime |

```sh
sudo ufw allow from 192.0.2.0/24 to any port 443 proto tcp
sudo ufw allow from 192.0.2.0/24 to any port 80 proto tcp
sudo ufw enable
```

**Name, not address.** Use a stable internal hostname (`rcs.example.lan`) in DNS. No IP address is baked into the
application: `Rcs:Hosting:BaseUrl` is configuration. If internal DNS is unavailable for the pilot, add one line to the
employee's workstation `hosts` file (`C:\Windows\System32\drivers\etc\hosts`) and record that as a pilot shortcut to
undo later:

```
192.0.2.10    rcs.example.lan
```

## 8. HTTPS

Preferred: a certificate from the organisation's **internal CA** for `rcs.example.lan`, installed on the server and
trusted by the workstation. **No ACME, no Let's Encrypt, no public CA** — a LAN hostname cannot use them, and reaching
for them would add exactly the Internet dependency this system refuses.

If no internal CA exists yet, the pilot-safe fallback is a self-signed certificate created on the server and installed
into the workstation's trust store, which gives the employee a normal padlock rather than a click-through warning:

```sh
sudo mkdir -p /etc/ssl/rcs
sudo openssl req -x509 -newkey rsa:4096 -sha256 -days 400 -nodes \
    -keyout /etc/ssl/rcs/rcs.example.lan.key -out /etc/ssl/rcs/rcs.example.lan.crt \
    -subj "/CN=rcs.example.lan" -addext "subjectAltName=DNS:rcs.example.lan"
sudo chmod 0600 /etc/ssl/rcs/rcs.example.lan.key
sudo install -m 0644 deploy/nginx/rcs.conf /etc/nginx/sites-available/rcs
sudo ln -sf /etc/nginx/sites-available/rcs /etc/nginx/sites-enabled/rcs
sudo nginx -t && sudo systemctl reload nginx
```

Copy the `.crt` to the workstation and import it into **Trusted Root Certification Authorities** (Local Machine).

If HTTPS cannot be arranged for the pilot at all, then `Rcs:Authentication:RequireSecureCookie` must be set to
`false`, the session cookie travels the LAN in clear, and `PILOT_READINESS.md` records that as an explicit, accepted
exception. It is a real weakening — do it knowingly or not at all.

## 9. Backup — before any real document is entered

A recovery point is **database + originals + configuration + release identity**. A database dump alone is not a
backup: it names documents whose bytes live in the object store.

```sh
sudo -u rcs-backup /opt/rcs/deploy/backup/rcs-backup.sh /srv/rcs-pilot/backups
```

Order matters and the script enforces it: **dump first, copy objects second.** The object store is append-only and an
object is published before the metadata that references it, so this order guarantees that every object the dump
mentions exists in the copy — the validity rule of ADR-021. The reverse order can produce a dump referencing an object
the copy does not have.

Preview artifacts are **not** backed up. They are derived cache and are regenerated after a restore
(`Rcs.Web preview regenerate <version-id>`, then `Rcs.Web preview run`; verify with `Rcs.Web preview verify`).

Run it **daily** during the pilot (cron or a systemd timer), and copy at least one recovery point onto separate media
that the server cannot write to.

## 10. Restore test — before go-live, not after

```sh
/opt/rcs/deploy/backup/rcs-restore-test.sh /srv/rcs-pilot/backups/<stamp>
```

It restores into a throw-away database, runs `check-schema`, then runs `verify-documents --rehash` against the
backed-up objects, which re-reads every stored file and compares size and SHA-256 with the database. **The live pilot
database and object store are never touched.** A recovery point that has not passed this is not yet a backup.

The restore command needs an explicitly authorized database-creation identity and runtime authentication to the
new database. Set `RCS_RESTORE_ADMIN`, `RCS_PG_HOST` and, optionally, `RCS_RESTORE_TEST_DB=rcs_restore_test_<unique-name>`.
Default socket peer authentication requires the corresponding OS identity/map. The script ignores `RCS_SECRETS_FILE`
to prevent a live configuration overriding its target; use a protected pgpass entry for the temporary database.
It exits on errors, verifies the manifest, and never drops a database. Inspect and clean up only the exact throw-away
name it prints. For regeneration use separate restored preview/temp directories and backed-up originals; never point
a restored test host at the live preview cache. The scripts do not validate LAN, TLS or service boot.

## 11. Updating during the pilot, and rolling back

```sh
sudo -u rcs-backup /opt/rcs/deploy/backup/rcs-backup.sh /srv/rcs-pilot/backups   # 1. recovery point
sudo systemctl stop rcs                                                          # 2. stop
# Stage and verify the NEW release; run its migrator with the operator-only migration secret.  # 3
# Deploy the new release into /opt/rcs, preserving external runtime configuration.             # 4
sudo -u rcs-app env DOTNET_ENVIRONMENT=Pilot RCS_SECRETS_FILE=/etc/rcs/rcs-runtime.secrets.json /opt/rcs/Rcs.Web check-schema # 5
sudo systemctl start rcs                                                         # 6. start
#    sign in, open a case, open a document                                       # 7. smoke test
```

**Rollback limits, stated plainly:**

- Rolling back **code** is straightforward while the schema is compatible: put the previous release back and start it.
- There are **no down-migrations** (ARCHITECTURE.md §7.5). A schema change cannot be undone by running something.
- If a release migrated the schema and must be withdrawn, the path is **restore the recovery point taken in step 1**,
  then fix forward. That is the reason step 1 is step 1.
- The application refuses to start against a schema it does not expect, so a mismatched rollback fails loudly rather
  than corrupting data.

## 12. What the pilot deliberately does not have

AD/LDAP or SSO · e-mail or messaging notifications · OCR, AI or content search · cloud anything · Docker or
Kubernetes · a second machine or automatic failover · production collation (PS-1) · full-text search, which is
deliberately deferred until the pilot tells us what the employee actually searches by.

---

# Part B — Windows single-laptop pilot

Sections 1–12 describe the **LAN server** pilot and remain the target for a department rollout. This part describes
the **other supported pilot mode**, added for the 10-day trial: RCS on **one Windows laptop**, used by one employee,
with no network service at all. Everything below is additional; nothing above is replaced.

Choose this mode when the pilot is one person on one machine. Choose the server mode when more than one person needs
access, or when document previews are required (see the limitation in §13.7).

## 13. Windows single-laptop pilot

### 13.1 What it is

| | |
|---|---|
| Runs on | one Windows 10/11 laptop |
| Reached at | `http://127.0.0.1:5080` — **loopback only**. Nothing listens on the network |
| Started by | a desktop icon: **RCS** |
| Database | a local PostgreSQL 17+ that is **already installed** on that laptop |
| Documents | `C:\ProgramData\RCS\Pilot\objects` — outside the program folder, outside any repository |
| Employee needs | the icon and their own username and password. No terminal, no command, no address to remember |

### 13.2 What the employee does

1. Double-clicks **RCS** on the desktop.
2. The launcher starts the application, waits until it reports healthy, and opens the browser at the sign-in page.
3. They sign in with their own account and work exactly as in the server mode.
4. A second double-click does **not** start a second copy — it re-opens the browser window.
5. **RCS-i dayandır** stops it; so does signing out of Windows.

If something is wrong, the launcher shows a message box naming the problem and where the logs are, rather than a
console that closes before it can be read.

> **If the icon does nothing at all**, Windows is blocking the unsigned executable — Smart App Control or a WDAC /
> AppLocker policy. This is a real possibility on a managed laptop and was observed on the engineering machine
> (*"An Application Control policy has blocked this file"*). Three ways out, best first:
>
> 1. **Sign** `Rcs.Launcher.exe` and `Rcs.Web.exe` with the organisation's code-signing certificate.
> 2. Have IT **allow** the two executables in the policy.
> 3. Point the desktop shortcut at the fallback instead — it does the same work under Microsoft-signed PowerShell:
>    `powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\Program Files\RCS\windows-pilot\start-rcs.ps1"`
>
> Test this **before** handing the laptop over: it is the one failure that looks to the employee like nothing happened.

### 13.3 Prerequisites on the laptop

- **PostgreSQL 17 or later, already installed and running**, with its superuser password known to whoever installs
  RCS. The pilot package deliberately does **not** bundle a database engine: installing one silently on somebody's
  laptop is not a decision a script should make.
- Administrator rights for the installation (writing to `C:\Program Files` and `C:\ProgramData`).
- Nothing else. The package is self-contained: **no .NET installation is required**.

### 13.4 Package layout

```
publish\                          (produced by: dotnet publish -c Release -r win-x64 --self-contained true)
  Rcs.Web.exe                     the application (also the migrate / check-schema / user commands)
  Rcs.Launcher.exe                the desktop icon's target
  launcher.json                   written by install.ps1
  appsettings.json                shipped defaults
  appsettings.Pilot.json          written by install.ps1 from the template, no secrets
  windows-pilot\                  copied by install.ps1 so maintenance lives on the laptop
    install.ps1  backup.ps1  restore-test.ps1  lib\RcsPilot.Common.ps1
```

Installed layout:

| Path | Holds |
|---|---|
| `C:\Program Files\RCS` | the release. Replaceable: nothing here is data |
| `C:\ProgramData\RCS\Pilot\objects` | **original documents** — the evidence. Backed up |
| `C:\ProgramData\RCS\Pilot\tmp-uploads` | uploads in flight |
| `C:\ProgramData\RCS\Pilot\previews`, `preview-tmp` | derived previews (unused in this mode, see §13.7) |
| `C:\ProgramData\RCS\Pilot\data-protection-keys` | cookie keys — why a reinstall does not sign the employee out |
| `C:\ProgramData\RCS\Pilot\logs` | application and launcher logs |
| `C:\ProgramData\RCS\Pilot\backups` | recovery points |

### 13.5 Installation

From an **elevated** PowerShell, in the published package:

```powershell
.\windows-pilot\install.ps1 -PublishPath . -PilotUser aad -PilotFullName "Ad Soyad" -PilotJobTitle "Baş mütəxəssis"
```

It asks once for the PostgreSQL superuser password and then, in ten steps: creates the ProgramData folders; checks
PostgreSQL is answering; creates the `rcs_app` / `rcs_migrate` roles with generated passwords and stores those only
in the employee's own `%APPDATA%\postgresql\pgpass.conf`; creates an **empty** `rcs_pilot` (an existing one is left
untouched); copies the release; writes `appsettings.Pilot.json` and `launcher.json` with **no secrets**; runs
`migrate` and `check-schema`; bootstraps `tech.admin` and the employee's account; records the department; grants
**Worker + Chief + Head**; creates the three desktop shortcuts; and finally starts the application once to confirm
it reports healthy.

It prints the two temporary passwords **once**. Hand the employee's password over in person; it must be changed at
first sign-in.

### 13.6 Backup and restore on the laptop

```powershell
# The employee can run this from the "RCS ehtiyat nüsxə" desktop shortcut.
.\windows-pilot\backup.ps1

# Prove a recovery point before real documents are entered, and then weekly.
.\windows-pilot\restore-test.ps1 -RecoveryPoint C:\ProgramData\RCS\Pilot\backups\20260921T101500Z
```

Same rules as the server mode: **database dumped first, documents copied second** (ADR-021), previews excluded
because they are derived, and a dump alone is never called a backup. The restore test only ever creates a database
named `rcs_restore_test_*`; the guard refuses any other name, including `rcs_pilot`, before anything is created.

**A laptop is a single point of failure.** Copy at least one recovery point onto a USB disk that stays elsewhere.

### 13.7 What this mode does not have, and why

| | |
|---|---|
| **Document previews** | **Off.** The converters (poppler, libvips, LibreOffice) and the bubblewrap sandbox that isolates them are Linux-only. The UI shows "Ön baxış mümkün deyil"; documents still upload, download and are listed normally. A pilot that needs previews uses the server mode |
| **HTTPS** | Not used, and not needed: the only address is `http://127.0.0.1`, which never leaves the machine. `RequireSecureCookie` is therefore false in this mode alone — it is the one setting that differs from the server template, and it is recorded as an explicit exception in `PILOT_READINESS.md` |
| **Access from another computer** | None. The launcher refuses any address that is not loopback |
| **A service that starts at boot** | No. RCS runs when the employee opens it. Nothing runs in the background otherwise |
| **Full-disk encryption, screen lock, endpoint protection** | Not RCS's to configure, and they matter more here than on a server: the documents are on a laptop that leaves the building. Confirm them before real documents are entered |
