# RCS — Permissions and Authorization

**Status:** Draft v1 (design only — no code, no migrations, no API, no UI)
**Authoritative inputs:** `/docs/PROJECT.md` (PROJECT SPEC v1), `/docs/DOMAIN_MODEL.md` (Domain Model v1,
frozen), `/docs/WORKFLOW.md`.
**Scale:** ~13 users, four roles, one department, LAN only.
**Post-review amendments (2026-09-17):** requirement resolution correction (§16, §23, §24, §26);
cross-case document sharing as a Chief disclosure (§16, §21.3, §23); version-scoped document visibility
(§19.2, §27.1); immediate suspension on departure (§23, §25, OQ-P5). Recorded in `DOMAIN_MODEL.md` §12.7.

---

## 14. Core authorization principle

### 14.1 Visibility and authority are different questions

Two independent checks, never collapsed into one:

| | **A. Visibility** | **B. Authority** |
|---|---|---|
| Question | *May this person see this object at all?* | *May this person perform this action on it?* |
| Default | ordinary cases are visible department-wide (`PROJECT.md` §13) | nothing beyond reading is implied by being able to read |
| Driven by | `case.is_restricted`, `assignment`, `case_access_grant`, role | role, plus a relationship condition for some actions |
| Failure mode if merged | either everyone can edit everything, or people cannot read cases they must cover | |

A Worker who can *see* every ordinary case can *close* none of them. That is the point.

### 14.2 The authorization rule

Conceptually — this is a specification, not an implementation:

```
can(actor, action, object) =
        actor.status == ACTIVE
    AND visible(actor, object)                         # A — §19
    AND capability(roles_of(actor, now), action)       # B — the matrix, §26
    AND relationship_ok(actor, action, object)         # assigned / cover / creator / no-dependents — §21
    AND state_allows(action, object)                   # workflow guards — WORKFLOW.md
    AND (not high_risk(action) OR reason_supplied)     # §23
```

Properties that must hold:

- **Deny by default.** An action not granted is denied. There is no implicit inheritance beyond the
  role ladder stated in §15–§18.
- **Roles are evaluated as of now**, from `user_role` validity windows — not from a cached login claim.
- **One decision point.** The same function answers for the UI, the API, a document download, a search
  result and an export. See §27.
- **Denials are recorded** as `PERMISSION_DENIED` audit events (Domain Model §2.18).

### 14.3 What this model deliberately is not

Not a configurable RBAC engine, not per-object ACLs, not permission groups, not a policy language. Four
roles, one restricted-case grant table, and a small set of relationship conditions. `PROJECT.md` §31
lists a "complex role designer" as a V1 non-goal, and at 13 users a configurable engine would cost more
to reason about than the thing it governs.

**And it contains no dispatch permissions.** There is no `SEND_OFFICIAL_LETTER`, no
`APPROVE_LETTER_FOR_DISPATCH`, and no equivalent — because this application neither sends official
correspondence nor gates its sending (`PROJECT.md` §1.1). Official letters are dispatched in a separate
external government system, which enforces whatever approval it requires. **Such a permission must not
be added here.** Chief and Head authority in this document governs **case and workflow decisions**,
never external transmission.

---

## 15. Worker

The role that does the work. Most staff are Workers.

**May:**

| Area | Actions |
|---|---|
| Visibility | view all ordinary cases department-wide; view a restricted case when assigned to it or explicitly granted (§19) |
| Registration | create a case; register **already-received** incoming and **already-sent** outgoing correspondence (`PROJECT.md` §1.1); attach copies of the official letters and their attachments; link documents to their business context |
| Workflow | create requests on cases they are assigned to or covering; register responses on **any visible case**; create requirements from responses; record evidence; fulfil requirements they are working on |
| Drafting | draft a final result on cases they are assigned to or covering |
| Notes | add internal records — notes, calls, meetings, site visits |
| Corrections | self-correct their **own** records while nothing else depends on them (§24) |
| Audit | view the activity history of any case they can see |

**May not:** waive a requirement; void a requirement for any reason other than a plain correction of
their own recent entry; close, reopen or cancel a case; approve a final result; override a closure
guard; assign or reassign anyone; grant restricted access; manage users or roles.

**The one deliberately unrestricted write:** *registering an official letter — incoming or outgoing —
and the responses it carries requires no assignment.* A letter that has already been received or sent in
the external system must be recorded here today, by whoever is doing the recording. Making that depend
on assignment is how the tracking record falls behind the official one (§21.3).

---

## 16. Chief

Operational authority over the department's work. `PROJECT.md` §12.

**Everything a Worker may do, without the assignment conditions** — a Chief may act on any case in the
department, assigned or not.

**Plus, and this is exactly what distinguishes Chief from Worker:**

