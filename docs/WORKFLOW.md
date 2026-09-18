# RCS — Workflow

**Status:** Draft v1 (design only — no code, no migrations, no API, no UI)
**Authoritative inputs:** `/docs/PROJECT.md` (PROJECT SPEC v1), `/docs/DOMAIN_MODEL.md` (Domain Model v1,
frozen).
**Companion:** `/docs/PERMISSIONS.md` — *who* may perform each transition described here.
**Post-review amendments (2026-09-17):** closure contract and one definition of `is_blocking` (§3.4,
§7.2, §7.4, §9); final-result replacement (§8.2, §8.6); response supersession edges (§4.3, §4.5);
requirement resolution correction Q7 (§5.2); informational late responses and R8/R9 (§3.2, §4.6, §10.3);
audit actor semantics (§0.1, §13.1). Recorded in `DOMAIN_MODEL.md` §12.7.
**Pre-schema pass (2026-09-17):** retraction of a supersession recorded in error, restoring the
superseded response (§3.2 R3/R9, §4.3, §4.7, §13.1 — Domain Model A-10, closes OQ-16). Decisions are logged
in `/docs/DECISIONS.md`.

---

## 0. Scope and standing rules

This document defines **what happens, in what order, under what guards**. It does not define who is
allowed to do it (that is `PERMISSIONS.md`), and it does not implement anything.

Four rules hold everywhere in this document and are not repeated in each section:

1. **The domain model is frozen.** Every state named here exists in Domain Model v1.1 — v1 plus the
   post-review amendments of its §12.7, which is the only place entities or cardinalities were added
   (`response_supersession`, `requirement_resolution_correction`); no state is added. Where this document
   defines a *transition* that the frozen model's
   lifecycle sketches did not draw, it is marked **[transition added here]** — defining the complete
   transition table is precisely this document's job (`PROJECT.md` §30, Phase 2), and none of them adds
   a state or a column.
2. **Nothing is deleted and no history is rewritten.** Every workflow step below is an append, a state
   transition with a who/when/why triple, or a new row that supersedes an earlier one.
3. **Nothing cascades automatically across a business decision.** Where one record's fate implies a
   question about another, the system **surfaces the question**; a person answers it. Automatic
   cascades are named explicitly where they exist, and they only ever set derived/system state.
4. **Operational status is derived, never typed in** (`PROJECT.md` §10). Only the five case states,
   the six request states, the six requirement states, the three response states and the five final
   result states are stored.
5. **This application never sends or receives official correspondence** (`PROJECT.md` §1.1). Official
   letters are dispatched and received in a separate external government system, which assigns their
   official numbers and dates. Everything in this document describes **recording, after the fact, what
   already happened there**. No workflow step in this system transmits anything, and no state means
   "waiting to be sent".

### 0.1 The two kinds of state change

| | **Human decision** | **System consequence** |
|---|---|---|
| What it is | Someone judges something and records it | A state that follows mechanically from a record the human already created |
| Examples | closing a case, waiving a requirement, deciding a response is conclusive | request `DRAFT → SENT` when its outgoing correspondence is registered with its external sent date, requirement `OPEN → IN_PROGRESS` when its child request is issued |
| Needs a reason? | Usually yes | Never |
| Who is the actor in the audit event? | The person (`actor_kind = USER`) | The person whose action triggered it, in `actor_user_id`, with `actor_kind = SYSTEM` and the triggering event's `correlation_id` — so the human decision that caused it stays visible, while nobody is recorded as having *decided* the consequence itself. Only a scheduled run with no initiating person is `actor_kind = JOB` with no user (Domain Model §2.18) |

This distinction is the answer to *"do not require the user to separately click RESPONSE RECEIVED"*.
A clerk registering an incoming letter makes exactly one judgement — what kind of communication it is
and what it decided (`response_type`, `response_outcome`, `is_conclusive`). Everything downstream
follows without another click.

---

## 1. Case lifecycle

Stored states (Domain Model §2.5): `REGISTERED`, `ACTIVE`, `ON_HOLD`, `CLOSED`, `CANCELLED`.

### 1.1 Transition table

| # | From | To | Trigger | Required data | Guard |
|---|---|---|---|---|---|
| T1 | — | `REGISTERED` | Case is created (§2) | requesting organization, subject/title, case number | — |
| T2 | `REGISTERED` | `ACTIVE` | **System consequence**: the first substantive record is created (a request, a requirement, an outgoing letter), or a user activates it explicitly | a current `RESPONSIBLE` assignment must exist | — |
| T3 | `REGISTERED` / `ACTIVE` | `ON_HOLD` | Work genuinely cannot proceed for a reason outside the branch structure | `reason_code` + note; `hold_until` optional | — |
| T4 | `ON_HOLD` | `ACTIVE` | Hold lifted | `reason_code` + note | — |
| T5 | `ACTIVE` | `CLOSED` | Work is finished | closure metadata (§9) | **Closure guards G1–G5 (§9.2)**, or a Head override of the eligible guards (§9.4) |
| T6 | `REGISTERED` / `ACTIVE` / `ON_HOLD` | `CANCELLED` | The dossier will not be pursued (withdrawn by the requester, duplicate, registered in error, merged) | `closure_type_id` + `closure_note`; `merged_into_case_id` when merged | every open request and requirement must first be explicitly `WITHDRAWN` or `VOID` (§1.4) |
| T7 | `CLOSED` | `ACTIVE` | Reopening (§10) | `reason_code` + note, mandatory | — |
| T8 | `CANCELLED` | `ACTIVE` | **Correction only** — the cancellation itself was a mistake **[transition added here]** | `reason_code = CANCELLATION_CORRECTED` + note | highest authority only (`PERMISSIONS.md` §23) |

**Not permitted:** `ON_HOLD → CLOSED` directly. A hold must be lifted first (T4, then T5). Closing
something the department is still formally holding misrepresents both states, and the extra step costs
one action. A case that is on hold and will never resume is **cancelled** (T6), not closed.

Every transition writes **both** a `case_state_change` row (business history) and an `audit_event`
(accountability) — Domain Model §2.6 and §2.18.

### 1.2 `ON_HOLD` is for external suspension, not for ordinary waiting

A case waiting for three authorities to reply is **`ACTIVE`**, not on hold. That wait is already
visible in derived progress (§11) and is the normal state of this system.

`ON_HOLD` means *the department has formally suspended the dossier*: the requester was asked for
something and has not answered, a superior instruction paused it, a legal precondition is absent.
Using it for ordinary waiting would destroy its reporting value and hide real stalls.

Whether a hold suspends any deadline clock is **unresolved** — see §12.4 and Domain Model OQ-5. Until
it is answered, a hold does **not** silently alter `due_at` on anything.

### 1.3 Reason fields

| Transition | Reason requirement |
|---|---|
| T3 hold, T4 unhold | `case_state_change.reason_code` + free note, both mandatory |
| T5 close | `closure_type_id` mandatory; `closure_note` mandatory when a guard was overridden (§9.4) |
| T6 cancel | `closure_type_id` + `closure_note`, both mandatory |
| T7 reopen | `reason_code` + note, both mandatory, no exceptions |
| T8 cancellation corrected | `reason_code` + note, both mandatory |
| T2 activate | none — it is a system consequence |

### 1.4 Cancellation does not sweep the branches

Cancelling a case does **not** silently terminate its open requests and requirements. Each must be
explicitly `WITHDRAWN` (we are recalling it) or `VOID` (it never validly applied) **before** T6, with
its own reason. This is deliberate: a bulk cascade would put a single reason on a dozen records that
each ended for a different reason, and would make "why did we stop asking the Utility Authority?"
unanswerable a year later.

The system may offer to walk the user through the open items in one screen. It may not decide them.

---

## 2. Case registration

### 2.1 The normal flow

A case begins when an official request that has **already been received** through the external
government system (`PROJECT.md` §1.1) is recorded here.

| Step | Record created | Notes |
|---|---|---|
| 1 | `correspondence` — `direction = IN`, `correspondence_kind = INITIATING` | the already-received letter: sender = requesting organization, recipient = the `is_own_organization` row, with the external system's letter number and the date it was actually received there |
| 2 | `case` — `lifecycle_state = REGISTERED` | `case_number`, `title`, `subject`, `registered_at` (business time) |
| 3 | `case.requesting_organization_id` | selected from master data, never typed as free text; if the organization is new it is created as master data first (`PERMISSIONS.md` §26) |
| 4 | subject/title entered | `title` for lists, `subject` for the substance |
| 5 | `assignment` — `scope = CASE`, `role = RESPONSIBLE`, `valid_until = NULL` | a case without a responsible person is an exception, not a default (§2.4) |
| 6 | `document` + `document_version` + `document_link` | linked to the **correspondence** (`PRIMARY_LETTER` for the letter, `ATTACHMENT`/`ANNEX` for the rest) — never to the case |
| 7 | `case` → `ACTIVE` (T2) | a system consequence of the first substantive record, not a button |

Steps 1 and 2 are normally one transaction, because `correspondence.case_id` is `NOT NULL` in
Domain Model v1.

### 2.2 When registration and case creation are not simultaneous

Three real situations, and how each is handled **without** changing the frozen model:

| Situation | Handling |
|---|---|
| The letter arrives, is scanned later | Create the case and the correspondence row together from the paper letter (its number, date, sender are on the page). The **files** are attached in step 6 whenever scanning happens — a `correspondence` with no documents yet is valid and shows as incomplete in derived progress. |
| The registry number is assigned by the external government system | `correspondence.registry_number` records **the number that system assigned** — this application never generates official correspondence numbers (`PROJECT.md` §1.1). It may be filled in later: it is not a primary key and nothing references it, so recording it late costs nothing. **A case does not wait for a registry number.** |
| A letter arrives that belongs to no case yet and nobody is sure which case it belongs to | **Not supported in V1** — `correspondence.case_id` is `NOT NULL`. The letter is registered against the case it concerns, and if it opens a new matter, that case is created. A true "unassigned mail tray" is Domain Model **OQ-10**, and answering it yes is a one-column change. |

`case_number` **is** assigned when the case is created, because staff quote it from that moment on. It
is the one number this application owns: internal case numbering is application-generated and entirely
separate from official correspondence numbering, which is external. Its format and reset rules are
Domain Model OQ-7 and do not affect this workflow.

