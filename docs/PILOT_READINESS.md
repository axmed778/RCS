# Controlled pilot readiness — verification record

Scope: **10 days, one business user**, Worker + Chief + Head; a separate TechAdmin. This is not production-ready.
Verified on 2026-09-21 in Ubuntu 24.04 WSL2, PostgreSQL 18 and .NET 10. The actual LAN server still needs section B.

## Starting state and preservation

- Windows repository: `feature/pilot-readiness`, starting HEAD `9db12e3859c02422192f8a44124fa9dc91e6d9b4`.
- Claude's uncommitted authentication, migration, UI, bootstrap, deployment, backup and tests were preserved.
- Existing WSL mirror: `~/src/rcs`, git base `dd08d2e51af082b5bd5e0719d37dae3f58185796` on `main`.
  Its source content matched Windows apart from unfinished documentation. Changed mirror files were backed up
  before copying; its older git history was retained. Release publishing explicitly supplies the Windows commit.
- Existing `rcs_pilot` already had schema 16, 2 users, 2 synthetic cases and 2 document versions. It was NOT erased
  or recreated. Clean installation is exercised by integration fixtures using the real migrations and no demo seed.
- Final commit: the commit containing this record on `feature/pilot-readiness` (`git log -1`). No push or PR.

## A. Verified on this engineering machine

| Check | Result and evidence |
|---|---|
| Locked restore and build | PASS; Argon2/Blake2 lock files updated; zero build warnings/errors |
| Full automated suite | **192 unit + 182 integration = 374**, zero failures/skips; 10 new regression tests beyond Claude's 364 |
| Migration 0016 | Existing checksum preserved; schema check reports 16; tables owned by `rcs_migrate`; runtime has SELECT/INSERT and limited column UPDATE, no DELETE or ownership |
| Real login and first password change | PASS via published HTTP host in Pilot; no Review Actor; business routes blocked before change |
| Password/session security | Canonical PHC with legacy-padding compatibility; random salts; no work-factor downgrade on rehash; temporary expiry also rejects existing sessions; reset/logout revocation and status/role refresh tested |
| Bootstrap and administration | First TECH_ADMIN only once; first-Head exception restricted to HEAD and closed permanently after any Head grant; no reopening by suspension; normal role grants Head-only |
| Public authentication behavior | Generic failure including lockout; CSRF rejection; per-source limit; bounded lock counter and expiry regression tests |
| Review mode and seed | Host refuses Review Actor outside Development; existing seed-demo environment guard retained; no review.* accounts in pilot |
| Own department | Existing own organization retained; idempotent CLI setup exercised; fresh setup tested in clean integration fixtures |
| Full HTTP smoke | **32/32 PASS**: anonymous redirect, real login/change, organization, Case, Request, Response, Requirement, PDF, fulfil, request close, FinalResult with PDF, issue/approve, close/reopen |
| Document and preview security | Authorized download equals original SHA256; bubblewrap PDF preview READY; PNG served; anonymous/guessed artifact refused |
| Audit | Synthetic workflow user events attributed to `pilot.user`; no review identity |
| Restart/persistence | Published process stopped/restarted; existing cookie still works with external data-protection keys; case and original PDF remain; logout and re-login verified |
| Storage | Originals `/srv/rcs-pilot/objects`; uploads, previews, staging and keys in separate external directories; overlap/release-path rejection tested |
| Backup | `/srv/rcs-pilot/backups/20260921T081509Z`; dump first, originals second; relative SHA256 manifest verified |
| Restore | `rcs_restore_test_finish_20260921121510`; schema 16; **3 cases, 2 users, 4 document versions** match source; 4 versions rehashed, **0 findings** |
| Preview rebuild after restore | No cache copied. Four fresh generations produced **8 artifacts**, verification **0 findings**; originals rehashed again with 0 findings |
| Deployment templates | Reviewed and corrected: loopback forwarded headers, nginx syntax compatible with Ubuntu 24.04 packaging, systemd start-limit section, external cookie keys and file permissions. Templates are not evidence of installed service/TLS |