| # | Chief-only action | Why it is not a Worker action |
|---|---|---|
| 1 | **Assign and reassign** cases, requests and requirements; arrange temporary cover | who does the work is a management decision |
| 2 | **Waive** a requirement | releases the department from a real obligation (`PROJECT.md` §12) |
| 3 | **Void** a requirement for a **business** reason — `NO_LONGER_REQUIRED`, `SUPERSEDED_BY_RESPONSE`, `BRANCH_REMOVED` | judging that an obligation no longer applies; distinct from a typing correction |
| 4 | **Close** a case | asserts the department's work is finished |
| 5 | **Reopen** a case | restarts a closed dossier |
| 6 | **Cancel** a case | terminates a dossier without a result |
| 7 | **Mark a requirement `FAILED`** | records an unmet obligation that will shape the decision |
| 8 | **Grant and revoke** restricted-case access (§20) | controls who sees confidential dossiers |
| 9 | **Correct** structured records after others depend on them (§24) | corrections with downstream effects need a second pair of eyes |
| 10 | **Resolve response conflicts** — record which response supersedes which (`WORKFLOW.md` §4.5), or retract a supersession recorded in error (`WORKFLOW.md` §4.7, amendment A-10) | choosing between two contradictory official letters |
| 11 | **Void a response** that is not the actor's own recent entry | removes an official act from the active record |
| 12 | **Approve a final result** — **only under approval Model A** (§28, OQ-P1) | unresolved business decision |
| 13 | **Correct a requirement resolution recorded in error** — return a `FULFILLED`/`WAIVED`/`VOID`/`FAILED` requirement to an open state (`WORKFLOW.md` Q7) | withdraws a recorded outcome that others relied on; the correct outcome then needs its own authority |
| 14 | **Share a document into another case** — a version-pinned link outside the document's home case (`DOCUMENT_MODEL.md` §4.8) | a disclosure decision: it makes that version visible to another dossier's viewers |

**May not:** override a closure guard (§9.4 of `WORKFLOW.md` — highest authority only); manage users,
roles or system configuration.

---

## 17. Head

The highest **business** authority. Not a system administrator.

**Everything a Chief may do, plus:**

| # | Head-only action |
|---|---|
| 1 | **Override closure guards** — close a case with unresolved obligations, mandatory reason, nothing falsified (`WORKFLOW.md` §9.4) |
| 2 | **Override the final-result readiness guard** (D1) and the supporting-document guard (D4) |
| 3 | **Approve a final result** — under either approval model; the second approval under Model B |
| 4 | **See every restricted case** without needing a grant |
| 5 | **Grant and revoke business roles** (Worker, Chief, Head) and the TechAdmin role (§25.2) |
| 6 | **Reactivate a cancelled case** (`WORKFLOW.md` T8) — correction of a mistaken cancellation |
| 7 | **View all audit history**, department-wide |

**Head does not receive technical responsibilities.** Head does not create accounts, configure
authentication, manage backups or deploy. That separation is the point of §18, and it runs both ways:
technical staff get no business authority, business authority gets no server access.

---

## 18. TechAdmin

Administers the system. Has **no business authority whatsoever**, by design (`PROJECT.md` §12).

**May:**

- create and deactivate user accounts; reset credentials; configure the authentication source
  (`user.auth_source` — `LOCAL` or `ACTIVE_DIRECTORY`);
- configure the application, storage volumes, retention jobs, integrity checks;
- configure and monitor backups, and report backup health (`PROJECT.md` §20);
- deploy, patch and maintain;
- view **technical** audit events: logins, permission denials, configuration changes, upload/download
  events, job failures.

**Must not — and the application must actively prevent, not merely omit from a menu:**

| Forbidden | Note |
|---|---|
| Approve or issue a final result | business authority |
| Waive, void or fail a requirement, or correct its resolution | business authority |
| Close, reopen or cancel a case | business authority |
| Register correspondence, responses or requirements | business record creation |
| Assign cases | management decision |
| **Grant any role, including TechAdmin** | prevents self-escalation (§25.2) |
| Act as another user, or "impersonate for support" | destroys attribution, which is the point of the audit trail |
| Browse ordinary case content through the application | §19.3 |

### 18.1 Technical access is not the same as application permission

A TechAdmin with a `TECH_ADMIN` role and no business role **cannot open a case screen**. If the same
person also needs to do departmental work, they are granted a **second, separate business role**, and
every action they take is attributed to them under whichever authority it required. Two roles on one
account, both visible in `user_role`, is honest; a technical role that quietly carries business power is
not.

### 18.2 What the application cannot promise

The person who administers the server can read the PostgreSQL database and the document storage
directly. **No application-level authorization prevents that**, and this document does not pretend
otherwise (§19.3).

---

## 19. Restricted cases

Uses the frozen model only: `case.is_restricted` + `case_access_grant`. No generic per-case ACL engine
(`PROJECT.md` §13, Domain Model §2.20).

### 19.1 Who can see a restricted case

| Who | Basis |
|---|---|
| The current responsible user | `assignment`, `role = RESPONSIBLE`, `valid_until IS NULL` |
| Anyone else currently assigned — co-worker, supervisor, **temporary cover** | `assignment`, any active role |
| Chief | role |
| Head | role |
| Explicitly authorised users | an `ACTIVE` `case_access_grant` |
| **Nobody else**, including other Workers | deny by default |

### 19.2 What restriction does and does not change