**This partly answers Domain Model OQ-7 ("who assigns the numbers?")** without changing the frozen
model, which deliberately left it open: **official correspondence numbers are assigned externally and
recorded here; internal case numbers are assigned by this application.** What remains open in OQ-7 —
format, yearly reset, uniqueness scope — is unaffected.

### 2.3 Registration is not a single-branch commitment

Nothing in registration forces the department to decide how many authorities will be involved.
Requests are added over the life of the case, in parallel, at any time while it is `ACTIVE` (§7).

### 2.4 A case with no responsible person

Permitted transiently (a case registered at 17:55 and assigned next morning), and it is exactly what the
"unassigned cases" dashboard exists to catch (`PROJECT.md` §19). A case may not reach `CLOSED` without
ever having had a `RESPONSIBLE` assignment.

---

## 3. Request workflow

A `request` is a **business/workflow tracking entity**: *"we officially requested X from Organization
Y."* It is not a message awaiting transmission, and it is not the letter (Domain Model decision C-1).
One outgoing letter may carry several requests.

Stored states (Domain Model §2.9, frozen): `DRAFT`, `SENT`, `ANSWERED`, `CLOSED`, `WITHDRAWN`, `VOID`.

### 3.1 What the states mean, now that dispatch is external

The state names are frozen in Domain Model v1. Under `PROJECT.md` §1.1 their **meaning** is stated
precisely as follows, and the label `SENT` is historical — it records that the request **was officially
issued externally**, never that this application sent anything.

| State | Means | Established by |
|---|---|---|
| `DRAFT` | **Planned, not yet officially issued.** The department intends to request this; no outgoing correspondence has been registered against it yet | a person creating the request during planning |
| `SENT` | **Officially issued.** An outgoing correspondence carrying this request has been registered, with the date it was actually sent in the external system | **registering that correspondence** — nothing else |
| `ANSWERED` | a conclusive response has been registered | registering the incoming letter |
| `CLOSED` / `WITHDRAWN` / `VOID` | terminal (§3.2) | a person |

**`DRAFT` is retained deliberately, and its purpose has changed.** It is no longer "written but not yet
transmitted" — this application never transmits. It is **internal planning**: a request the department
has decided to make but has not yet issued in the external system. That remains useful for workflow
tracking ("three authorities to approach, one still to do"), and it is the only thing `DRAFT` now means.
A department that always issues the letter first and records it afterwards will simply never see a
`DRAFT` request, and nothing breaks.

**Three states that do not exist, and why:**

- **`READY` / "ready to send" — removed.** There is nothing to be ready for. This application performs
  no dispatch, so there is no queue, no pending-send and no gate between preparation and transmission.
- **No "Mark as sent" action.** Registering the outgoing correspondence with its external sent date
  **already proves** the request was issued. Requiring a second click to assert a fact the record
  already contains would be exactly the duplicate confirmation §0.1 forbids — and it would allow the
  two to disagree.
- **`RESPONSE_RECEIVED` — removed**, for the same reason: registering the incoming letter *is* the
  record, and `ANSWERED` follows mechanically (§3.3).
- **`OVERDUE` is never a state** (§12).

**There is no dispatch approval in this application.** Whether an outgoing letter needs anyone's
approval is a question for the external government system, which performs the sending. Closed as
**OQ-W1** (§14).

### 3.2 Transition table

| # | From | To | Trigger | Kind | Required |
|---|---|---|---|---|---|
| R1 | — | `DRAFT` | Request created during planning, top-level or from a requirement | human | `case_id`, `target_organization_id`, subject; `source_requirement_id` if it is a child |
| R1b | — | `SENT` | Request created **directly from registering an already-issued outgoing correspondence** — the normal path when the letter went out before anyone recorded it **[creation path added here]** | human (one act) | as R1, plus the registered correspondence |
| R2 | `DRAFT` | `SENT` | **System consequence** of registering an outgoing `correspondence` that carries this request and has its external `sent_at` | system | correspondence registered, recipient = target organization |
| R3 | `SENT` | `ANSWERED` | **System consequence** of registering an `ACTIVE` response with `is_conclusive = true` — or of a conclusive response returning to `ACTIVE` when the supersession edge over it is retracted (§4.7) | system | — |
| R4 | `ANSWERED` | `CLOSED` | Human closes the work item | human | `closed_by_user_id`, `closed_at`; **frozen closure rule** (§3.4) |
| R5 | `DRAFT` / `SENT` / `ANSWERED` | `WITHDRAWN` | The department recalls the request | human | `withdrawal_reason`, actor, time. If it was already issued, a withdrawal letter is sent **in the external system** and then registered here as a `WITHDRAWAL` correspondence |
| R6 | `DRAFT` / `SENT` / `ANSWERED` | `VOID` | The request was recorded in error and never validly existed | human | `void_reason`, actor, time |
| R7 | `ANSWERED` | `SENT` | **System consequence**: the conclusive response is superseded by a non-conclusive one, or is voided | system | — |
| R8 | `CLOSED` | `SENT` | **System consequence**: the response(s) that discharged the request are superseded by non-conclusive ones or voided, so no `ACTIVE` conclusive response remains **[transition added here; trigger narrowed post-review]** | system | §4.6 |
| R9 | `CLOSED` | `ANSWERED` | **System consequence**: work reappears under a closed request while an `ACTIVE` conclusive response stays in force — a **blocking** requirement is raised against one of its responses, a late or restored response creates an unresolved conflict (§4.5, §4.7), or a blocking requirement raised by one of its responses returns to an open state through a resolution correction (Q7, §5.2) **[transition added post-review]** | system | §4.6, §5.2 |

**No auto-close.** R4 is always a human act — the frozen closure rule requires `closed_by_user_id`.
The system surfaces "ready to close"; it never closes by itself.

**R1b is the common path.** In a department that works letter-first, a request is normally created at
the same moment its already-sent correspondence is registered — one act by one person, producing a
request that is `SENT` from birth. R1 followed by R2 is the planning path, used when the department
tracks an intention before acting on it. Both are ordinary; neither involves this application sending
anything.

R1b adds **no state and no column** — `SENT` and `DRAFT` are both frozen values and the proof of
issuance is the same registered correspondence in either path. It is marked because the frozen lifecycle
sketch (Domain Model §5.2) draws `DRAFT` as the only entry point. Requiring every request to pass
through `DRAFT` would mean writing a state that was never true — the letter had already gone out — and
then immediately transitioning away from it. Recording a fiction to satisfy a diagram is exactly what
this clarification exists to prevent.

### 3.3 Issuance and answering are both consequences, not claims

**Issuance.** The single human act is *registering the outgoing correspondence*, with the date it was
actually sent in the external system. `SENT` follows from that record alone. There is no second
confirmation, because the registered letter is already the proof — and a separate flag could come to
disagree with it.

**Answering.** The only judgement the clerk makes is `is_conclusive` on the response they are registering anyway:
*does this letter discharge what we asked?* An acknowledgement, a partial answer, a request for
clarification and a deadline extension are all `is_conclusive = false` and leave the request `SENT`
— visibly "partially answered" in derived progress, without a state for it.

### 3.4 Closure rule (frozen, Domain Model §2.9)

A request may reach `CLOSED` only when **all** hold:

1. at least one `ACTIVE` response with `is_conclusive = true`, and no unresolved response conflict
   (§4.5); **and**
2. every **blocking** `requirement` (`is_blocking = true`) whose `source_response_id` belongs to this
   request is terminal (`FULFILLED`, `WAIVED`, `VOID`, `FAILED`); **and**
3. a person with the authority records `closed_by_user_id` and `closed_at`.

Condition 2 is what stops "the authority gave its final opinion" from closing a branch whose blocking
conditions are still outstanding. A non-blocking requirement does not hold its request open — the same
meaning `is_blocking` has for the branch, for readiness and for case closure (§9.2; Domain Model
amendment A-3).

### 3.5 Top-level and child requests are the same entity

| | Top-level | Child |
|---|---|---|
| `source_requirement_id` | `NULL` | the requirement it satisfies |
| Created because | the case needs this authority's input | a response imposed a condition |
| Lifecycle | identical | identical |
| Withdrawal, closure, responses | identical | identical |

There is no separate child-request workflow. The only difference is *why it exists*, and that is a
column, not a process (§6).

### 3.6 Several requests in one letter

When one already-sent letter carrying three requests is registered: **one** `correspondence`, **three**
`request` rows, all with the same `dispatch_correspondence_id`, all becoming `SENT` from that one
registration (R1b or R2). From then on they
diverge — each has its own `due_at`, its own responses, its own requirements and its own closure.
The authority may answer two of them in one letter and the third a month later; that is three
`response` rows across two letters, and two of the requests close while the third stays `SENT`.

---

## 4. Response workflow

A `response` is an **immutable official act**: one answer, against one request, carried by one incoming
letter. Its business facts are never edited (Domain Model §2.10). There is **no `ResponseVersion`**.

### 4.1 Registration

A response exists in this application **only after** the corresponding official communication has
already been received through the external government system and is registered here manually
(`PROJECT.md` §1.1). Nothing arrives in this application by itself.

| Step | What happens |
|---|---|
| 1 | The already-received letter is registered as `correspondence` (`direction = IN`), with the external system's letter number, the letter date and the date it was actually received there. If it replies to one of our letters, `parent_correspondence_id` points at it. |
| 2 | Its files are attached as `document` + `document_version` + `document_link` **to the correspondence**. |
| 3 | For **each request the letter answers**, one `response` row is created: `request_id`, `correspondence_id`, `response_type`, `response_outcome`, `is_conclusive`, `summary`, `received_at`. |
| 4 | If the letter imposes conditions, one `requirement` per condition is created from that response (§5.1). |
| 5 | System consequences fire: R3/R7/R8 on the request, requirement transitions, derived progress. |

**One letter, several answers.** A reply that answers three of our requests produces **one**
`correspondence` and **three** `response` rows — possibly for requests in different cases. The letter is
registered once; its attached map is stored once and linked, never copied. This is the mechanism that
makes a case↔correspondence M:N junction unnecessary (Domain Model §2.8).

**A response in another case discloses nothing of the letter.** The letter, its files and its other
responses stay governed by the case the letter is filed in. The response in the second case shows that
case its own facts only — type, outcome, conclusiveness, dates, summary, the requirements it raised. If
the second case needs the actual file, it is shared by an explicit, version-pinned link into that case —
a disclosure decision (`DOCUMENT_MODEL.md` §4.8, `PERMISSIONS.md` §16).

### 4.2 The two axes in practice (frozen decision C-2)