Smoke used loopback HTTP with Secure-cookie requirement disabled **only in the untracked WSL smoke configuration**.
The committed HTTPS template requires Secure cookies. Published path: `/opt/rcs-pilot-app`.
Smoke resets the synthetic `pilot.user` and leaves a newly generated temporary credential undisclosed; reset it through
the CLI before further use. This account is not the employee's final account. The throw-away restore database and its
derived preview directory are retained for inspection. Neither `rcs_dev` nor `rcs_pilot` was dropped/overwritten.
Recovery was exercised with the existing local operator identity; the dedicated backup account must be configured/tested
on the real server. The backup intentionally excludes password files and cookie-protection keys; recover credentials
separately and require fresh login after restoring to a new server.

## B. Real pilot server — NOT VERIFIED / NO-GO

| Item | Required evidence |
|---|---|
| Second LAN workstation | Real employee can sign in, upload, download and preview through the server hostname |
| nginx HTTPS | Real certificate trusted on workstation, successful TLS traffic and Secure cookies |
| systemd boot/reboot | Install unit, enable/start and reboot; verify automatic recovery and preview sandbox under service restrictions |
| Database network isolation | Set and verify PostgreSQL socket-only listening; workstation cannot connect to 5432; firewall only intended LAN services |
| Backup operations | Dedicated least-privilege backup account can read originals/dump; daily schedule works; off-server copy exists; repeat restore on actual server |
| Real employee identity | Named account, roles and temporary password handed over; synthetic verification data excluded from the real-use dataset |
| Preview converters | Tools/fonts installed and conversions tested under the actual service identity |
| Manual browser walkthrough | Follow PILOT_USER_GUIDE and the lifecycle above from the employee's workstation |
| Host protection | Server hardware, storage capacity, physical access and UPS decided/tested |
| Handover | Employee receives guide; product owner accepts pilot limitations and signs off |

**Do not start real use until these items are resolved or an explicit, documented owner exception is recorded.**

## C. Known limitations

- PS-1 Azerbaijani collation remains open; pilot uses builtin C.UTF-8. No production database sign-off.
- One person temporarily has three business roles; separation of duties is suspended for this pilot only.
- No search, AD/LDAP, MFA, notifications, failover or second server.
- DWG/ArchiCAD previews remain honestly unsupported; originals can still be downloaded.
- Unknown-user login timing differs from a known account. Generic messages/rate limits do not eliminate this signal.
- Password blocklist is local and short; cost settings need tuning on the actual server.
- No per-job cgroup quotas; converter limits plus service resource ceilings need real-server verification.
- A sole Head lost/deactivated needs the documented recovery procedure; bootstrap cannot be used to bypass Head authority.
- Restoring excludes derived preview cache: explicitly regenerate and verify it before declaring recovery complete.
- Engineering smoke is HTTP automation, not a second-workstation or human browser usability test.

## D. Sign-off

| Responsibility | Name/date/evidence |
|---|---|
| Technical administrator: server, network, backup/recovery | Pending |
| Employee: account and walkthrough | Pending |
| Product owner: 10-day pilot and exceptions | Pending |

---

# E. Windows single-laptop mode — separate checklist

Use this list **instead of** B1–B10 when the pilot runs on one Windows laptop (`PILOT_DEPLOYMENT.md` §13). Sections
A (authentication, roles, storage, backup rules) and C (limitations) still apply.

## E1. Verified on the engineering machine