| Changes | Does not change |
|---|---|
| Who may **see** the case and everything hanging off it — correspondence, documents, requests, responses, requirements, audit | **What anyone may do.** A granted user is still a Worker with Worker authority |
| Whether it appears in search results, dashboards, exports and counts | The workflow — guards, transitions and closure rules are identical |

**Restriction filters at the source, not at the screen.** A restricted case must be absent from search
results, list counts, dashboard totals and exports for anyone who cannot see it. A count that reveals
"there are 3 cases you cannot see" leaks the existence of the dossier (§27.2).

**Documents are visible per version, through links** (`DOCUMENT_MODEL.md` §10.1). A document shared from
a restricted case into another case exposes there only the version pinned by that link — not the
document's other versions, their filenames or their existence. A response registered in another case
from a letter filed in a restricted case exposes nothing of that letter (`DOMAIN_MODEL.md` §2.8).

### 19.3 TechAdmin and restricted cases

**Preferred principle, adopted: TechAdmin has no ordinary case UI access at all** — restricted or not
— purely by virtue of administering the system. Administering a system is not a reason to read its
contents.

And the honest part:

> The OS and database administrator can read the PostgreSQL tables and the document storage directory
> directly. On a 13-person deployment this is very likely the same person as the TechAdmin.
> **Application-level authorization cannot prevent root-level access, and nothing in this document
> should be read as claiming it does.**

What can be done, and where it belongs:

| Control | Nature | Owner |
|---|---|---|
| No business role on the technical account | preventive, application-level | this document |
| No filesystem share exposing the document storage to workstations (`PROJECT.md` §23) | preventive, infrastructure | `ARCHITECTURE.md` |
| Backups not all writable from the primary machine (`PROJECT.md` §20) | preventive, infrastructure | `ARCHITECTURE.md` |
| Audit of administrative and technical access | **detective, not preventive** | `SECURITY.md` |
| Separating the OS/DB administrator from the application administrator | organizational; may be impossible at this size | the organization |

The realistic control at this scale is **detective**: make administrative access visible and
attributable rather than pretending it is impossible.

---

## 20. `case_access_grant`

### 20.1 What it is

A revocable, reason-bearing, **view-only** authorization for **one named user** on **one restricted
case**. Nothing more (Domain Model §2.20).

| Field | Rule |
|---|---|
| `case_id`, `user_id` | one grant, one user, one case — never a group, never a role, never a pattern |
| `granted_by_user_id`, `granted_at` | who and when |
| `reason_note` | **mandatory.** A grant without a stated reason is not auditable |
| `valid_from`, `valid_until` | `valid_until` optional; a time-boxed grant is preferred where the need is temporary |
| `status`, `revoked_by_user_id`, `revoked_at`, `revocation_reason_note` | revoked, **never deleted** |

### 20.2 Workflow

| Step | Rule |
|---|---|
| **Grant** | Chief or Head. Requires the case to be `is_restricted` — a grant on an ordinary case is meaningless and must be rejected rather than stored |
| **Revoke** | Chief or Head; also the original grantor. Revocation is a status change with a reason, never a delete |
| **Expire** | a grant past `valid_until` stops granting visibility automatically; the row remains |
| **On un-restricting a case** | existing grants become inert but are **not** revoked or deleted — if the case is restricted again later, the history of who had access is intact |
| **On case closure** | grants remain as they are; a closed restricted case is still restricted |
| **Audit** | granting and revoking are audited (`WORKFLOW.md` §13.1) |

### 20.3 What it must never become

| Never | Why |
|---|---|
| Per-action rights on a grant ("may edit", "may close") | that is the ACL matrix `PROJECT.md` §13 explicitly rejects for V1 |
| Grants on ordinary cases | ordinary cases are visible department-wide; a grant there is noise that will later be mistaken for a permission |
| Group or role grants | one row, one person — anything else is a permission system in disguise |
| A way to *withhold* access | a grant only ever **adds** a viewer. There is no negative grant, no deny rule |

If per-case action rights are ever genuinely needed, that is a new design decision requiring an ADR in
`DECISIONS.md` — not an extra column here.

---

## 21. Assignment versus permission

### 21.1 They are different things

**Assignment** answers *who is responsible*. **Permission** answers *who may act*. Conflating them
produces a system where work stops when one person is away — the failure `PROJECT.md` §11 and §19 are
written to avoid.

### 21.2 The four relationship conditions

| Condition | Meaning |
|---|---|
| **none** | any user whose role allows the action, on any case they can see |
| **assigned** | an `ACTIVE` `assignment` on the case (or the specific request/requirement), any role |
| **cover** | an `ACTIVE` `TEMPORARY_COVER` assignment, inside its validity window — treated identically to *assigned* |
| **creator, no dependents** | the actor created the row **and** nothing else references it yet (§24.2) |

### 21.3 Which writes need which

