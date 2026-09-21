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