`response_type` = *what kind of communication arrived*. `response_outcome` = *what it decided*.

| The letter says | `response_type` | `response_outcome` | `is_conclusive` | Raises requirements? |
|---|---|---|---|---|
| "We have received your request" | `ACKNOWLEDGEMENT` | `NOT_APPLICABLE` | false | no |
| "Approved, no conditions" | `OPINION` | `APPROVED` | **true** | no |
| "Refused, because…" | `OPINION` | `REJECTED` | **true** | no |
| "Approved subject to a utility map" | `OPINION` | `CONDITIONAL` | false | **yes** |
| "Before we can opine, provide X" | `ADDITIONAL_REQUIREMENT` | `CONDITIONAL` | false | **yes** |
| "What exactly do you mean by…?" | `INFORMATION_REQUEST` | `NOT_APPLICABLE` | false | sometimes |
| "For your information, here is the map" | `INFORMATION` | `NOT_APPLICABLE` | often **true** | no |
| "Here is the annex we omitted" | `ADDITIONAL_DOCUMENT` | `NOT_APPLICABLE` | false | no |
| "We need until 30 April" | `DEADLINE_EXTENSION` | `NOT_APPLICABLE` | false | no |
| Registered, not yet read properly | best guess, else `OTHER` | **`UNDETERMINED`** | false | not yet |

Note the fourth and seventh rows. A **conditional approval is not conclusive** — the authority has not
finished with us until its conditions are met, and marking it conclusive would let the branch close over
an open obligation. An **informational letter carrying the thing we asked for** often *is* conclusive:
the map was the answer, and no verdict was ever due (`NOT_APPLICABLE`). Conclusiveness and outcome are
independent, which is exactly why they are separate columns.

`UNDETERMINED` is an explicit backlog signal: *a verdict was expected but is not yet established*. It
should appear on a "responses awaiting classification" list. `NOT_APPLICABLE` must never appear there.

### 4.3 Supersession — revised official letters

An authority sends a corrected or revised letter. This is **not** a version of the old letter; it is a
new official act.

1. Register the new `correspondence` and its files.
2. Create a new `response` on the **same request**, classified for what the new letter is.
3. Record a `response_supersession` edge from the new response to **each** earlier response it replaces
   — one, or several when the letter replaces more than one (Domain Model §2.21).
4. Each earlier response becomes `SUPERSEDED` in the same transaction — **not** deleted, **not** edited,
   still queryable, and **still the source of any requirements it raised** (§6.4).

There is no `REVISION` response type. Supersession is the relationship; a label that could disagree with
it would be a defect waiting to happen.

**Constraints (frozen, amended A-1, A-10):** a superseding response must belong to the same request; a
response cannot supersede itself; a response has at most one `ACTIVE` incoming edge; both responses are
`ACTIVE` when the edge is recorded, which makes cycles impossible. A supersession between two responses
that already exist (§4.5) is recorded the same way — an edge, never an edit of either response. An edge
recorded in error is **retracted**, never deleted (§4.7).

### 4.4 Responses that create requirements

Conditions in a letter become `requirement` rows with `requirement_origin_type = RESPONSE` and
`source_response_id` = that response. **One response may raise several requirements** — three separate
conditions in one letter are three requirements, because each is separately satisfiable, separately
waivable and may be owed to a different organization.

They are created **by the person registering the response**, at registration time, as part of reading
the letter. A condition noticed a week later is still created against that same response — the causal
link is to the letter that imposed it, not to the day someone noticed.

### 4.5 Conflicting responses

Two `ACTIVE` responses on one request, both `is_conclusive`, disagreeing (one `APPROVED`, one
`REJECTED`), and neither marked as superseding the other.

The system must **not** guess — not by date, not by "latest wins". Dates on official letters are
unreliable and the later letter is sometimes the mistaken one.

| Step | Behaviour |
|---|---|
| Detection | Derived condition: >1 `ACTIVE` conclusive response on one request with different outcomes |
| Display | The request shows **"conflicting responses — resolution required"** and ranks high in derived progress (§11, P4) |
| Blocking | The request may **not** be `CLOSED` while unresolved |
| Resolution | A person with authority records which response supersedes which — a `response_supersession` edge between the two existing responses (§4.3) — or voids one as a registration error, or writes to the authority for clarification — which produces a third response that supersedes **both**: two edges from the same new response (Domain Model §2.21) |
| Never | No automatic resolution, no silent preference |

### 4.6 A response arriving after the request looked finished

Late letters are normal in interagency work. Registering one is **always** permitted, whatever the
request's state — refusing would force staff to falsify the record or leave an official letter unfiled.

**Informational only vs creates new work.** A late response **creates new work** when it — at
registration or afterwards — (a) raises a requirement, (b) supersedes a response in force, or (c) creates
an unresolved conflict (§4.5). Anything else is **informational only** — an acknowledgement, a copy, a
covering letter, a deadline notice, information that asks nothing of the department — **whatever its
`is_conclusive` value**. Being non-conclusive is never, by itself, a reason to change a request's state or
to reopen a case.

| The request was | The late response | Result |
|---|---|---|
| `ANSWERED` | is informational only | stays `ANSWERED`; the response is recorded |
| `ANSWERED` | raises a requirement | stays `ANSWERED`; not closeable while that requirement is blocking and open |
| `ANSWERED` | is conclusive and supersedes the earlier conclusive one | stays `ANSWERED` with the new response in force |
| `ANSWERED` | supersedes the conclusive response(s) with **non-conclusive** ones | **R7**: back to `SENT` — the branch is genuinely unfinished again |
| `CLOSED` | is informational only | stays `CLOSED`; the response is recorded against the closed request |
| `CLOSED` | supersedes the discharging response(s), leaving no `ACTIVE` conclusive response | **R8**: back to `SENT` — the answer was withdrawn |
| `CLOSED` | raises a **blocking** requirement, or creates a conflict, while a conclusive response stays in force | **R9**: back to `ANSWERED` — still answered, no longer closeable |
| `CLOSED` | raises a non-blocking requirement, or conclusively supersedes a conclusive response | stays `CLOSED` — nothing it holds is open (§3.4); the case-level rule below still applies |
| `WITHDRAWN` / `VOID` | anything | the response is recorded; the request's state does not change. An authority answering a request we withdrew is a fact worth keeping, not a reason to revive it |

**If the case itself is `CLOSED`:**

- an **informational-only** response is recorded and the case **stays `CLOSED`**. The closure asserted
  that the department's work is finished, and a letter that asks nothing of anyone leaves that true;
- a response that **creates new work** — or recording, on that case, a requirement or a supersession
  against an earlier response — requires the case to be **reopened** (§10) first. The system refuses it on
  a closed case and offers the reopening. Filing new work into a closed dossier without reopening it would
  make the closure record false.

### 4.7 Voiding a response

Only for registration errors — wrong request selected, duplicate entry, letter recorded against the
wrong case. `status = VOID` + `void_reason` + actor + time. The letter itself (`correspondence`) usually
remains: it did arrive.

**Voiding a response does not cascade to its requirements.** The system lists the affected requirements
and a person decides each one: `VOID` if the demand disappeared with the mistake, unchanged if the
condition is real and simply attached to the wrong row (in which case a corrected response is registered
and the requirements are recreated against it).

**Voiding a response that superseded others does restore them** *(pre-schema amendment A-10, closes Domain
Model OQ-16)*. Its supersession edges were part of the same mistake, so in the same transaction they are
**retracted** (status `RETRACTED`, attributed to the person voiding, `actor_kind = SYSTEM`) and each response
they superseded returns `SUPERSEDED → ACTIVE`. This is a mechanical consequence, not a judgement: a
response cannot remain superseded by an entry that never validly existed. What follows is ordinary
evaluation — R3, R7 or a conflict (§4.5) as the restored response dictates — and the restored responses are
listed for review, because a person may still need to record the *correct* supersession.

Example: A (`OPINION`/`APPROVED`, conclusive) is in force on request R. A clerk registers letter C as a
response on R superseding A — but C actually answers a different request. C is voided (`DATA_ENTRY_ERROR`)
→ the C→A edge is retracted → A is `ACTIVE` again and R is answered by A; C's letter is registered on the
correct request. When the authority's genuine revision D of A later arrives, D supersedes A normally —
possible only because the retracted edge no longer counts towards the one `ACTIVE` incoming edge.

**An edge recorded in error between two valid responses** — the wrong direction chosen when resolving a
conflict — is retracted explicitly by a Chief, with a mandatory note; the response it superseded is
restored the same way, and the correct edge is then recorded. A supersession that was *true* is never
retracted to express a later change of position: that is a new response superseding the newer one. Because
retraction changes which answer is in force, on a `CLOSED` case it is preceded by reopening (§9.1).

---

## 5. Requirement workflow

Stored states (frozen): `OPEN`, `IN_PROGRESS`, `FULFILLED`, `WAIVED`, `VOID`, `FAILED`.
Semantics are fixed by Domain Model §2.11 and are not restated differently here.

### 5.1 Creation

| Origin | `requirement_origin_type` | `source_response_id` | Typical creator |
|---|---|---|---|
| A condition imposed by an authority's letter | `RESPONSE` | **mandatory** | the person registering that response |
| A condition stated in the original incoming request | `INCOMING_REQUEST` | `NULL` | the person registering the case |
| The department itself identifies a precondition | `INTERNAL` | `NULL` | any assigned worker |
| A standing regulatory precondition | `REGULATION` | `NULL` | any assigned worker |

Also recorded at creation: `title`, `description`, `raised_by_organization_id` (who demands it),
`addressed_to_organization_id` (who is expected to supply it, when known), `due_at` if stated, and
**`is_blocking`** — *does this prevent the final result?* `is_blocking` defaults to true and is the
single flag that decides whether an open requirement stops the case (§9.2 G1).

### 5.2 Transition table