| Operation | Condition | Rationale |
|---|---|---|
| View an ordinary case | none | department-wide by default |
| **Register incoming correspondence** (already received externally) | **none** | *the absence rule.* A letter that has arrived must be recordable today by whoever is doing the recording |
| **Register outgoing correspondence** (already sent externally) | **none** | same — the letter has already gone out; recording it here is bookkeeping, not an act with external effect |
| **Register a response** | **none** | same — an official answer must reach the record promptly |
| **Upload and link a document** | **none** | scanning is often done by whoever is at the scanner. Within the document's home case; a link into **another** case is a Chief disclosure decision (§16, row 14) |
| Add an internal note | none | |
| Create a request | assigned / cover (Chief+: none) | deciding that the department requests something of an authority is case work, not filing. (Creating it *while registering the already-sent letter* is part of that registration — see §21.4) |
| Create a requirement from a response | assigned / cover, **or the person registering that response** | reading conditions out of a letter is part of registering it |
| Record evidence | assigned / cover | |
| Fulfil a requirement | assigned / cover | judging an obligation satisfied is case work |
| Edit case metadata (title, subject, requesting organization) | assigned / cover (Chief+: none) | |
| Draft a final result | assigned / cover | |
| Withdraw a document, void a response, void a requirement as a correction | creator, no dependents | §24 |
| Everything in §16 and §17 | role only — assignment is irrelevant | management authority is not delegated by assignment |

### 21.4 Registering a letter may create the request it carries

Under `WORKFLOW.md` R1b, registering an already-sent outgoing letter normally creates the request(s) it
carried, in one act. That creation is **part of the registration** and therefore needs **no assignment**
— otherwise the absence rule would be defeated by its own side effect, and a letter would sit unrecorded
because the person recording it did not own the case.

This grants nothing else: the resulting request is an ordinary request, and every later decision on it
(closing it, raising requirements from its responses) follows the normal conditions above.

### 21.5 Assignment grants no extra authority

Being assigned never confers a Chief action. An assigned Worker still cannot close the case they are
responsible for. Assignment **widens what a Worker may do on that case**; it never **raises their
role**.

And no role in this application confers authority over external dispatch, because no such authority
exists here (§14.3).

---

## 22. Temporary coverage

### 22.1 The requirement

Worker A is absent for a week. Worker B covers. Nobody shares a password, and the record must show that
**B** did the work while **A** remained the responsible officer.

### 22.2 How it works

| Step | Record |
|---|---|
| 1 | Chief creates an `assignment`: `scope = CASE`, `assignee = B`, `role = TEMPORARY_COVER`, `valid_from`/`valid_until` = the absence period, `covers_assignment_id` → A's `RESPONSIBLE` assignment |
| 2 | **A's `RESPONSIBLE` assignment is not ended.** A remains the responsible officer of record throughout |
| 3 | B signs in **as B**. Every action B takes is attributed to B in `audit_event`, with B's own identity snapshot and roles at that moment |
| 4 | Permission checks treat B as *assigned* for the window (§21.2) |
| 5 | At `valid_until` the cover lapses automatically; the row remains as permanent history |
| 6 | If the absence extends, a **new** cover row is created. Existing rows are not edited |

### 22.3 What this preserves

| Question | Answer, from the rows |
|---|---|
| Who was responsible on 12 April? | A — the `RESPONSIBLE` assignment spanning that date |
| Who actually registered the letter on 12 April? | B — `audit_event.actor_user_id` |
| Under what authority did B act? | the `TEMPORARY_COVER` assignment, with its dates and who arranged it |
| Did anyone share credentials? | no mechanism exists to |

The non-overlap constraint on `RESPONSIBLE` assignments (Domain Model §2.7) is unaffected: cover is a
different role and coexists with the responsible assignment.

### 22.4 Cover is not a role change

A covering Worker gains **no** Chief authority over the covered case. If the absent person was a Chief,
their authority does not transfer through cover — Chief actions on their cases are performed by another
Chief or by Head. Authority follows the role, never the assignment (§21.5).

---

## 23. High-risk actions

Actions that change the meaning of the record, release obligations, or alter who can see or do what.

| Action | Minimum role | Reason required | Additional condition |
|---|---|---|---|
| Waive a requirement | **Chief** | **yes** — authoriser + reason code + note | — |
| Void a *genuine* requirement (business reason) | **Chief** | **yes** — `void_reason` + note; `void_source_response_id` where a letter caused it | — |
| Mark a requirement `FAILED` | **Chief** | **yes** — failure reason | must be visible on the decision screen (`WORKFLOW.md` §8.5) |
| Correct a requirement resolution recorded in error (Q7) | **Chief** | **yes** — reason code + note | errors only; the case must be open to work (reopen first if `CLOSED`); evidence retracted row by row; the correct terminal state then needs its own authority |
| Link a document into another case | **Chief** | **yes** | pinned to one version; a disclosure decision (`DOCUMENT_MODEL.md` §4.8, §10.3) |
| Void a response | **Chief** (Worker: own, no dependents — §24) | **yes** | affected requirements are surfaced, never cascaded |
| Close a case | **Chief** | closure type; note if a guard was overridden | guards G1–G5 |
| Reopen a case | **Chief** | **yes — always** | same case only |
| Cancel a case | **Chief** | **yes** | open items terminated individually first |
| **Override a closure guard** | **Head** | **yes — mandatory, naming the guard** | unresolved records left untouched |
| Override final-result guards D1/D4 | **Head** | **yes** | — |
| Approve a final result | **Head** (Chief under Model A only) | — | §28 OQ-P1 |
| Revoke an issued final result | **Head** | **yes** | — |
| Grant / revoke restricted access | **Chief** | **yes** | case must be restricted |
| Reactivate a cancelled case | **Head** | **yes** | correction only |
| Grant or revoke a role | **Head** | **yes** | §25.2 |
| Suspend a departing user's account | **TechAdmin**, on business instruction | **yes** | **immediate** — sign-in blocked and sessions revoked at once; never waits for reassignment (§25.3) |
| Deactivate a user account | **TechAdmin** | **yes** | final step after handover: open work reassigned first (§25.4) |
| Technical configuration | **TechAdmin** | — | no business effect |

