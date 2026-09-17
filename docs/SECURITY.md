# RCS — Security Requirements and Threat Model

**Status:** Draft v1 (design only — no code, no migrations, no configuration files)
**Authoritative inputs (all frozen v1):** `/docs/PROJECT.md`, `/docs/DOMAIN_MODEL.md`,
`/docs/WORKFLOW.md`, `/docs/PERMISSIONS.md`, `/docs/DOCUMENT_MODEL.md`.
**Deferred to `ARCHITECTURE.md`:** deployment topology, host and network build, backup product and
media rotation, monitoring stack. This document states the security requirements those decisions must
satisfy.
**Post-review amendments (2026-09-17):** per-version document authorization and cross-case letters
(§7.2, §7.3, B-19, G-07, invariant 7); bounded type detection (§10.1, invariant 10); backup
recovery-point invariant (§14.3, B-48, G-10); immediate suspension on departure (§3.1, §17.2, B-13, G-08,
invariant 5). Recorded in `DOMAIN_MODEL.md` §12.7.

---

## 1. Purpose

A confidential municipal case management system, ~13 employees, LAN only, no cloud, no remote access,
no Internet dependency for normal operation, and no integration with the external government
correspondence system in V1 (`PROJECT.md` §1.1, §2).

It holds official correspondence copies, maps, land-related records, interagency opinions, supporting
documents and the complete internal workflow history of every case.

The objective is **strong practical security for a small on-premise system, without enterprise-security
theatre** — controls this department will still be operating correctly in five years.

### 1.1 What this document refuses to do

| Not doing | Because |
|---|---|
| Treating "no Internet" as a security control | it removes one class of attacker and none of the others in §3 |
| Prescribing ceremonies nobody will sustain | a control performed twice and then abandoned is worse than one never adopted — it produces false confidence |
| Claiming application code can constrain root or the DBA | it cannot, and saying so would make every other claim here less trustworthy (§4.4) |
| Inventing organizational facts | where the physical or network environment is unknown, it is written as an assumption or an open question, not as a control |
| Mixing "must exist before go-live" with "nice if the risk ever justifies it" | §19 and §20 are separate for exactly that reason |

---

## 2. Security principles

| # | Principle | Consequence in this document |
|---|---|---|
| P1 | **Prefer controls that fail safe and need no upkeep** | binding PostgreSQL to localhost is worth more than a quarterly review meeting, because it keeps working while nobody is watching |
| P2 | **Where prevention is impossible, build detection** | root access cannot be prevented; it can be made visible and attributable (§4.4) |
| P3 | **Be honest about residual risk** | an acknowledged gap gets managed; a hidden one gets discovered during an incident |
| P4 | **Authorization is server-side, once, for every path** | UI, API, download, search, export and audit all pass the same check (§7) |
| P5 | **Untrusted input is untrusted everywhere** | uploaded files are never executed, never rendered inline, never parsed by a privileged process (§10, §11) |
| P6 | **Least privilege for processes, not just people** | the application runtime is not a DB superuser and cannot write outside its own directories (§12) |
| P7 | **Availability and recoverability are security properties** | ransomware and accidental deletion are the most likely serious incidents here, so backup design is a security control, not an operations afterthought (§14) |
| P8 | **Evidence is not deletable** | the frozen no-hard-delete model (`PROJECT.md` §29.6) is also a security control: a compromised user account cannot erase business history (§14.4) |
| P9 | **Every business user is an individual** | shared accounts destroy attribution, which is the foundation of every other control here (§6.1) |
| P10 | **Usable security only** | a control that pushes staff toward shared logins or paper workarounds has made things worse (§8.4, §6.3) |

---

## 3. Threat model

### 3.1 Actors and what they can realistically do

| # | Threat | Realistic capability | Primary controls |
|---|---|---|---|
| T1 | **Careless employee** | mis-files a letter, links a document into the wrong case, emails a copy out, leaves a screen unlocked | authorization (§7), audit (§9), OS screen lock (§8.5), no-hard-delete (§P8) |
| T2 | **Compromised workstation** | acts with the logged-in user's rights; reads what they read; uploads what they can upload | session controls (§8), no direct store access (§5.3), bounded user authority (§7) |
| T3 | **Malware via USB** | infects a workstation; encrypts what that workstation can write | **workstations cannot write the document store** (§5.3, §14.4) |
| T4 | **Malicious external document** | macro or exploit runs **on the workstation that opens it**, not on the server | never executed server-side (§10), download-only (§10.3), endpoint AV (§11) |
| T5 | **Departed employee account** | logs in with credentials that were never revoked | **immediate suspension** + session revocation, not waiting for handover (§17), departure checklist (§17.2) |
| T6 | **Shared passwords** | destroys attribution; one leak becomes everyone's access | individual accounts (§6.1), usable password policy (§6.3), practical timeouts (§8.4) |
| T7 | **Unlocked workstation** | full access as that user, with no trace that it was someone else | **OS screen lock is the only real control** (§8.5) — application timeouts do not solve this |
| T8 | **Another device on the LAN** | sniffs traffic, spoofs a hostname, probes services | HTTPS (§5.4), minimal exposed services (§5.2), no DB or file-share exposure (§5.3) |
| T9 | **Unauthorized LAN access** (visitor port, shared switch, weak Wi-Fi) | reaches the application's network position | same as T8, plus segmentation if available (§5.5) |
| T10 | **Misuse by a legitimate user** | reads or exports cases they are entitled to see, for the wrong reason | authorization limits *scope*; audit provides *accountability* (§9, §16). Cannot be prevented, only attributed |
| T11 | **TechAdmin / OS administrator** | full read/write to database, files and backups | **not preventable** — detective and organizational controls only (§4.4, §8 of `PERMISSIONS.md`) |
| T12 | **DB administrator** | reads and modifies any row, including audit | same as T11 (§9.5) |
| T13 | **Stolen server or backup media** | offline access to everything on the disk | full-disk encryption (§13.3), **encrypted backup media** (§13.4) |
| T14 | **Ransomware** | encrypts everything reachable from the compromised account or host | blast-radius design (§14.4) — this is the most likely serious incident |
| T15 | **Accidental deletion** | a wrong click, a wrong script, a wrong `DROP` | no hard delete in the business model, backups, migration role separation (§12.2) |
| T16 | **Corrupted backups** | the backup exists and is useless | integrity verification (§14.5), **tested restore** (§14.6, §21) |
| T17 | **Physical server access** | boots from USB, removes the disk, resets credentials | locked room/cabinet, BIOS/boot controls, FDE (§15) |
| T18 | **Future developer/maintainer** | good-faith changes that quietly remove a control | invariants (§23), documented rationale, go-live gate (§21) |

### 3.2 Explicitly out of scope for V1

Nation-state adversaries, hardware implants, side-channel attacks, and insider threat by a
security-cleared adversary with unlimited time. A 13-person municipal department cannot defend against
these, and pretending otherwise would distort every proportionate decision in this document.

### 3.3 The two most likely serious incidents

Stated plainly, because they should drive the priorities:

1. **Ransomware reaching the server or the backups.** Everything in §14 exists for this.
2. **Loss of data through accident or hardware failure combined with an untested backup.** §14.6 and
   the go-live gate (§21) exist for this.

Sophisticated targeted attack is a distant third.

---

## 4. Trust boundaries

### 4.1 The boundaries

```
 ┌── UNTRUSTED ────────────────────────────────────────────────────────┐
 │  Uploaded file content · the LAN itself · any other device on it    │
 └─────────────────────────────────────────────────────────────────────┘
        │
 [B1] workstation / browser  ──HTTPS──▶  [B2] reverse proxy
                                              │
                                        [B3] application process
                                          │            │
                        ┌─────────────────┘            └──────────────┐
                  [B4] PostgreSQL                          [B5] object store
                  (localhost only)                         (app OS user only)
                                          │
                                   [B6] backup target  ──pull──▶  [B7] offline media
                                          ▲
                                          │
                        [B8] TechAdmin / OS root — above B3, B4, B5
```

### 4.2 What each boundary is, and whether code can enforce it

| # | Boundary | Enforceable by application code? | Enforced by |
|---|---|---|---|
| B1 | Workstation → network | **No** | the workstation is outside our control; treat every request as coming from a possibly-compromised client |
| B1→B2 | Network → reverse proxy | Partly | TLS (§5.4), firewall, minimal exposed ports (§5.2) |
| B2→B3 | Proxy → application | **Yes** | authentication (§6), authorization (§7), session handling (§8) — **this is where almost all enforceable security lives** |
| B3→B4 | Application → PostgreSQL | **Yes, partly** | DB role privileges (§12.2); the app cannot exceed its granted rights |
| B3→B5 | Application → object store | **Yes, partly** | filesystem ownership and mode (§12.4); the app process cannot read outside its own tree |
| B4/B5 → workstation | **Must not exist** | **Yes** | DB bound to localhost, no SMB/NFS share (§5.3) — invariants 2 and 3 |
| B6 | Server → backup target | **Yes, by direction** | the backup **pulls**; the server holds no write credential for it (§14.2) |
| B7 | Offline media | **No** (physical) | physical custody, encryption (§13.4, §15) |
| B8 | Root / DBA → everything | **No. Not at all.** | §4.4 |

### 4.3 Where the enforceable perimeter actually is

Everything this application can meaningfully control happens at **B2→B3**: a request arrives, is
authenticated, is authorised, and is audited. Below that line the application is a client of the
database and the filesystem, constrained by the privileges the operating system gives it. Above that
line it controls nothing.

This is why §7 (authorization) and §12 (least privilege) carry most of the weight, and why §5.3 matters
so much: **anything reachable without passing through B3 has no security at all.**

### 4.4 The honest statement about root and the DBA

> A person with root on the server, or superuser on the database, **can read every case, read every
> document, alter any record including the audit trail, and disable any control described in this
> document.** No application-level measure changes this. On a 13-person deployment the TechAdmin is
> very likely the same person who administers the operating system and the database.
>
> **This document does not claim otherwise, and no control below should be read as constraining that
> person.**

What is actually achievable, and is therefore what §9.5, §12 and §14 are built around:

| Control type | Example | Honest strength |
|---|---|---|
| **Separation** | the application's DB role is not superuser; a separate human credential is needed for administration | stops *accidents* and makes deliberate action a distinct, visible act — does not stop a determined admin |
| **Detection** | administrative logins and privileged actions logged where the admin does not routinely operate (§16.3) | an admin can erase the log — but must decide to, which is itself a different act |
| **Independence** | backups the primary server cannot write (§14.2); offline copies | **the strongest control available here**, because it removes one machine's ability to destroy the evidence of what it did |
| **Organizational** | a second person holds Head authority; break-glass credentials in a sealed envelope; two-person custody of backup media | not technical, and the most effective of the four at this scale |

The honest summary: **a hostile administrator is not a solvable problem in a 13-person department. A
careless or compromised one largely is**, and that is what these controls target.

---

## 5. Network security

### 5.1 Assumed topology

Only what `PROJECT.md` §2 confirms: ~13 workstations, a local LAN, one local application server running
the application, PostgreSQL, document storage and audit logs. Browser access over the LAN to an internal
hostname (`PROJECT.md` §2, `https://cases.internal.example`, never `.local`).

**Nothing else is assumed.** VLANs, managed switches, a domain controller, 802.1X and network monitoring
may or may not exist; where a control depends on one, it is written conditionally or raised in §22.