| # | From | To | Trigger | Kind | Evidence | Reason |
|---|---|---|---|---|---|---|
| Q1 | — | `OPEN` | Created | human | — | — |
| Q2 | `OPEN` | `IN_PROGRESS` | **System consequence**: a child request reaches `SENT`; **or** a person starts work explicitly | system / human | — | — |
| Q3 | `OPEN` / `IN_PROGRESS` | `FULFILLED` | The obligation was satisfied | human | **≥1 `ACTIVE` `requirement_evidence`**, or an explicit `resolution_note` | — |
| Q4 | `OPEN` / `IN_PROGRESS` | `WAIVED` | An authorised person releases the case from it | human | none — the waiver *is* the justification | `waiver_authorised_by_user_id` + `waiver_reason_id` + note, all mandatory |
| Q5 | `OPEN` / `IN_PROGRESS` | `VOID` | It no longer applies, or never did | human | none | `void_reason_id` mandatory; `void_source_response_id` when a later letter caused it |
| Q6 | `OPEN` / `IN_PROGRESS` | `FAILED` | It applied, was not released, and could not be satisfied | human | none | `failure_reason_note` mandatory |
| Q7 | `FULFILLED` / `WAIVED` / `VOID` / `FAILED` | `IN_PROGRESS` if one of its child requests is `SENT`, otherwise `OPEN` | **Resolution corrected** — the terminal state was **recorded in error**: wrong evidence, wrong requirement, clerical error **[transition added post-review — Domain Model amendment A-2]** | human (Chief) | wrong evidence rows retracted individually — never cascaded | `reason_code` + note, both mandatory; a `requirement_resolution_correction` row preserves the withdrawn resolution verbatim (Domain Model §2.22) |

**Terminal states are final as business outcomes.** A requirement that comes back because something
*changed* — the authority repeats a demand it had dropped, a released obligation is re-imposed — is a
**new** requirement, and the history of the first one stays intact. There is **no terminal-to-terminal
transition**, and no `FULFILLED → OPEN` for a change of circumstances.

**Q7 is the one exit, and it is for errors only** — the terminal state was false when it was recorded.
It does not decide the correct outcome; it removes the false one. The correct terminal state, if any, is
then reached by the ordinary transition (Q3–Q6) with that transition's own evidence, reason and authority
— a mistaken `FULFILLED` that should have been `WAIVED` still needs a Chief's waiver. Q7:

- **never erases** — the original terminal event stays in `audit_event`; the requirement's resolution
  columns are copied verbatim into the correction record before they are cleared; evidence rows are
  retracted, not deleted, each with its own reason;
- **never fabricates** — no replacement requirement, no official act, no change to `source_response_id`,
  child requests or the causal chain (§6.3);
- **requires a case open to work** — on a `CLOSED` case it is preceded by reopening (T7); on a
  `CANCELLED` case it is unavailable unless the cancellation itself is corrected (T8);
- is a **Chief** action (`PERMISSIONS.md` §16, §23).

After Q7 the requirement takes part in everything exactly as any open requirement does:

| Concern | Effect |
|---|---|
| Blockers | if `is_blocking`, it blocks again — G1 (§9.2), readiness (§7.4), branch state (§7.2) and the progress ladder (§11) all see it as open |
| Parent request | if it is blocking and its request is `CLOSED`, **R9** returns the request to `ANSWERED` (§3.2) |
| Progress and overdue | counted as open; overdue again if its `due_at` has passed (§12.2) |
| Final result | an `ISSUED` result that relied on the corrected state is **not** changed automatically. It is surfaced for a decision: a replacement (§8.6), a revocation (F4), or leaving it in force |
| Reporting | current-state statistics count the requirement as open; the withdrawn resolution appears only as a correction record — never as a fulfilment, waiver, void or failure |

`IN_PROGRESS` is a convenience signal, not a decision. Skipping it (`OPEN → FULFILLED` directly, when
the requester simply hands in the document) is normal and permitted.

### 5.3 What evidence means

`requirement_evidence` rows (Domain Model §2.12) point at **a response**, **a document** (optionally
pinned to an exact `document_version`), or **an internal record**. Evidence is M:N in both directions:

- one requirement may need several pieces;
- **one document may satisfy two requirements** — when two authorities each demanded the same map, that
  is two requirements sharing one piece of evidence, each closed separately.

Evidence is **retracted, never deleted**. A requirement wrongly marked fulfilled must still show that it
once was, and why that was withdrawn — its wrong evidence is retracted row by row, and the requirement
itself returns to an open state through Q7 (§5.2).

### 5.4 `WAIVED` vs `VOID` vs `FAILED` in operation

The distinction is *does the obligation still apply* (Domain Model §2.11). In workflow terms:

| The situation | State | Why |
|---|---|---|
| The map arrived | `FULFILLED` | satisfied |
| The map is genuinely needed, but the Chief authorises proceeding without it | `WAIVED` | still owed; we were released |
| The Architecture Authority's later opinion says the map is unnecessary | `VOID` + `void_source_response_id` | no longer applies |
| The source response was superseded and the new letter drops the condition | `VOID`, reason `SUPERSEDED_BY_RESPONSE` | no longer applies |
| The whole branch was withdrawn | `VOID`, reason `BRANCH_REMOVED` | no longer applies |
| Created by mistake | `VOID`, reason `DATA_ENTRY_ERROR` | never applied |
| The Utility Authority formally refuses, and no other authority holds the map | `FAILED` | applied, not released, unobtainable |

A requirement that became irrelevant must **never** be marked `FULFILLED` to clear the board
(`PROJECT.md` §5.6), and `FAILED` must never be softened into `WAIVED` — nobody released us from a
`FAILED` requirement, which is exactly why it has to be visible when the final result is decided (§8.5).

### 5.5 One requirement, several child requests

Permitted and normal. The Utility Authority cannot supply the map, so a second request goes to the
Infrastructure Authority for the same requirement. Both requests carry `source_requirement_id = R1`;
the requirement stays `IN_PROGRESS` until one of them produces evidence.

The reverse is not permitted: a request has **one** `source_requirement_id`. If one letter is used to
chase conditions from two different requirements, that is two `request` rows sharing one dispatch
`correspondence` (§3.6) — each keeping its own reason for existing.

### 5.6 Effect on the parent branch

When a requirement reaches any terminal state, the system re-evaluates the request whose response
raised it:

- if **every blocking** requirement from that request's responses is terminal **and** an `ACTIVE`
  conclusive response exists → the request becomes **closeable** (surfaced, not auto-closed);
- if the requirement was blocking and is now terminal → the case's derived progress moves on to the next
  blocker (§11);
- nothing else changes automatically. In particular, fulfilling a requirement does **not** send the
  follow-up letter to the authority that imposed it — that is an official act a person performs (§6.2).

---

## 6. Child requests and dependency behaviour

### 6.1 The chain, as rows

```
Request A                    request.source_requirement_id = NULL        (top-level)
  └─ Response A1             response.request_id = A                     CONDITIONAL, not conclusive
       └─ Requirement R1     requirement.source_response_id = A1         OPEN, is_blocking = true
            └─ Request B     request.source_requirement_id = R1          (child)   → R1 becomes IN_PROGRESS
                 └─ Response B1   response.request_id = B                          conclusive
                      └─ requirement_evidence(R1) → B1 (+ the map document, version-pinned)
                                                                          → R1 FULFILLED
       └─ Response A2        response.request_id = A                     OPINION / APPROVED, conclusive
                                                                          → A ANSWERED → closeable
```

Three foreign keys carry the whole chain. No workflow table, no graph blob, no step engine.

### 6.2 The department still has to write the letter

Between `R1 FULFILLED` and `Response A2` there is a **human act performed outside this application**:
someone sends the map to the Architecture Authority **through the external government system**. It is
then registered here as an outgoing `correspondence` (`parent_correspondence_id` = our original letter
to them) carrying a `document_link` to **the same document** — the file is not copied.