**No approval ceremonies are added.** Every row above is a single action by one authorised person with
a reason — except the final-result approval, where a second person is *one of the two candidate models*
and remains an open business decision (§28). For 13 people, reasons and attribution are the control;
multi-step sign-offs would be friction without a stated requirement.

---

## 24. Corrections

People make mistakes, and the system forbids deletion. Correction authority must therefore be
**designed**, or staff will work around it.

### 24.1 The governing rule

> A Worker may correct **their own** record **while nothing else depends on it**.
> Once something depends on it, or it is not theirs, a **Chief** corrects it.

"Depends on it" is a property of the rows, not a clock: no response registered against the request, no
child request created from the requirement, no evidence linked, no response yet registered against the
correspondence, the document not yet cited as evidence. This is deterministic, needs no timer, and
degrades in the safe direction — the more the record has been built on, the more authority it takes to
change it.

**Corrected by the external-correspondence clarification.** This rule previously used *"the
correspondence not yet dispatched"* as one of its dependency tests. Under `PROJECT.md` §1.1 an outgoing
letter is **always already sent** before it is recorded here, so that test would have been permanently
true — silently removing a Worker's ability to fix a typo in the record they had just entered, which is
the opposite of the intent. The dependency that actually matters is whether **other records in this
application** have been built on it. Correcting our copy of a letter has no external effect at all: the
official record lives in the external system and nothing done here touches it.

*(A simple time window — "within the same working day" — is an acceptable simplification if the
dependency check proves awkward in practice. It is weaker, and it is not what this document specifies.)*

### 24.2 The common mistakes

| Mistake | Mechanism | Worker (own, no dependents) | Otherwise |
|---|---|---|---|
| Wrong organization on a `DRAFT` request, not yet issued | edit in place; audited | ✔ | Chief |
| Wrong organization recorded on a registered letter | edit the metadata in place — **the official letter is unaffected**, only our transcription of it was wrong; `before`/`after` in audit | ✔ while nothing depends on it | **Chief** once responses depend on it |
| Wrong file linked | `document_link` → `REMOVED` + a new link. The file is not deleted | ✔ | Chief |
| Wrong document uploaded entirely | `document_version` → `WITHDRAWN` with reason; bytes retained | ✔ | Chief |
| Wrong external letter number or date on correspondence | edit in place; it is our transcription of an external fact, and the external record remains authoritative and untouched | ✔ while nothing depends on it | **Chief** once responses depend on it |
| **Response registered against the wrong request** | response is immutable → `VOID` it (reason `DATA_ENTRY_ERROR`) and register a new one on the correct request | ✔ | **Chief** |
| Duplicate response | `VOID` the duplicate, reason `DUPLICATE` | ✔ | Chief |
| Requirement created accidentally | `VOID`, reason `DATA_ENTRY_ERROR` | ✔ | **Chief** |
| Requirement created against the wrong response | `VOID` it and create it against the right one — the causal link is not editable | ✔ | Chief |
| Wrong case entirely (letter filed in the wrong dossier) | `VOID` the response(s); the `correspondence` is re-registered against the correct case | ✘ | **Chief** |
| Requirement wrongly marked fulfilled (or wrongly waived, voided or failed) | the requirement returns to `OPEN`/`IN_PROGRESS` through the correction transition Q7 (`WORKFLOW.md` §5.2), which preserves the withdrawn resolution in a correction record; wrong evidence is **retracted** row by row (not deleted); the correct terminal state, if any, is then recorded by its own transition | ✘ | **Chief** |
| Case created in error | `CANCELLED`, closure type "registered in error" | ✘ | **Chief** |

### 24.3 What correction never means

- Never a hard delete (`PROJECT.md` §29.6).
- Never an edit to an immutable business fact — `response` and `document_version` are corrected by
  `VOID`/`WITHDRAW` plus a new row, never by rewriting.
- Never a change to a causal pointer. `source_requirement_id` and `source_response_id` are write-once
  (Domain Model §6.4); a wrong link is corrected by voiding the row and creating the right one.
- Never a jump from one terminal state to another. A requirement resolution recorded in error returns to
  an open state first (Q7); the correct outcome is then a separate, separately authorised transition.
- Never silent. Every correction is an `audit_event` with `before_state` and `after_state`.

---

## 25. Account lifecycle

### 25.1 States

`user.status`: `ACTIVE`, `SUSPENDED`, `DEACTIVATED`. **Never deleted** (`PROJECT.md` §14). Every audit
entry, upload, assignment and decision keeps resolving to a real person forever.