### 5.2 Exposed services

| Service | Bind | Reachable from | Rationale |
|---|---|---|---|
| HTTPS (application, via reverse proxy) | server LAN address, **443** | workstation subnet | the only service normal users need |
| HTTP, **80** | optional | workstation subnet | **redirect to HTTPS only**, serving no content. Omit entirely if the redirect is not needed |
| PostgreSQL, 5432 | **localhost / Unix socket only** | nothing outside the host | invariant 2 |
| Administrative access (SSH or console) | restricted | a named admin workstation or the physical console | not the general workstation subnet |
| Everything else | — | — | **default deny inbound** |

**Firewall expectation:** default-deny inbound on the server, with explicit allows for the rows above.
This is a host firewall requirement, so it holds even if the network has no segmentation at all.

### 5.3 Two things that must never be exposed

| Never | Why | Invariant |
|---|---|---|
| **PostgreSQL reachable from workstations** | it bypasses B3 entirely — every authorization rule in `PERMISSIONS.md` becomes irrelevant to anyone with a client and a credential | 2 |
| **The document store as an SMB/NFS share** | it bypasses B3 *and* hands ransomware on any workstation a writable path to every case document. "Just for convenience" is how this gets added | 3, and §14.4 |

The object store is accessed **only by the application process**, as its own OS user (§12.4).
Workstations reach documents through the application, which authorises and audits every download
(`PROJECT.md` §23, `DOCUMENT_MODEL.md` §6.6).

If someone later needs bulk access to files, the answer is an authorised, audited **export** (§10.6),
not a share.

### 5.4 HTTP vs HTTPS — HTTPS, with a realistic certificate story

**Why plain HTTP is not acceptable**, even on a LAN: any device on the same network can capture
credentials and session cookies in cleartext, and can spoof an internal hostname to harvest them. "No
Internet" does nothing about T8 and T9. A shared municipal LAN is not a trusted medium.

**Mechanism:** an **internal certificate authority** (or a locally managed equivalent), whose root
certificate is installed in the workstation trust stores.

| Requirement | |
|---|---|
| **No public ACME, no external DNS** | production must not depend on either (`PROJECT.md` §25, §29) |
| **No OCSP/CRL URLs pointing at the Internet** | a certificate carrying an unreachable revocation URL can make browsers stall or fail while disconnected. Internal certificates must omit them or point only at an internal responder (§18.3) |
| **Root certificate distribution** | installed once per workstation, by the same process that builds a workstation. Long-lived (e.g. 10 years) so this is a one-time task |
| **Server certificate lifetime** | practical rather than fashionable — long enough that renewal is a rare, planned event; short enough to be replaceable |
| **Certificate must cover the hostname staff actually type**, and that hostname must resolve locally (hosts file or internal DNS), never via external DNS | |

**Expiry must never become a surprise outage.** This is the single most likely way HTTPS fails in a
small department:

| Control | |
|---|---|
| **The application monitors its own certificate** and shows an expiry warning to TechAdmin and Head, starting well before expiry — reusing the operational-health surface `PROJECT.md` §20 already requires for backups | |
| **A named person owns renewal**, with a named deputy (§22, OQ-S2) | |
| **A documented renewal procedure** that works entirely offline, stored where it can be read when the system is down (not only inside the system) | |
| **Recovery if it expires anyway:** issue a replacement from the internal CA and restart the proxy. Browsers will warn until then. **The fallback is never "turn off TLS"** — a permanent downgrade always follows a temporary one | |

### 5.5 LAN segmentation

**Recommended if the organization has the capability**: the server on its own network segment, reachable
from workstations only on 443, with administrative access restricted further.

**If it does not** — which is entirely possible here — the host firewall (§5.2) and the two
never-expose rules (§5.3) still hold and are the controls that matter. Segmentation is defence in depth,
not a prerequisite.

**Risk if the LAN is shared with other departments:** every other department's workstations, printers
and unmanaged devices sit in the same broadcast domain as this server. An infected machine anywhere in
the building can reach port 443 and attempt to authenticate, and can attempt to spoof the internal
hostname. This raises the value of HTTPS (§5.4), rate limiting (§6.4) and host-level default-deny
(§5.2). It is an accepted V1 condition, recorded as **OQ-S1** (§22).

---

## 6. Authentication

The frozen model supports `LOCAL` and `ACTIVE_DIRECTORY` as identity sources
(`DOMAIN_MODEL.md` §2.3). **Neither is implemented here; AD integration is not designed.**

### 6.1 Individual identity is non-negotiable

Every business user has their own account (invariant 4). No shared accounts, no role accounts, no
"reception" login. Every control that depends on attribution — the entire audit trail, temporary
coverage (`PERMISSIONS.md` §22), high-risk action accountability — collapses without this.

**There is no "log in as another user" feature, ever** (invariant 18, `PERMISSIONS.md` §18). Support is
performed by watching the user's screen or by reproducing on test data (§18.4), never by assuming their
identity.

### 6.2 Local account credential storage

| Requirement | |
|---|---|
| **Hashing** | a memory-hard algorithm designed for passwords — **Argon2id preferred**; bcrypt or scrypt acceptable. Never a bare SHA/MD5, never unsalted, never reversible encryption |
| **Per-password salt**, generated by the algorithm, stored with the hash | |
| **Parameters tuned on the actual server** to a deliberate cost, and **recorded** so they can be reviewed later | |
| **Stored in a separate table** from `user` (`DOMAIN_MODEL.md` §2.3 already requires this) so user records can be read freely for display | |
| **Upgradeable on login**: if parameters or algorithm change, rehash transparently at next successful login | |
| **Never logged, never echoed, never included in audit payloads, never in error messages** | |

### 6.3 Password policy — long, not baroque

Complexity theatre produces `Summer2026!` on a sticky note. The policy is deliberately simple:

| Rule | Value | Why |
|---|---|---|
| **Minimum length** | **12 characters**, passphrases encouraged | length is what matters; a four-word phrase is both stronger and more memorable than mandated punctuation |
| **Character composition rules** | **none** | they reduce entropy in practice by pushing everyone to the same patterns |
| **Forced periodic rotation** | **none** | it drives incrementing suffixes and sticky notes. Rotate on *evidence*: suspected compromise, shared credential, departure (§17) |
| **Blocklist** | reject the obvious — the department name, the application name, `password`, sequences, and a **local** list of common passwords shipped with the release | no online breach-check service: that would be an Internet dependency (§18.3) |
| **Maximum length** | none below a sane bound (e.g. 128) | never truncate silently |
| **First login** | **must change** the temporary credential before anything else (§6.6) | |

### 6.4 Rate limiting and failed logins

| Control | Baseline |
|---|---|
| **Progressive delay** per account after consecutive failures — a short, growing pause | slows guessing without a hard lock |
| **Temporary lock** after a sustained run of failures, auto-releasing after a configurable interval | avoids a permanent self-inflicted denial of service on a 13-person system where an admin may be absent |
| **Per-source limiting** as well as per-account | one host attempting many accounts is the more interesting pattern |
| **TechAdmin can release a lock immediately**, audited | someone must be able to unstick a colleague |
| **Every failure logged** with account, source and time (§16.2) | a lock that nobody can see is not a control |
| **Generic failure message** — never distinguish "no such user" from "wrong password" in the response | cheap; marginal at 13 known users, but free |

**Deliberately not adopted for V1:** CAPTCHA (needs external assets or adds a local dependency for very
little here) and MFA (§20.1).

### 6.5 Account states

Per `DOMAIN_MODEL.md` §2.3 and `PERMISSIONS.md` §25: `ACTIVE`, `SUSPENDED`, `DEACTIVATED`; never
deleted.

**A `SUSPENDED` or `DEACTIVATED` user cannot authenticate, and their existing sessions are revoked
immediately** — not at next expiry (invariant 5). This requires server-side session state (§8.2); a
self-contained stateless token cannot be revoked, which is why one is not used.

### 6.6 Active Directory — requirements only, not a design

If AD is adopted later (`PROJECT.md` §14): the directory authenticates; **this application still owns
authorization** (`PERMISSIONS.md` roles and grants are not directory groups in V1); `user.auth_source`
and `directory_identifier` record the mapping; a disabled directory account must be reflected here
promptly; and the connection must be to an **internal** directory over a protected channel. No external
or cloud identity provider is acceptable (`PROJECT.md` §14).

**Break-glass must survive directory failure**: at least one local account with administrative
capability must exist so the department is not locked out when the directory is unavailable (§6.8).

### 6.7 Password reset — realistic, with no email or SMS

There is no email, SMS or cloud recovery channel. The reset path is **in person**, which for a
13-person department in one building is not a limitation.

| Step | Rule |
|---|---|
| 1 | The user asks TechAdmin, in person. Identity is verified by recognition — write down whatever the department's actual practice is (§22, OQ-S3) |
| 2 | TechAdmin triggers a reset. The system generates a **random temporary credential**; TechAdmin never chooses it and never learns the user's old one |
| 3 | The temporary credential is delivered directly to the user, is **single-use**, and **expires quickly** |
| 4 | **All existing sessions for that account are revoked** (§8.3) |
| 5 | The user **must change it at first login** before doing anything else |
| 6 | The reset is audited: actor = **TechAdmin**, target = the user, plus the later self-service change by the user. Two events, two actors, no ambiguity about who did what |
| 7 | **TechAdmin cannot use the temporary credential to log in as the user** — using it is an authentication as that user, which the audit records; the protection is that it is single-use, short-lived and immediately visible to the user, who will find their password no longer works |

**Honest limit:** a TechAdmin who resets someone's password *can* use it before the user does. This is
inherent to any reset mechanism without an independent channel. It is mitigated by being single-use,
short-lived, loudly audited and immediately obvious to the user — detection, not prevention (§P2).

### 6.8 Break-glass recovery

Three scenarios the department must survive, none requiring an online channel.

| Scenario | Path |
|---|---|
| **Head locked out** | Another user holding the Head role restores access. **The primary control is organizational: at least two people should hold Head.** `PERMISSIONS.md` §25.2 makes Head the sole role granter, so a single Head is a genuine single point of failure — see §6.9 |
| **Sole TechAdmin unavailable** (ill, on leave, departed) | A sealed break-glass credential with administrative capability, held outside the TechAdmin's control |
| **Administrator departure** | Break-glass used once, then **every secret they held is rotated** (§13.5) |

**Break-glass credential requirements:**

| Requirement | |
|---|---|
| Physically sealed and tamper-evident — a signed envelope in the department safe is sufficient and will actually be maintained | |
| **Custody by two people who are not the same person as TechAdmin** — e.g. Head plus a deputy | |
| Its use is **auditable and conspicuous**, and must be reported after the fact | |
| **Rotated after every use**, and after any departure of a custodian | |
| **Tested** — at least once, before go-live, confirm it actually works (§21). An untested break-glass is a piece of paper | |
| Its existence, location and procedure are documented **outside the system**, because the system may be what is broken | |

### 6.9 A single-Head deployment is a recovery risk

`PERMISSIONS.md` §25.2 deliberately makes Head the only role granter, so that a technical administrator
cannot grant themselves business authority. The consequence is that **if exactly one person holds Head
and they are unavailable, no role can be granted or revoked** until they return or break-glass is used.