The system surfaces this as an actionable internal step ("R1 fulfilled — Architecture Authority is
waiting for it"), and later records that it happened. **It never sends anything, and it has no way to.**
Nothing in this system transmits official correspondence (`PROJECT.md` §1.1).

### 6.3 How a user sees *why* Request B exists

Request B's screen shows its origin chain by walking `source_requirement_id` →
`source_response_id` → `request_id`, upward until a request with `source_requirement_id IS NULL`
(Domain Model §6.3):

> **Request B** exists to satisfy **R1 — "utility communication map"**,
> which was imposed by the **Architecture Authority's letter №7/119 of 20.03.2026** (Response A1),
> which answers **Request A**, the case's top-level request to the Architecture Authority.

This is a query over indexed foreign keys, available on every child request, at any depth, forever —
including after R1 is voided and after A1 is superseded. **The chain is never rewritten**
(`source_requirement_id` is write-once, Domain Model §6.4).

### 6.4 If Response A1 is later superseded and R1 becomes VOID

The four things that must **not** happen: R1 is not deleted; Request B is not deleted; B's
`source_requirement_id` is not cleared; A1 is not edited.

| Record | What happens |
|---|---|
| Response A1 | `SUPERSEDED`. Still readable, still the recorded source of R1. |
| Requirement R1 | **Not automatic.** The system lists R1 for review. A person decides: `VOID` (reason `SUPERSEDED_BY_RESPONSE`, `void_source_response_id` = the new response) if the new letter drops the condition; **unchanged** if the new letter repeats it. |
| Request B | **Not automatic** — see §6.5. |
| The chain | Intact. B still shows "exists to satisfy R1", and R1 shows "voided because letter №… of … removed the need". A year later both questions are still answerable. |

### 6.5 What happens to Request B when R1 becomes VOID

A person decides, because Request B is an **official letter already outside the building**:

| Request B is | Normal decision | Why |
|---|---|---|
| `DRAFT`, never sent | `VOID` (reason `DATA_ENTRY_ERROR`) or simply left as a draft | nothing left the building |
| `SENT`, unanswered | **`WITHDRAWN`**, normally with a withdrawal letter to the authority | courtesy and accuracy: the authority is working on something we no longer need |
| `SENT`, unanswered, but the answer is still useful | **left running** | a map we no longer strictly need may still belong in the dossier |
| `ANSWERED` or `CLOSED` | **nothing** | the work happened; erasing it would falsify the record |

In every case Request B keeps pointing at the now-`VOID` R1. That is not a dangling reference — it is
the answer to "why did we ever write to the Utility Authority?", and it must survive.

### 6.6 What happens if Request B fails

The Utility Authority refuses, or answers that it does not hold the map: a conclusive `response` with
`response_outcome = REJECTED`. Request B becomes `ANSWERED` and is closeable — **B succeeded as a
request**; it is R1 that is now in trouble.

R1 stays `IN_PROGRESS` and becomes a visible blocker. A person chooses:

| Option | Effect |
|---|---|
| Ask a different authority | new child request on R1 (§5.5); R1 stays `IN_PROGRESS` |
| The requester supplies it directly | evidence is a document; R1 → `FULFILLED` |
| Authorised release | R1 → `WAIVED` — the case proceeds without it, on the record |
| Nobody can supply it | R1 → `FAILED` — the case proceeds *with a recorded unmet obligation*, which normally shapes a negative or partial final result (§8.5) |

There is no automatic path. A failed dependency is a decision point, and the system's job is to make it
impossible to miss, not to resolve it.

---

## 7. Parallel branches

### 7.1 What a branch is

A **branch** is a top-level request (`source_requirement_id IS NULL`) and everything descending from it:
its responses, the requirements they raised, the child requests satisfying those, recursively. A case
with three authorities has three branches.

Branches are **independent by construction** — there is no relationship between two top-level requests,
so nothing in the model can make one wait for another. The only place they meet is the final result
(§8) and case closure (§9).

### 7.2 Derived branch state

Not stored. Computed from the branch's rows, evaluated in this order (first match wins):

| # | Branch state | Condition |
|---|---|---|
| B1 | **FAILED** | any `FAILED` requirement in the branch with `is_blocking = true`. (Requirement states are terminal as business outcomes, so a `FAILED` requirement is never later fulfilled — if the department tries again, that is a **new** requirement, and the branch is then judged on that new one as well as the failed one. Only a `FAILED` recorded in error leaves this state, through Q7, §5.2) |
| B2 | **BLOCKED** | a **blocking** requirement is `OPEN`/`IN_PROGRESS` with **no** child request in `SENT` and no `ACTIVE` evidence recorded — i.e. *we* owe the next move |
| B3 | **WAITING_EXTERNAL** | any request in the branch is `SENT` without a conclusive response, or a **blocking** requirement is `IN_PROGRESS` with a child request `SENT` |
| B4 | **WAITING_INTERNAL** | any request in the branch is `DRAFT`, or a response is `UNDETERMINED`, or a fulfilled requirement still owes a follow-up letter (§6.2) |
| B5 | **WITHDRAWN** | the top-level request is `WITHDRAWN` or `VOID` |
| B6 | **COMPLETE** | the top-level request is `CLOSED` and every **blocking** requirement in the branch is terminal (open non-blocking requirements stay listed — §9.2) |

B2 and B4 are the branches where *we* are the bottleneck; B3 is where an authority is. That distinction
drives derived progress precedence (§11) and the "waiting for me" dashboard.

### 7.3 Branches never block each other

A `BLOCKED` Architecture branch does not stop anyone registering the Emergency Authority's approval,
sending the Property Authority's request, or uploading documents anywhere in the case. The only
case-wide effects are:

- the case's **headline** progress message shows the most actionable branch (§11);
- **readiness for the final result** requires every branch to be terminal (§7.4).

### 7.4 Readiness for the final result

A case is **ready for final result** when **all** hold:

1. every branch is `COMPLETE`, `FAILED` or `WITHDRAWN` (no branch in B2/B3/B4);
2. no requirement anywhere in the case is `is_blocking = true` and `OPEN`/`IN_PROGRESS`;
3. no request is in `DRAFT`, `SENT` or `ANSWERED` (all terminal: `CLOSED`, `WITHDRAWN`, `VOID`);
4. no unresolved response conflict (§4.5);
5. no `final_result` already `ISSUED` — **except** when the result being issued is a replacement that
   names the currently `ISSUED` result in `supersedes_final_result_id` (§8.6). An issued result never
   blocks its own replacement.

A `FAILED` branch does **not** prevent readiness. It shapes the decision — the department can and must
be able to issue a refusal or a partial approval precisely because something could not be obtained.

---

## 8. Final result

Stored states (Domain Model §2.16): `DRAFT`, `ISSUED`, `SUPERSEDED`, `REVOKED`, `VOID`.

### 8.1 When it may be created

A `DRAFT` may be created at any time while the case is `ACTIVE` — people draft decisions while waiting.
A draft has no effect on anything: it does not block branches and does not signal completion.

`ISSUED` is the meaningful act (§8.3).

### 8.2 Transition table

| # | From | To | Trigger | Required |
|---|---|---|---|---|
| F1 | — | `DRAFT` | Someone starts drafting | `case_id`, `decision_type_id`, summary |
| F2 | `DRAFT` | `ISSUED` | The decision is made and signed | guards D1–D4 (§8.3); `decided_by_user_id`, `decided_at`, `approved_by_user_id`, `approved_at`, `issued_at`. For a replacement, performed atomically with F3 (§8.6) |
| F3 | `ISSUED` | `SUPERSEDED` | **System consequence** of issuing a replacement (F2) that names this result in `supersedes_final_result_id` — **in the same transaction, before the replacement becomes `ISSUED`** | — |
| F4 | `ISSUED` | `REVOKED` | The decision is withdrawn without a replacement | `revoked_by_user_id`, `revoked_at`, `revocation_reason_note` |
| F5 | `DRAFT` / `ISSUED` | `VOID` | Recorded in error | `void_reason`, actor, time |

### 8.3 Guards for `ISSUED`

| # | Guard | Override |
|---|---|---|
| D1 | The case is ready for final result (§7.4) | yes — highest authority, mandatory reason (§9.4) |
| D2 | `decided_by_user_id` and `decided_at` are recorded | **no** |
| D3 | `approved_by_user_id` and `approved_at` are recorded — **one approval, by the Head** (§8.4, ADR-040) | **no** |
| D4 | At least one `ACTIVE` `document_link` with role `FINAL_RESULT_DOCUMENT` — the signed decision | yes — mandatory reason; the document must follow |

D4 exists because `PROJECT.md` §4 and §32.12 require the system to answer *"which documents prove the
final result"*. An issued decision with nothing proving it cannot answer that. The override exists
because a decision may legitimately be signed before it is scanned.

### 8.4 Approval: one approval, by the Head

**Decided by the product owner on 2026-09-18** (DECISIONS.md **ADR-040**, closing OQ-W2 / Domain Model OQ-14 /
`PERMISSIONS.md` OQ-P1). Of the two models this section previously carried, **Model A is adopted, with the
Head as the approver**:

| | **Adopted** |
|---|---|
| How many approvals | **exactly one** |
| Who approves | **the Head**. A Chief may draft and decide a final result but may not approve it |
| Relationship to `decided_by_user_id` | unconstrained: the Head may also be the decision-maker, and no four-eyes `CHECK` is imposed |
| Workflow | F2 is **one step**: the Head issues the result, recording `decided_by`/`decided_at` and `approved_by`/`approved_at` |
| Schema | **unchanged** — both columns already exist (`DOMAIN_MODEL.md` §2.16) |

A `DRAFT` result whose `decided_by_user_id` is set while `approved_by_user_id` is still empty remains a
**derived** "awaiting approval" (ladder row P15), not a stored state.

**Rejected:** four eyes — for thirteen staff it adds a bottleneck and a deputy problem the department did not
ask for. Revisiting it later is a new ADR and still no schema change.

### 8.5 Failed obligations must reach the decision

When a case is decided with a `FAILED` requirement, or with a `WAIVED` one, the decision screen must
show them and the `reasoning` field should address them. This is a workflow obligation, not a
constraint: the system cannot judge whether a refusal was correct, but it must not let the decision be
taken while the unmet obligation is invisible.

### 8.6 Multiple results, corrections and amendments

- **At most one `ISSUED` at a time** (frozen partial unique index). Multiple results over the life of a
  case are normal; multiple *in force* are not.
- **Correction or amendment** = a **new row** with `supersedes_final_result_id` → the old one, which
  becomes `SUPERSEDED` (F3). The original is never edited and stays fully readable — it is what the
  requester acted on at the time.
- **Issuing a replacement is one atomic operation** (Domain Model amendment A-4). `supersedes_final_result_id`
  is set while the replacement is `DRAFT` and may name only the case's currently `ISSUED` result. At
  issue, in one transaction under the case-level serialization of `ARCHITECTURE.md` §12.5:
  1. **identify** the currently `ISSUED` result and verify it is the one named — if it has meanwhile been
     revoked or superseded, the issue is refused (with no `ISSUED` result left, the draft is issued as a
     first result instead);
  2. **retire** it: `ISSUED → SUPERSEDED` (F3);
  3. **issue** the replacement: `DRAFT → ISSUED` (F2), with guards D1 (readiness, §7.4 — condition 5 does
     not block a replacement) to D4, and its own pins taken at issue;
  4. **preserve** everything of the old result — its dispatch letter, its pinned documents, its approval
     metadata and its audit history are untouched; both transitions are audited under one
     `correlation_id`.

  Step 2 precedes step 3 so that the "at most one `ISSUED`" index holds at every statement. Reopening
  the case therefore never deadlocks against its own previous decision.
- **Revocation without replacement** = F4, with a reason.
- A superseding result normally follows a **reopening** (§10), because issuing a new decision on a
  closed case means the work resumed.
- The result is normally conveyed to the requester by an official letter sent **in the external
  government system**, which is then registered here as an outgoing `correspondence`
  (`FINAL_RESULT_DISPATCH`) referenced by `dispatch_correspondence_id`. Whether such a letter is
  mandatory is Domain Model OQ-6. Issuing a result in this application does not send anything.

---

## 9. Case closure

### 9.1 What `CLOSED` means

The department has finished its work on this dossier: every branch reached a terminal state, every
blocking obligation was resolved one way or another, and the result (where one is due) has been issued —
**or** the Head authorised closing with named exceptions, recorded as such (§9.4).

**This section is the one authoritative closure contract** (Domain Model §2.5 defers to it, amendment
A-3):

| | Normal closure | Exceptional closure |
|---|---|---|
| Condition | every guard G1–G5 passes (§9.2) | the Head overrides the eligible guards that fail (G1, G2, G3, G5 — never G4) |
| Record | `case_state_change` + `audit_event` | the same, with `reason_code = CLOSURE_GUARD_OVERRIDE`, a mandatory note naming each overridden guard and why, the actual actor and time |
| Unresolved records | only non-blocking requirements can remain open | stay in their **true** states — never auto-fulfilled, auto-voided or auto-failed by the closure |
| Reported as | closed; open non-blocking requirements listed | `closed_with_unresolved_items` (derived — Domain Model §9.1) |

`CLOSED` is **not** an archive flag and **not** a permissions boundary. A closed case remains fully
readable to everyone who could read it before. What it stops is *new work*: a new request or requirement,
a supersession, or a response that creates new work (§4.6) is not added to a closed case — the case is
reopened first (§10), which is one action and leaves a record. An **informational-only** response may be
recorded on a closed case without reopening it, because it changes nothing the closure asserted.

### 9.2 Closure guards

| # | Guard | Rationale | Overridable |
|---|---|---|---|
| **G1** | No requirement with `is_blocking = true` in `OPEN` or `IN_PROGRESS` | closing over a live obligation is the failure mode this system exists to prevent | **yes** (§9.4) |
| **G2** | No request in `DRAFT`, `SENT` or `ANSWERED` — all terminal | an unanswered letter to an authority is unfinished work | **yes** |
| **G3** | A `final_result` with `status = ISSUED` exists, **unless** `closure_type` is one that does not produce a decision (withdrawn by the requester, duplicate, merged) | `PROJECT.md` §32 requires "what was the final result" to be answerable | **yes** |
| **G4** | Closure metadata present: `closed_by_user_id`, `closed_at`, `closure_type_id` | — | **no** |
| **G5** | The case has, or has had, a `RESPONSIBLE` assignment | a case nobody ever owned should not reach closure silently | **yes** |

**One definition of blocking.** A non-blocking requirement (`is_blocking = false`) **holds nothing
open** — not its request's closure (§3.4), not its branch (§7.2), not readiness for the final result
(§7.4), not case closure (G1). Open non-blocking requirements are shown on the closure screen, and the
person closing decides whether to resolve them first. This is what `is_blocking` is for. **Requests are
never exempt:** a request sent to satisfy a non-blocking requirement is still an open letter to an
authority, and G2 still requires it to be terminal.

**Guards are evaluated under serialization.** G1–G5, and readiness (§7.4) for D1, read many rows. They
are evaluated under the case-level serialization convention of `ARCHITECTURE.md` §12.5, shared with every
operation that adds or removes closure-relevant work in the case — a row-version check alone cannot stop
a requirement being created while the case is being closed.

### 9.2.1 Unresolved non-blocking requirements: warn, never touch *(OB-4, confirmed 2026-09-18)*

The product owner has confirmed that a case **may** be closed normally while non-blocking requirements are
still open (ADR-012, ADR-042 is unrelated). Three obligations come with that permission, all of them
presentation and derivation — no guard changes and no record is rewritten:

| # | Obligation |
|---|---|
| 1 | The closure screen **states plainly** that unresolved non-blocking requirements remain, how many, and which. Closure is then a **deliberate confirmation**, not a click the person could make without seeing them |
| 2 | Those requirements **keep their true state**. Closure never auto-fulfils, auto-voids or auto-fails anything — the same rule the override already obeys (§9.4) |
| 3 | Afterwards the case **shows that it was closed with unresolved work**, derived from the rows (`closed_with_unresolved_items`, Domain Model §9.1) and never stored. The derivation distinguishes the two ways it can arise: non-blocking requirements left open at a **normal** closure, and anything left open by a Head **override** |

A **blocking** requirement is unaffected: it still fails G1, and only the Head's override (§9.4) closes over
it. A Chief cannot bypass G1 by any route.

### 9.3 Closure is never automatic

Even with every guard satisfied, the system only surfaces **"ready to close"**. Closure is recorded by a
person, with a `closure_type`, because it is the assertion that the department's work is done.

### 9.4 Override — closing without falsifying the record

Real cases sometimes must be closed with something unresolved: an authority that will never reply, an
obligation overtaken by events, an instruction from above.

The wrong answer is forcing the user to mark an open requirement `FULFILLED` or `WAIVED` just to pass
the guard. That corrupts the data the whole system is built to protect, and it is exactly what a rigid
guard produces in practice.

So:

| Rule | Behaviour |
|---|---|
| Who | the highest business authority only (`PERMISSIONS.md` §23) |
| Reason | **mandatory**, free text, naming which guard was overridden and why |
| Record | a `case_state_change` row with `reason_code = CLOSURE_GUARD_OVERRIDE` plus the note, and a `STATE_CHANGE` `audit_event` |
| **Unresolved records are left exactly as they are** | an `OPEN` requirement stays `OPEN` on the closed case; a `SENT` request stays `SENT`. Nothing is back-dated, re-labelled, swept, auto-fulfilled, auto-voided or auto-failed |
| Consequence | the case is reportable as **`closed_with_unresolved_items`** — derived, never stored (Domain Model §9.1) — a genuinely useful metric that only exists because nothing was falsified. Its unresolved items leave the live operational lists (§12.2) |

A closed case carrying an open requirement is not a data error. It is an accurate record of an
authorised exception, and it is the reason the override mechanism exists.

---

## 10. Reopening

### 10.1 Rules

| Aspect | Rule |
|---|---|
| Transition | `CLOSED → ACTIVE` (T7), on the **same case**. Never a replacement case, never a copy, never a new case number |
| Who | operational management authority (`PERMISSIONS.md` §23) |
| Reason | `case_state_change.reason_code` + note, **mandatory, no exception** |
| Closure metadata | **retained**, not cleared. The case shows it was closed on a date, by a person, for a reason, and reopened later. A later closure overwrites the `case.closed_*` columns; **every closure episode remains a `case_state_change` row** (Domain Model §2.6) |
| Previous final result | **stays `ISSUED` and in force**. Reopening does not revoke a decision. If the reopening changes the outcome, a replacement supersedes it atomically at F2/F3 (§8.6) — the issued result never blocks its own replacement; if the decision is withdrawn outright, that is F4 |
| New work | permitted in full — new requests, responses, requirements, documents, assignments |
| Assignment | the previous `RESPONSIBLE` assignment ended at closure; reopening requires assigning someone again (it may be the same person, as a new assignment row) |
| Audit | `STATE_CHANGE` on the case + a `case_state_change` row; the reason is part of the permanent record |

### 10.2 Why the same case

`PROJECT.md` §10 requires reopening; Domain Model §5.1 fixes it as `CLOSED → ACTIVE` on the same row.
Creating a successor case would split one administrative matter across two dossiers and two case
numbers, break the origin chains of everything already in it, and make "what happened in this case?"
— the question the system exists to answer — require knowing that a second dossier exists.

### 10.3 The common trigger

A late official letter about a closed case **that creates new work** (§4.6). The sequence is: reopen the
case (with the letter as the stated reason) → register the correspondence → register the response → let
the request transitions follow → close again when resolved. Four steps, each recorded, none of them
falsifying anything. A letter that is informational only is simply recorded; the case stays closed.

---

## 11. Derived progress

The case stores five lifecycle values. Everything an observer calls "progress" is computed at read time
from rows that staff create as a by-product of doing the work (Domain Model §9). This section defines
the rules precisely enough that two independent implementations would produce the same sentence.

### 11.1 Shape of the output

Two parts, always:

```
HEADLINE   the single most actionable blocking condition        ← precedence ladder, §11.3
SUMMARY    counts, always shown, never the headline             ← §11.4
```

Example:

```
Waiting for utility communication map — Utility Authority, overdue 6 days
3 authority branches · 2 of 3 responses received · 1 open requirement
```

### 11.2 Precedence principles

1. **Case lifecycle first.** A non-`ACTIVE` case says so and nothing else.
2. **Then actionability.** The headline is what a person should act on *next*, not what is most recent.
3. **Ours before theirs, except when theirs is overdue.** Work we can do today outranks waiting on an
   authority — but an overdue external item outranks our routine internal work, because chasing it is
   today's action.
4. **Exceptions outrank routine.** Conflicts, failures and things needing a decision come before
   ordinary waiting.
5. **Deterministic ties.** Where two items tie, order by: most overdue → earliest `due_at` → earliest
   `raised_at`/`sent_at` → lowest `id`. No implicit ordering, ever.

### 11.3 Headline precedence ladder

Evaluated top to bottom; **first match wins**; the matching row supplies the message.

| # | Condition | Message template |
|---|---|---|
| **P0** | `case.lifecycle_state = CANCELLED` | "Cancelled — {closure_type} ({closed_at})" |
| **P1** | `= CLOSED` | "Closed {closed_at} — {decision_type of the ISSUED result, or closure_type}{ · n unresolved item(s), when `closed_with_unresolved_items`}" |
| **P2** | `= ON_HOLD` | "On hold{ until hold_until} — {reason} (by {actor of the hold})" |
| **P3** | `= REGISTERED` and no request exists | "Registered — no requests sent yet" |
| **P4** | An unresolved response conflict exists (§4.5) | "Conflicting responses from {organization} — resolution required" |
| **P5** | A `FAILED` blocking requirement exists and no result is issued | "Blocked: {requirement.title} could not be obtained — decision required" |
| **P6** | A blocking requirement is `OPEN`/`IN_PROGRESS` **and overdue** | "Overdue: waiting for {requirement.title} from {addressed_to_organization.short_name} — {n} days" |
| **P7** | A request is `SENT`, unanswered **and overdue** | "Overdue: no response from {target_organization.short_name} — {n} days" |
| **P8** | A blocking requirement is `OPEN` with **no** child request sent and no evidence (branch state B2 — *we* owe the move) | "Waiting for {requirement.title} — no request sent yet" |
| **P9** | A request is `DRAFT` — planned but not yet officially issued (§3.1) | "{n} planned request(s) not yet issued" |
| **P10** | A response is classified `UNDETERMINED` | "{n} response(s) awaiting classification" |
| **P11** | A blocking requirement is `FULFILLED` but the authority that imposed it has not been written to (§6.2) | "{requirement.title} obtained — {raised_by_organization.short_name} is waiting for it" |
| **P12** | A blocking requirement is `IN_PROGRESS` with a child request sent | "Waiting for {requirement.title} from {addressed_to_organization.short_name}" |
| **P13** | Exactly one request is `SENT` and unanswered | "Waiting for response from {target_organization.short_name}" |
| **P14** | More than one request is `SENT` and unanswered | "Waiting for {n} authority responses" |
| **P15** | A `final_result` is `DRAFT` with approval outstanding | "Final result drafted — awaiting approval" |
| **P16** | Ready for final result (§7.4) | "Ready for final result" |
| **P17** | A `final_result` is `ISSUED` and the case is not closed | "Result issued {issued_at} — ready to close" |
| **P18** | None of the above | "Active — no outstanding items" (this should be rare and is worth investigating) |

Notes on the ordering that are not obvious:

- **P4 and P5 above everything operational**: both need a human decision and both silently corrupt
  downstream work if ignored.
- **P6/P7 (overdue) above P8–P14 (routine)**: principle 3.
- **P8 above P12**: a requirement with no request sent is *ours*; one with a request sent is *theirs*.
- **P11 exists** because it is the step most easily forgotten — the map arrived, and the authority
  waiting for it was never told.
- **P9/P10 below the external overdue rows** but above routine waiting: they are ours, quick, and
  cheap to clear.
- **P9 never says "waiting to send a letter."** This application does not send letters. It reports an
  internal planning item — a request the department decided to make and has not yet issued in the
  external system (§3.1). A department that never uses `DRAFT` will never see P9.

### 11.4 Summary line

Always shown alongside the headline, never as the headline:

| Element | Source |
|---|---|
| "{n} authority branches" | count of top-level requests not `VOID` |
| "{x} of {y} responses received" | y = requests not `VOID`/`WITHDRAWN`; x = those with an `ACTIVE` conclusive response |
| "{n} open requirement(s)" | requirements in `OPEN` + `IN_PROGRESS` |
| "{n} overdue" | derived per §12 |
| "{n} waived / {n} failed" | shown only when non-zero — they matter at decision time (§8.5) |

### 11.5 Per-branch progress

The same ladder, restricted to one branch, produces each branch's line in the case's dependency tree
(`PROJECT.md` §18). The case headline is the branch headline with the highest precedence, ties broken
per §11.2(5).

### 11.6 Rules for whoever implements this

- **Nothing here is stored.** No `current_progress` column exists and none may be added (Domain Model
  decision C-3).
- If profiling later demands it, a **rebuildable materialised read model** is permitted, clearly
  labelled as a cache, never user-writable — the same standard `case.last_activity_at` is held to.
- The ladder is **data, not code**: it should be expressible as an ordered rule list so that adding a
  condition does not mean rewriting a function.
- Every message must name the **organization** where one is involved. "Waiting for a response" without
  saying from whom is the failure mode this replaces.

---

## 12. Deadlines and overdue

### 12.1 Where deadlines come from

`request.due_at` and `requirement.due_at` — entered by a person or defaulted by configuration, with
`deadline_basis` recording whether it is statutory, internal or agreed. `request.original_due_at`
preserves the first value when a deadline is extended.

**The department's deadline rule, confirmed 2026-09-18** (DECISIONS.md **ADR-041**, closing OB-2 / OQ-W3 and
OQ-5 for requests):