### 25.2 Who does what — the separation that matters

| Action | Who |
|---|---|
| Create an account | **TechAdmin** |
| Configure `auth_source` (`LOCAL` / `ACTIVE_DIRECTORY`), reset credentials | **TechAdmin** |
| Suspend / deactivate / reactivate an account | **TechAdmin**, on a business instruction |
| **Grant or revoke a business role** (Worker, Chief, Head) | **Head only** |
| **Grant or revoke the TechAdmin role** | **Head only** |

Two consequences of that split, both intentional:

1. **A newly created account has no roles and can do nothing.** Account existence and business authority
   are separate acts by separate people.
2. **A TechAdmin cannot mint another TechAdmin, or promote themselves.** Every role grant carries
   `user_role.granted_by_user_id`, and that will always be a Head.

**Bootstrap:** the first Head role is granted during deployment, as a documented installation step
performed by TechAdmin and recorded like any other grant. It is the one unavoidable exception and it
should be visible in the audit trail from day one rather than quietly special-cased.

### 25.3 Temporary absence versus permanent departure

| | **Temporary absence** | **Permanent departure** |
|---|---|---|
| Account | stays `ACTIVE` (or `SUSPENDED` for a long leave) | **`SUSPENDED` immediately** — sign-in blocked and every session revoked at once, never waiting for handover (`SECURITY.md` §6.5); then **`DEACTIVATED`**, with reason, once handover is complete |
| Assignments | **kept** — the person remains responsible of record | kept only during handover; **ended**, with `end_reason = USER_DEACTIVATED`, as the work is reassigned |
| Coverage | a `TEMPORARY_COVER` assignment (§22) | the work is **reassigned** to a new responsible person |
| History | untouched | untouched |

Access removal and administrative closure are two different steps, deliberately: the first must be
instant, the second must be orderly, and making the first wait for the second leaves a departed
employee able to sign in for as long as reassignment takes.

### 25.4 Deactivation guard

**The guard never delays access removal.** Suspension on departure (§25.3) is immediate and unguarded.
The guard applies only to the final `DEACTIVATED` step: a user holding `ACTIVE` `RESPONSIBLE` assignments
**may not be deactivated** until those cases are reassigned. The system lists them — including while the
user is suspended — and a Chief reassigns them (one action per case, or a bulk reassignment that still
writes one `assignment` row per case with a shared reason). This prevents the common failure of orphaned
cases discovered months later.

A deactivated user cannot sign in, cannot receive new assignments, and keeps appearing in every
historical record exactly as before.

---

## 26. Permission matrix

**✔** allowed  ·  **△** conditional or limited (every one explained below)  ·  **✘** not allowed

| Action | Worker | Chief | Head | TechAdmin |
|---|---|---|---|---|
| View normal Case | ✔ | ✔ | ✔ | ✘ |
| View restricted Case | △¹ | ✔ | ✔ | ✘² |
| Create Case | ✔ | ✔ | ✔ | ✘ |
| Edit Case metadata | △³ | ✔ | ✔ | ✘ |
| Register Correspondence | ✔⁴ | ✔ | ✔ | ✘ |
| Upload Document | ✔⁴ | ✔ | ✔ | ✘ |
| Withdraw own incorrect Document | △⁵ | ✔ | ✔ | ✘ |
| Create Request | △³ | ✔ | ✔ | ✘ |
| Register Response | ✔⁴ | ✔ | ✔ | ✘ |
| Create Requirement | △⁶ | ✔ | ✔ | ✘ |
| Fulfill Requirement | △³ | ✔ | ✔ | ✘ |
| Waive Requirement | ✘ | ✔ | ✔ | ✘ |
| Void Requirement | △⁷ | ✔ | ✔ | ✘ |
| Correct Requirement resolution (Q7) | ✘ | ✔ | ✔ | ✘ |
| Link Document into another Case | ✘ | ✔ | ✔ | ✘ |
| Assign Case | ✘ | ✔ | ✔ | ✘ |
| Reassign Case | ✘ | ✔ | ✔ | ✘ |
| Close Case | ✘ | ✔ | ✔ | ✘ |
| Reopen Case | ✘ | ✔ | ✔ | ✘ |
| Cancel Case | ✘ | ✔ | ✔ | ✘ |
| Create FinalResult (draft) | △³ | ✔ | ✔ | ✘ |
| Approve FinalResult | ✘ | △⁸ | ✔ | ✘ |
| Override closure guard | ✘ | ✘ | ✔ | ✘ |
| Grant restricted access | ✘ | ✔ | ✔ | ✘ |
| Manage users | ✘ | ✘ | △⁹ | ✔ |
| Manage roles | ✘ | ✘ | ✔ | ✘¹⁰ |
| View audit history | △¹¹ | ✔ | ✔ | △¹² |
| Technical system configuration | ✘ | ✘ | ✘ | ✔ |

### Every △ explained

**¹ Worker — view restricted Case.** Only when currently assigned to it (any assignment role, including
`TEMPORARY_COVER`) or holding an `ACTIVE` `case_access_grant`. Otherwise the case is absent from their
search results, lists and counts entirely (§19.2).

