# RCS — Decision Log

**Status:** authoritative decision log · created 2026-09-17 (pre-schema pass)
**Scope:** architecture and domain decisions for V1. Each entry states *what* was decided and *why*; the
cited sections hold the detailed rules. Design only — no code, no migrations, no scaffolding.

---

## How to use this log

| Status | Meaning |
|---|---|
| **Frozen** | Settled. Changing it requires a **new ADR that explicitly supersedes it**. Code, schema and documents must not diverge silently |
| **Frozen · reopen trigger** | Settled; the entry names the **only** condition under which it may be reopened |
| **Required before …** | Must be decided before the named gate; not guessed in the meantime |
| **Deferred** | Deliberately undecided; bounded by the frozen decisions it cites; owner and trigger named |
| **Open (business)** | Needs a product-owner answer; the V1 assumption is stated |

Rules:

1. **Authority.** `PROJECT.md` defines scope. A Frozen ADR overrides any conflicting statement in the other
   design documents; such a conflict is a defect to fix in that document, not a licence to reinterpret the
   ADR.
2. **Supersede, never rewrite.** The *Decision* of a Frozen ADR is never edited in place. Add a new ADR with
   "Supersedes ADR-0nn" and mark the old one "Superseded by ADR-0mm". Source references and typos may be
   corrected.
3. **AI and developer rule** (`PROJECT.md` §29). Claude, Codex and developers do not reopen a Frozen ADR on
   their own initiative; they raise it with the product owner.
4. Numbering is permanent. Deferred items (`DEF-nn`), pre-production gates (`PS-n`) and business questions
   (`OB-n`) become ADRs when decided.

---

## Index

| ID | Decision | Status |
|---|---|---|
| ADR-001 | Modular monolith | Frozen |
| ADR-002 | ASP.NET Core LTS / C#, self-contained | Frozen · reopen trigger |
| ADR-003 | Server-rendered frontend, no SPA | Frozen |
| ADR-004 | PostgreSQL authoritative; hand-written SQL migrations own the schema | Frozen |
| ADR-005 | Local SHA-256 content-addressed document storage | Frozen |
| ADR-006 | Official correspondence happens externally | Frozen |
| ADR-007 | Request vs Correspondence | Frozen |
| ADR-008 | Response type and outcome are separate axes; no ResponseVersion | Frozen |
| ADR-009 | Response supersession uses a relationship table | Frozen |
| ADR-010 | Requirement resolution correction | Frozen |
| ADR-011 | Closure override preserves truth | Frozen |
| ADR-012 | Non-blocking requirement semantics | Frozen (business confirmation OB-4 pending) |
| ADR-013 | Final result replacement is atomic | Frozen |
| ADR-014 | Official correspondence files are version-pinned | Frozen |
| ADR-015 | Version-scoped document authorization | Frozen |
| ADR-016 | Cross-case document disclosure is explicit | Frozen |
| ADR-017 | Scope-specific temporal exclusion for assignments | Frozen |
| ADR-018 | Audit is append-only at application privilege | Frozen |
| ADR-019 | Case-level serialization for cross-row guards | Frozen (mechanism DEF-02) |
| ADR-020 | Durable operation identity for retried mutations | Frozen (mechanism DEF-03) |
| ADR-021 | Backup validity requires a common recovery point | Frozen (tooling DEF-01) |
| ADR-022 | Native service deployment | Frozen · reopen trigger |
| ADR-023 | Linux preferred, Windows Server supported | Frozen · reopen trigger |
| ADR-024 | No external search engine | Frozen |
| ADR-025 | No message broker or Redis in V1 | Frozen |
| ADR-026 | No physical garbage collection of content objects in V1 | Frozen |
| ADR-027 | Current document version = the `ACTIVE` version; reinstatement | Frozen *(new, pre-schema)* |
| ADR-028 | Supersession recorded in error is retracted; response restored | Frozen *(new, pre-schema — closes OQ-16)* |
| ADR-029 | Operational progress is derived; five stored case states | Frozen |
| ADR-030 | No hard delete | Frozen |
| ADR-031 | UUIDv7 keys; business numbers are never keys | Frozen |
| ADR-032 | Authorization model | Frozen |
| ADR-033 | Authentication and sessions | Frozen |
| ADR-034 | Pull-based backup topology, no automatic HA | Frozen |
| ADR-035 | Uploaded files are untrusted and never processed server-side | Frozen |
| ADR-036 | Documents reach context only through links; exclusive arcs | Frozen |
| **PS-1** | **Database collation and Unicode behaviour** | **REQUIRED BEFORE PRODUCTION DATABASE INITIALIZATION** |
| PS-2 | Server operating system | Required before server build |
| DEF-01 … DEF-11 | Implementation and infrastructure choices | Deferred |
| OB-1 … OB-8 | Business questions | Open (business) |

Entries below are grouped by area (architecture · domain · documents, authorization and security); this
index is the numeric view.

---

## Architecture

### ADR-001 — Modular monolith

**Status:** Frozen · **Sources:** `ARCHITECTURE.md` §3, §9.1, §24 (1); `PROJECT.md` §26

- **Context:** ~13 users on one LAN; the Request → Response → Requirement chain is transactional and
  tightly interconnected; maintainability over ten years outweighs scalability.