| | |
|---|---|
| Where deadlines exist | **outgoing requests**. A requirement or a case carries a `due_at` only where one was genuinely stated |
| Default | **the date the letter was sent externally + 10 calendar days** |
| Calendar | **calendar days — weekends and holidays count.** There is no working-day calculation and no holiday table |
| Editable | the default is **suggested** and the person registering the request may change it before saving; the saved value stands and is never recomputed behind them |
| Historical registrations | a request recorded long after its letter went out gets the same rule from that letter's **actual** sent date, so a back-dated registration is overdue immediately and truthfully |
| `ON_HOLD` | **pauses nothing** — see §12.5 |
| Overdue | **derived** (§12.2), never a state and never a manually edited flag |

### 12.2 Overdue is derived, never stored

| Item | Overdue when |
|---|---|
| **Request** | `due_at < now()` **and** `status = SENT` **and** no `ACTIVE` response with `is_conclusive = true` |
| **Requirement** | `due_at < now()` **and** `status IN (OPEN, IN_PROGRESS)` |
| **Case** | `statutory_due_at < now()` and `lifecycle_state IN (REGISTERED, ACTIVE, ON_HOLD)` |

A `due_at` that is `NULL` is never overdue. Nothing with a terminal state is ever overdue —
retrospective overdue reporting uses the recorded completion timestamps against `due_at`, which is a
reporting question, not a state. Requests and requirements left unresolved on a `CLOSED` case by an
authorised override (§9.4) are not reported as live overdue work; they are reported through
`closed_with_unresolved_items`.