| # | Item | State | Evidence |
|---|---|---|---|
| E1.1 | The Windows package publishes and is self-contained | **GO** | `dotnet publish -r win-x64 --self-contained` produced `Rcs.Web.exe` (console) and `Rcs.Launcher.exe` (GUI subsystem — no console window), 371 files, ~111 MB, no .NET needed on the laptop |
| E1.13 | The published `Rcs.Web.exe` executes on Windows | **GO** | run on this machine: `check-schema` started and reported "ConnectionStrings:Runtime is not configured", exit 2 — the binary runs; only a database was absent |
| E1.14 | An Application Control policy can block the unsigned launcher | **FINDING** | `Rcs.Launcher.exe` was refused here: *"An Application Control policy has blocked this file"* (Smart App Control). `start-rcs.ps1` is the documented fallback; signing both executables is the real fix. Carried into E2.1 |
| E1.2 | The Linux-only preview worker is excluded from the Windows package | **GO** | the publish target is skipped for `win-*` runtimes |
| E1.3 | Launcher: one instance per laptop, loopback only, never Development | **GO** | unit tests — a second claim from another thread is refused; routable and non-loopback addresses rejected; `EnvironmentName=Development` rejected |
| E1.4 | Launcher waits for **healthy**, not merely "started", and reports failure in a window | **GO** | unit tests for ready / application-exited / timed-out; message box on Windows, no console |
| E1.5 | All pilot paths are under `C:\ProgramData\RCS\Pilot` | **GO** | unit test over the shipped template: storage, previews and data-protection keys, none inside the release or a repository |
| E1.6 | Data-protection keys persist outside the release | **GO** | unit test: `…\Pilot\data-protection-keys`, so a reinstall does not sign the employee out |
| E1.7 | The template carries no secret and never enables the review actor | **GO** | unit test: no `Password=`, empty migration connection string, `Review:Enabled=false` |
| E1.8 | Every script parses under Windows PowerShell 5.1 | **GO** | parsed with the real PowerShell parser on Windows; all four files, 0 errors |
| E1.9 | The database-name guard refuses anything but a throw-away target | **GO** | executed on Windows: `rcs_pilot`, `rcs_dev`, `postgres`, `template1`, an injection attempt and an empty name are all refused; `rcs_restore_test_*` accepted |
| E1.10 | The directory guard refuses drive roots, Windows, Program Files and any repository | **GO** | executed on Windows: all refused; `C:\ProgramData\RCS\Pilot` accepted |
| E1.11 | Backup dumps the database before copying documents, and never mirrors or deletes | **GO** | unit test on ordering; no `/MIR`, no `Remove-Item`; credentials excluded from the recovery point |
| E1.12 | Scripts are UTF-8 with BOM so PowerShell 5.1 reads them correctly | **GO** | unit test; this was a real defect found by running the parser, not by reading the files |

## E2. NOT verified — must be done on the employee's laptop

Nothing in this section could be tested here: this engineering machine has **Smart App Control enforcing**, which
blocks locally built executables, and it has **no native Windows PostgreSQL**. Every item below is therefore
unverified by construction, not by omission.

| # | Item | State | What closes it |
|---|---|---|---|
| E2.1 | `Rcs.Launcher.exe` actually runs on the laptop | **NO-GO** | **Expect a block if the laptop runs Smart App Control or WDAC/AppLocker — it was blocked on the engineering machine (E1.14).** Test before the employee sees it; if blocked: sign both executables, have IT allow them, or point the desktop shortcut at `start-rcs.ps1`, which runs under Microsoft-signed PowerShell |
| E2.2 | `install.ps1` completes end to end against the laptop's PostgreSQL | **NO-GO** | run it elevated; it must reach step 10 and report healthy |
| E2.3 | Desktop icon → browser → sign-in works for the employee | **NO-GO** | watch them do it once, including the forced password change |
| E2.4 | Second double-click re-opens the browser instead of starting a second copy | **NO-GO** | double-click twice; check only one `Rcs.Web.exe` in Task Manager |
| E2.5 | Case creation, document upload and download on the laptop | **NO-GO** | one real case, one real document, downloaded and compared |
| E2.6 | Data survives an application restart and a laptop reboot | **NO-GO** | stop, reboot, open again; the case must still be there and the session must be signed out cleanly |
| E2.7 | `backup.ps1` from the desktop shortcut produces a recovery point | **NO-GO** | run it; check `RECOVERY_POINT` and `objects.sha256` exist |
| E2.8 | `restore-test.ps1` passes against that recovery point | **NO-GO** | run it; `verify-documents --rehash` must report 0 findings |
| E2.9 | One recovery point copied to media kept off the laptop | **NO-GO** | copy to a USB disk; a laptop is a single point of failure |
| E2.10 | Disk encryption, screen lock and endpoint protection are on | **NO-GO** | confirm with IT before real documents are entered |

## E3. Limitations specific to this mode

- **No document previews** (Linux-only converters and sandbox). Documents upload and download normally and the UI
  says so honestly.
- **`RequireSecureCookie` is false** — accepted because the only address is `http://127.0.0.1` on that machine.
- **No access from any other computer**, by design: the launcher refuses a non-loopback address.
- **The laptop is the whole system.** Its loss is the loss of the pilot data unless a recovery point is off it.