This is not a contradiction — it is the intended trade-off — but the department should know it.
**Recommended:** at least two Head-role holders. Otherwise break-glass (§6.8) becomes a routine
dependency rather than an emergency measure. Recorded as **OQ-S4** (§22).

---

## 7. Authorization

`PERMISSIONS.md` is authoritative for *who may do what*. This section states only the **security
requirements for enforcing it**.

### 7.1 Server-side, every path, no exceptions

The conceptual check is `PERMISSIONS.md` §14.2:

```
can(actor, action, object) =
        actor.status == ACTIVE
    AND visible(actor, object)
    AND capability(roles_of(actor, now), action)
    AND relationship_ok(actor, action, object)
    AND state_allows(action, object)
    AND (not high_risk(action) OR reason_supplied)
```

**Every one of these must pass it** (invariant 6):

| Path | Requirement |
|---|---|
| Page / view access | checked server-side before rendering anything |
| **Search results** | **filtered in the query**, not filtered after retrieval and not hidden in the UI |
| API / RPC actions | every call, reads included |
| **Document metadata** | a metadata view is a read of case content |
| **Document download** | re-checked on **every** download, against the context it is requested through (§7.3) |
| Export, print, bulk download | same checks, plus an `EXPORT`/`PRINT` audit event (§10.6) |
| Restricted case access | §7.2 |
| **Audit viewer** | `PERMISSIONS.md` §26 footnotes ¹¹ and ¹² — Workers see cases they can see; TechAdmin sees technical events only (§9.4) |

**Hiding a button is not a permission.** Every action must fail server-side when attempted directly,
and the refusal is recorded as `PERMISSION_DENIED` (§16.2).

**Roles are evaluated at action time** from `user_role` validity windows, never from a cached login
claim (`PERMISSIONS.md` §27.3). A revoked role, an expired temporary cover or a revoked grant takes
effect on the **next action**.

### 7.2 Restricted cases

Visibility per `PERMISSIONS.md` §19: the assigned users, Chief, Head, and holders of an `ACTIVE`
`case_access_grant`. Nobody else — deny by default.

**Obscurity is not a control** (invariant 7). None of these may reach a restricted case:

| Attempt | Why it fails |
|---|---|
| Guessing a document, case or version UUID | identity is not authority — the check still runs |
| Seeing a document through one version and requesting another version of it | authorization is **per version**: only a visible `ACTIVE` link that exposes that version authorises it (`DOCUMENT_MODEL.md` §10.1) |
| Following a response registered in an ordinary case to the letter it came from, filed in a restricted case | the letter, its files, its metadata and its audit stay governed by the owning case; the response exposes only its own facts (`DOMAIN_MODEL.md` §2.8) |
| Relying on a link that was removed as a wrong placement | a `REMOVED` link grants nothing (`DOCUMENT_MODEL.md` §10.2) |
| Reusing a download URL seen elsewhere | every download re-authorises; no URL carries a grant |
| Requesting by content hash | the hash is never an API input; it is storage-internal |
| Constructing a filesystem path | workstations have no path to the store (§5.3) |
| A search, a count, a dashboard total, an export | **filtered before aggregation** (§7.4) |
| A previously-valid session after a grant was revoked | evaluated now, not at login (§7.1) |

### 7.3 One document linked to both a normal and a restricted case

The frozen rule (`DOCUMENT_MODEL.md` §10.3), restated here because it is the subtlest authorization
decision in the system:

| Question | Rule |
|---|---|
| May the user see the document at all? | if **any** `ACTIVE` link's context is visible to them |
| **Which versions may they see?** | **only those exposed by such a link** — the pinned version, or, for a floating link (home case only), the document's versions. The union is per version, never per document *(post-review)* |
| Which links are listed to them? | **only** those whose contexts they may view |
| Which context authorises a download? | **the one the user requested it through**, and only for a version that link exposes |
| Does the restricted link restrict the document elsewhere? | **No** |

**The tradeoff, stated honestly.** The alternative rule — *any restricted link restricts the document
everywhere* — sounds safer and is worse:

| | Union rule (chosen) | Intersection rule (rejected) |
|---|---|---|
| A map already visible as an attachment of an ordinary letter | stays visible | **silently disappears** for people who legitimately used it, the moment someone cites it in a restricted case |
| Effect on users | predictable | a document vanishing from a case with no explanation — and the explanation cannot be given, because it would reveal the restricted case |
| Leak risk | **linking into another case is a disclosure**, and is treated as one: a Chief action, reason-bearing, audited (`DOCUMENT_MODEL.md` §4.8) | retroactive hiding does not un-disclose anything already seen |
| Failure mode | a deliberate, audited act by a Chief | an invisible, automatic, unexplainable removal |

The union rule is chosen because restriction is a property of **the case**, not of the bytes, and
because the real control sits at the moment of linking — a deliberate, authorised, audited disclosure
decision — rather than in a retroactive hide that cannot undo what has already been read.

**What the disclosure covers is exactly one version.** A link into another case is always pinned, and
new versions can be added only in the document's home case (`DOCUMENT_MODEL.md` §4.4). So disclosing a
map from a restricted case discloses that map as it was chosen — never the revisions the restricted case
makes afterwards, and never their filenames or existence.

**No new ACL system is introduced.** `case_access_grant` remains view-only, one user, one case, and must
not grow per-action rights (`PERMISSIONS.md` §20.3).

### 7.4 Aggregates leak

A restricted case must be **absent** from counts, totals, charts, date histograms, dashboards and
exports for anyone who cannot see it. *"47 cases (3 hidden)"* discloses that a confidential dossier
exists, roughly when it was opened and often who is working on it. **Filter before aggregating**
(`PERMISSIONS.md` §27.2).

### 7.5 TechAdmin is not a business user

Per `PERMISSIONS.md` §18 and invariant 8: the TechAdmin role grants **no** business capability — cannot
approve a final result, waive a requirement, close or reopen a case, grant any role including their own,
or open a case screen at all. If the same person also does departmental work, they hold a **separate
business role** and every action is attributed under whichever authority it required.

This is an *application-level* separation. It does not constrain the same person at the OS or database
level (§4.4), and §12 is where that is addressed as far as it can be.

---

## 8. Session security

### 8.1 Cookie and request protections

| Control | Requirement |
|---|---|
| **`Secure`** | set — cookies never sent over plain HTTP. Requires §5.4 |
| **`HttpOnly`** | set — script cannot read the session cookie, limiting the damage of any XSS that slips through (§10.7) |
| **`SameSite`** | `Lax` minimum; `Strict` preferred — there is no cross-site flow in a LAN-only application with no external integrations |
| **CSRF protection** | required on every state-changing request: a per-session token bound to the session, validated server-side. `SameSite` alone is a useful second layer, not the primary control |
| **Session identifier** | high-entropy, generated by a cryptographically secure source, opaque, never in a URL |
| **Rotation** | a new session identifier on **login** and on any privilege change, to defeat fixation |
| **Scope** | cookie scoped to the application host and path; not shared with any other internal service |

### 8.2 Server-side session state

Sessions are **server-side records**, not self-contained stateless tokens. The reason is invariant 5:
deactivating a user, revoking a role or resetting a password must **immediately** end existing sessions,
and a stateless token cannot be revoked before it expires.

Each session records: user, creation time, last-activity time, source address, and a revocation flag.

### 8.3 Revocation

Sessions are revoked immediately on: account deactivation or suspension, password reset or change,
role revocation, explicit "sign out everywhere", and administrative action.

**Revocation is checked on each request**, not only at session creation.

### 8.4 Timeouts — a practical baseline, configurable

| Setting | Baseline | Reasoning |
|---|---|---|
| **Idle timeout** | **60 minutes**, configurable | long enough that a caseworker reading a long letter or attending a short meeting is not thrown out; short enough to bound an unattended session. A 10-minute timeout in a municipal office produces shared logins and paper workarounds (§P10) |
| **Absolute lifetime** | **one working day (≈12 hours)**, configurable | a session cannot survive overnight; tomorrow requires logging in |
| **Re-authentication for high-risk actions** | **not required in V1** | see §20.2; it is real friction and the actions are already reason-bearing and audited |

Both are configurable so the department can tighten them after real use. They should be reviewed
against actual behaviour rather than set aggressively on day one.

### 8.5 Unlocked workstations — the application cannot solve this

> An unattended, unlocked workstation with a live session is **full access as that user, with no trace
> that it was somebody else.** No application timeout prevents this: the attacker is present *during*
> the session, not after it.

The effective control is an **OS-enforced screen lock** on every workstation, after a short idle period
(e.g. 10–15 minutes), requiring the user's own credentials. That is an endpoint policy, outside this
application — but it is the control, and it belongs in the go-live checklist (§21) and in §22 (OQ-S5) if
the organization has no means to enforce it centrally.

Application idle timeout complements it by bounding *abandoned* sessions. It does not replace it.

---

## 9. Audit security

`DOMAIN_MODEL.md` §2.18 fixes the audit record: append-only, `event_seq` ordered, actor identity
snapshotted at event time, `before_state` / `after_state`, `entity_version`, `case_id` scope,
`correlation_id`. `WORKFLOW.md` §13 and `DOCUMENT_MODEL.md` §13 fix which actions are recorded.

### 9.1 The write path

| Requirement | |
|---|---|
| Audit rows are written **by the application, in the same transaction** as the business change | a committed change with no audit row, or an audit row for a rolled-back change, are both worse than useless |
| Writing audit is **not optional** for the actions the frozen documents list | |
| The audit write must not be suppressible by request parameters, headers or configuration flags | |
| Failure to write audit **fails the business operation** | the alternative is silent unaudited change |

### 9.2 Append-only, enforced by database grants

This is the concrete control and it is deliberately simple:

| Role | `audit_event` privileges |
|---|---|
| **application runtime role** | `INSERT` and `SELECT` only. **No `UPDATE`, no `DELETE`, no `TRUNCATE`** |
| **migration role** | DDL during deployment only; not used at runtime (§12.2) |
| **backup role** | `SELECT` only |
| **DB superuser** | everything — see §9.5 |

With no `UPDATE`/`DELETE` grant, **ordinary users cannot modify audit history even if the application
has a bug or an injection flaw** (invariant 13), because the privilege simply is not there. That is a
far stronger guarantee than application-side care.

### 9.3 Sequence ordering, and what it does not prove

`event_seq` (a database identity column) gives a **monotonic total order** independent of wall-clock
time, which matters because clocks move (§16.5).

**An honest caveat that must not be forgotten:** identity columns allocate outside transactions, so a
rolled-back transaction consumes a value. **Gaps in `event_seq` are normal and are not evidence of
tampering.** Any future integrity tooling must not treat gap-freeness as a tamper check — it would
produce constant false alarms and be switched off within a month.

### 9.4 Who may read audit

Per `PERMISSIONS.md` §26 footnotes ¹¹ and ¹²:

| Who | Business audit | Technical/security log |
|---|---|---|
| Worker | cases they can see, plus their own actions | no |
| Chief | department-wide | no |
| Head | all | no |
| **TechAdmin** | **no** — not the `before_state`/`after_state` of business records | **yes** — logins, denials, configuration, jobs, integrity (§16) |

Audit is case content: reading it is reading the case. Restricted-case audit follows restricted-case
visibility (§7.2).

### 9.5 What audit does and does not defend against