### 12.3 Overdue never changes lifecycle state

There is no `OVERDUE` state on anything, and no transition fires when a deadline passes. Overdue is a
**comparison performed at read time**. Consequences:

- no scheduled job mutates business data (a job may still *notify*);
- a clock change, a corrected `due_at` or a back-dated response instantly produces the correct answer,
  with no stale state to repair;
- "how many overdue items on 1 March?" is answerable historically, because nothing was overwritten.

### 12.4 Deadline extensions

An authority asking for more time is registered as a `response` with
`response_type = DEADLINE_EXTENSION`, `response_outcome = NOT_APPLICABLE`, `is_conclusive = false`.
`request.due_at` is then updated while `original_due_at` keeps the first value, and the change is in
`audit_event`. The request does **not** leave `SENT`.

Whether extensions must be separately reportable is Domain Model **OQ-11**.

### 12.5 Holds do not pause deadlines *(answered 2026-09-18)*

**OQ-W3 / Domain Model OQ-5 is answered for requests** (ADR-041): 10 calendar days from the external sent
date, weekends included, suggested and editable. What this section already required stands unchanged, and is
now the decision rather than the interim position:

- `due_at` is an **absolute timestamp**; the system compares it to `now()` and does nothing cleverer;
- **`ON_HOLD` does not pause, freeze, extend or shift any `due_at`.** A hold is a statement about the
  department's work, not about the authority's clock;
