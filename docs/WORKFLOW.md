# RCS — Workflow

**Status:** Draft v1 (design only — no code, no migrations, no API, no UI)
**Authoritative inputs:** `/docs/PROJECT.md` (PROJECT SPEC v1), `/docs/DOMAIN_MODEL.md` (Domain Model v1,
frozen).
**Companion:** `/docs/PERMISSIONS.md` — *who* may perform each transition described here.

---

## 0. Scope and standing rules

This document defines **what happens, in what order, under what guards**. It does not define who is
allowed to do it (that is `PERMISSIONS.md`), and it does not implement anything.

Four rules hold everywhere in this document and are not repeated in each section:

1. **The domain model is frozen.** Every state named here exists in Domain Model v1. No state, entity,
   column or cardinality is added. Where this document defines a *transition* that the frozen model's
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
| Who is the actor in the audit event? | The person | The person whose action triggered it, with `actor_kind = SYSTEM` noted in the event |

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
| T5 | `ACTIVE` | `CLOSED` | Work is finished | closure metadata (§9) | **Closure guards G1–G4 (§9.2)** |
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
| R3 | `SENT` | `ANSWERED` | **System consequence** of registering an `ACTIVE` response with `is_conclusive = true` | system | — |
| R4 | `ANSWERED` | `CLOSED` | Human closes the work item | human | `closed_by_user_id`, `closed_at`; **frozen closure rule** (§3.4) |
| R5 | `DRAFT` / `SENT` / `ANSWERED` | `WITHDRAWN` | The department recalls the request | human | `withdrawal_reason`, actor, time. If it was already issued, a withdrawal letter is sent **in the external system** and then registered here as a `WITHDRAWAL` correspondence |
| R6 | `DRAFT` / `SENT` / `ANSWERED` | `VOID` | The request was recorded in error and never validly existed | human | `void_reason`, actor, time |
| R7 | `ANSWERED` | `SENT` | **System consequence**: the conclusive response is superseded by a non-conclusive one, or is voided | system | — |
| R8 | `CLOSED` | `SENT` | **System consequence**: a late response arrives that is non-conclusive **or** raises a requirement **[transition added here]** | system | §4.6 |

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

1. at least one `ACTIVE` response with `is_conclusive = true`; **and**
2. every `requirement` whose `source_response_id` belongs to this request is terminal
   (`FULFILLED`, `WAIVED`, `VOID`, `FAILED`); **and**
3. a person with the authority records `closed_by_user_id` and `closed_at`.

Condition 2 is what stops "the authority gave its final opinion" from closing a branch whose conditions
are still outstanding.

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
3. Set `supersedes_response_id` to the earlier response.
4. The earlier response becomes `SUPERSEDED` — **not** deleted, **not** edited, still queryable, and
   **still the source of any requirements it raised** (§6.4).

There is no `REVISION` response type. Supersession is the relationship; a label that could disagree with
it would be a defect waiting to happen.

**Constraint (frozen):** a superseding response must belong to the same request.

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
| Resolution | A person with authority records which response supersedes which (§4.3), or voids one as a registration error, or writes to the authority for clarification — which produces a third response that supersedes both |
| Never | No automatic resolution, no silent preference |

### 4.6 A response arriving after the request looked finished

Late letters are normal in interagency work. Registering one is **always** permitted, whatever the
request's state — refusing would force staff to falsify the record or leave an official letter unfiled.

| The request was | The late response is | Result |
|---|---|---|
| `ANSWERED` | informational, non-conclusive | stays `ANSWERED`; the response is recorded |
| `ANSWERED` | conclusive, superseding the earlier one | stays `ANSWERED` with the new response in force |
| `ANSWERED` | supersedes the conclusive response with a **non-conclusive** one | **R7**: back to `SENT` — the branch is genuinely unfinished again |
| `CLOSED` | purely informational | stays `CLOSED`; the response is recorded against the closed request |
| `CLOSED` | non-conclusive, **or** raises a requirement | **R8**: back to `SENT` — work has genuinely resumed, and the frozen closure rule would otherwise be violated by an open requirement under a closed request |
| `WITHDRAWN` / `VOID` | anything | the response is recorded; the request's state does not change. An authority answering a request we withdrew is a fact worth keeping, not a reason to revive it |