**² TechAdmin — view restricted Case.** ✘ at the application level, for restricted *and* ordinary cases
alike. The honest qualification is §19.3: direct database and filesystem access cannot be prevented by
this document.

**³ Worker — edit Case metadata, create Request, fulfil Requirement, draft FinalResult.** Requires an
`ACTIVE` assignment on the case or an in-window `TEMPORARY_COVER`. These are case work, not filing.
Lifecycle state is never editable this way — it changes only through the transitions in
`WORKFLOW.md` §1.

**⁴ Worker — register Correspondence (incoming **and** outgoing), register Response, upload Document: no
assignment required.** The absence rule (§21.3). The letter has already been sent or received in the
external government system (`PROJECT.md` §1.1); recording it here is bookkeeping that must not wait for
the right person to return from leave. This is the single most important permission decision in this
document: every alternative ends with this application's record drifting behind the official one. Under
§21.4 the registration may also create the request(s) the letter carried.

**⁵ Worker — withdraw own incorrect Document.** Their own upload, and only while it is not cited as
`requirement_evidence` or referenced by an issued final result. Withdrawal is a status change with a
reason; the bytes are never removed (Domain Model §2.14).

**⁶ Worker — create Requirement.** Assigned/cover, **or** the person registering the response that
imposes it — reading conditions out of a letter is part of registering that letter, and forcing a
handoff there would mean conditions get missed.

**⁷ Worker — void Requirement.** Only as a plain correction of their **own** recent entry
(`void_reason` ∈ {`DATA_ENTRY_ERROR`, `DUPLICATE`}) and only while no child request, evidence or other
record depends on it. Voiding for a **business** reason — `NO_LONGER_REQUIRED`,
`SUPERSEDED_BY_RESPONSE`, `BRANCH_REMOVED` — is a Chief judgement (§16, row 3). This is the precise
line `PROJECT.md` asks for between correction and decision.

**⁸ Chief — approve FinalResult.** Permitted **only if approval Model A** (single authorised approval)
is adopted. Under Model B (four-eyes) approval is Head's, and the approver must differ from the
decision-maker. **Unresolved — OQ-P1 (§28).**

**⁹ Head — manage users.** Head may *order* an account deactivated and may grant or revoke roles, but
does not create accounts, reset credentials or configure the authentication source. Account mechanics
are technical; who holds authority is not.

**¹⁰ TechAdmin — manage roles.** ✘ by design, including the TechAdmin role itself. This is what stops
the technical administrator from granting themselves business authority (§25.2). They administer
accounts; Head decides what those accounts may do.

**¹¹ Worker — view audit history.** The activity history of any case they can see, plus their own
actions. Not other people's actions on cases invisible to them, and not system-wide audit.

**¹² TechAdmin — view audit history.** Technical events only: logins, permission denials, configuration
changes, upload/download events, job failures. Not the business audit trail — not `before_state` /
`after_state` payloads of case records. Subject to §19.3: at the database level this is a policy, not
an enforced boundary.

---

## 27. Server-side enforcement

### 27.1 One decision point, every path

`can(actor, action, object)` (§14.2) is evaluated **on the server, on every request**, and it is the
same function for:

| Path | What must be re-checked |
|---|---|
| UI | never the authority — the UI only *reflects* decisions made server-side |
| API / RPC | every call, including reads |
| **Document download** | on **every** download, by **version** and by the context it is being fetched through — the requested version must be one that context's link exposes (`DOCUMENT_MODEL.md` §10.1). A URL, a document or version identifier, a hash or a file path is **not** an authorization token |
| **Search** | results are **filtered at query time**, not hidden after retrieval |
| **Exports and printing** | same checks as the screen, plus an `EXPORT` / `PRINT` audit event |
| Dashboards, counts, aggregates | included in the same filtering (§27.2) |

**Hiding a button is not a permission.** Every action in §26 must fail server-side when attempted
directly, and the failure must be recorded as `PERMISSION_DENIED`.

### 27.2 Aggregates leak

A restricted case must not appear in a count, a total, a "cases by employee" chart or a date-range
histogram for anyone who cannot see it. Saying *"47 cases (3 hidden)"* tells the viewer a confidential
dossier exists, roughly when it was opened and who is working on it. Filter before aggregating, always.

### 27.3 Evaluate roles at action time

Authority comes from `user_role` validity windows evaluated **now**, and visibility from `assignment`
and `case_access_grant` evaluated **now**. A revoked role, an expired cover or a revoked grant must take
effect on the next action, not at the next sign-in. Long-lived sessions must not carry stale authority.

### 27.4 What is deliberately not designed here

Authentication mechanics, session handling, password policy, AD binding, CSRF/transport concerns and
audit hash chaining. Those belong to `SECURITY.md` and `ARCHITECTURE.md`. This document defines *what
the answer must be*, not *how the question is transported*.

---

## 28. Open questions

### 28.1 Business decisions still needed