| Actor | Effect |
|---|---|
| **A** — normal application user | **Cannot** modify audit. No application path exists and no DB grant exists (§9.2). This is the case audit is genuinely built for |
| **B** — the application database role | **Cannot** modify audit — `INSERT`/`SELECT` only. So even a full application compromise (injection, RCE in the app process) cannot rewrite history, only append to it |
| **C** — DB superuser / root | **Can do anything**: alter rows, drop the table, reset the sequence, disable logging. **No application-level control changes this** (§4.4) |

Case B is the valuable one and is achieved with nothing more than a `GRANT` statement.

### 9.6 Restore implications

Restoring an older database backup **rolls the audit trail back with it** — events between the backup
and the failure are gone, exactly like the business data they describe. Consequences:

- audit is only as complete as the backup regime (§14);
- after any restore, the gap must be **recorded** — a note of what period was lost and why, made outside
  the restored system so it survives the next restore;
- the integrity sweep (`DOCUMENT_MODEL.md` §11) must run after restore, since document metadata and
  objects can now disagree (§14.6).

### 9.7 Cryptographic tamper-evidence — deliberately NOT in V1

`DOMAIN_MODEL.md` §2.18 deferred this here. **The decision is: not in V1**, and the reasoning matters
more than the conclusion.

A hash chain over audit rows defends against someone who can modify rows but **cannot recompute the
chain**. In this deployment the only actor who can modify rows is the DB superuser — who can also
recompute the chain, because it is computed on the same machine from the same data. **A hash chain
stored beside the data it protects, on a machine its adversary controls, is theatre.**

A chain only becomes meaningful when its head is **anchored somewhere the administrator cannot rewrite**.
That is a real control, it is cheap, and it belongs in optional hardening (§20.3) rather than V1:
periodically write a digest of the audit head to write-once or offline media held by someone else.

For V1 the robust, understandable controls are: append-only grants (§9.2), independent backups (§14.2),
and organizational custody (§4.4). They are far more likely to still be working in five years than a
chain nobody verifies.

---

## 10. Document, file and application input security

Uploaded files are untrusted input (invariant 9). This section is the security view of
`DOCUMENT_MODEL.md` §9; nothing here changes that model.

### 10.1 The four verbs, kept separate

| Verb | V1 position |
|---|---|
| **Store** | yes — bytes retained, content-addressed, immutable (`DOCUMENT_MODEL.md` §6) |
| **Download** | yes — authorised per request, as an attachment (§10.3) |
| **Preview** | **no server-side preview in V1** (§11.4) |
| **Process / parse** | **no.** The application does not open, parse, convert, extract text from, unzip or thumbnail uploaded files |