If the case itself is `CLOSED` when a late letter arrives, the case is **reopened** (§10) before the
response is registered. Filing official correspondence into a closed dossier without reopening it would
make the closure record false.

### 4.7 Voiding a response

Only for registration errors — wrong request selected, duplicate entry, letter recorded against the
wrong case. `status = VOID` + `void_reason` + actor + time. The letter itself (`correspondence`) usually
remains: it did arrive.

**Voiding a response does not cascade to its requirements.** The system lists the affected requirements
and a person decides each one: `VOID` if the demand disappeared with the mistake, unchanged if the
condition is real and simply attached to the wrong row (in which case a corrected response is registered
and the requirements are recreated against it).

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

**All four terminal states are final.** A requirement that comes back is a **new** requirement — the
history of the first one stays intact. There is no `FULFILLED → OPEN`.

`IN_PROGRESS` is a convenience signal, not a decision. Skipping it (`OPEN → FULFILLED` directly, when
the requester simply hands in the document) is normal and permitted.

### 5.3 What evidence means

`requirement_evidence` rows (Domain Model §2.12) point at **a response**, **a document** (optionally
pinned to an exact `document_version`), or **an internal record**. Evidence is M:N in both directions:

- one requirement may need several pieces;
- **one document may satisfy two requirements** — when two authorities each demanded the same map, that
  is two requirements sharing one piece of evidence, each closed separately.

Evidence is **retracted, never deleted**. A requirement wrongly marked fulfilled must still show that it
once was, and why that was withdrawn.

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

- if **every** requirement from that request's responses is terminal **and** an `ACTIVE` conclusive
  response exists → the request becomes **closeable** (surfaced, not auto-closed);
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
| B1 | **FAILED** | any `FAILED` requirement in the branch with `is_blocking = true`. (Requirement states are terminal, so a `FAILED` requirement is never later fulfilled — if the department tries again, that is a **new** requirement, and the branch is then judged on that new one as well as the failed one) |
| B2 | **BLOCKED** | a requirement is `OPEN`/`IN_PROGRESS` with **no** child request in `SENT` and no `ACTIVE` evidence recorded — i.e. *we* owe the next move |
| B3 | **WAITING_EXTERNAL** | any request in the branch is `SENT` without a conclusive response, or a requirement is `IN_PROGRESS` with a child request `SENT` |
| B4 | **WAITING_INTERNAL** | any request in the branch is `DRAFT`, or a response is `UNDETERMINED`, or a fulfilled requirement still owes a follow-up letter (§6.2) |
| B5 | **WITHDRAWN** | the top-level request is `WITHDRAWN` or `VOID` |
| B6 | **COMPLETE** | the top-level request is `CLOSED` and every requirement in the branch is terminal |

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
5. no `final_result` already `ISSUED`.

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
| F2 | `DRAFT` | `ISSUED` | The decision is made and signed | guards D1–D4 (§8.3); `decided_by_user_id`, `decided_at`, `approved_by_user_id`, `approved_at`, `issued_at` |
| F3 | `ISSUED` | `SUPERSEDED` | **System consequence** of a newer result being issued with `supersedes_final_result_id` set | — |
| F4 | `ISSUED` | `REVOKED` | The decision is withdrawn without a replacement | `revoked_by_user_id`, `revoked_at`, `revocation_reason_note` |
| F5 | `DRAFT` / `ISSUED` | `VOID` | Recorded in error | `void_reason`, actor, time |

### 8.3 Guards for `ISSUED`

| # | Guard | Override |
|---|---|---|
| D1 | The case is ready for final result (§7.4) | yes — highest authority, mandatory reason (§9.4) |
| D2 | `decided_by_user_id` and `decided_at` are recorded | **no** |
| D3 | `approved_by_user_id` and `approved_at` are recorded, per the approval model (§8.4) | **no** |
| D4 | At least one `ACTIVE` `document_link` with role `FINAL_RESULT_DOCUMENT` — the signed decision | yes — mandatory reason; the document must follow |

D4 exists because `PROJECT.md` §4 and §32.12 require the system to answer *"which documents prove the
final result"*. An issued decision with nothing proving it cannot answer that. The override exists
because a decision may legitimately be signed before it is scanned.

### 8.4 Approval: the unresolved decision, modelled both ways