**OQ-P1 — Single approval or four-eyes for the final result?** (Domain Model OQ-14, `WORKFLOW.md`
OQ-W2.) Both models are designed (`WORKFLOW.md` §8.4); **neither is assumed**. The only thing that
changes here is matrix footnote ⁸ — whether Chief may approve. No schema change either way.
*Blocking:* the final-result screen, nothing else.

**OQ-P2 — CLOSED: NO.** There is no dispatch approval in this application, because there is no dispatch
in this application (`PROJECT.md` §1.1, `WORKFLOW.md` §14.0). No permission row, no approver column and
no change to the frozen domain model are needed. A `SEND_OFFICIAL_LETTER` or
`APPROVE_LETTER_FOR_DISPATCH` permission must not be introduced (§14.3).

**OQ-P3 — May a Chief grant restricted access to any case, or only to cases in their own area?**
Current design: any case in the department (there is one department). If the department later has
sections, this needs revisiting.

**OQ-P4 — Should Head be able to *see* every restricted case, or only to grant access to it?**
Current design: Head sees all (§17), which follows `PROJECT.md` §12 "access all Cases". Some
organizations prefer that even the highest authority needs an explicit, logged grant. Stated so the
choice is conscious.

**OQ-P5 — CLOSED (post-review): both, as two steps.** A departing employee's account is **suspended
immediately** — sign-in blocked, sessions revoked — and **deactivated after handover** (§25.3, §25.4).
Access removal never waits for reassignment. No model change: `SUSPENDED` and `DEACTIVATED` already exist.

### 28.2 Implementation decisions safely deferred

| Question | Why it can wait |
|---|---|
| Authentication mechanism — local accounts first, or AD from day one | `user.auth_source` already represents both; `PROJECT.md` §14 leaves it to infrastructure |
| Session lifetime, re-authentication for high-risk actions | §27.3 fixes the *rule* (evaluate now); duration is a `SECURITY.md` parameter |
| Whether the dependency check in §24.1 or a simple time window governs self-correction | §24.1 specifies the dependency rule and names the weaker alternative explicitly |
| UI presentation of denied actions — hidden or disabled-with-explanation | §27.1 makes this purely cosmetic |
| Bulk reassignment tooling | §25.4 fixes the rule — one `assignment` row per case |
| Audit retention and archival | Domain Model OQ-9 |

---

## 29. Self-review

| # | Question | Answer | Mechanism |
|---|---|---|---|
| 6 | Can a Worker register an incoming letter while another employee is absent? | **Yes** | §21.3 / matrix footnote ⁴ — registering correspondence, responses and documents requires no assignment |
| 7 | Can temporary coverage work without shared credentials? | **Yes** | §22 — `TEMPORARY_COVER` assignment; B signs in as B; A stays responsible of record; every action attributed to B |
| 11 | Can TechAdmin administer the application without becoming a business approver? | **Yes** | §18 — no business capability, cannot grant roles including their own, cannot open case screens; §19.3 states plainly what application authorization cannot prevent |
| 12 | Can restricted visibility be granted without a full ACL matrix? | **Yes** | §20 — one user, one case, view-only, reason-bearing, revocable; §20.3 lists what it must never become |
| 13 | Is every high-risk action attributable to the actual person? | **Yes** | §23 + Domain Model §2.18 — actor, identity snapshot, roles at the time, reason, `correlation_id`; no impersonation mechanism exists (§18) |

Plus the checks specific to this document:

| Check | Result |
|---|---|
| Visibility and authority kept separate | **Yes** — §14.1; every matrix row is an *action*, and viewing is one row among them, not a prerequisite for the rest |
| Does any △ hide an unstated rule? | **No** — all twelve are explained in §26 with their exact condition |
| Can work stall because one person is absent? | **No** — the four unconditional Worker writes (register correspondence, register response, upload, note) plus cover and Chief override |
| Is the role model still readable by one person? | **Yes** — four roles, one grant table, four relationship conditions, no policy language |
| Any dependency on a domain change? | **None.** OQ-P2 is now closed NO, so the one conditional dependency is gone |
| Does any permission imply sending or approving dispatch? | **No** — §14.3 states the prohibition explicitly; no matrix row, role section or high-risk entry concerns transmission |
| Can a Worker record an already-sent outgoing letter with no assignment? | **Yes** — §21.3, §21.4, footnote ⁴ |
| Can a Worker still fix a typo in a letter record they just entered? | **Yes** — §24.1, restored by removing the "not yet dispatched" test the clarification made permanently true |

### Tension with Domain Model v1

**None.** This document uses `user`, `role`, `user_role`, `assignment`, `case.is_restricted`,
`case_access_grant` and `audit_event` exactly as frozen. No entity, column, cardinality or invariant is
added, and no ACL structure is introduced.

**Post-review (2026-09-17).** §24.2 permitted correcting a requirement wrongly marked fulfilled by
recording "a new terminal state", which contradicted "terminal states are final". Resolved by the Q7
correction transition and Domain Model amendment A-2; the permissions above use it without adding a role,
a grant type or an ACL. Departure now suspends immediately (OQ-P5 closed), and document visibility is per
version — both without model change.

---

*End of document. No application code, database migrations, API endpoints or UI components are defined
in or implied by this authorization design.*