- **Decision:** V1 is one deployable modular monolith. Modules are namespaces with boundary rules
  (explicit service interfaces, no module writes another's tables, no module cycles), not processes.
- **Consequences:** one use case = one database transaction; one deployment unit; a module may later be
  extracted along its boundary if a demonstrated need appears.
- **Rejected:** microservices — network calls, partial failure and distributed transactions for no benefit.

### ADR-002 — ASP.NET Core LTS with C#

**Status:** Frozen · reopen trigger · **Sources:** `ARCHITECTURE.md` §5.1, §5.2, §5.5, §16.3; `PROJECT.md` §27

- **Context:** offline deployment, long support, static typing over a constraint-heavy domain, mature
  PostgreSQL driver, streaming uploads, in-process background work.
- **Decision:** backend is ASP.NET Core (current LTS) with C#, published **self-contained** so the server
  needs no runtime installation.
- **Consequences:** two ecosystems to maintain (.NET, PostgreSQL); the rest of the architecture is
  stack-independent.
- **Reopen trigger — only this:** the people who will actually maintain the system (OQ-A1) cannot operate
  .NET, making the stack operationally unsuitable. Then Django or Spring Boot per `ARCHITECTURE.md` §3.2.
- **Rejected:** Node.js/TypeScript (dependency churn); PHP (long-horizon dependency risk).

### ADR-003 — Server-rendered frontend

**Status:** Frozen · **Sources:** `ARCHITECTURE.md` §5.3, §10, §22 (candidate ADR-18); `SECURITY.md` §18.3; `PROJECT.md` §25

- **Decision:** server-rendered HTML with a small, locally vendored progressive-enhancement JavaScript
  layer; HTML partials; JSON endpoints only where needed. No SPA, no npm build in the production pipeline,
  no CDN, no runtime Internet fetch, no GraphQL, no public API in V1.
- **Consequences:** authorization is rendered server-side from the same `can()` (ADR-032); the frontend is
  rebuildable offline.
- **Rejected:** React/SPA — a build toolchain, a second authorization surface and an API layer that exists
  only to feed it.

### ADR-004 — PostgreSQL is the authoritative structured store

**Status:** Frozen · **Sources:** `ARCHITECTURE.md` §7.1, §7.4, §7.5; `DOMAIN_MODEL.md` §1.4; `PROJECT.md` §6

- **Decision:** PostgreSQL, local, bound to localhost/socket, is the authoritative relational store.
  **Hand-written, ordered, forward-only SQL migrations own the schema.** An ORM may map and query; it never
  generates the production schema. Migrations run under a dedicated migration role, never at application
  start.
- **Consequences:** exclusive-arc `CHECK`s, partial unique indexes and `EXCLUDE` constraints are
  database-enforced and cannot be dropped by an ORM regeneration. Recovery from a bad migration is
  restore-then-fix-forward.
- **Rejected:** ORM-generated migrations; automatic down-migrations; PgBouncer.

### ADR-005 — Local content-addressed document storage

**Status:** Frozen · **Sources:** `DOCUMENT_MODEL.md` §6; `DOMAIN_MODEL.md` §2.14; `ARCHITECTURE.md` §8; `PROJECT.md` §6

- **Decision:** file bytes live on the local filesystem at `sha256/ab/cd/<full_hash>`; metadata lives in
  PostgreSQL. No `bytea`, no large objects. Objects are immutable; uploads write bytes first (fsync, atomic
  same-volume rename), metadata second.
- **Consequences:** identical bytes are stored once while logical versions stay distinct;
  `UNIQUE (document_id, content_hash)`; filenames are metadata only; the store is never a network share.
- **Rejected:** S3/MinIO/NAS abstractions, cloud storage, database BLOBs, paths derived from case numbers or
  filenames.

### ADR-006 — Official correspondence happens externally

**Status:** Frozen · **Sources:** `PROJECT.md` §1.1; `WORKFLOW.md` §0 (rule 5), §3.1, §14.0; `PERMISSIONS.md` §14.3

- **Decision:** this application never sends or receives official letters. Letters are dispatched and
  received in the external government system, which assigns official numbers and dates; this application
  records those already-existing acts afterwards, manually.
- **Consequences:** no Send button, dispatch queue, "mark as sent", dispatch approval or delivery
  confirmation; `DRAFT` means "planned, not yet issued"; business time and recording time routinely differ.
  No integration with the government system in V1.

### ADR-022 — Native service deployment

**Status:** Frozen · reopen trigger · **Sources:** `ARCHITECTURE.md` §16.2, §16.4, §20.4; `PROJECT.md` §26

- **Decision:** native OS services (systemd / Windows Services) installed from a checksummed offline release
  bundle. No Docker requirement.
- **Reopen trigger — only this:** the local IT team already operates containers competently and prefers
  them; then images are archived into the bundle and PostgreSQL still runs natively.
- **Rejected, not reopenable by that trigger:** Kubernetes or any container orchestration.

### ADR-023 — Linux preferred, Windows Server supported

**Status:** Frozen · reopen trigger · **Sources:** `ARCHITECTURE.md` §2 (A9), §6, §22 (candidates ADR-3, ADR-6), §23.1 (PS-2)

- **Decision:** a long-support Linux distribution is the preferred server OS; Windows Server is an
  acceptable fallback **if local IT skills justify it** (OQ-A2, gate PS-2). Reverse proxy follows the OS:
  nginx on Linux, IIS or nginx on Windows.
- **Consequences:** business and domain architecture must not depend on Linux-only behaviour; on Windows,
  NTFS ACLs must reproduce the `0700` store intent and are verified at go-live (`SECURITY.md` G-04).

### ADR-024 — No external search engine

**Status:** Frozen · **Sources:** `ARCHITECTURE.md` §13; `DOMAIN_MODEL.md` §9.5; `PROJECT.md` §31

- **Decision:** search is PostgreSQL-native: B-tree indexes and application-maintained normalized columns
  produced by one normalization component. `pg_trgm` may be added later **only if measured use justifies
  it**; full-text search only if justified. No OCR or file-content indexing in V1.
- **Rejected:** Elasticsearch, Meilisearch — a second copy of confidential data, a synchronisation problem,
  and a service to secure and restore consistently.

### ADR-025 — No message broker or Redis in V1

**Status:** Frozen · **Sources:** `ARCHITECTURE.md` §14, §20.4; `PROJECT.md` §26

- **Decision:** background work runs in an in-process hosted service coordinated by a PostgreSQL job/lock
  table. Any future document-parsing job (preview, OCR, scanning) runs as a separate constrained process.
- **Rejected:** RabbitMQ, Kafka, Redis, a separate worker process in V1 — unless an actual requirement later
  appears.

### ADR-034 — Pull-based backup topology, no automatic HA

**Status:** Frozen · **Sources:** `PROJECT.md` §20; `SECURITY.md` §14.2, §14.6; `ARCHITECTURE.md` §15.1, §15.2, §15.6

- **Decision:** a second machine **pulls** backups; the primary holds no credential that can write to or
  delete them; at least one encrypted offline copy is unwritable from the primary. A mirror is not a backup.
  Total primary loss is handled by manual promotion; a full restore is tested before go-live.
- **Consequences:** depends on a second machine being available (OQ-A8). Consistency of each backup is
  ADR-021.
- **Rejected:** push-based backups; automatic HA clusters and failover.

---

## Domain

### ADR-007 — Request vs Correspondence

**Status:** Frozen · **Origin:** Domain decision C-1 · **Sources:** `DOMAIN_MODEL.md` §2.8, §2.9, §12.2

- **Decision:** `correspondence` is the official communication; `request` is a logical work item carried by
  it. One outgoing letter may carry several requests (`request.dispatch_correspondence_id`, N:1); one
  incoming letter may carry several responses (N:1). Each correspondence has one owning case.
- **Consequences:** deadlines, statuses and requirements attach to the request, not the letter.
- **Rejected:** `correspondence.related_request_id` — **must not be added**; a case ↔ correspondence M:N
  junction in V1.

### ADR-008 — Response type and outcome are separate axes

**Status:** Frozen · **Origin:** Domain decision C-2 · **Sources:** `DOMAIN_MODEL.md` §2.10, §5.3, §12.2; `WORKFLOW.md` §4.2

- **Decision:** `response_type` (what kind of communication arrived) and `response_outcome` (what it
  decided) are separate, both required; `NOT_APPLICABLE` and `UNDETERMINED` are explicit non-verdicts.
  Responses are immutable official acts. **No `ResponseVersion`.**
- **Rejected:** one mixed classification enum; a `REVISION` type; versioning responses.

### ADR-009 — Response supersession uses a relationship table

**Status:** Frozen · **Origin:** Amendment A-1, amended by A-10 (ADR-028) · **Sources:** `DOMAIN_MODEL.md` §2.21; `WORKFLOW.md` §4.3, §4.5

- **Context:** a single `supersedes_response_id` could not record one later letter replacing two conflicting
  responses, and recording supersession between existing responses would edit an immutable row.
- **Decision:** `response_supersession` edges replace the column. One response may supersede several;
  no self-supersession; same request only (composite foreign keys); both responses `ACTIVE` when an edge is
  recorded; at most one `ACTIVE` incoming edge per response; acyclic by construction.
- **Consequences:** responses stay immutable; supersession between existing responses is an edge insert.

### ADR-010 — Requirement resolution correction

**Status:** Frozen · **Origin:** Amendment A-2 · **Sources:** `DOMAIN_MODEL.md` §2.22, §5.4; `WORKFLOW.md` §5.2 (Q7); `PERMISSIONS.md` §16, §24.2

- **Context:** terminal requirement states were "final", yet a requirement wrongly marked fulfilled had to
  be correctable.
- **Decision:** terminal outcomes are historical facts. A terminal state **recorded in error** is withdrawn
  by one correction transition (Q7: terminal → `OPEN`/`IN_PROGRESS`), by a Chief, with a mandatory reason.
  A `requirement_resolution_correction` row keeps a verbatim copy of the withdrawn resolution; evidence is
  retracted row by row. The correct outcome is then reached by its ordinary transition and authority.
- **Consequences:** no terminal→terminal transition exists; the causal chain is untouched; a closed case is
  reopened first; current statistics count the corrected requirement as open.
- **Rejected:** arbitrary terminal→terminal changes; a replacement requirement (breaks the causal chain);
  audit-only correction (erases the business-readable resolution).

### ADR-011 — Closure override preserves truth

**Status:** Frozen · **Origin:** Amendment A-3 · **Sources:** `WORKFLOW.md` §9.1, §9.2, §9.4; `DOMAIN_MODEL.md` §2.5, §2.6, §9.1; `PERMISSIONS.md` §17

- **Decision:** normal closure passes guards G1–G5. The Head may override eligible guards (G1, G2, G3, G5 —
  never G4) with a mandatory note naming each guard, recorded as `CLOSURE_GUARD_OVERRIDE` with actor, time
  and audit. **Unresolved requests and requirements keep their true states.**
- **Consequences:** `closed_with_unresolved_items` is derived, never stored, and such items leave live
  operational lists. Every closure and reopening is a `case_state_change` row, so closure episodes survive
  close → reopen → close. `CANCELLED` remains for dossiers not pursued.
- **Rejected:** forcing records to `FULFILLED`/`VOID`/`FAILED` to pass a guard; a closure model where any
  open work forces `CANCELLED`.

### ADR-012 — Non-blocking requirement semantics

**Status:** Frozen (business confirmation OB-4 pending) · **Origin:** Amendment A-3 · **Sources:** `WORKFLOW.md` §3.4, §7.2, §7.4, §9.2; `DOMAIN_MODEL.md` §2.9

- **Decision:** a requirement with `is_blocking = false` **holds nothing open** — not its request's closure,
  not its branch, not final-result readiness, not case closure (G1). It stays visible on the closure screen.
  Requests are never exempt: a request sent for a non-blocking requirement must still be terminal (G2).
- **Consequences:** one definition of closure. If the business answers OB-4 "every requirement must be
  resolved", G1 and the request closure rule tighten together; no schema change.

### ADR-013 — Final result replacement is atomic

**Status:** Frozen · **Origin:** Amendment A-4 · **Sources:** `WORKFLOW.md` §7.4, §8.2, §8.6; `DOMAIN_MODEL.md` §2.16

- **Decision:** a replacement names the currently `ISSUED` result in `supersedes_final_result_id`. Issuing it
  is one transaction: verify the named result is still the issued one (otherwise refuse), move it
  `ISSUED → SUPERSEDED`, then issue the replacement with its own guards and pins.
- **Consequences:** the "at most one `ISSUED`" index holds at every statement; readiness never blocks a
  replacement; the old result, its letter, pins and history remain. Nothing is deleted.
- **Rejected:** revoke-then-issue (a window with no decision in force); overriding readiness to escape the
  deadlock.

### ADR-017 — Scope-specific temporal exclusion for assignments

**Status:** Frozen (predicate expression DEF-04) · **Origin:** Amendment A-7 · **Sources:** `DOMAIN_MODEL.md` §2.7; `ARCHITECTURE.md` §7.6

- **Decision:** responsibility is a temporal `assignment` table (never a `responsible_user_id` column).
  Non-overlap of `RESPONSIBLE` assignments is enforced by **separate** `EXCLUDE` constraints for case,
  request and requirement scope, over half-open `[valid_from, valid_until)` intervals, **including `ENDED`
  rows** (only `VOID` excluded), with `valid_until > valid_from`.
- **Consequences:** `btree_gist` is an accepted, required PostgreSQL dependency — the only extension in V1.
- **Rejected:** partial unique index on `valid_until IS NULL`; exclusion filtered to `ACTIVE` rows.

### ADR-027 — Current document version is the `ACTIVE` version; reinstatement

**Status:** Frozen *(new, pre-schema)* · **Origin:** Amendment A-9 · **Sources:** `DOMAIN_MODEL.md` §2.14, §5.6; `DOCUMENT_MODEL.md` §5.2, §5.3, §7.4, §8.2, §8.3, §13.1

- **Context:** `DOCUMENT_MODEL.md` said that after an erroneous v2 is withdrawn, v1 "becomes `ACTIVE` again",
  but no such transition existed, and `UNIQUE (document_id, content_hash)` forbids re-uploading v1's bytes.
- **Decision:** "current version" is the one `ACTIVE` version (existing partial unique index); there is no
  pointer column. When a document has no `ACTIVE` version, its **newest non-withdrawn version** may be
  **reinstated**: `SUPERSEDED → ACTIVE`. It is an explicit act with a mandatory reason, normally in the same
  action as the withdrawal, never automatic. Uploads, withdrawal of the `ACTIVE` version and reinstatement
  are serialised per document.
- **Consequences:** same row, same bytes, same creation facts — nothing is re-uploaded or rewritten; the
  withdrawn v2 keeps its who/when/why and stays in authorized history; a re-upload of v1's bytes converges on
  v1 without changing its status. Reaching an older version means reinstating then withdrawing each newer
  one, each with a reason. **No field or table added.**
- **Rejected:** `document.current_version_id` (a circular `document ↔ document_version` foreign key needing
  deferred constraints on every first upload, and a second truth beside `status`); re-uploading v1 as a new
  version (forbidden, and a fabricated upload); deriving "current" purely from order (changes the frozen
  status vocabulary and silently re-designates current on every withdrawal); automatic reinstatement
  (sometimes no version should be current).

### ADR-028 — Supersession recorded in error is retracted; the response is restored

**Status:** Frozen *(new, pre-schema — closes Domain Model OQ-16)* · **Origin:** Amendment A-10 · **Sources:** `DOMAIN_MODEL.md` §2.10, §2.21; `WORKFLOW.md` §4.7; `PERMISSIONS.md` §16

- **Context:** with `UNIQUE (superseded_response_id)`, a response superseded by an entry later voided stayed
  `SUPERSEDED` for ever and could never be superseded correctly; the request showed no answer in force.
  *Example:* A (APPROVED, conclusive) is in force on request R; letter C is registered on R superseding A, but
  C belongs to another request; C is voided — A remained superseded by a void entry, and the authority's
  genuine revision D could not supersede A.
- **Decision:** `response_supersession` carries `status` (`ACTIVE`/`RETRACTED`) and a retraction triple; the
  uniqueness becomes a partial index over `ACTIVE` edges. Voiding a superseding response retracts its edges in
  the same transaction (`actor_kind = SYSTEM`); a Chief may retract an edge that was itself recorded in error,
  with a mandatory note. The superseded response returns `SUPERSEDED → ACTIVE` and is listed for review. Errors
  only: a true supersession is never retracted to express a later change of position.
- **Consequences:** four columns on `response_supersession` and one response transition; a non-`VOID`
  response is `SUPERSEDED` exactly while it has an `ACTIVE` incoming edge; acyclicity re-proved
  (`DOMAIN_MODEL.md` §2.21); on a `CLOSED` case, retraction is preceded by reopening. No response fact is
  rewritten.
- **Rejected:** leaving the graph stuck; deleting edges; treating edges as ineffective by derivation without a
  column (loses database-enforced uniqueness and cannot fix a wrong edge between two valid responses);
  re-registering the original letter as a duplicate response.

### ADR-029 — Operational progress is derived

**Status:** Frozen · **Origin:** Domain decision C-3 · **Sources:** `DOMAIN_MODEL.md` §1.2 (d), §9, §12.2; `WORKFLOW.md` §11, §12; `PROJECT.md` §10

- **Decision:** the case stores five lifecycle states (`REGISTERED`, `ACTIVE`, `ON_HOLD`, `CLOSED`,
  `CANCELLED`). Progress, "waiting for", overdue, readiness and branch state are computed at read time.
  Responsible employee, original letter and final result are relationships, not case columns.
- **Consequences:** no `current_progress`, no counters, no `OVERDUE` state. The only cache column is the
  rebuildable `case.last_activity_at`; any other cache must be a rebuildable read model.
- **Rejected:** manual status sprawl; workflow graphs stored as JSON.

### ADR-030 — No hard delete

**Status:** Frozen · **Sources:** `DOMAIN_MODEL.md` §1.4, §8; `DOCUMENT_MODEL.md` §8.4, §12; `PROJECT.md` §7, §29.6

- **Decision:** business rows leave use through a state plus a who/when/why triple, never `DELETE`. All
  business foreign keys are `RESTRICT`; **no `ON DELETE CASCADE`**. Users, organizations and lookup rows are
  deactivated. The only V1 file deletion is orphaned temporary upload files.
- **Rejected:** a generic `is_deleted` flag; cascading deletes.

### ADR-031 — UUIDv7 keys; business numbers are never keys

**Status:** Frozen (generator DEF-05) · **Sources:** `DOMAIN_MODEL.md` §1.4; `WORKFLOW.md` §2.2; `ARCHITECTURE.md` §7.7; `PROJECT.md` §1.1

- **Decision:** every table has a UUIDv7 primary key. `case_number`, `registry_number`, `letter_number`,
  `request_number` and `result_number` are unique human-facing columns, never foreign keys. Official
  correspondence numbers are **recorded**, never generated; the internal case number is generated by the
  application.
- **Rejected:** sequential integer keys exposed in URLs; business numbers as primary or foreign keys.

### ADR-036 — Documents reach context only through links; exclusive arcs

**Status:** Frozen · **Sources:** `DOMAIN_MODEL.md` §1.4, §2.13, §2.15, §11 (R1); `DOCUMENT_MODEL.md` §4.3

- **Decision:** `document` has no `case_id` and no `correspondence_id`; context comes only from
  `document_link`. Multi-parent tables (`document_link`, `assignment`, `requirement_evidence`) use exclusive
  arcs — real nullable foreign keys plus `CHECK (num_nonnulls(...) = 1)`. A direct case link is allowed only
  for `SUPPORTING`/`WORKING_COPY`. Only `audit_event` uses `entity_type` + `entity_id`.
- **Rejected:** a generic attachments table or `document.case_id`; polymorphic `object_type`/`object_id`.

---

## Documents, authorization and security

### ADR-014 — Official correspondence files are version-pinned

**Status:** Frozen · **Origin:** Amendment A-5 · **Sources:** `DOCUMENT_MODEL.md` §4.3 (L8–L10), §4.4, §4.7; `DOMAIN_MODEL.md` §2.15

- **Decision:** every link to a `correspondence`, whatever its role, pins an exact `document_version`
  (`CHECK (correspondence_id IS NULL OR document_version_id IS NOT NULL)`). Evidence links are always pinned;
  final-result documents are pinned no later than issue. Floating links exist only for non-historical
  working placements in the document's **home case**, and new versions are added only through the home case.
- **Consequences:** a letter's file composition never changes after the fact; closes `DOCUMENT_MODEL.md`
  OQ-D7. A floating link may be frozen once, never re-pinned.
- **Rejected:** v1 "contextual links float"; pinning as an optional default.

### ADR-015 — Version-scoped document authorization

**Status:** Frozen · **Origin:** Amendment A-5 · **Sources:** `DOCUMENT_MODEL.md` §10.1–§10.4; `SECURITY.md` §7.2, §7.3; `PERMISSIONS.md` §27.1

- **Decision:** visibility of a logical document does not imply visibility of all its versions. A user may
  access a version only through a visible `ACTIVE` link that exposes **that** version — a pinned link exposes
  exactly its version; a floating (home-case) link exposes the document's versions. `REMOVED` links grant
  nothing.
- **Consequences:** version history, filenames, version metadata, search matches, exports, download URLs and
  audit views all apply the same per-version rule; unexposed versions are not counted or hinted at.
  Cross-case references do not disclose source-case files (ADR-016).

### ADR-016 — Cross-case document disclosure is explicit

**Status:** Frozen · **Origin:** Amendment A-6 · **Sources:** `DOCUMENT_MODEL.md` §3.2, §4.8; `DOMAIN_MODEL.md` §2.8; `PERMISSIONS.md` §16, §23

- **Decision:** a letter, its files, its other responses and its audit stay governed by the owning case. A
  response in case B shows case B only its own facts. If a file from case A must be visible in case B, a
  Chief creates an **explicit, audited, reason-bearing, version-pinned** link into a case-B context.
- **Rejected:** implicit disclosure by traversing response → correspondence → files; a case ↔ correspondence
  junction; a "filed elsewhere" marker (it would itself disclose a dossier).

### ADR-018 — Audit is append-only at application privilege

**Status:** Frozen · **Origin:** incl. Amendment A-8 · **Sources:** `SECURITY.md` §4.4, §9.1–§9.7, §12.2; `DOMAIN_MODEL.md` §2.18; `ARCHITECTURE.md` §7.2, §12.1

- **Decision:** the runtime database role has `INSERT` and `SELECT` on `audit_event` only — no `UPDATE`,
  `DELETE` or `TRUNCATE`. Audit rows commit in the same transaction as the business change; a failed audit
  write fails the operation. `actor_user_id` is the initiating person (empty only for `JOB` events);
  `actor_kind` records how the change was executed.
- **Consequences:** a compromised application can append but not rewrite history. Gaps in `event_seq` are
  normal. **Root and the DBA can alter anything; this is acknowledged, not hidden.**
- **Rejected for V1:** a cryptographic hash chain stored beside the data (theatre against the only actor able
  to modify rows). An externally anchored digest remains optional hardening (`SECURITY.md` H-03).

### ADR-019 — Case-level serialization for cross-row guards

**Status:** Frozen (mechanism DEF-02) · **Sources:** `ARCHITECTURE.md` §12.5; `WORKFLOW.md` §9.2; `DOMAIN_MODEL.md` §2.21

- **Context:** `row_version` prevents lost updates on one row, not write skew across rows (closing a case
  while another transaction creates a blocking requirement).
- **Decision:** every operation that evaluates a case-scoped cross-row guard (close, override, cancel,
  reopen, final-result issue, request close, response supersession and its retraction) and every operation
  that adds, removes or changes closure-relevant work takes **the same case-level lock first**. Operations
  spanning two cases lock both in a fixed order.
- **Consequences:** one convention for the whole set; the exact mechanism (row lock or serializable
  isolation applied uniformly) is chosen in implementation.

### ADR-020 — Durable operation identity for retried mutations

**Status:** Frozen (mechanism DEF-03) · **Sources:** `ARCHITECTURE.md` §12.6; `DOCUMENT_MODEL.md` §7.4; `DOMAIN_MODEL.md` §2.14

- **Context:** `UNIQUE (document_id, content_hash)` stops phantom versions but not a retried first upload,
  which would create a second document, link and audit trail.
- **Decision:** commands vulnerable to retry after an uncertain commit carry an operation identifier
  generated once per prepared form or upload, recorded durably in the same transaction as the command's
  effects, unique and bound to the actor. A retry with a recorded identifier returns the original result.
- **Consequences:** at minimum uploads, correspondence and response registration, request and requirement
  creation. A small technical table, not a domain entity.
- **Rejected:** relying on business unique constraints; a generic distributed idempotency platform or cache.

### ADR-021 — Backup validity requires a common recovery point

**Status:** Frozen (tooling DEF-01) · **Sources:** `DOCUMENT_MODEL.md` §6.7; `ARCHITECTURE.md` §15.3–§15.6; `SECURITY.md` §14.3, B-48, G-10

- **Context:** "copy objects first, database second" fails: objects copied 22:00, upload commits 22:01,
  database backup 22:02 references bytes the object backup lacks.
- **Decision:** **a recovery point is valid only if every object referenced by that database recovery point
  exists and verifies in the retained object set.** Met by a consistent snapshot whose referenced objects
  are then copied and verified, or by base backup + WAL published only up to an object-complete boundary.
  **A logical dump is never a base for WAL replay.**
- **Consequences:** offline copies carry a complete recovery point; restore = published point + its objects +
  integrity sweep; the combined RPO is bounded by the last verified boundary.
- **Rejected:** assuming any copy ordering is inherently safe.

### ADR-026 — No physical garbage collection of content objects in V1

**Status:** Frozen · **Sources:** `DOCUMENT_MODEL.md` §6.7, §11.1 (I2), §12.4; `ARCHITECTURE.md` §8.5, §12.2

- **Decision:** unreferenced final content objects are detected and **reported**, and **retained**. Nothing
  deletes them automatically.
- **Consequences:** an "orphan" may belong to an in-flight upload or be referenced by a backed-up recovery
  point; deleting it could break a valid restore. Physical purge would need a retention decision (OB-8).

### ADR-032 — Authorization model

**Status:** Frozen · **Sources:** `PERMISSIONS.md` §14, §18–§20, §25.2, §27; `SECURITY.md` §7, §12.2; `PROJECT.md` §12, §13

- **Decision:** four roles (Worker, Chief, Head, TechAdmin); one server-side `can()` on every path; deny by
  default; roles and grants evaluated at action time. Ordinary cases are visible department-wide; a
  restricted case is visible to assignees, Chief, Head and holders of a view-only, one-user, one-case
  `case_access_grant`. TechAdmin has no business authority and cannot grant roles; Head is the sole role
  granter. No dispatch permission exists.
- **Rejected:** configurable RBAC or per-object ACLs; per-action rights on grants; PostgreSQL row-level
  security duplicating the rules; any impersonation feature.

### ADR-033 — Authentication and sessions

**Status:** Frozen (session store DEF-07) · **Sources:** `SECURITY.md` §6, §8, §17.2; `ARCHITECTURE.md` §11.1; `PERMISSIONS.md` §25; `PROJECT.md` §14

- **Decision:** individual accounts only; local accounts first (Argon2id), Active Directory additive later
  through `user.auth_source` with the credential verifier as the only directory-aware component.
  Server-side, revocable sessions. Suspension or deactivation revokes sessions immediately; a departing
  employee is **suspended at once** and deactivated after handover. No MFA in V1.
- **Rejected:** stateless self-contained tokens; shared or role accounts; external or cloud identity.

### ADR-035 — Uploaded files are untrusted and never processed server-side

**Status:** Frozen · **Sources:** `SECURITY.md` §10, §11; `DOCUMENT_MODEL.md` §9; `PROJECT.md` §23, §24

- **Decision:** the server only hashes file bytes and performs **bounded signature-based type detection**
  (no decompression, traversal, rendering or execution). No preview, conversion, OCR or parsing in V1.
  Downloads are always attachments with `nosniff`; nothing user-supplied renders inline under the application
  origin. Any future parsing runs in an isolated, unprivileged, network-less process.
- **Rejected:** server-side preview in V1; cloud scanning; deleting a file because of a malware verdict.

---

## Pre-production gates

### PS-1 — Database collation and Unicode behaviour

**Status: REQUIRED BEFORE PRODUCTION DATABASE INITIALIZATION** · **Sources:** `ARCHITECTURE.md` §7.6, §13.5, §23.1; `DOMAIN_MODEL.md` OQ-15

- **Why it gates:** collation provider and locale are fixed at `CREATE DATABASE`; changing them later
  requires a dump and reload of live data. A wrong choice is invisible until names sort oddly or a search
  misses an organization.
- **Settled so far:** UTF-8; ICU provider recommended; deterministic database default; case/accent-insensitive
  behaviour applied per column or per query; `C` collation for hashes and codes. **The ICU locale is not
  chosen** (`az`, `und`/root or another).
- **The experiment must cover:**
  - Azerbaijani **İ / I / ı / i** — case folding, case-insensitive equality and ordering;
  - **ə** and the other Azerbaijani letters (ç, ğ, ö, ş, ü) — ordering against base letters, equality;
  - **Cyrillic aliases**, including mixed Latin/Cyrillic lists;
  - **composed vs decomposed Unicode** (e.g. `ö` U+00F6 vs `o` + U+0308; `İ` U+0130 vs `I` + U+0307) — whether
    equality treats them as equal and which normalization form the normalization component stores;
  - **punctuation** — quotes (`"`, `«»`), hyphens, dots in abbreviations, repeated spaces;
  - **ordering**, **equality**, **uniqueness** under unique constraints, and **search behaviour**
    (prefix and pattern matching on normalized columns, index usability).
- **Test data:** approved **non-confidential** organization names plus synthetic fixtures — never production
  case data.
- **Output:** a new ADR recording provider, locale, normalization form and the fixture results.
- **Does not block:** repository scaffold, schema design, migration design or implementation planning.
  Development databases may use a provisional collation and are recreated once PS-1 is decided; any migration
  that fixes database- or column-level collation is finalized only after PS-1.

### PS-2 — Server operating system

**Status:** Required before the server is built and deployment scripts are written · **Sources:** `ARCHITECTURE.md` §6.2, §23.1 (OQ-A2)

- Linux is preferred, Windows Server acceptable per ADR-023. Depends on the OS local IT actually administers.
  Does not block scaffold, schema or migration design.

---

## Deferred decisions

Bounded by the cited ADRs; decided at the stated point without reopening them.

| ID | Decision | Bounded by | Decide when |
|---|---|---|---|
| DEF-01 | Backup software; how object-complete recovery boundaries are established and recorded | ADR-021, ADR-034 | second machine available (OQ-A8) |
| DEF-02 | Case-level lock mechanism (row lock vs uniformly applied serializable isolation) | ADR-019 | implementation |
| DEF-03 | Operation-identity table shape, covered command list, retention | ADR-020 | schema / implementation |
| DEF-04 | Row-local expression of the `RESPONSIBLE` predicate in the assignment exclusions | ADR-017 | schema design |
| DEF-05 | UUIDv7 generated by the server or the application | ADR-031 | implementation |
| DEF-06 | Exact ORM (must not own the schema), migration runner, test framework, logging library, JS helper library | ADR-002–004 | implementation |
| DEF-07 | Session store mechanism (server-side and revocable) | ADR-033 | implementation |
| DEF-08 | `INTEGRITY_CHECK` audit action code (`DOCUMENT_MODEL.md` OQ-D8) | ADR-018 | schema design |
| DEF-09 | Azerbaijani/Russian normalization rules; adoption of `pg_trgm` (OQ-A6, OQ-15) | ADR-024, PS-1 | after PS-1 and measured use |
| DEF-10 | Separate file-delivery origin (`SECURITY.md` H-04) | ADR-035 | if inline preview is ever introduced |
| DEF-11 | Internal hostname resolution — internal DNS or hosts file (OQ-A7); never `.local` | `SECURITY.md` §5.4 | server build |

---

## Open business questions

None of these blocks repository scaffolding, schema design or migration design. Each is additive or a
rule/configuration change.

| ID | Question | Source IDs | V1 assumption | What it blocks |
|---|---|---|---|---|
| OB-1 | Single approval or four-eyes for the final result? | OQ-14, OQ-W2, OQ-P1 | neither assumed; both designed; no schema change either way | the final-result approval screen and its `CHECK` rule |
| OB-2 | Deadline rules — calendar or working days, start date, does `ON_HOLD` pause clocks? | OQ-5, OQ-W3 | `due_at` absolute; holds alter nothing; both facts displayed | legal accuracy of overdue figures only |
| OB-3 | What counts as "activity" for inactivity lists? | OQ-13, OQ-W4 | no rule assumed; `last_activity_at` rebuildable | the "no activity for N days" list |
| OB-4 | Confirm that a non-blocking requirement may stay open at normal closure | OQ-W6 | yes, uniformly (ADR-012) | nothing; "no" tightens rules without schema change |
| OB-5 | Accepted and forbidden upload formats; macro-enabled Office; DWG/DXF | OQ-D3, OQ-D4, OQ-D5, OQ-S6 | accept all, download-only, warn on active formats | an allow/deny policy list |
| OB-6 | Approve or revise RPO/RTO (proposed: DB ≤ 15 min, documents ≤ 1 h, restore within a business day) | OQ-A3 | proposals only, not contractual | backup cadence and go-live acceptance |
| OB-7 | Maximum upload size | OQ-A5 | none chosen; configuration | production configuration |
| OB-8 | Retention period and whether physical purge is ever permitted | OQ-9, OQ-D1, OQ-D2, OQ-S11, OQ-A9 | nothing physically deleted | nothing in V1 |

**Other open questions stay in their source documents** and are unaffected by this log: OQ-1, OQ-2, OQ-3,
OQ-6 / OQ-W5, OQ-7, OQ-10, OQ-11 (`DOMAIN_MODEL.md` §10); OQ-P3, OQ-P4 (`PERMISSIONS.md` §28); OQ-D6 / OQ-S7,
OQ-D9, OQ-D10, OQ-D11 (`DOCUMENT_MODEL.md` §16); OQ-S1–S5, S8–S10, S12–S14 (`SECURITY.md` §22); OQ-A1 (the
ADR-002 reopen trigger), OQ-A2 (PS-2), OQ-A4, OQ-A8 (DEF-01), OQ-A10, OQ-A11 (`ARCHITECTURE.md` §23).
Domain Model OQ-16 is **closed** by ADR-028.

---

## Numbering map

`ARCHITECTURE.md` §22 used candidate labels before this log existed; the domain amendments used `A-n`. Where
they went:

| Earlier label | Now | Earlier label | Now |
|---|---|---|---|
| ARCH ADR-1 backend stack | ADR-002 | ARCH ADR-13 background jobs | ADR-025 |
| ARCH ADR-2 frontend | ADR-003 | ARCH ADR-14 backup topology | ADR-034 |
| ARCH ADR-3 server OS | ADR-023, PS-2 | ARCH ADR-15 backup software | DEF-01 |
| ARCH ADR-4 / ADR-5 deployment, containers | ADR-022 | ARCH ADR-16 RPO/RTO | OB-6 |
| ARCH ADR-6 reverse proxy | ADR-023 | ARCH ADR-17 upload size | OB-7 |
| ARCH ADR-7 collation | PS-1 | ARCH ADR-18 API style | ADR-003 |
| ARCH ADR-8 extensions | ADR-017, ADR-024 | Domain C-1 / C-2 / C-3 | ADR-007 / ADR-008 / ADR-029 |
| ARCH ADR-9 data access | ADR-004 | A-1 / A-2 | ADR-009 / ADR-010 |
| ARCH ADR-10 authentication | ADR-033 | A-3 | ADR-011, ADR-012 |
| ARCH ADR-11 hostname and TLS | DEF-11 (TLS itself: `SECURITY.md` §5.4 baseline) | A-4 / A-5 / A-6 | ADR-013 / ADR-014, ADR-015 / ADR-016 |
| ARCH ADR-12 search normalization | ADR-024, DEF-09 | A-7 / A-8 / A-9 / A-10 | ADR-017 / ADR-018 / ADR-027 / ADR-028 |

---

## Readiness

| Next step | Ready? | Condition |
|---|---|---|
| Repository scaffold | **Yes** | stack recorded (ADR-002, ADR-003, ADR-004); reopened only by the ADR-002 trigger |
| Schema design | **Yes** | constraint requirements are recorded (ADR-009, 014, 017, 027, 028, 030, 031, 036); DEF-03, DEF-04, DEF-08 are decided during it |
| Migration design | **Yes** | collation-setting migrations are finalized only after PS-1 |
| Implementation planning | **Yes** | ADR-019 and ADR-020 are first-class work items with their own tests |
| **Production database initialization** | **No** | **blocked by PS-1** |
| Server build / deployment scripting | No | PS-2, DEF-11; backup tooling DEF-01 before go-live |