**This is a business decision that has not been made** (Domain Model OQ-14, `PROJECT.md` §12 says Head
holds "final approval where required" without saying when it is required). Both models are designed;
**neither is assumed**. Nothing else in this document depends on which is chosen.

| | **Model A — single authorised approval** | **Model B — four-eyes** |
|---|---|---|
| Rule | `approved_by_user_id` may be the same person as `decided_by_user_id` | `approved_by_user_id` **must differ** from `decided_by_user_id` |
| Who approves | the authorised decision-maker | a second person of the required authority |
| Data model impact | **none** — both columns already exist | **none** — both columns already exist |
| Workflow impact | F2 is one step | F2 is two steps: *submit for approval*, then *approve*. A `DRAFT` awaiting approval is derived (`DRAFT` + `decided_by` set + `approved_by` null), **not a new state** |
| Cost | fastest | one extra person on every decision, for 13 staff |
| Risk | the same person decides and approves | bottleneck when the approver is absent — needs a documented deputy |

**Whichever is chosen, no schema change is required.** The choice is a `CHECK`-level rule plus a
screen. It can be made after this document is frozen, and it can be changed later. See **OQ-W2** (§14).

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
blocking obligation was resolved one way or another, and the result (where one is due) has been issued.

`CLOSED` is **not** an archive flag and **not** a permissions boundary. A closed case remains fully
readable to everyone who could read it before. What it stops is *ordinary operational writing*: new
requests, responses and requirements are not added to a closed case — the case is reopened first (§10),
which is one action and leaves a record.

### 9.2 Closure guards

| # | Guard | Rationale | Overridable |
|---|---|---|---|
| **G1** | No requirement with `is_blocking = true` in `OPEN` or `IN_PROGRESS` | closing over a live obligation is the failure mode this system exists to prevent | **yes** (§9.4) |
| **G2** | No request in `DRAFT`, `SENT` or `ANSWERED` — all terminal | an unanswered letter to an authority is unfinished work | **yes** |
| **G3** | A `final_result` with `status = ISSUED` exists, **unless** `closure_type` is one that does not produce a decision (withdrawn by the requester, duplicate, merged) | `PROJECT.md` §32 requires "what was the final result" to be answerable | **yes** |
| **G4** | Closure metadata present: `closed_by_user_id`, `closed_at`, `closure_type_id` | — | **no** |
| **G5** | The case has, or has had, a `RESPONSIBLE` assignment | a case nobody ever owned should not reach closure silently | **yes** |

Non-blocking requirements (`is_blocking = false`) that are still open do **not** prevent closure. They
are shown on the closure screen, and the person closing decides whether to resolve them first. This is
what `is_blocking` is for.

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
| **Unresolved records are left exactly as they are** | an `OPEN` requirement stays `OPEN` on the closed case; a `SENT` request stays `SENT`. Nothing is back-dated, re-labelled or swept |
| Consequence | the case is reportable as *"closed with unresolved obligations"* — a genuinely useful metric that only exists because nothing was falsified |

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
| Closure metadata | **retained**, not cleared. The case shows it was closed on a date, by a person, for a reason, and reopened later |
| Previous final result | **stays `ISSUED` and in force**. Reopening does not revoke a decision. If the reopening changes the outcome, a new result supersedes it at F2/F3 (§8.6); if the decision is withdrawn outright, that is F4 |
| New work | permitted in full — new requests, responses, requirements, documents, assignments |
| Assignment | the previous `RESPONSIBLE` assignment ended at closure; reopening requires assigning someone again (it may be the same person, as a new assignment row) |
| Audit | `STATE_CHANGE` on the case + a `case_state_change` row; the reason is part of the permanent record |

### 10.2 Why the same case

`PROJECT.md` §10 requires reopening; Domain Model §5.1 fixes it as `CLOSED → ACTIVE` on the same row.
Creating a successor case would split one administrative matter across two dossiers and two case
numbers, break the origin chains of everything already in it, and make "what happened in this case?"
— the question the system exists to answer — require knowing that a second dossier exists.

### 10.3 The common trigger

A late official letter about a closed case (§4.6). The sequence is: reopen the case (with the letter as
the stated reason) → register the correspondence → register the response → let the request transitions
follow → close again when resolved. Four steps, each recorded, none of them falsifying anything.

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
| **P1** | `= CLOSED` | "Closed {closed_at} — {decision_type of the ISSUED result, or closure_type}" |
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