**The application never executes document bytes** (invariant 10). It hashes them, stores them and hands
them back. Exactly **two** operations read file content *(post-review clarification — v1 said "hashing is
the only operation", which contradicted content-based type detection)*:

- **hashing**, which cannot be influenced by that content; and
- **bounded type detection** (`DOCUMENT_MODEL.md` §9.2) — a fixed, limited number of leading bytes
  compared against a table of known file signatures.

Detection is **not parsing**: it does not decompress, traverse internal structure, follow references,
render or execute anything, and its cost does not depend on what the file contains. Anything beyond that
— opening a ZIP, reading a PDF object tree, extracting text — is processing, and remains forbidden
server-side (§11.5).

### 10.2 Never rendered inline under the application origin

Invariant 11. Serving user-supplied content inline from the authenticated origin gives that content the
application's session context — one uploaded SVG or HTML file would be enough to read another user's
data.

| Control | |
|---|---|
| **`Content-Disposition: attachment`** on every file response | never `inline` |
| **`X-Content-Type-Options: nosniff`** | stops the browser second-guessing the declared type |
| **A neutral content type** for anything potentially active (HTML, SVG, scripts) rather than one the browser will render | |
| **A restrictive `Content-Security-Policy`** on application pages, and a sandboxing policy on any file-serving response | |
| **Separate file-delivery origin** — recommended, not required in V1 (§20.4) | even a rendering mistake then cannot reach application session state |

### 10.3 Download safety

| Requirement | |
|---|---|
| Authorised on **every** download, against the requested context (§7.3) | |
| **Never a direct filesystem path, never a hash, in any URL or response** (invariant 7, `DOCUMENT_MODEL.md` §7.7) | |
| **Safe download filename** derived at request time from the document title, stripped of control, bidi and path characters, with the canonical extension for the **detected** type (`DOCUMENT_MODEL.md` §5.6) | a filename is user-supplied data, not an instruction |
| Detected type wins over the original extension, always | |
| Every download audited with `document_hash` and context (§16, `DOCUMENT_MODEL.md` §13.3) | |
| Range requests and caching headers must not create an unauthenticated path to bytes | |

### 10.4 Risky-but-required file types

The department may be **legally or operationally required to retain an official document that is
technically dangerous** — a macro-enabled spreadsheet from an authority is a real example. Refusing it
is a records-management failure dressed as security.

The policy is therefore **store safely, warn, restrict, allow controlled download**:

| | |
|---|---|
| **Store** | yes — bytes retained unmodified; the received document is evidence |
| **Warn** | the file's type is displayed prominently, with a clear indication that it may contain active content |
| **Restrict** | never previewed, never rendered inline, never parsed server-side |
| **Download** | permitted to authorised users, as an attachment, audited |
| **Responsibility** | opening it happens on the user's workstation, where endpoint protection applies (§11.2) |

Which types are accepted, and whether anything is refused outright, is a business decision —
`DOCUMENT_MODEL.md` OQ-D3 and OQ-D4, carried forward as **OQ-S6** (§22).

### 10.5 Upload-side validation

| Check | |
|---|---|
| Declared size vs bytes received; incomplete uploads never enter the store (`DOCUMENT_MODEL.md` §7.4) | |
| A configured maximum upload size, enforced at the proxy **and** the application | protects against trivial disk exhaustion |
| Type **detected from content**; the client's declaration is never trusted, and a mismatch is recorded in the audit event | |
| **The stored path derives from the content hash alone** — never from a filename, a title or a case number | this is why path traversal via filename is structurally impossible (`DOCUMENT_MODEL.md` §6.2) |
| Temporary upload files in a dedicated directory owned by the application user, mode 0700, never the system temp | §12.4 |
| Filenames stored verbatim as metadata, sanitised only for display and download (§10.3) | |

### 10.6 Export, print and bulk download

Legitimate paths by which confidential data leaves the application — `PROJECT.md` §16 search results,
case exports (`DOCUMENT_MODEL.md` §14), printing.

| Principle | |
|---|---|
| **Same authorization as the screen**, no shortcuts, filtered before aggregation (§7.4) | |
| **Bulk actions are audited as such** — `EXPORT` / `PRINT` with scope and item count; a case export records the hashes of what it contained | |
| **Restricted-case exports** are visible in audit as what they are: a bulk disclosure of a confidential dossier | |
| **Unusual bulk activity is a security event** (§16.2) — not blocked automatically, but visible | |
| **No DRM is attempted** | |

**Acknowledged and unpreventable:** an authorised user can photograph the screen, copy text by hand, or
print and carry paper out. No software control changes this. The response is attribution and
proportionate deterrence — the user knows the export was recorded — not an impossible technical barrier.

### 10.7 Application input and output security

User-entered text reaches many places: case titles and subjects, notes and internal records,
organization names and aliases, document titles, letter subjects, and filenames.

| Risk | Control |
|---|---|
| **Stored XSS** | **contextual output encoding at render time** — the durable control, because it protects data already in the database and data arriving from anywhere else. Input sanitisation is a supplement, never the primary defence |
| **Reflected XSS** | same encoding discipline; no user input reflected into script or attribute contexts |
| **HTML in user text** | user text is **not** rich HTML in V1. If formatted notes are ever wanted, that is a new decision with its own sanitisation requirement, not a quiet relaxation |
| **CSRF** | §8.1 |
| **SQL injection** | **parameterised queries only.** No string-built SQL anywhere, including dynamic search filters and sort orders — sort and filter names map through an allowlist, never a passthrough |
| **Path traversal** | structurally prevented: storage paths derive from the content hash, never from user input (§10.5) |
| **Unsafe filenames** | §10.3 |
| **Malformed upload metadata** | §10.5 |
| **Redirects** | no open redirects; any post-login destination validated against an internal allowlist |
| **Error messages** | no stack traces, SQL fragments, file paths or internal hostnames to the browser. Detail goes to the server log with a correlation id the user can quote |

Framework-level safe defaults are preferred over hand-written protection once a stack is chosen
(`DECISIONS.md`); this document fixes the requirements, not the mechanism.

---

## 11. Malware and preview security

### 11.1 Scanning is an optional infrastructure control, not an application feature

**No cloud scanning, ever** (`PROJECT.md` §2, §25). If scanning is adopted it is a **local engine on the
server**, and the application's only involvement is recording the verdict.

### 11.2 The offline signature problem, stated honestly

An on-premise scanner with no Internet access has **stale signatures**. Signature updates must arrive by
removable media, which is a manual task that will be performed enthusiastically for two months and then
sporadically. A scanner with six-month-old signatures provides real but **limited and quietly
degrading** value.

This is the reason server-side scanning is **not** the primary defence here. The controls that do not
degrade are:

| Control | Why it holds |
|---|---|
| **The server never executes or parses uploaded content** (§10.1) | no signature required |
| **Nothing is rendered inline under the application origin** (§10.2) | no signature required |
| **The document store is not reachable from workstations** (§5.3) | no signature required |
| **Endpoint protection on workstations** | workstations are where documents are actually opened, and are more likely to have a maintained update path |

Server-side scanning, if adopted, is **defence in depth on top of these** — never a substitute.

### 11.3 If scanning is adopted

| Requirement | |
|---|---|
| **Scan at upload**, out of band; a pending scan must not block registering an official letter (§P10) | |
| **Re-scan** stored objects when signatures are updated — yesterday's clean verdict is only as good as yesterday's signatures | |
| **Verdict is metadata**, recorded with the engine name, signature version and timestamp | |
| **Never silently delete, never quarantine by moving bytes out of the content store** | |
| **A malware finding must not destroy evidential history** (§11.4) | |
| Scanning runs **unprivileged**, with no database credentials and no write access to the content store | it is parsing hostile input, and is therefore itself a target |

### 11.4 A malware verdict does not delete the document

An infected file may still be **an official document the department genuinely received** and is required
to retain. Deleting it destroys evidence to solve a problem that deleting does not solve.

| | |
|---|---|
| **Bytes are retained** — `DOCUMENT_MODEL.md` §12 permits no physical deletion in V1 except orphaned temporary files | |
| **Downloads are blocked or heavily gated** by policy while the verdict stands, with the reason shown | |
| **The business record is unchanged**: `document_version.status` is not altered, because the business fact has not changed — only the safety judgement about the bytes (`DOCUMENT_MODEL.md` §9.6) | |
| **Audit records the finding** as a `JOB` event with no user, since no person initiated the scan (§16.2, `DOMAIN_MODEL.md` §2.18) | |

**Carried-forward open question:** whether a malware verdict should also be visible as a *business*
state is `DOCUMENT_MODEL.md` **OQ-D6**, and answering "yes" would require adding a value to
`document_version.status` — a change to the frozen model. **Not decided here** (§22, OQ-S7).

### 11.5 Preview and conversion — none in V1

No server-side preview, conversion, thumbnailing, text extraction or OCR in V1
(`PROJECT.md` §24, §31; `DOCUMENT_MODEL.md` §9.5). Users download and open files in their own
workstation applications.

If preview is ever built, five constraints hold — parsing untrusted documents is historically one of the
richest sources of remote code execution, so the parser must be assumed to be exploitable:

| # | Constraint |
|---|---|
| 1 | **Never a privileged process on the primary server.** An unprivileged, isolated worker: its own OS user, a constrained filesystem view, no database credentials, no write access to the content store |
| 2 | **No network access at all** from the converter — inbound or outbound |
| 3 | **Resource limits** — CPU, memory, wall-clock, output size — so a malformed file cannot exhaust the host (a decompression bomb is the easy case) |
| 4 | **A fresh temporary working directory per job**, destroyed afterwards; never the content store, never a shared temp |
| 5 | **Output is inert and derivative** — a flat image or plain text, treated as a regenerable cache, excluded from exports and evidence manifests. **The original file remains authoritative** |

No software is selected here (`DECISIONS.md`).

---

## 12. Database and server security

### 12.1 Network position

PostgreSQL binds to **localhost or a Unix socket only** (§5.2, invariant 2). If a future requirement ever
separates the database onto another host, that link must be on a restricted segment with TLS and
certificate verification — and it is a decision requiring an ADR, not a configuration change.

### 12.2 Database roles — least privilege, four roles

| Role | Privileges | Used by | Notes |
|---|---|---|---|
| **Application runtime** | `SELECT`/`INSERT`/`UPDATE`/`DELETE` on business tables as the model requires; **`INSERT`+`SELECT` only on `audit_event`**; **no DDL**; **not superuser**; cannot create roles or read other databases | the running application | invariants 12 and 13. The single highest-value privilege decision in this document |
| **Migration / deployment** | DDL on the application schema | deployment only, never at runtime | so a compromised running application cannot alter the schema, drop a table or remove an audit constraint |
| **Backup** | `SELECT` only (or the minimum a physical backup needs) | the backup process | a read-only credential is worth much less to an attacker |
| **Administrator** | superuser | **a human, interactively, rarely** | never embedded in the application, never used by a service |

**`DELETE` privileges deserve a second look at schema time.** The business model has no hard delete
(`PROJECT.md` §29.6), so the runtime role's `DELETE` need is limited — plausibly to session rows and
orphaned temporary records. Narrowing it further is cheap and reduces the blast radius of an injection
flaw or a compromised application process (§20.5).

**Row-level security is deliberately not used in V1.** Authorization lives in the application
(`PERMISSIONS.md` §27), and a second enforcement point expressing the same rules in a different language
would drift from it — the drift being more dangerous than the defence in depth is valuable, because the
two would disagree silently. Reconsider only if a frozen requirement ever demands enforcement below the
application (§20.6).

### 12.3 Credentials and connections

| | |
|---|---|
| Application DB credentials in a **file owned by the application user, mode 0600**, outside the source tree and outside the web root — never in source, never in a container image layer, never in a command line visible to `ps` (§13.1) | |
| Distinct credentials per role (§12.2); rotated on administrator departure (§13.5) | |
| Local socket connections preferred; if TCP on localhost, still authenticated | |
| Connection errors logged **without** credentials | |

### 12.4 Server filesystem

Separate OS users, minimum rights, nothing world-readable:

| Path | Owner | Mode | Notes |
|---|---|---|---|
| Application binaries / code | a **deploy** user (or root) | `0755`, **not writable by the application user** | a compromised application process cannot rewrite its own code |
| Configuration and secrets | application user or root | **`0600`** | §13.1 |
| **Document object store** | **application user** | **`0700`** | no other user, no group access, **no network share** (§5.3, invariant 3) |
| Upload temp directory | application user | **`0700`** | **same filesystem volume** as the store, so the final move is an atomic rename (`DOCUMENT_MODEL.md` §7.3) |
| Application logs | application user, readable by admins | `0640` | may contain identifiers; never credentials or file content |
| PostgreSQL data directory | **postgres user only** | `0700` | untouched by the application user |
| Backup credentials / keys | a **separate backup user** | **`0600`** | deliberately **not** readable by the application user (§14.2) |

| Principle | |
|---|---|
| **The application runs as its own unprivileged OS user**, never root | |
| **Separate service accounts** for application, database and backup — so a compromise of one is not automatically a compromise of the others | |
| A restrictive `umask` so new files do not become group- or world-readable by accident | |
| **Not reliant on convention.** "Developers agree not to edit that folder" is not a control; ownership and mode are (§P1) | |
| Hardening the host itself — minimal packages, no unused services, timely patching — belongs to `ARCHITECTURE.md` | |

---

## 13. Secrets and encryption

### 13.1 The secrets that exist

| Secret | Held by | Recovery matters? |
|---|---|---|
| Application database password | application user | yes — but reconstructible by an admin |
| **TLS private key** | reverse proxy user | yes — **losing it means reissuing from the internal CA** |
| **Internal CA private key** | offline, not on the application server | **critical** — it issues all future certificates |
| Session signing secret | application user | no — rotating it simply logs everyone out |
| Backup credentials | backup user, **not the application user** | yes |
| **Backup encryption passphrase** | **custodians, outside the server** | **absolutely critical — see §13.4** |
| Break-glass credential | sealed, two-person custody (§6.8) | critical |
| Future document encryption keys | only if §13.5 is ever adopted | critical |

### 13.2 Storage, and why not a vault

**Never in source code** (invariant 16), never in the repository, never in a build artifact, never in a
URL or command line.

Stored as **files owned by the right OS user with mode 0600**, injected as environment or config at
service start. For 13 users, **a dedicated secret-management system is not justified**: it adds a
service that must be highly available, backed up and recovered before the application can start, and it
introduces a new way for the department to be locked out of its own system. OS file permissions plus
user separation deliver most of the benefit with a fraction of the operational risk (§P1, §P10).

**Backup of secrets** is a separate, physical problem: the internal CA key, the backup encryption
passphrase and the break-glass credential must be recoverable **independently of the server**, because
the scenario in which they are needed is the one where the server is gone. Sealed envelopes in the
department safe, with named custodians, is the appropriate mechanism here and is likely to be
maintained.

### 13.3 Full-disk encryption — what it does and does not solve

| Solves | Does **not** solve |
|---|---|
| A stolen server or a removed disk | anything at all while the machine is running — the volume is unlocked |
| Disks sent for repair or decommissioned | root, the DBA, or a compromised application (§4.4) |
| Casual physical access to storage media | ransomware — the encryptor runs above the encryption layer |

**Recommended** for the server, with one operational reality made explicit:

> **An encrypted volume must be unlocked to boot.** Either someone is physically present at every
> restart — including unattended restarts after a power failure — or the key is released automatically
> by the hardware, which means anyone who steals the whole machine also steals the key.
>
> Neither option is wrong; **choosing one accidentally is.**

Key custody: the recovery key stored **outside the server**, with the other sealed secrets (§13.2). A
full-disk-encrypted server whose recovery key exists only on that server is a self-inflicted data-loss
risk, and a more likely one than the theft it defends against.

### 13.4 Backup encryption — the strongest case for encryption here

**Removable and offsite backup media should be encrypted.** It is the layer where encryption solves a
threat that actually matters: a backup drive is portable, lives outside the server room, is handled by
people, and contains the entire confidential dataset. T13 applies to it far more realistically than to
a server bolted in a locked cabinet.

> **The passphrase must be recoverable independently of the server and of any single person.** An
> encrypted backup whose passphrase is lost is not a backup. This risk is larger, in practice, than the
> theft risk it mitigates — so custody (§13.2) and a **restore test that actually decrypts** (§14.6) are
> not optional extras; they are what make backup encryption safe to adopt.

### 13.5 Application-level document encryption — NOT recommended for V1

Encrypting each document with an application-managed key sounds stronger and, here, is not:

| Consideration | |
|---|---|
| The application must decrypt on every download, so **the key lives on the same server as the data** | it does not defend against root, the DBA, a compromised application, or ransomware |
| It defeats **content addressing**, deduplication and hash-based integrity verification (`DOCUMENT_MODEL.md` §6, §11) unless carefully layered — a significant loss of real, working controls | |
| It adds a key-management failure mode where **losing a key silently destroys evidence** | |
| What it *does* add over full-disk encryption is protection of bytes at rest from someone with filesystem-but-not-application access — a narrow gap already covered by §12.4 ownership | |

**Full-disk encryption plus physical control plus encrypted backup media is the proportionate answer.**
Revisit only if a policy requirement demands it (§20.7), and design key custody first.

### 13.6 Rotation

Rotate on **evidence, not on a calendar**: suspected compromise, departure of anyone who held the
secret (§17), a shared credential discovered, and after every break-glass use (§6.8). Each secret in
§13.1 has a named owner responsible for rotating it (§22, OQ-S2).

---

## 14. Backup and ransomware security

Backup is a **security control** here, not an operations detail: it is the only defence that works after
prevention has failed. Media, software and rotation are `ARCHITECTURE.md`; the security requirements are
below.

### 14.1 A mirror is not a backup

`PROJECT.md` §20. A live mirror faithfully replicates deletion, corruption and encryption. It protects
against disk failure and nothing else in §3.

### 14.2 The primary server must not be able to destroy every backup

Invariant 14, and the single most important requirement in this section.

| Requirement | Mechanism |
|---|---|
| **The backup target pulls**, the server does not push | the server holds **no credential** that can write to or delete from the backup store. Ransomware running with full server privileges cannot reach it |
| **Backup credentials are not readable by the application user** (§12.4) | a compromised application cannot enumerate or reach backups |
| **At least one copy is offline** or otherwise unwritable from the primary — removable media, disconnected between rotations, or a write-once/immutable target | this is the copy that survives a total compromise of the running system |
| **Backup account is not a domain-wide administrator** | a compromise of the backup path must not become a compromise of everything else |

### 14.3 Consistency between database and object store

`DOCUMENT_MODEL.md` §6.7, restated because a restore that mixes vintages produces a broken evidence
record. **Corrected post-review:** v1 required "object store before the database", which is not
sufficient — an upload committing after the object copy but before the database backup yields a database
that references bytes the backup lacks. The requirement is:

> **Every object referenced by the selected database recovery point must exist, and verify against its
> hash, in the retained object set. A combined recovery point is valid only once that has been checked.**

Met either by a consistent database snapshot whose required object set is then copied and verified, or by
a physical base backup plus WAL whose recovery targets are published only up to an object-complete
boundary (`DOCUMENT_MODEL.md` §6.7, `ARCHITECTURE.md` §15.4). A logical dump is never a base for WAL
replay. The security consequence: **an offline or rotated copy is only a backup if it carries a valid
combined recovery point** — the database state *and* every object it references.

### 14.4 Ransomware blast radius

| Path | Reach | Why it is contained |
|---|---|---|
| **Infected workstation** | whatever that workstation can write | **The document store is not a share and PostgreSQL is not exposed** (§5.3). The malware can reach only the application's HTTPS interface, where it is confined to that user's authority — **and the business model has no hard delete**, so it cannot erase or encrypt case records through the application at all (§P8) |
| **Macro-enabled document opened by a user** | that workstation | same as above. The server never opens the file (§10.1) |
| **Malicious USB** | that workstation | same, plus §15.3 |
| **Compromised server / admin credentials** | the database, the object store, the running system | **the pull-based and offline backups are the control** (§14.2). This is precisely why the primary must not hold write credentials for its own backups |

The honest position: a compromised administrator account is a bad day. **Whether it is a bad day or the
end of the department's records depends entirely on whether §14.2 was implemented.**

### 14.5 Backup integrity must be visible

| Requirement | |
|---|---|
| Backup success/failure is **visible in the application**, per `PROJECT.md` §20 — last backup, last verified restore, status | a silently failing backup is the worst possible state: all of the cost, none of the protection |
| **Failure is surfaced to people who will act**, not only written to a log nobody reads (§16.2) | |
| Backups are **verified**, not merely written — at minimum a checksum/readability check, ideally a periodic real restore | |
| The document-store integrity sweep (`DOCUMENT_MODEL.md` §11) must run **after every restore**, since metadata and objects can now disagree | |

### 14.6 Restore must be tested before go-live

Invariant 15. An untested backup is a hypothesis.

The test must cover the whole path, not just the database: restore the database **and** the object
store to a scratch environment, **decrypt encrypted media using the custodial passphrase** (§13.4), run
the integrity sweep, and confirm documents open and cases read correctly. Record the date and result;
`PROJECT.md` §20 already wants "last verified restore" on screen.

Repeat periodically, and after any significant change to the backup arrangement (§20.8).

---

## 15. Physical and removable-media security

Assumptions here must match what the organization actually has. What it does have is **OQ-S8** (§22);
below is what should be true, separated into baseline and optional.

### 15.1 Server

| Baseline | |
|---|---|
| The server is in a **locked room or locked cabinet**, not under a desk in an open office | |
| Physical access is limited to named people | |
| **UPS** with clean shutdown — power loss during a write is a realistic corruption source, and this department will have power cuts before it has attackers | |
| **BIOS/UEFI password set** and **boot order fixed** to prevent trivial USB boot; an attacker who can boot from USB has the disk unless it is encrypted (§13.3) | |
| Backup media stored **in a different room** from the server, ideally a safe — a fire or theft that takes the server should not take the backups | |

| Optional hardening | |
|---|---|
| Badge/keycard access with logging; cameras covering the server room; rack-level locks; tamper-evident seals; environmental monitoring | proportionate only if the organization already operates such facilities (§20.9) |

### 15.2 Workstations

| | |
|---|---|
| **OS screen lock** after a short idle period, requiring the user's own credentials — the control for T7 (§8.5) | |
| Full-disk encryption on workstations if any of them store downloaded case documents locally — and they will | |
| Endpoint anti-malware with a maintained update path (§11.2) | |
| A stolen workstation is a **credential and local-copy** problem: the response is session revocation, password reset (§6.7) and knowing what was stored locally | |

### 15.3 USB and removable media

**"Disable all USB" is not the answer.** Offline backup rotation *requires* removable media (§14.2), as
does moving antivirus signatures into an offline environment (§11.2). A blanket ban would be worked
around within a week, and would break the backup strategy that matters most.

| Risk | Realistic control |
|---|---|
| **Malware in** | endpoint AV scanning on insert where available; autorun disabled; **the document store is unreachable from workstations**, so a USB infection cannot reach case documents directly (§5.3) |
| **Data out (exfiltration)** | **not preventable at this scale** by technical means, and pretending otherwise wastes effort. Any authorised user can download to a USB drive, or photograph the screen (§10.6). The controls are: bulk downloads are audited (§10.6), unusual volume is visible (§16.2), and staff know both are true |
| **Backup drives** | **dedicated, labelled, encrypted** (§13.4), used for nothing else, never used to move day-to-day files, stored in the safe between rotations |
| **Maintenance media** | a known, controlled set rather than whatever is in someone's pocket |

If the organization has central endpoint management, a policy of "approved removable media only" is
worth adopting. If it does not, **do not write it down as a control** — it would be fiction (§1.1).

---

## 16. Logging and monitoring

### 16.1 Two different records, deliberately separate

| | **Business audit** (`audit_event`) | **Technical / security log** |
|---|---|---|
| Answers | what happened to this case, and who did it | what happened to this system |
| Governed by | `DOMAIN_MODEL.md` §2.18, `WORKFLOW.md` §13, `DOCUMENT_MODEL.md` §13 | this section |
| Read by | Workers (their cases), Chief, Head | **TechAdmin** |
| Lives in | the database, append-only (§9.2) | server log files |

They are separate because their readers, retention and purposes differ — and because business audit is
case content subject to case visibility (§9.4), while the technical log is not.

### 16.2 Security events worth logging

| Event | Why |
|---|---|
| **Failed login** (account, source, time) | the basic signal |
| **Login attempt on a disabled or suspended account** | a departed employee, or someone using their credentials |
| **Account lockout** and release | |
| **Password reset** performed (actor and target) | §6.7 |
| **Role granted or revoked** | the highest-privilege change in the system |
| **Restricted-case access granted or revoked** | a deliberate confidentiality decision |
| **Repeated authorization denials** for one user | either a broken screen or someone probing |
| **Unusual bulk download or export** | §10.6 — the realistic data-loss path |
| **Document integrity failure** | `DOCUMENT_MODEL.md` §11 — corruption or tampering |
| **Malware detection** | §11.3 |
| **Backup failure or missed backup** | §14.5 — arguably the single most important operational alert here |
| **Certificate approaching expiry** | §5.4 |
| **Large clock jump** | §16.5 |
| **Application start/stop, configuration change, migration run** | |

### 16.3 Where the log lives

Locally on the server, rotated by size and age. **Optional hardening (§20.10):** ship security events to
a second machine — the same machine that pulls backups is a natural candidate — so that an admin
operating on the primary is not also operating where the record of it is kept (§4.4, detective control).

### 16.4 Not surveillance

Log **security-relevant events**, not a behavioural record of thirteen colleagues.

| Do log | Do not log |
|---|---|
| Logins, failures, privilege changes, exports, denials, document downloads (already required as business audit) | every page view and every scroll as a monitoring feed |
| Enough to answer "who read the restricted case's documents?" after an incident | a productivity dashboard built from access records |

Document downloads *are* audited — `DOCUMENT_MODEL.md` §13.3 requires it, and it is the point at which
confidential content leaves the application. That is accountability for a specific confidentiality-
relevant act, not general monitoring, and the distinction should be explained to staff rather than left
to be discovered.

**Retention:** the technical log is kept long enough to investigate an incident noticed weeks later —
months rather than days. **No specific legal duration is invented**; business audit retention is
`DOMAIN_MODEL.md` OQ-9 and `DOCUMENT_MODEL.md` OQ-D1, unresolved (§22).

### 16.5 Time

Audit and history depend on time, and **public NTP cannot be assumed** with no Internet.

| Requirement | |
|---|---|
| **Use an internal time source if one exists** — a domain controller, router or appliance already serving the LAN | |
| If none exists, the server clock is **set correctly at install and monitored for drift** (§22, OQ-S9) | |
| **Ordering does not depend on the wall clock**: `event_seq` is a monotonic database sequence (§9.3), so audit order survives a clock change | |
| **Warn on large jumps** in either direction — forwards or backwards — as a security event (§16.2) | |
| **Never refuse to run because the clock looks wrong.** A refusal turns a clock problem into an outage | |
| A badly wrong clock **will** break TLS validation (§5.4) — a further reason to monitor it | |

---

## 17. Account lifecycle

Per `PERMISSIONS.md` §25. Accounts are **never deleted** (`PROJECT.md` §14) — history must keep
resolving to a real identity.

### 17.1 Creation and roles

TechAdmin creates the account; **Head grants the business role** (`PERMISSIONS.md` §25.2). A new account
has no roles and can therefore do nothing until a business decision is taken. TechAdmin cannot grant any
role, including their own — the control that prevents technical self-escalation.

### 17.2 Departure checklist

Conceptual, to be performed as one sequence:

| # | Step | Why |
|---|---|---|
| 1 | **Suspend the account immediately** (`status = SUSPENDED`, with reason) — **first, and never waiting for handover** *(post-review: v1 put reassignment before deactivation, so access stayed open for as long as reassignment took)* | `PERMISSIONS.md` §25.3 |
| 2 | **Revoke all sessions immediately** (§8.3) | invariant 5 — suspension without revocation leaves a live session |
| 3 | **Revoke business roles** (Head), leaving the historical `user_role` rows intact | |
| 4 | **Reassign open case responsibility** — the handover; required before final deactivation (`PERMISSIONS.md` §25.4), so no case is orphaned | |
| 5 | **Reassign or reconsider open requirements** assigned to them | |
| 6 | **Review and revoke `case_access_grant` rows** — a departed employee's grants to restricted cases should not persist | |
| 7 | **Rotate any shared secret they held** (§13.6) — for a departing TechAdmin this means DB credentials, TLS/CA keys, backup credentials and break-glass, and it is the whole point of §6.8 | |
| 8 | **Collect physical items** — workstation, keys, backup media, any sealed envelope in their custody | |
| 9 | **Deactivate the account** (`status = DEACTIVATED`, with reason) once handover is complete | final administrative step |
| 10 | **Record the departure** as a security event (§16.2) | |

**Never:** delete the account, reassign their identity to someone else, or reuse their username for a new
employee. Each would corrupt the audit trail that names them.

### 17.3 Absence is not departure

Temporary absence uses `TEMPORARY_COVER` assignments (`PERMISSIONS.md` §22): the account stays active,
the responsible officer of record is unchanged, and **the covering colleague signs in as themselves**.
Credential sharing is never the mechanism, and no feature exists that would permit it (§6.1).

---

## 18. Release and offline security

### 18.1 Building and shipping a release

| Requirement | |
|---|---|
| **Dependency lockfiles committed**, with exact pinned versions and integrity hashes | |
| **Dependencies vendored or mirrored** so a production build or install never fetches from the Internet (`PROJECT.md` §25) | |
| **Release artifacts are checksummed** (SHA-256) and the checksums are transferred separately from the artifact | |
| **Reproducible builds preferred**, not mandated — the practical requirement is that a given release can be rebuilt from a tagged source and verified | |
| **Repository access controlled**; releases tagged and traceable to source | |
| Dependency vulnerability review at release time, from a locally-held advisory source | no runtime callout, no telemetry |

### 18.2 Deployment

Migrations run under the **migration role**, not the runtime role (§12.2). The runtime service never
has DDL rights. Deployment **never** requires copying production data anywhere (§18.4).

### 18.3 No Internet dependency — the review checklist

Invariant 17. Production must work fully disconnected. These are the ways it accidentally will not:

| Hidden dependency | Requirement |
|---|---|
| **CDN-hosted JS/CSS** | all assets bundled and served locally |
| **Google Fonts or any remote font** | fonts bundled locally |
| Runtime package downloads | never; everything installed at build/deploy time |
| Telemetry / analytics / crash reporting | **none**, at any level, including framework defaults that are on unless disabled |
| License or update checks | none |
| **Certificate OCSP / CRL / AIA URLs pointing to the Internet** | internal certificates must omit them or point only at an internal responder — otherwise browsers may stall or fail while disconnected (§5.4). *A classic way an "offline" system turns out not to be* |
| External DNS | the internal hostname resolves locally |
| Cloud antivirus, cloud OCR, cloud AI | none (`PROJECT.md` §31) |
| Remote avatars, map tiles, link previews, iframe embeds | none |
| Browser-side integrity checks that fetch remotely | none |

**Verification is empirical, not a promise:** before go-live, disconnect the server's uplink and confirm
login, search, upload, download, export and printing all work (§21).

### 18.4 Developer and support access to production data

> **Production confidential data is not copied to a developer's laptop.** Not for debugging, not
> temporarily, not "just the one case".

| Situation | Path |
|---|---|
| Reproducing a bug | synthetic or anonymised data in a separate environment |
| A problem that only appears with real data | investigate **on the server**, with a named person present, under a temporary account or the developer's own audited account — not by extracting a copy |
| Needing a schema or volume sample | structure and synthetic rows, never real case content |
| Any exception | a documented, time-boxed, authorised decision by Head — not an engineering convenience (§22, OQ-S10) |

The reason is simple: a laptop leaves the building, and a copy of the case database is the entire
confidentiality of the department in one file. A support model that depends on copying it will
eventually lose it.

---

## 19. Required V1 baseline

**Controls that must exist before real confidential cases are entered.** Everything here is either free,
one-time, or something the department will maintain without effort. Nothing here requires a security
specialist.

### 19.1 Network and transport

| # | Control | § |
|---|---|---|
| B-01 | HTTPS with an internal CA certificate; root distributed to workstations; HTTP redirects only | 5.4 |
| B-02 | No Internet-pointing OCSP/CRL/AIA URLs on internal certificates | 5.4, 18.3 |
| B-03 | Named owner **and deputy** for certificate renewal; in-application expiry warning | 5.4 |
| B-04 | **PostgreSQL bound to localhost/Unix socket**, unreachable from workstations | 5.2 |
| B-05 | **Document store is not an SMB/NFS share**, reachable only by the application process | 5.3 |
| B-06 | Host firewall default-deny inbound; only 443 (and an optional 80 redirect) from workstations; admin access restricted | 5.2 |

### 19.2 Identity and access

| # | Control | § |
|---|---|---|
| B-07 | **Individual accounts for every user**; no shared or role logins | 6.1 |
| B-08 | **No "log in as user" feature exists** | 6.1 |
| B-09 | Argon2id (or bcrypt/scrypt) password hashing, per-password salt, tuned cost | 6.2 |
| B-10 | 12-character minimum, no composition rules, no forced rotation, local blocklist | 6.3 |
| B-11 | Rate limiting with progressive delay, auto-releasing lock, all failures logged | 6.4 |
| B-12 | First-login password change for every new or reset account | 6.7 |
| B-13 | **Suspension or deactivation immediately revokes existing sessions**; a departure suspends at once, before handover | 6.5, 8.3, 17.2 |
| B-14 | Documented in-person reset procedure, audited, TechAdmin cannot impersonate | 6.7 |
| B-15 | **Break-glass credential sealed, two-person custody, tested, documented outside the system** | 6.8 |
| B-16 | **At least two people hold the Head role** (or break-glass is accepted as routine) | 6.9 |

### 19.3 Authorization

| # | Control | § |
|---|---|---|
| B-17 | **Server-side `can()` on every path** — views, API, search, metadata, download, export, audit | 7.1 |
| B-18 | Search and aggregates **filtered before returning or counting** | 7.4 |
| B-19 | **Direct identifiers cannot bypass case restrictions**; downloads re-authorised per request against a context **and a version that context exposes** | 7.2, 7.3 |
| B-20 | Roles and grants evaluated **at action time**, not from a login claim | 7.1 |
| B-21 | **TechAdmin has no business capability and cannot grant roles** | 7.5 |

### 19.4 Sessions

| # | Control | § |
|---|---|---|
| B-22 | Secure, HttpOnly, SameSite cookies; server-side session records | 8.1, 8.2 |
| B-23 | CSRF protection on every state-changing request | 8.1 |
| B-24 | Session identifier rotated on login and privilege change | 8.1 |
| B-25 | Idle timeout (baseline 60 min) and absolute lifetime (baseline ~12 h), both configurable | 8.4 |
| B-26 | **OS screen lock enforced on workstations** — an endpoint prerequisite, not an application control | 8.5, 15.2 |

### 19.5 Audit and data

| # | Control | § |
|---|---|---|
| B-27 | **Application DB role has `INSERT`+`SELECT` only on `audit_event`** | 9.2 |
| B-28 | Audit written in the same transaction as the change; failure to audit fails the operation | 9.1 |
| B-29 | Audit readable per `PERMISSIONS.md`; **TechAdmin sees technical events, not business audit** | 9.4 |
| B-30 | **Application runtime is not a DB superuser and has no DDL rights** | 12.2 |
| B-31 | Separate migration, backup and administrator DB roles | 12.2 |

### 19.6 Files

| # | Control | § |
|---|---|---|
| B-32 | **No parsing, conversion, preview or execution of uploaded content server-side** | 10.1, 11.5 |
| B-33 | Downloads always `Content-Disposition: attachment` + `nosniff`; neutral type for active formats | 10.2, 10.3 |
| B-34 | **No filesystem paths or content hashes exposed to users** | 10.3 |
| B-35 | Type detected from content; declared type never trusted; mismatch recorded | 10.5 |
| B-36 | Upload size limit at proxy and application; incomplete uploads never stored | 10.5 |
| B-37 | Content-Security-Policy on application pages; no user content rendered inline under the app origin | 10.2 |

### 19.7 Application input

| # | Control | § |
|---|---|---|
| B-38 | Contextual output encoding everywhere; no rich HTML from users in V1 | 10.7 |
| B-39 | Parameterised queries only; sort/filter names via allowlist | 10.7 |
| B-40 | No stack traces, SQL or paths in browser-visible errors | 10.7 |

### 19.8 Server and secrets

| # | Control | § |
|---|---|---|
| B-41 | Application runs as its own unprivileged OS user; separate users for app, database, backup | 12.4 |
| B-42 | Object store `0700`, owned by the application user; temp dir `0700` on the same volume | 12.4 |
| B-43 | Application user **cannot write its own code directory** | 12.4 |
| B-44 | **Secrets in 0600 files, never in source control** | 13.2 |
| B-45 | Internal CA key, backup passphrase and break-glass credential held **outside the server**, sealed, with named custodians | 13.2 |

### 19.9 Backup and recovery

| # | Control | § |
|---|---|---|
| B-46 | **Backup target pulls; the server holds no write credential for backups** | 14.2 |
| B-47 | **At least one backup copy offline or otherwise unwritable from the primary** | 14.2 |
| B-48 | A combined recovery point is **published only when every object its database state references exists and verifies** in the retained object set; documented restore procedure | 14.3 |
| B-49 | **A full restore tested successfully**, including decrypting encrypted media | 14.6 |
| B-50 | Backup success/failure visible in the application and surfaced to a person | 14.5 |
| B-51 | **Removable/offsite backup media encrypted**, passphrase in custody (§13.4) | 13.4 |

### 19.10 Physical, time and release

| # | Control | § |
|---|---|---|
| B-52 | Server in a locked room or cabinet; UPS; BIOS/boot controls set | 15.1 |
| B-53 | Backup media stored in a different room from the server | 15.1 |
| B-54 | Full-disk encryption on the server, with recovery key in custody and the unlock model chosen deliberately | 13.3 |
| B-55 | Server clock correct, monitored, large jumps warned; ordering independent of wall clock | 16.5 |
| B-56 | Security events logged (§16.2) and backup/integrity failures alert a person | 16.2 |
| B-57 | **No Internet dependency — verified empirically with the uplink disconnected** | 18.3 |
| B-58 | Dependencies vendored/pinned; release artifacts checksummed | 18.1 |
| B-59 | **No production data on developer machines**; documented support model | 18.4 |
| B-60 | Departure checklist documented and usable | 17.2 |

---

## 20. Optional hardening

**Deliberately deferred.** Each is a legitimate control that is *not* justified for V1 at this size.
Adopt individually if risk, policy or incident experience later warrants — not as a batch, and not to
feel thorough.

| # | Control | Why deferred | What would trigger it |
|---|---|---|---|
| H-01 | **Multi-factor authentication** | hardware tokens cost money and get lost; TOTP needs enrolment, a device policy and a lockout path. On a LAN-only system with no remote access the marginal gain over a strong individual password is modest | remote access is ever introduced, or a credential compromise occurs |
| H-02 | **Re-authentication before high-risk actions** | real friction on actions already reason-bearing and audited | evidence of unlocked-workstation misuse |
| H-03 | **Audit digest anchoring** — periodically write a hash of the audit head to write-once or offline media held by someone other than the administrator | the only form of tamper-evidence that is not theatre (§9.7); still needs a custodian and a verification habit | a requirement to demonstrate audit integrity to an external body |
| H-04 | **Separate origin for file delivery** | genuinely valuable, but a second hostname and certificate to maintain; §10.2 headers already prevent inline rendering | inline preview is ever introduced (§11.5), where it becomes close to mandatory |
| H-05 | **Narrower runtime `DELETE` privileges**, table by table | cheap and worthwhile, but needs the final schema to specify precisely | schema design (do it then) |
| H-06 | **Row-level security in PostgreSQL** | duplicates `PERMISSIONS.md` in a second language; drift between the two is more dangerous than the defence in depth is valuable (§12.2) | a requirement to enforce access below the application layer |
| H-07 | **Application-level document encryption** | does not defend against the actors who matter here, and breaks content addressing and integrity verification (§13.5) | an explicit policy requirement, designed key-custody-first |
| H-08 | **Periodic automated restore rehearsal** | B-49 requires a tested restore; automating rehearsals is maturity, not a prerequisite | after the first year of operation |
| H-09 | **Physical access logging** — badges, cameras, rack locks, tamper seals | only proportionate if the organization already operates such facilities (§15.1) | the organization adopts them building-wide |
| H-10 | **Shipping security logs to a second machine** | a real detective control against administrator action (§4.4, §16.3); needs somewhere to ship to and someone to read it | the backup-target machine exists and can host it — then it is cheap |
| H-11 | **Server-side malware scanning** | offline signatures degrade; the non-degrading controls are already in the baseline (§11.2) | a maintained offline signature path exists, or policy requires scanning |
| H-12 | **Network segmentation / VLAN for the server** | depends on infrastructure the organization may not have (§5.5) | managed switching becomes available |
| H-13 | **Intrusion detection, file integrity monitoring on the host** | needs someone to tune and read it; unread alerts are worse than none | dedicated IT capacity exists |
| H-14 | **Formal security review / penetration test before major releases** | worth doing once the system holds years of records | first major version, or a policy requirement |

---

## 21. Go-live security gate

**Minimum conditions before real confidential case data is entered.** Concise on purpose — every item is
verifiable in an afternoon, and none is a document review.

| # | Condition | Verified by |
|---|---|---|
| G-01 | **HTTPS works** from a normal workstation with no certificate warning; the internal CA root is installed on every workstation | open the application on each workstation |
| G-02 | Certificate expiry date recorded, owner and deputy named, renewal procedure written down **outside the system** | show the document |
| G-03 | **PostgreSQL is not reachable from a workstation** | attempt a connection from a workstation — it must fail |
| G-04 | **The document store is not browsable from a workstation** (no share, no path) | attempt it — it must fail |
| G-05 | **Every user has an individual account**; no shared logins exist; all bootstrap/default credentials removed or changed | list accounts; confirm with staff |
| G-06 | **Permission model tested** with a real Worker, Chief and Head account: each can do what they should and is refused what they should not | a short scripted walkthrough |
| G-07 | **Restricted-case test passed**: a Worker without a grant cannot see the case in search, counts, or by direct document **or version** link; a document shared into an ordinary case exposes only its pinned version there; a granted Worker can see the case; revoking the grant takes effect on the next action | the decisive test — do it deliberately |
| G-08 | **Suspension/deactivation test passed**: suspending (and, separately, deactivating) a logged-in user kills their live session immediately | two browsers |
| G-09 | **First backup completed**, and visible in the application | the backup panel (`PROJECT.md` §20) |
| G-10 | **A restore has been tested successfully** — database **and** object store from one valid combined recovery point (§14.3), including decrypting the media — the integrity sweep reports no missing object, and documents open afterwards | record the date |
| G-11 | **An offline/unwritable backup copy exists** and the server cannot delete it | inspect credentials and media |
| G-12 | **Break-glass tested once** and resealed; custodians know where it is | do it before go-live, not during the emergency |
| G-13 | **Server time is correct**, and the drift/jump warning works | |
| G-14 | **Disconnect the Internet uplink** and confirm login, search, upload, download, export and print all work | the empirical test for invariant 17 |
| G-15 | **Recovery for TechAdmin and Head absence is documented**, stored outside the system, and a second person knows where | §6.8, §6.9 |
| G-16 | Workstation screen lock is enforced | check a sample |

**If G-03, G-04, G-07, G-10 or G-11 fails, do not go live.** The others can be remediated in parallel;
those five are the ones whose absence is not recoverable after confidential data is entered.

---

## 22. Security open questions

Separated by who must answer. **Nothing is guessed.**

### 22.1 Business and policy decisions

**OQ-S1 — Is the LAN shared with other departments, and is segmentation available?**
Determines whether §5.5 is defence in depth or the only boundary. *V1 position:* host-level default-deny
and the two never-expose rules hold regardless.

**OQ-S3 — How is a user's identity verified at password reset?**
In a 13-person office, recognition is realistic; it should still be written down (§6.7).

**OQ-S4 — Will at least two people hold the Head role?**
If not, the department accepts break-glass as a routine dependency (§6.9).

**OQ-S6 — Which file types are accepted, and is anything refused outright?**
Carried from `DOCUMENT_MODEL.md` OQ-D3/OQ-D4. *V1 position:* accept and store everything, download-only,
warn on active formats (§10.4).

**OQ-S7 — Should a malware verdict be visible as a business state?**
Carried from `DOCUMENT_MODEL.md` OQ-D6. Answering **yes would require a change to the frozen domain
model** (a new `document_version.status` value), which is why it is raised and not assumed (§11.4).

**OQ-S8 — What physical security does the organization actually have?**
Server room, cabinet, safe, UPS, access control (§15.1). Controls must match reality, not aspiration.

**OQ-S10 — Under what circumstances, if any, may production data be accessed for support?**
*V1 position:* never copied off the server; on-server investigation only, authorised by Head (§18.4).

**OQ-S11 — Data retention.** Carried from `DOMAIN_MODEL.md` OQ-9 and `DOCUMENT_MODEL.md` OQ-D1/OQ-D2.
Unresolved; V1 deletes nothing. Security consequence: storage and backup volume grow without bound, and
encrypted backup media accumulate confidential data that is never purged.

**OQ-S12 — Are staff informed that document downloads and exports are recorded?**
A transparency question, not a technical one. `DOCUMENT_MODEL.md` §13.3 requires the logging; telling
people is both fairer and a more effective deterrent than discovering it after an incident (§16.4).

### 22.2 Infrastructure decisions

**OQ-S2 — Who owns each secret, certificate renewal and backup media rotation, and who is their deputy?**
Every control in §13 and §14 depends on a named person. Unowned controls decay (§P1).

**OQ-S5 — Can workstation screen lock be enforced centrally?**
If there is no central management it becomes a written policy plus staff habit, which is weaker — and
that should be a known weakness rather than an assumed control (§8.5).

**OQ-S9 — Is there an internal time source on the LAN?**
Determines whether §16.5 is automatic or manual.

**OQ-S13 — Where does the backup target live, and who controls it?**
The pull-based requirement (§14.2) needs a second machine. Whether one exists is an infrastructure fact.

**OQ-S14 — Is there any maintained offline path for antivirus signature updates?**
Determines whether H-11 is worth adopting at all (§11.2).

### 22.3 Implementation decisions

| Question | Why it can wait |
|---|---|
| Framework and its security defaults | §10.7 fixes the requirements; `DECISIONS.md` picks the stack |
| Argon2id parameters | tuned on the actual server hardware (§6.2) |
| Session store mechanism | §8.2 fixes only that it must be server-side and revocable |
| Exact rate-limit thresholds and lockout durations | §6.4 fixes the shape; numbers are configuration |
| Reverse proxy product, TLS cipher configuration | `ARCHITECTURE.md` |
| Log format, rotation, shipping mechanism | §16.3 fixes the content |
| Backup software and media rotation schedule | §14 fixes the security properties they must satisfy |
| Preview/conversion tooling, if ever | §11.5 fixes the five constraints |

---

## 23. Final security invariants

The commitments of this document. Any future change that breaks one requires a decision record in
`DECISIONS.md` — not a configuration tweak.

| # | Invariant | Guaranteed by |
|---|---|---|
| **1** | No production cloud dependency | §18.3, verified empirically at G-14 |
| **2** | PostgreSQL is not directly reachable from user workstations | §5.2, B-04, tested at G-03 |
| **3** | Document storage is not a general LAN file share | §5.3, B-05, tested at G-04 |
| **4** | Every business user has an individual identity | §6.1, B-07 |
| **5** | Suspended or deactivated users cannot continue using existing sessions, and a departure never waits for handover to remove access | §6.5, §8.2–§8.3, §17.2, tested at G-08 |
| **6** | Authorization is enforced server-side, on every path | §7.1, B-17 |
| **7** | Direct document or version identifiers cannot bypass case permissions or version exposure | §7.2–§7.3, tested at G-07 |
| **8** | TechAdmin does not automatically receive business authority | §7.5, `PERMISSIONS.md` §18 |
| **9** | Uploaded files are treated as untrusted | §10, §11 |
| **10** | Document bytes are never executed or parsed by the application | §10.1 — the only operations reading content are hashing and bounded signature-based type detection |
| **11** | Active content is never rendered inline under the authenticated origin | §10.2, B-33, B-37 |
| **12** | The application runtime is not a DB superuser | §12.2, B-30 |
| **13** | Ordinary users cannot modify audit history | §9.2 — enforced by DB grant, not by application care |
| **14** | At least one backup is outside the primary server's write control | §14.2, B-46/B-47, tested at G-11 |
| **15** | Backup restore is tested before production go-live | §14.6, G-10 |
| **16** | Secrets are never stored in source code | §13.2, B-44 |
| **17** | Production remains functional with no Internet access | §18.3, G-14 |
| **18** | There is no "log in as another user" feature | §6.1, B-08 |
| **19** | Root/DBA power is acknowledged honestly, not hidden behind fictional controls | §4.4, §9.5, §10.5 of `PERMISSIONS.md` |
| **20** | Every V1 control is maintainable by a small department | §19 contains nothing needing a specialist; everything else is §20 |

---

## Appendix A — Self-review

| # | Question | Answer | Mechanism |
|---|---|---|---|
| 1 | Can malware on a Worker PC directly write or encrypt the document store? | **NO** | the store is not a share and is reachable only by the application process as its own OS user (§5.3, §12.4). Through the application, a compromised account is bounded by that user's authority and **cannot hard-delete anything** (§14.4) |
| 2 | Can the primary application server destroy every backup? | **NO** | the backup target **pulls**; the server holds no write credential for it, and at least one copy is offline (§14.2, B-46/B-47, G-11) |
| 3 | Can TechAdmin approve a FinalResult by virtue of being TechAdmin? | **NO** | the role carries no business capability and cannot grant itself one — Head is the sole role granter (§7.5, §17.1). *At OS/DB level see §4.4; the honest answer is stated, not hidden* |
| 4 | Can a guessed Document UUID bypass a restricted Case? | **NO** | identity is not authority; every download re-authorises against a context the user can see (§7.2, §7.3), tested at G-07 |
| 5 | Can a disabled employee keep an old session indefinitely? | **NO** | server-side sessions, revoked immediately on suspension or deactivation and checked every request (§6.5, §8.2, §8.3); a departure suspends at once rather than waiting for handover (§17.2); tested at G-08 |
| 6 | Can an uploaded HTML/SVG execute under the authenticated app origin? | **NO** | never rendered inline — attachment disposition, `nosniff`, neutral content type, CSP (§10.2); no server-side parsing at all (§10.1) |
| 7 | Is PostgreSQL reachable directly from ordinary workstations? | **NO** | localhost/socket binding plus default-deny firewall (§5.2), tested at G-03 |
| 8 | Does V1 need the Internet for login, assets, certificates, scanning or operation? | **NO** | internal CA with no Internet revocation URLs, bundled assets, no telemetry, no cloud scanning (§18.3), verified with the uplink disconnected at G-14 |
| 9 | Can the department recover if the only TechAdmin leaves? | **YES** | sealed break-glass credential in two-person custody, tested before go-live, documented outside the system (§6.8), plus the departure rotation checklist (§17.2). §6.9 additionally flags the single-Head risk |
| 10 | Can this be deployed without enterprise-security bureaucracy? | **YES** | the §19 baseline is 60 items, almost all one-time or free; nothing needs a security specialist; everything requiring ongoing expertise is in §20 and deliberately deferred |

No answer fails, so no revision was required by this review.

---

## Appendix B — Tension with the frozen documents

**No contradiction was found.** `PROJECT.md`, `DOMAIN_MODEL.md`, `WORKFLOW.md`, `PERMISSIONS.md` and
`DOCUMENT_MODEL.md` are unmodified.

Two points where this document **resolves** something the frozen set deferred to it, neither requiring a
model change:

| # | Deferred to security design | Resolution | Where |
|---|---|---|---|
| 1 | `DOMAIN_MODEL.md` §2.18 deferred **cryptographic audit chaining** here | **Not adopted in V1**, with the reasoning: a chain computed and stored on the machine its only capable adversary controls proves nothing. The cheap, honest alternative — anchoring a digest on media the administrator does not control — is optional hardening H-03 | §9.7 |
| 2 | `DOCUMENT_MODEL.md` §9.6 left **malware scanning** to this document | Optional local infrastructure control, never cloud; explicitly **not** the primary defence because offline signatures degrade; a verdict never deletes bytes or alters the business record | §11 |

One carried-forward question (**OQ-S7**, from `DOCUMENT_MODEL.md` OQ-D6) *would* require an additive
change to the frozen domain model if answered "yes" — a new `document_version.status` value for a
malware finding. It is raised, not acted on.

**Post-review (2026-09-17).** Four statements here were inconsistent with the rest of the set and are
corrected: authorization by document rather than by version (§7.2, §7.3); "hashing is the only operation
on content" beside mandatory content-based type detection (§10.1); the "objects before database" backup
ordering (§14.3); and a departure checklist that removed access only after reassignment (§17.2). No
security invariant is weakened; invariants 5, 7 and 10 are made more precise.

---

*End of document. No application code, configuration files, database migrations or infrastructure
definitions are contained in or implied by this security design.*