- where a held case has overdue items, the display shows **both** facts ("overdue 6 days · case on
  hold") rather than suppressing either, so nobody is misled in either direction.

What remains open is narrower and unrelated to requests: the **case-inactivity** definition (OB-3 / OQ-13,
§14.1 OQ-W4). A statutory *case* deadline, if the department ever states one, is still entered by a person.

---

## 13. Workflow audit events

Every action below writes an `audit_event` (Domain Model §2.18): append-only, `event_seq` ordered,
actor identity snapshotted, `before_state`/`after_state`, `entity_version`, `case_id` scope, and a
`correlation_id` grouping everything written by one user action.

### 13.1 Required events

| Workflow action | `action_code` | `entity_type` | Must also carry |
|---|---|---|---|
| Case registered | `CREATE` | `case` | requesting organization, case number |
| Case activated | `STATE_CHANGE` | `case` | `actor_kind = SYSTEM` (consequence T2) |
| Case put on hold / released | `STATE_CHANGE` | `case` | reason code + note |
| Assignment created / ended / covered | `ASSIGN` | `assignment` | assignee, role, `valid_from`/`valid_until`, `end_reason` |
| **Outgoing correspondence registered** (recording a letter already sent externally) | `CREATE` | `correspondence` | `direction = OUT`, counterparty, external letter number, external `sent_at`, `recorded_at` |
| **Incoming correspondence registered** (recording a letter already received externally) | `CREATE` | `correspondence` | `direction = IN`, counterparty, external letter number, `received_at`, `recorded_at` |
| Document uploaded | `UPLOAD` | `document_version` | **`document_hash`**, filename, size |
| Document withdrawn | `WITHDRAW` | `document_version` | reason |
| Document linked / unlinked | `LINK` / `UNLINK` | `document_link` | role, target entity |
| Request created | `CREATE` | `request` | target organization, `source_requirement_id` (or null = top-level) |
| **Request linked to outgoing correspondence** — the request is thereby officially issued | `STATE_CHANGE` | `request` | the correspondence, its external `sent_at`, `actor_kind = SYSTEM` (consequence R2) |
| Request closed / withdrawn / voided | `STATE_CHANGE` / `WITHDRAW` / `VOID` | `request` | reason where applicable |
| Response registered | `CREATE` | `response` | type, outcome, `is_conclusive`, correspondence |
| Response superseded | `CREATE` + `STATE_CHANGE` | `response_supersession` + each superseded `response` | superseding and superseded response; one event pair per edge, one `correlation_id` |
| Response voided | `VOID` | `response` | reason |
| **Response supersession retracted** (by voiding the superseding response, or explicitly) | `STATE_CHANGE` ×2 | `response_supersession` + the restored `response` | retraction note — **mandatory** when explicit; the void's `correlation_id` when consequential (`actor_kind = SYSTEM`) |
| Requirement created | `CREATE` | `requirement` | origin type, `source_response_id`, `is_blocking` |
| Requirement started | `STATE_CHANGE` | `requirement` | `actor_kind = SYSTEM` when triggered by a child request |
| Requirement fulfilled | `STATE_CHANGE` | `requirement` | evidence references |
| Requirement waived | `STATE_CHANGE` | `requirement` | **authoriser, reason — mandatory** |
| Requirement voided | `STATE_CHANGE` | `requirement` | **`void_reason`, `void_source_response_id` where applicable** |
| Requirement failed | `STATE_CHANGE` | `requirement` | **failure reason** |
| **Requirement resolution corrected** (Q7) | `STATE_CHANGE` + `CREATE` | `requirement` + `requirement_resolution_correction` | **reason code + note — mandatory**; corrected and restored status; `before_state` carries the withdrawn resolution in full |
| Child request created | `CREATE` | `request` | `source_requirement_id` — this is what makes the causal chain auditable, not just queryable |
| Evidence recorded / retracted | `LINK` / `UNLINK` | `requirement_evidence` | what it points at, pinned version if any |
| Final result drafted | `CREATE` | `final_result` | decision type |
| Final result issued | `STATE_CHANGE` | `final_result` | decided_by, approved_by, issued_at |
| Final result superseded / revoked | `STATE_CHANGE` | `final_result` | reason, `supersedes_final_result_id`; a supersession is `actor_kind = SYSTEM`, attributed to the person issuing the replacement, under that issue's `correlation_id` (§8.6) |
| Case closed | `STATE_CHANGE` | `case` | closure type, closed_by |
| **Closure guard overridden** | `STATE_CHANGE` | `case` | **`case_state_change.reason_code = CLOSURE_GUARD_OVERRIDE`** + note naming the guard |
| Case reopened | `STATE_CHANGE` | `case` | **reason — mandatory** |
| Case cancelled | `STATE_CHANGE` | `case` | closure type + note |
| Restricted access granted / revoked | `CREATE` / `STATE_CHANGE` | `case_access_grant` | grantee, reason, grantor |
| Role granted / revoked | `CREATE` / `STATE_CHANGE` | `user_role` | role, grantor, validity |
| Permission denied | `PERMISSION_DENIED` | the attempted object | attempted action |

### 13.2 Audit wording must not claim dispatch

Audit entries describe **what this application recorded**, never an act it performed. "Outgoing
correspondence registered" and "Request linked to outgoing correspondence" are accurate; *"letter sent"*
would be a false statement about this system, and a misleading one in any later dispute — the sending
happened in the external system, on its own date, under its own record.

The distinction shows in the timestamps, which is why both must be present: `occurred_at` /`sent_at`
carry the external event's date, `recorded_at` carries when an employee entered it here.

### 13.3 A note on the override event

An override is recorded with the existing `STATE_CHANGE` code plus a **reserved
`case_state_change.reason_code`**, so it is findable by an exact-match query without extending the
frozen `action_code` vocabulary. Adding a dedicated `OVERRIDE` code later would be a vocabulary
refinement, explicitly permitted by Domain Model §12.6 — but it is **not needed**, and this document
does not assume it.

### 13.4 Out of scope here

Cryptographic chaining of audit events belongs to `SECURITY.md` (Domain Model §2.18). Nothing in this
document depends on it, and nothing here should be designed around a hash format that does not exist
yet.

---

## 14. Open questions

Separated the way Domain Model §10 separates them: what needs a **business decision** versus what is an
**implementation choice safely deferred**.

### 14.0 Closed by the external-correspondence clarification

**OQ-W1 — Does an outgoing official letter require Chief approval before dispatch? — CLOSED: NO.**
There is no dispatch approval workflow in this application, because there is no dispatch in this
application (`PROJECT.md` §1.1). Official letters are sent in the external government system; if that
system requires an approval, it enforces it. Nothing needs to be recorded here, and the additive change
to `correspondence` that a "yes" would have required is **not needed** — the frozen domain model stands
unchanged.

**Integration with the external government correspondence system — CLOSED for V1: NONE.**
Manual registration only. No API, no import, no export, no polling, no delivery confirmation. Future
integration may be reconsidered as a separate decision, and **must not shape V1 architecture**
(`PROJECT.md` §1.1, §31).

### 14.1 Business decisions still needed

**OQ-W2 — CLOSED 2026-09-18: one approval, by the Head.** (DECISIONS.md ADR-040; Domain Model OQ-14,
`PERMISSIONS.md` OQ-P1.) Model A is adopted and the approver is the Head; a Chief may decide but not approve.
The design is §8.4 and no schema change was needed.

**OQ-W3 — CLOSED 2026-09-18: 10 calendar days from the external sent date, suggested and editable; holds
pause nothing.** (DECISIONS.md ADR-041; Domain Model OQ-5 for requests.) See §12.1 and §12.5.

**OQ-W4 — What counts as "activity" for inactivity reporting?** (Domain Model OQ-13, OB-3.) **Still open,
and now narrower.** The practical reminder rule the department asked for is settled — a request awaiting an
external answer whose due date is today or past (§12, ADR-041) — and that is what V1 surfaces. A general
*case*-inactivity definition ("no activity for N days") is still undefined; `case.last_activity_at` remains
rebuildable cache data that no rule reads, so fixing it later is a recomputation, not a migration.

**OQ-W5 — Must a final result always be dispatched to the requester?** (Domain Model OQ-6.)
`dispatch_correspondence_id` is nullable today.

**OQ-W6 — CLOSED 2026-09-18: yes, with a warning.** (OB-4; DECISIONS.md ADR-012.) A case may be closed
normally while non-blocking requirements are open, provided the closure screen warns about them explicitly,
their states are left untouched, and the closed case shows that unresolved work remains (§9.2.1). A blocking
requirement still fails G1 and only the Head's override closes over it.

### 14.2 Implementation decisions safely deferred

| Question | Why it can wait |
|---|---|
| Exact wording and localisation of derived progress messages | §11 fixes the *conditions and precedence*; the words are presentation |
| Whether the progress ladder is evaluated on read or in a materialised read model | §11.6 permits either, with the cache rules |
| Notification/reminder mechanism for overdue items | §12.3 guarantees no business data is mutated by any job, so notification is purely additive |
| Screen layout of the dependency tree | `PROJECT.md` §18 already prefers a simple expandable tree |
| Whether "ready to close" and "ready for final result" appear as dashboard lists or case badges | both are derived from §7.4 / §9.2 |
| Bulk tools (e.g. resolving all open items when cancelling a case) | §1.4 fixes the rule — each item gets its own decision and reason; a wizard is a convenience over that rule |

---

## 15. Self-review

| # | Question | Answer | Mechanism |
|---|---|---|---|
| 1 | Three authority branches progress independently? | **Yes** | branches share no relationship; §7.3 states nothing cross-blocks except final-result readiness |
| 2 | Can one Response create several Requirements? | **Yes** | §4.4 — one condition per requirement, each separately satisfiable and waivable |
| 3 | Can each Requirement create child Requests? | **Yes** | §5.5 — and several, to different authorities, for the same requirement |
| 4 | Can a child Request exist without losing why it exists? | **Yes** | §6.3 — `source_requirement_id` is write-once and survives voiding of the requirement (§6.4) |
| 5 | Can a Requirement become VOID because a later Response makes it irrelevant? | **Yes** | §5.4, with `void_source_response_id` naming the letter that did it |
| 6 | Can a Worker register an incoming letter while a colleague is absent? | **Yes** | registering correspondence and responses requires no assignment — `PERMISSIONS.md` §21 |
| 7 | Temporary coverage without shared credentials? | **Yes** | `TEMPORARY_COVER` assignment; the cover acts as themselves — `PERMISSIONS.md` §22 |
| 8 | Is a Case closed only when meaningful obligations are resolved? | **Yes** | guards G1–G5, §9.2 |
| 9 | Can an authorised exception close a Case without falsifying Requirements? | **Yes** | §9.4 — unresolved records are left untouched and the override is recorded |
| 10 | Can a closed Case be reopened without destroying history? | **Yes** | §10 — same case, closure metadata retained, previous result stays in force |
| 14 | Are manual statuses minimised? | **Yes** | five case states; `ANSWERED`, `IN_PROGRESS`, `SUPERSEDED` and activation are system consequences; no `READY`, no `RESPONSE_RECEIVED`, no `OVERDUE` |
| 15 | Is derived progress deterministic enough to implement? | **Yes** | §11.3 ladder is ordered, first-match-wins, with explicit tie-breakers in §11.2(5) |

*(Questions 11–13 concern authorization and are answered in `PERMISSIONS.md` §29.)*

### External-correspondence clarification — verification

| # | Check | Result |
|---|---|---|
| 1 | Can an employee record an outgoing letter sent yesterday in the external system? | **Yes** — R1b registers the already-sent letter and its request in one act; `sent_at` carries yesterday's date, `recorded_at` today's (§3.2, §3.3) |
| 2 | Can `occurred_at` differ from `recorded_at`? | **Yes, routinely** — they are separate frozen columns and the clarification makes divergence the norm (`PROJECT.md` §1.1, §5.3) |
| 3 | Does registering outgoing correspondence establish issuance without a second "mark sent" action? | **Yes** — R2 is a system consequence; §3.1 forbids a confirmation step |
| 4 | Is there no Send button implied anywhere? | **Yes** — no state, transition, action or audit event in this document transmits anything (§0 rule 5, §3.1, §6.2, §13.2) |
| 5 | Is there no Chief dispatch approval implied anywhere? | **Yes** — OQ-W1 closed NO (§14.0); no approval gate exists between `DRAFT` and `SENT` |
| 6 | Is this described as the tracking layer, not the official channel? | **Yes** — §0 rule 5 and `PROJECT.md` §1.1 |
| 7 | Can incoming correspondence be registered manually after receipt? | **Yes** — §4.1, which now states the external-receipt precondition explicitly |
| 8 | Are official registry numbers treated as external metadata? | **Yes** — recorded, never generated (`PROJECT.md` §1.1); Domain Model §2.8 already held `letter_number` as the counterparty's number |
| 9 | Does Request → Response → Requirement → child Request causality remain intact? | **Yes** — untouched. No causal pointer, cardinality or transition in §6 changed |

### Tension with Domain Model v1

**None found.** Every state, column and invariant used here exists in the frozen model. Three
transitions and one creation path are defined that its lifecycle sketches did not draw — R7, R8, T8 and
**R1b** — each marked **[transition added here]** / **[creation path added here]**; none adds a state, a
column or a cardinality, and defining the complete transition table is this document's assigned job
(`PROJECT.md` §30, Phase 2).

**The external-correspondence clarification required no change to Domain Model v1.** It changes what
the frozen states *mean* (§3.1), not what they are. In fact it strengthens the frozen separation of
`request` (internal workflow concept) from `correspondence` (record of an official communication that
happened externally) — decision C-1, which now carries more weight than when it was made.

The one question that *would* have required an additive change to the frozen model — **OQ-W1**, a place
to record a dispatch approver — is now **closed NO** (§14.0), so that dependency is gone.

### Post-review amendments — verification (2026-09-17)

The "none found" above was not correct: the independent review found contradictions between this document
and Domain Model v1. They are resolved as follows, with the domain side in Domain Model §12.7.

| # | Contradiction | Resolution here |
|---|---|---|
| 1 | Domain Model v1 forbade `CLOSED` with non-terminal work; §9.4 permitted a Head override that leaves it | This document governs: §9.1 closure contract; Domain Model §2.5 amended (A-3) |
| 2 | Non-blocking requirements "do not prevent closure", yet held their request open and so failed G2 | One definition of blocking: §3.4, §5.6, §7.2, §9.2 |
| 3 | An issued result blocked readiness, so a reopened case could not issue its replacement | Readiness condition 5 exempts a replacement; F2/F3 atomic (§7.4, §8.6) |
| 4 | "Supersedes both" (§4.5) was not representable with one supersession column | Supersession edges (§4.3, §4.5; A-1) |
| 5 | "All terminal states are final" vs correcting a requirement wrongly marked fulfilled | Q7 for errors only, with a correction record (§5.2; A-2) |
| 6 | Any non-conclusive late response reopened requests and forced case reopening | "Creates new work" vs "informational only"; R8 narrowed, R9 added (§3.2, §4.6, §9.1, §10.3) |
| 7 | Consequences were attributed to "the person … with `actor_kind = SYSTEM`" while the Domain Model said `SYSTEM` ⟹ no user | Initiating person + `actor_kind = SYSTEM`; `JOB` has no user (§0.1; A-8) |
| 8 | T5 cited guards G1–G4 while §9.2 defines G1–G5 | T5 cites G1–G5 or override |

---

*End of document. No application code, database migrations, API endpoints or UI components are defined
in or implied by this workflow design.*