**No legal deadline values are invented here.** This document defines only how a deadline that exists
behaves.

### 12.2 Overdue is derived, never stored

| Item | Overdue when |
|---|---|
| **Request** | `due_at < now()` **and** `status = SENT` **and** no `ACTIVE` response with `is_conclusive = true` |
| **Requirement** | `due_at < now()` **and** `status IN (OPEN, IN_PROGRESS)` |
| **Case** | `statutory_due_at < now()` and `lifecycle_state IN (REGISTERED, ACTIVE, ON_HOLD)` |

A `due_at` that is `NULL` is never overdue. Nothing with a terminal state is ever overdue —
retrospective overdue reporting uses the recorded completion timestamps against `due_at`, which is a
reporting question, not a state.

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

### 12.5 The unresolved deadline question

**Domain Model OQ-5 is not answered here** and must not be answered by implementation default:
calendar days or working days; counted from the letter date, the date it was sent externally, or the
receipt date; whether
`ON_HOLD` suspends a statutory clock; whether holidays count.

Until it is answered:

- `due_at` is an **absolute timestamp**; the system compares it to `now()` and does nothing cleverer;
- `ON_HOLD` does **not** silently alter any `due_at`;
- where a held case has overdue items, the display shows **both** facts ("overdue 6 days · case on
  hold") rather than suppressing either, so nobody is misled in either direction.

None of the above is a workaround that would need unpicking: whatever the answer, it changes how
`due_at` is *calculated*, not how overdue is *derived*.

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
| Response superseded | `STATE_CHANGE` | `response` | `supersedes_response_id` on the new row |
| Response voided | `VOID` | `response` | reason |
| Requirement created | `CREATE` | `requirement` | origin type, `source_response_id`, `is_blocking` |
| Requirement started | `STATE_CHANGE` | `requirement` | `actor_kind = SYSTEM` when triggered by a child request |
| Requirement fulfilled | `STATE_CHANGE` | `requirement` | evidence references |
| Requirement waived | `STATE_CHANGE` | `requirement` | **authoriser, reason — mandatory** |
| Requirement voided | `STATE_CHANGE` | `requirement` | **`void_reason`, `void_source_response_id` where applicable** |
| Requirement failed | `STATE_CHANGE` | `requirement` | **failure reason** |
| Child request created | `CREATE` | `request` | `source_requirement_id` — this is what makes the causal chain auditable, not just queryable |
| Evidence recorded / retracted | `LINK` / `UNLINK` | `requirement_evidence` | what it points at, pinned version if any |
| Final result drafted | `CREATE` | `final_result` | decision type |
| Final result issued | `STATE_CHANGE` | `final_result` | decided_by, approved_by, issued_at |
| Final result superseded / revoked | `STATE_CHANGE` | `final_result` | reason, `supersedes_final_result_id` |
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

**OQ-W2 — Single approval or four-eyes for the final result?** (Domain Model OQ-14.)
Both models are fully designed in §8.4 and **neither is assumed**. No schema change either way.
*Impact if unanswered:* the final-result screen cannot be specified, but nothing else is blocked.

**OQ-W3 — Deadline calculation rules.** (Domain Model OQ-5.)
Calendar or working days; counted from which date; whether `ON_HOLD` suspends a statutory clock.
*Current behaviour:* `due_at` is absolute, holds do not alter it, and both facts are displayed
together (§12.5). *Impact if unanswered:* overdue figures may not match the department's legal
interpretation. No structural consequence.

**OQ-W4 — What counts as "activity" for inactivity reporting?** (Domain Model OQ-13.)
Deliberately unresolved. `case.last_activity_at` is rebuildable cache data, so fixing the rule later is
a recomputation, not a migration. **No production rule is assumed.**

**OQ-W5 — Must a final result always be dispatched to the requester?** (Domain Model OQ-6.)
`dispatch_correspondence_id` is nullable today.

**OQ-W6 — May a case be closed with a non-blocking requirement still open?**
Current design: yes (§9.2), which is the purpose of `is_blocking`. If the department wants *every*
requirement resolved before closure, `is_blocking` loses its meaning and G1 tightens.

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

---

*End of document. No application code, database migrations, API endpoints or UI components are defined
in or implied by this workflow design.*
