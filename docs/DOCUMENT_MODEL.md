# RCS — Document and File Management Model

**Status:** Draft v1 (design only — no code, no migrations, no API, no UI)
**Authoritative inputs (all frozen v1):** `/docs/PROJECT.md`, `/docs/DOMAIN_MODEL.md`,
`/docs/WORKFLOW.md`, `/docs/PERMISSIONS.md`.
**Deferred to later documents:** threat model and audit chaining → `SECURITY.md`; storage volume layout,
backup mechanics and deployment → `ARCHITECTURE.md`; technology choices → `DECISIONS.md`.
**Post-review amendments (2026-09-17):** official correspondence placements are version-pinned and
floating links are confined to the home case (§4.4, OQ-D7 closed); authorization is per version
(§10); cross-case letters and responses (§3.2, §4.8); removed links grant nothing (§10.2); backup
recovery-point invariant (§6.7); command idempotency (§7.4); bounded type detection (§9.2); no physical
garbage collection in V1 (§12.4). Recorded in `DOMAIN_MODEL.md` §12.7.
**Pre-schema pass (2026-09-17):** version reinstatement replaces "v1 becomes ACTIVE again" (§5.2, §5.3,
§7.4, §8.2, §8.3, §13.1). Decisions are logged in `/docs/DECISIONS.md`.

---

## 1. Purpose

In this system a document is **not an attachment**. It is the **evidential record** of an
administrative case — the thing that proves what was asked, what was answered, what was required and
what was decided.

The model must let anyone, years later, answer:

| Question | Answered by |
|---|---|
| Which official letter was received, and when? | `correspondence` + its `PRIMARY_LETTER` link |
| Which official letter was sent? | the outgoing `correspondence` and its links |
| Which files belonged to that letter? | `document_link` rows on that correspondence — each pinned to the exact version registered with the letter (§4.4) |
| Which version of a file existed on a given date? | `document_version` rows and their status history |
| Which exact file proved a requirement? | `requirement_evidence`, version-pinned (§4.6) |
| Which exact files supported the final result? | `document_link` with `FINAL_RESULT_DOCUMENT`, version-pinned (§4.7) |
| Was a file later replaced, withdrawn or superseded? | `document_version.status` + `supersedes_version_id` |
| Who uploaded or linked it, when, and why? | `uploaded_by_user_id`, `linked_by_user_id`, reason fields, `audit_event` |

Two design commitments follow from that, and everything else in this document serves them:

1. **A file's business meaning is never inferred from where its bytes sit.** Meaning comes from links;
   bytes are addressed by hash.
2. **Nothing that once served as evidence stops being retrievable.** Corrections change what is
   *current*; they never change what *was*. Bytes are never removed; access to them is always through a
   link that is still `ACTIVE` and exposes that version (§10.1).

**A note on authority.** Official letters are sent and received in a separate external government
system (`PROJECT.md` §1.1); a file held here is a **copy or reference artifact** attached to the tracked
case. That does not weaken anything in this document — the copy must still be immutable, hashed,
version-controlled and access-controlled, because it is what the department reads, cites and relies on
day to day. It does mean this application is not the authoritative dispatch or receipt record, and
nothing here should be read as claiming otherwise.

### 1.1 Relationship to the frozen model

This document **adds no entity, column or cardinality**. (The post-review amendments add pinning
constraints and a per-version authorization rule over the existing columns — Domain Model amendment A-5.)
It uses `document`,
`document_version`, `document_link`, `requirement_evidence`, `correspondence`, `case`, `request`,
`response`, `requirement` and `final_result` exactly as frozen in `DOMAIN_MODEL.md` §2.13–§2.15, §2.12
and §7.

Two places where the frozen model left an operational rule undefined are **settled here**, which is
this document's job; neither required a change to the frozen model:

- **§4.6** — the relationship between `requirement_evidence` and `document_link(requirement_id)`, both
  of which the frozen model defines and uses.
- **§4.2** — whether "map / drawing / spreadsheet" is a *link role* or a *document kind*.

---

## 2. Concepts

Three entities, three different questions. Confusing any two of them is the single most damaging
mistake available in this part of the system (§17, R1).

### 2.1 `document` — *what this is, as a business object*

A **logical business document**: a thing with an identity that survives revision.

> "The utility communication map issued by the Utility Authority for case 2026/114."

It has a `title` (the human name people use), a `document_kind`, optionally an
`issuing_organization_id` and the issuer's own `document_reference`. It has **no bytes**, **no
filename**, **no `case_id`** and **no `correspondence_id`**.

Examples: official incoming letter · official outgoing letter · map · spreadsheet · technical drawing ·
certificate · supporting reference · working copy.

### 2.2 `document_version` — *the exact bytes*

One **immutable byte representation** of that document. Its bytes never change (§5.1). A correction is a
new version, never an overwrite.

```
document:  "Utility Map"                          (one logical document, stable identity)
  ├── document_version 1   map_received_2026-09-01.pdf    sha256:9f3c…   SUPERSEDED
  └── document_version 2   corrected_map_2026-09-04.pdf   sha256:41ab…   ACTIVE
```

Version 1 remains forever unless a retention policy that does not yet exist explicitly permits physical
destruction (§12). Its bytes stay on disk; its row stays in the database; anything that pinned it still
resolves.

### 2.3 `document_link` — *why this document is here*

A **placement**: it says that a document (optionally, a specific version of it) belongs to one business
object, in one named role.

```
document_link:  document="Utility Map"  →  correspondence K-IN-040   role=ATTACHMENT            version=v1 (pinned)  is_origin=true
document_link:  document="Utility Map"  →  requirement  R1           role=REQUIREMENT_EVIDENCE  version=v1 (pinned)
document_link:  document="Utility Map"  →  correspondence K-OUT-050  role=ANNEX                 version=v1 (pinned)
```

One document, one set of bytes, three placements. The file is not copied (§18/§4.5).

### 2.4 The three questions, side by side

| | `document` | `document_version` | `document_link` |
|---|---|---|---|
| Answers | *what is it?* | *what are the exact bytes?* | *why is it here?* |
| Changes when | the business identity changes (rare) | a new file arrives (append-only) | context changes (append + retire) |
| Carries | title, kind, issuer | filename, hash, size, MIME, uploader, upload time | role, ordinal, version pin (required wherever the link records history, §4.4), who linked it and when |
| Mutable? | title/description editable, audited | **never** — only status transitions | `ACTIVE` → `REMOVED`; a floating link may additionally be frozen to a version **once** (§4.4) |
| Count | 1 per business artefact | 1..n per document | 1..n per document, across contexts |

---

## 3. Entity responsibilities

### 3.1 Who owns what

| Entity | Owns | Must never own |
|---|---|---|
| `case` | the dossier; **no documents directly**, except `SUPPORTING`/`WORKING_COPY` links (§4.3) | a general attachment list |
| `correspondence` | the files that arrived or left **with that letter** — one `PRIMARY_LETTER` plus attachments and annexes | business meaning about *why* a file matters elsewhere |
| `request` / `response` | no files of their own; their documents are reached through their correspondence (§3.2) | duplicated copies of the letter's files |
| `requirement` | its **evidence** (`requirement_evidence`) and its working placements | the authoritative copy of a file that arrived by letter |
| `final_result` | its signed decision and annexes, **version-pinned** (§4.7) | anything unpinned that could silently change after issue |
| `internal_record` | files created internally — site photos, calculations, file notes | official correspondence files |
| `document` | title, kind, issuer, status | any business context |
| `document_version` | bytes, hash, size, MIME, filename, uploader | business meaning |
| `document_link` | role, ordinal, optional pin, provenance | the bytes |

### 3.2 A request's document view is derived, not stored

`PROJECT.md` §8 requires every request to expose its own document context:

```
Request → Architecture Authority
  Outgoing:   official_request.pdf, attachment.xlsx
                 ← document_link rows on request.dispatch_correspondence_id
  Incoming:   architecture_response.pdf, scheme.pdf, map.pdf
                 ← document_link rows on the correspondence of each of the request's responses
```

Both sides are **queries**, not stored lists. This matters because one outgoing letter may carry
several requests (`DOMAIN_MODEL.md` decision C-1): the letter's files appear under each request the
letter carried, from one set of link rows, with no duplication and nothing to keep in sync.

**Both sides obey the letter's own visibility.** Where a response's letter is filed in another case, its
files appear on the incoming side **only for viewers of that owning case** (§4.8). For everyone else the
request shows the response's own facts and no trace of the letter.

The frozen arc does allow `document_link.request_id` and `.response_id` directly. Those are for files
that belong to the *work item* rather than to a letter — a draft being prepared, an internal analysis of
a response — and they carry `SUPPORTING` or `WORKING_COPY` roles. **The files of an official letter are
always linked to the correspondence**, never copied onto the request.

---

## 4. Link model

### 4.1 Roles belong on the link, kinds belong on the document

This is the answer to *"whether roles belong on DocumentLink or elsewhere"*, and the frozen model
already separates the two axes:

| | `document.document_kind` | `document_link.document_link_role` |
|---|---|---|
| Question | **what the file is** | **why it is in this context** |
| Stable? | a map is always a map | changes by context — the same map is an `ATTACHMENT` here and an `ANNEX` there |
| Frozen vocabulary | `LETTER_BODY`, `MAP`, `DRAWING`, `SPREADSHEET`, `PHOTO`, `CERTIFICATE`, `PERMIT`, `OTHER` | `PRIMARY_LETTER`, `ATTACHMENT`, `ANNEX`, `REQUIREMENT_EVIDENCE`, `FINAL_RESULT_DOCUMENT`, `SUPPORTING`, `WORKING_COPY` |

So the roles requested as `MAP`, `DRAWING`, `SPREADSHEET` are **document kinds**, not link roles — and
they must be, because the same map is an attachment of an incoming letter *and* an annex of an outgoing
one. Putting `MAP` in the role vocabulary would force the file to be re-declared a map in every context,
and the two declarations would eventually disagree.

`MAIN_LETTER` in the brief is the frozen `PRIMARY_LETTER`. Same concept, frozen spelling.

**The vocabulary is not overbuilt and should not grow casually.** Both are lookup tables precisely so
they *can* grow, but a new role must answer a genuinely new question about *why* a file is present.

### 4.2 Correspondence documents

One letter, many files (`PROJECT.md` §5.3, §5.7):

| Role | Meaning | Cardinality per correspondence |
|---|---|---|
| `PRIMARY_LETTER` | the signed letter itself | **at most one `ACTIVE`** (frozen partial unique index) |
| `ATTACHMENT` | a file sent with the letter and listed in it | many |
| `ANNEX` | a substantive enclosure, often a document in its own right | many |
| `SUPPORTING` | a file filed with the letter but not part of it — e.g. the delivery receipt | few |

Ordering within a letter is `document_link.ordinal`, so the file list can be shown the way the letter
itself lists them.

A five-attachment letter is therefore: **1** `correspondence`, **6** `document` rows, **6**
`document_version` rows (v1 each), **6** `document_link` rows — one `PRIMARY_LETTER`, five others,
each with `is_origin = true` for its own document.

### 4.3 The exclusive arc, and why not a generic polymorphic reference

`document_link` carries seven nullable foreign keys — `correspondence_id`, `requirement_id`,
`request_id`, `response_id`, `final_result_id`, `internal_record_id`, `case_id` — with
`CHECK (num_nonnulls(...) = 1)`.

**Why not `object_type text + object_id uuid`:**

| | Exclusive arc (chosen) | Generic polymorphic |
|---|---|---|
| Referential integrity | **real foreign keys** — a link to a deleted or non-existent requirement is impossible | none; the database cannot tell you the target exists |
| Joins | plain, indexable, planner-friendly | a join per type, or a union, or application-side resolution |
| Typos in `object_type` | impossible | silently orphan the row |
| Cost | one nullable column per target type | one column |
| Fit here | seven known target types, all stable | suits open-ended plugin systems, which this is not |

The one place the frozen model *does* use a generic reference is `audit_event`, and
`DOMAIN_MODEL.md` §2.18 justifies that exception explicitly. Documents are not that case.

**Constraints the database must later guarantee** (stated so schema design does not have to rediscover
them):

| # | Constraint | Kind |
|---|---|---|
| L1 | exactly one non-null target FK per row | `CHECK (num_nonnulls(...) = 1)` |
| L2 | `case_id` target permitted only with role `SUPPORTING` or `WORKING_COPY` | `CHECK` |
| L3 | at most one `ACTIVE` `PRIMARY_LETTER` link per correspondence | partial unique index |
| L4 | at most one `ACTIVE` link with `is_origin = true` per document | partial unique index |
| L5 | `document_version_id IS NOT NULL ⟹ that version belongs to `document_id`` | `CHECK` via FK pair, or trigger |
| L6 | every document has ≥1 `ACTIVE` link | **application invariant** — not expressible as a plain constraint; a deferred constraint or trigger is possible, and this document does not mandate the mechanism |
| L7 | all business FKs `ON DELETE RESTRICT`; no cascade | FK definition |
| L8 | every link to a `correspondence` is pinned: `correspondence_id IS NULL OR document_version_id IS NOT NULL` | `CHECK` *(post-review)* |
| L9 | `REQUIREMENT_EVIDENCE` links are pinned; `FINAL_RESULT_DOCUMENT` links are pinned once their result is `ISSUED` | application invariant (role is a lookup) *(post-review)* |
| L10 | **home-case confinement**: a link whose context lies outside the document's home case is pinned; floating links exist only in the home case; a new version is added only through a home-case context | application invariant *(post-review)* |

L2 is the structural reason "attach everything to the case" cannot happen by accident: there is no
column for it except a `CHECK`-restricted arc that names itself as supporting material.

### 4.4 Version pinning: the rule

A link may point at a **specific version** (pinned) or at the **document** (floating).

| | `document_version_id` set — **pinned** | `document_version_id IS NULL` — **floating** |
|---|---|---|
| Exposes | **exactly that version**, forever | the document's versions, presenting the `ACTIVE` one as current |
| Effect of a new version | **nothing** — the link still shows v1 | the link presents v2 as current |
| Use for | **everything that records history**: every file of a registered letter, evidence, issued results, anything shared into another case | **non-historical working placements only**, inside the document's home case |
| Rationale | what an official act contained, or what a decision relied on, must never change under it | a working file in progress should show its latest state |

**The rule, stated plainly** *(post-review amendment — replaces the v1 rule "contextual links float",
which let a registered letter silently show a later file)*:

> **Anything that records history pins. Floating is a narrow, same-case working convenience.**
>
> 1. **Every link to a `correspondence` is pinned** — `PRIMARY_LETTER`, `ATTACHMENT`, `ANNEX` and
>    `SUPPORTING` alike (L8). Reopening letter №7/119 years later shows exactly the files registered
>    with it, whatever versions exist since. A later upload can never change what an older official
>    letter is shown to contain.
> 2. **`REQUIREMENT_EVIDENCE` is pinned, always. `FINAL_RESULT_DOCUMENT` is pinned no later than
>    issue** (§4.7) (L9).
> 3. **A link outside the document's home case is pinned** (L10). The **home case** is the case the
>    document's `ACTIVE` `is_origin` link resolves to — where the file entered the system. A link into
>    any other case discloses exactly one chosen version, never "whatever the document becomes".
> 4. **Floating links** — `SUPPORTING` or `WORKING_COPY` on a case, request, response or internal
>    record, or the `FINAL_RESULT_DOCUMENT` of a `DRAFT` result — **exist only in the home case**.
> 5. **New versions are added only through a context in the home case** (L10). A revised file arriving
>    in any other case becomes a **new document** there; identical bytes are still stored once (§6.3).
>
> Rules 3–5 together guarantee that **a floating link never exposes a version introduced under another
> case**: every version entered through the home case, and every floating link sits in it.

**Freezing.** A floating link may be frozen **once** — its `document_version_id` set to a version it
currently exposes, normally the `ACTIVE` one. It is never re-pinned to another version and never
unpinned; a different version means a new link. Issuing a final result freezes its document links
exactly this way (§4.7).

**If the home case moves.** Correcting a document's origin into another case (§8.2, case 2) moves its
home case. Any floating link left in the old case is removed — or re-created pinned, as a disclosure —
in the same correlated action, so rule 4 keeps holding.

### 4.5 One document, several links

The frozen worked example (`DOMAIN_MODEL.md` §6.2, §7.4): the Utility Authority's map arrives as an
attachment, is accepted as evidence for requirement R1, and is later re-sent as an annex of our letter
to the Architecture Authority.

```
document "Utility communication map"     ← ONE document
  └── document_version v1  sha256:41ab…  ← ONE set of bytes, ONE file on disk

  document_link → correspondence K-IN-040   ATTACHMENT           pinned v1   is_origin=true
  document_link → requirement  R1           REQUIREMENT_EVIDENCE pinned v1
  document_link → correspondence K-OUT-050  ANNEX                pinned v1
```

Three placements, zero duplication. Duplicating the file instead would produce three hashes for one
artefact and make *"is this the same map the Utility Authority sent us?"* unanswerable — which is
precisely the question the department will be asked.

### 4.6 `requirement_evidence` and `document_link` — how they relate

**Settled here.** The frozen model defines both `requirement_evidence` (§2.12) and a
`document_link` arc to `requirement_id` with role `REQUIREMENT_EVIDENCE` (§2.15), and uses both in its
own examples. Their relationship was left undefined. The rule:

| | `requirement_evidence` | `document_link(requirement_id)` |
|---|---|---|
| Says | **"this proves the obligation was met"** | "show this file in the requirement's panel" |
| Authoritative for | fulfilment — the `FULFILLED` guard reads **this** | display grouping and ordering |
| Can point at | a **response**, a **document**, or an **internal record** | a document only |
| Retirement | `RETRACTED` with reason | `REMOVED` with reason |

**Invariant:** when a *document* is accepted as evidence, both rows are written **in one transaction**,
with the same `document_id` and the same `document_version_id` pin. A `document_link` with role
`REQUIREMENT_EVIDENCE` must have a matching `ACTIVE` `requirement_evidence` row; retracting the
evidence retires both. Evidence that is a **response** or an **internal record** produces an evidence
row and **no** document link — there is no file to place.

This is an **application invariant**, like L6; the database cannot express it cheaply, and stating it
here is what stops two records of the same fact from drifting apart.

### 4.7 Final result evidence

`PROJECT.md` §4 and §32.12 require the system to answer *"which documents prove the final result"*.

**Recommended and adopted: decision-critical evidence is always version-pinned.**

| Rule | |
|---|---|
| Every `FINAL_RESULT_DOCUMENT` link **must** set `document_version_id` | a decision that silently changes when someone uploads a new file is not a record |
| Pins are taken **at issue** (`DRAFT → ISSUED`, `WORKFLOW.md` F2) | while a result is `DRAFT`, links to home-case documents may float; issuing freezes them (§4.4). A link to a document from another case is pinned from creation |
| A superseding result (F3) gets **its own** pinned links | the superseded result keeps pointing at what it actually relied on |
| At least one `ACTIVE` `FINAL_RESULT_DOCUMENT` link is required to issue | guard D4, `WORKFLOW.md` §8.3 |

So *"which exact documents supported this final result?"* is one query returning exact
`document_version` rows with their hashes — the basis of an evidence manifest if one is ever required
(§13.4, §14).

### 4.8 One letter concerning several cases

The frozen ownership model is unchanged: **`correspondence.case_id` is single and owning**
(`DOMAIN_MODEL.md` §2.8, decision C-1). A letter answering requests in two cases is registered **once**
and produces **several `response` rows** — that is the frozen mechanism, and it needs nothing from this
document.

**The letter stays governed by its owning case** *(post-review — Domain Model amendment A-6)*. A
`response` in case B that references a letter filed in case A is case B's record of what the letter
meant for case B's request. It exposes, to case B's viewers, only the response's own facts — type,
outcome, conclusiveness, dates, summary, the requirements it raised. It does **not** expose the letter's
metadata (numbers, subject, parties), the letter or its attachments, the other responses it carries, or
case A's audit. A case-B viewer who cannot see case A sees no trace of the letter at all — not even a
"filed elsewhere" marker, which would itself disclose a dossier (`PERMISSIONS.md` §27.2).

For the *documents* the letter carried:

| Need | Mechanism |
|---|---|
| The file must appear in the second case's dossier | an **explicit, version-pinned** `document_link` from a case-B context — the requirement it evidences, the case-B response (role `SUPPORTING`), or the case with role `SUPPORTING` — created by a Chief as a disclosure decision (`PERMISSIONS.md` §16) |
| Bytes must not be duplicated | they are not: same `document`, same `document_version`, same object |
| The letter itself stays filed once | unchanged — the `correspondence` row belongs to its owning case; no `case ↔ correspondence` junction is introduced |

**Access-control consequence, deliberate:** a document from case A linked into case B becomes visible
to viewers of case B *through the B link* — **and only the version that link pins** (§10.1). A later
version added in case A does not reach case B. Linking a document into another case is therefore a
disclosure decision, not a filing convenience, and it is audited as such.

**What that disclosure includes.** The document-level fields — title, kind, issuer, reference —
accompany the disclosed version, because they identify what was disclosed; a later edit of the title in
the home case is visible wherever the document is linked, which the edit screen must show (§19,
principle 9). Nothing about **other** versions is disclosed: not their existence, count, numbers,
filenames, sizes, dates or uploaders.

---

## 5. Version model

### 5.1 Immutability

> **A `document_version`'s bytes never change.** Not by re-upload, not by correction, not by
> administrative action.

The only writable fields after insert are the status transition, its who/when/why triple, and
`integrity_checked_at`. Everything else — filename, hash, size, MIME, uploader, upload time — is
written once.

There is no operation anywhere in this system that replaces the bytes behind an existing version. A
corrected file is **version n+1**.

### 5.2 Version lifecycle

```
upload ──▶ ACTIVE ──a newer version is uploaded──▶ SUPERSEDED    (bytes retained)
              │  ▲                                       │
              │  └───────────────────────────────────────┘  reinstated: no ACTIVE version remains —
              │                                             the newest non-withdrawn version; same row
              └──uploaded in error / recalled───▶ WITHDRAWN      (bytes retained, reason recorded)
```

| Status | Meaning | Visible? | Downloadable? |
|---|---|---|---|
| `ACTIVE` | the current representation of this document | yes | yes |
| `SUPERSEDED` | replaced by a later version; still historically valid | yes, in the revision history of users it is exposed to (§10.1) | **yes** — to users a visible link exposes it to; pinned links resolve to it |
| `WITHDRAWN` | should not have been uploaded, or was recalled | yes, visually secondary | yes to users a visible link exposes it to, with the withdrawal reason shown |

Rules:

- `UNIQUE (document_id, version_no)`; `version_no` increments, never reuses.
- **At most one `ACTIVE` version per document** (frozen partial unique index).
- `supersedes_version_id` points from the new version at the version that was `ACTIVE` when it was
  uploaded (`NULL` if none was). It is a creation fact and is never changed afterwards.
- A new version is added only through a context in the document's home case (§4.4, rule 5).
- **Reinstatement** *(pre-schema amendment — `DOMAIN_MODEL.md` A-9, `DECISIONS.md` ADR-027)*. When a
  document has no `ACTIVE` version, its **newest version that is not `WITHDRAWN`** may be made current
  again: `SUPERSEDED → ACTIVE`.
  - It is the **existing row**: same `version_no`, bytes, hash, filename, uploader and upload time. No
    bytes are re-uploaded, no version is created, and `UNIQUE (document_id, content_hash)` is untouched.
  - It is an **explicit act with a mandatory reason**, normally performed in the same action as the
    withdrawal that made it necessary (one `correlation_id`). Withdrawing the `ACTIVE` version never
    reinstates anything by itself — sometimes no version should be current.
  - The withdrawn version keeps its own `withdrawn_*` who/when/why and stays in the history, exposed under
    §10.1 like any version.
  - To reach an older version, the newer one is reinstated and then withdrawn with its own reason — every
    step is on the record; there is no jump back past a version nobody has judged.
  - Upload of a new version, withdrawal of the `ACTIVE` version and reinstatement are **serialised per
    document** (lock or version-check the `document` row); the partial unique index is the backstop.
  - Pinned links are unaffected; floating links (home case only) present the reinstated version as
    current again.
- A withdrawn version's bytes are **never** removed (§12).
- A document whose every version is withdrawn is still a valid historical row. It is not deleted.

### 5.3 "Current version" means one thing

The `ACTIVE` version. Floating links (home case only) present it as current; pinned links ignore it.
There is no per-link notion of "current" and no separate current-version pointer to keep in sync — which
is also why restoring an earlier version is a status transition of that version (reinstatement, §5.2), not
an update of a pointer on `document`.

### 5.4 Four things that are not the same

The brief's §9 asks these to be distinguished, and the confusion between them is a real risk (§17, R2):

| Event | Entity affected | Mechanism | Means |
|---|---|---|---|
| **A version is superseded** | `document_version` | v2 uploaded, v1 → `SUPERSEDED` | *the file changed* — same business document, better bytes |
| **A document is superseded by another document** | `document` | a **new `document`** replaces it in the relevant context; the old document's links are `REMOVED` and new ones created; old document may go `WITHDRAWN` | *the artefact was replaced* — e.g. a wholly different map from a different authority |
| **A link is withdrawn** | `document_link` | `status = REMOVED` + reason | *the file was in the wrong place* — the file and its bytes are untouched and still correct elsewhere |
| **A correspondence is superseded** | `correspondence` | `supersedes_correspondence_id`, old → `SUPERSEDED` (`DOMAIN_MODEL.md` §5.5) | *a later official act replaced an earlier one* — the letter itself, not its files |

**There is no generic `deleted` flag anywhere**, and none may be added. Each of the four carries a
different reason vocabulary, a different authority level and a different meaning in reporting. A single
boolean would collapse all four into "gone", which is exactly the information loss this system exists to
prevent.

### 5.5 Filenames and titles

The original filename is **preserved and never trusted**. It is metadata; it is not identity, not a
storage key, and not a type declaration.

| Field | Where | What it is |
|---|---|---|
| `document.title` | document | the **display title** — the human name ("Utility communication map"). Editable, audited |
| `document_version.original_filename` | version | exactly the bytes-name supplied by the uploader or the authority. **Never edited**, stored verbatim |
| `document_version.mime_type` | version | the **detected** type, determined from content (§9.2) — not the client's declaration |
| extension | derived | taken from `original_filename` for display; **never** used to decide type or handling |
| safe download filename | derived at download time | §5.6 |

**No new columns are required.** The brief's "display_title" is the frozen `document.title`; "extension"
and "safe download filename" are derived.

The client-declared content type is not stored as a column. Where it differs from the detected type,
that mismatch is recorded in the upload `audit_event` payload — it is a fact about an event, not a
property of the file.

### 5.6 Hostile and awkward filenames

Every one of these occurs in practice and must not break anything:

| Input | Handling |
|---|---|
| Two files with the same name | **Normal and supported.** Names are not identity; the storage address is the hash. Two documents may both hold `scan.pdf` |
| Azerbaijani (`ə ğ ı ö ş ü ç`), Russian/Cyrillic | Stored as-is. Storage is `UTF-8`/`text`; names are never transliterated or normalised destructively for storage. A *normalised* form may be computed for search (§15) without touching the original |
| Very long names | Stored in full; truncated **for display only**, with the full name available |
| Names differing only by case | Distinct and both kept. The storage layer never relies on filename case, so a case-insensitive filesystem cannot collide them |
| RTL/bidi control characters (`U+202E` and friends) | **Stripped or escaped for display**, because they can make `report.exe` render as `report.fdp`. The original remains byte-exact in `original_filename` |
| Path separators, `..`, absolute paths, null bytes, control characters | **Never** used to build a path — the path is derived from the hash alone (§6.2), so these are inert. Sanitised for display and for the download filename |
| Misleading extension (`invoice.pdf.exe`, or a ZIP named `.docx`) | Type comes from content detection (§9.2). A mismatch is recorded and surfaced; the extension never decides handling |
| No extension at all | Fine. Type comes from detection |

**Safe download filename**, constructed at download time and never stored: a sanitised
`document.title` (or the original filename where the title is empty), stripped of control and bidi
characters and path separators, with the **canonical extension for the detected MIME type** appended,
transmitted with correct RFC-compliant header encoding for non-ASCII names. If detection and the
original extension disagree, the **detected** type wins and the delivered name says so.

---

## 6. Storage model

### 6.1 The split

| | |
|---|---|
| **PostgreSQL** | all metadata: documents, versions, links, hashes, sizes, MIME types, filenames, uploaders, timestamps, business relationships, audit |
| **Local filesystem** | the bytes, and nothing else |

No `bytea`, no large objects, no base64 columns — frozen (`DOMAIN_MODEL.md` §2.14). No external or
cloud storage of any kind (§20, `PROJECT.md` §2).

### 6.2 Content addressing

The stored object's path is derived from its SHA-256 and from nothing else:

```
<storage_volume_root>/sha256/ab/cd/<full_64_hex_hash>
                      ▲       ▲  ▲
                      │       │  └── next 2 hex chars — fan-out level 2
                      │       └───── first 2 hex chars — fan-out level 1
                      └───────────── algorithm prefix, so a future hash function
                                     never collides with existing objects
```

The two-level fan-out keeps directory sizes sane (256 × 256 buckets) on any ordinary filesystem — at
this system's scale most buckets will hold a single file, which is fine.

`document_version` records `content_hash`, `hash_algorithm`, `storage_volume_code` and
`stored_relative_path`. The path is *derivable*, and is stored anyway so that a future layout change
does not require recomputing history.

**Why the path must not depend on case number or filename:**

| If the path contained… | What breaks |
|---|---|
| the case number | a file legitimately shared by two cases (§4.8) would need two copies or a decision about which case "owns" the bytes; renumbering or merging a case would require moving files |
| the original filename | duplicate names collide; hostile names become path traversal; Cyrillic and Azerbaijani names become filesystem-encoding problems; a case-insensitive filesystem silently merges two files |
| any mutable business fact | correcting a business record would mean moving bytes — and a moved file is an opportunity to lose one |

The hash depends on nothing but the content. That is what makes the object store **append-only and
business-agnostic**: no business correction ever touches the filesystem.

### 6.3 Identical bytes and deduplication

Deduplication is not a feature added on top; it is an automatic consequence of content addressing.

| Consequence (all five are frozen, `DOMAIN_MODEL.md` §2.14) | |
|---|---|
| 1 | Identical bytes **resolve to one physical object**, however many rows reference it |
| 2 | **Logical `document_version` rows stay distinct** even when their bytes are identical — each keeps its own filename, uploader, upload time, status and history |
| 3 | Physical removal **must never** be triggered by a business action |
| 4 | Any cleanup must first prove **no `document_version`, active or historical**, references that hash |
| 5 | Business withdrawal and physical garbage collection are **separate concepts** with separate rules and authority |

**Two different documents may intentionally reference identical bytes.** The frozen uniqueness rule is
`UNIQUE (document_id, content_hash)` — scoped *per document*. It prevents the same document holding the
same bytes twice (which is what makes retries safe, §7.4); it does **not** prevent two documents sharing
bytes, which is legitimate and common: the same map filed as an attachment of an incoming letter and
separately registered as an annex issued by another authority.

### 6.4 Reference counting and reachability

With no `storage_object` table in the frozen model, reachability is a query:

> An object is **reachable** if any `document_version` row — in **any** status, `ACTIVE`,
> `SUPERSEDED` or `WITHDRAWN` — carries its `content_hash`.

Since V1 never deletes `document_version` rows, **every object that was ever stored remains reachable**,
and the only unreachable files are orphaned temporaries (§7.6). This is the conservative default the
system needs, and it means V1 requires no reference-counting machinery at all.

A dedicated `storage_object` table with a maintained count becomes worthwhile only if retention policy
ever permits physical destruction — `DOMAIN_MODEL.md` Appendix C, triggered by **OQ-9**.

### 6.5 Storage volumes

`storage_volume_code` names a configured root rather than embedding an absolute path, so the root can
move by configuration without an `UPDATE` across history. Volume layout, filesystem choice and mount
strategy are **ARCHITECTURE.md** decisions (§16.2).

### 6.6 What the object store is not

It is not a user-visible file share. Workstations reach documents **through the application**, never by
browsing the directory (`PROJECT.md` §23). A filesystem share exposing this directory would bypass every
access rule in §10 in a single step.

### 6.7 Relationship to backup

Metadata and bytes are two halves of one record, and a restore that mixes vintages produces one of two
outcomes:

| Mismatch | Severity | Why |
|---|---|---|
| **Object exists, no metadata row** | harmless | an invisible orphan; nothing references it; **retained and reported, never collected in V1** (§12.4) |
| **Metadata row exists, object missing** | **critical** | an evidence record pointing at nothing — the exact failure this system must not have |

**Correction (post-review).** v1 stated the rule *"back up the object store before the database"*. That
is **not sufficient**. Counterexample: the object copy finishes at 22:00; an upload commits at 22:01; the
database backup at 22:02 includes the new metadata row — and the retained objects do not include its
bytes. Ordering the copies does not make a recovery point consistent. The rule that does:

> **The recovery-point invariant.** Every object referenced by the selected database recovery point must
> exist, and verify against its hash, in the retained object set. A combined recovery point is **valid
> only when this has been checked** — never because of copy ordering alone.

The required object set of a database recovery point is every `content_hash` of every `document_version`
row in it, **in any status**. Two ways to meet the invariant in V1 — the backup product is not chosen here
(`DECISIONS.md` DEF-01):

| Strategy | How the invariant is met |
|---|---|
| **A — consistent database snapshot** | 1. take a transactionally consistent database snapshot; 2. derive its required object set from that snapshot; 3. copy each required object into the retained object set, or confirm it is already there, and verify it by hash; 4. mark the combined recovery point **valid only after both** the snapshot and every required object verify |
| **B — physical base backup + continuous WAL archive** | a recovery target inside the WAL stream is **published as a recovery point only up to an object-complete boundary** — once every object referenced as of that target is present and verified in the retained object set. Until the object copy catches up, the newest published recovery point lags behind the newest WAL |

**A logical dump is a recovery point on its own. It is never a base for WAL replay** — only a physical
base backup is.

**Why "snapshot first, objects second" works — the reverse of the v1 rule.** Objects are immutable, never
deleted in V1 (§12.4), and durably stored *before* their metadata row commits (§7.2). So every object a
snapshot references already existed when the snapshot was taken, and an object copy pass that **begins
after the snapshot completes** will find all of them. The verification in step 4 is still required — the
ordering is what makes it pass, not a substitute for it. Extra objects in the retained set are harmless
orphans, and the retained object set is never pruned in V1.

**Restoring.** Choose a valid recovery point; restore the database to it; restore at least its required
object set from the retained objects; then run the full integrity sweep (§11) before the system is
declared usable. Full backup architecture, media, rotation and the offline copy required by `PROJECT.md`
§20 belong to **ARCHITECTURE.md** (§15.4) and **SECURITY.md** (§14.3); this section fixes only the
invariant they must honour.

---

## 7. Upload protocol

### 7.1 The sequence

From `PROJECT.md` §22, with the transaction boundary made explicit:

```
                                                        ┌─ outside the DB transaction ─┐
 1. stream bytes to a temporary file on the SAME volume  │                              │
 2. compute SHA-256 while streaming                      │                              │
 3. verify completion — declared vs received size, EOF   │                              │
 4. detect MIME type from content (§9.2)                 │                              │
 5. fsync the temporary file                             │                              │
 6. atomic rename into sha256/ab/cd/<hash>               │                              │
 7. fsync the containing directory                       └──────────────────────────────┘
                                                        ┌─ one DB transaction ─────────┐
 8. create or reuse `document`                           │                              │
 9. create `document_version` (or converge — §7.4)       │                              │
10. create `document_link` in the business context,      │                              │
    pinned as §4.4 requires                              │                              │
11. write `audit_event` (UPLOAD + LINK)                  │                              │
12. COMMIT                                               └──────────────────────────────┘
```

### 7.2 Why the bytes land first

The ordering is deliberate and is the same asymmetry as §6.7:

- **Bytes first, metadata second** → a failure between them leaves an unreferenced object: invisible,
  harmless, and retained — V1 performs no physical garbage collection (§12.4).
- **Metadata first, bytes second** → a failure between them leaves a database row pointing at nothing:
  a broken evidence record.

Never the second. The object store tolerates extra files; the database does not tolerate missing ones.

### 7.3 Why the rename is atomic and same-volume

The temporary file is written on the **same filesystem volume** as its destination, so step 6 is a
`rename(2)` within one filesystem: atomic, and never a partially-visible file. A cross-device move is a
copy — which can be interrupted halfway and leave a truncated object at a path whose name asserts a hash
the bytes do not have. That object would pass a "file exists" check and fail a re-hash, which is the
worst combination.

If the object already exists at the destination path, the rename is **skipped**, the temporary file is
discarded, and the existing object is used. Identical content is identical content.

### 7.4 Idempotency and convergence

Two different guarantees, kept apart *(post-review correction)*:

- **Version convergence** is keyed on **`(document_id, content_hash)`** — the frozen unique constraint
  (`DOMAIN_MODEL.md` §2.14). A retry onto an existing document never produces a phantom version.
- **Command idempotency** — a retry after an *uncertain* commit not creating a second logical document, a
  duplicate link or duplicate audit events — is **not** provided by that constraint. A retried *first*
  upload has no `document_id` to converge on. It requires the durable **operation identifier** of
  `ARCHITECTURE.md` §12.6: generated once when the upload is prepared, recorded in the same transaction as
  the upload's effects, and answered from that record on any retry.

| Scenario | What happens |
|---|---|
| **Connection drops mid-upload** | the temporary file is incomplete and is never renamed (step 3 fails). Nothing reaches the database. The orphan temp is collected (§7.6) |
| **Browser retries after a timeout** | the retry carries the **same operation identifier**. If the first attempt committed, the recorded operation returns its result and nothing is re-executed — no second document, link or audit trail. If it did not commit, the attempt runs normally: the bytes hash to the same value, step 6 finds the object present and skips, and for an existing document step 9 **converges on** the `(document_id, content_hash)` row rather than creating version 2 |
| **User uploads the same file twice by accident, to the same document** | same as above — one version, no phantom v2 |
| **User re-uploads v1's bytes to "restore" v1 after an erroneous v2** | not a restoration: step 9 converges on the existing v1 row, and convergence **never changes a version's status**. The user is told the file already exists as v1 and is offered **reinstatement** (§5.2), which is how v1 becomes current again |
| **User uploads the same file to a *different* document, intentionally** | a new `document_version` under that document, **same object on disk**, same hash. Permitted and normal (§6.3) |
| **User uploads the same file to a second context of the same document** | no new version at all — a **new `document_link`**, because the need was placement, not content (§4.5) |
| **Two users upload the same bytes simultaneously** | both hash identically; the rename is idempotent; the unique constraint serialises the metadata, and the loser converges on the winner's row |
| **Bytes written, transaction fails at step 11** | the object remains, unreferenced, invisible and retained (§12.4); the user is told the upload failed and retries; nothing was committed, so no operation record exists and the retry runs normally. No partial business record is ever visible |

**A retry must never produce a phantom version.** A document showing "v1, v2, v3" where all three have
the same hash is a data-quality failure that misleads anyone reading the revision history later.

### 7.5 A genuinely new version

A new version is created only when the bytes **differ** — a different hash under the same
`document_id` — and only through a context in the document's home case (§4.4). Then: `version_no + 1`,
`supersedes_version_id` → the version `ACTIVE` at that moment (if any), that version → `SUPERSEDED`, both
sets of bytes retained, pinned links still resolving to the old one.

### 7.6 Orphan temporary files

| Aspect | Rule |
|---|---|
| Location | a dedicated temp directory **on the storage volume**, never the system temp |
| Naming | random and opaque — never derived from the user's filename |
| Lifetime | removed on successful rename; otherwise collected by age |
| Collection | a periodic sweep removing temporaries older than a configured threshold, comfortably longer than the largest plausible upload |
| Safety | this is **the only file deletion V1 performs**, and it can only ever touch files in the temp directory that were never renamed into the content store (§12.4) |

### 7.7 What the user never sees

No hash, no path, no volume code, no version number in a URL that grants access. The upload returns a
business result — "map.pdf attached to letter №7/119" — and every later retrieval is by document
identity plus context, authorised on each request (§10).

---

## 8. Correction and withdrawal

Real users upload the wrong thing. If correction is hard, they will work around it, and the workaround
will be worse than the mistake. If correction destroys history, the system stops being evidence.

### 8.1 The governing rule

The same rule as `PERMISSIONS.md` §24.1, applied to documents:

> A Worker may correct **their own** document record **while nothing else depends on it**.
> Once something depends on it, or it is not theirs, a **Chief** corrects it.

"Depends on it" for documents means: the version is cited as `requirement_evidence`, or pinned by an
issued `final_result`, or a response has already been registered against the correspondence carrying it.

*(This test previously read "or the correspondence carrying it has been dispatched". Official
correspondence is sent and received in a separate external system and only then recorded here
(`PROJECT.md` §1.1), so an outgoing letter is **always** already dispatched by the time our copy exists
— the test would have been permanently true and would have blocked a Worker from fixing their own
freshly-entered record. Correcting our copy has no external effect; the official record is untouched.)*

**No correction in this section requires database administration.** Every one is an ordinary
application action.

### 8.2 The seven cases

| # | Mistake | Metadata editable? | Link action | New version? | What remains in audit | Minimum role |
|---|---|---|---|---|---|---|
| 1 | **Correct file, wrong Correspondence** | n/a | old link → `REMOVED` + reason; new `document_link` to the right correspondence, pinned to the same version | **no** — bytes are correct | both links, both reasons, both actors; the removed link stays queryable as history and **grants nothing** (§10.2) | Worker (own, no dependents) · **Chief** otherwise |
| 2 | **Correct file, wrong Case context** | n/a | as #1, but the new link is in another case's context; treated as a **disclosure decision** (§4.8). The wrong-case link is removed — it is never kept `ACTIVE` to preserve access, and once removed it stops granting access. If the moved link is the origin, the home case moves with it (§4.4) | no | as #1, plus the cross-case link is separately visible | **Chief** |
| 3 | **Wrong file entirely** | n/a | link → `REMOVED`; the correct file is uploaded and linked | the wrong file's version → `WITHDRAWN` with reason; **bytes retained**. If the wrong file was uploaded as a new version of a document whose previous version is the correct one, that previous version is **reinstated** (§5.2) — not re-uploaded | the withdrawn version, its reason, its uploader; the reinstatement with its reason | Worker (own, no dependents) · Chief otherwise — reinstatement follows the authority of the withdrawal it accompanies |
| 4 | **Duplicate upload** | n/a | if it converged (§7.4) there is nothing to fix. If a genuine duplicate *document* was created, its link is `REMOVED` and the document → `WITHDRAWN`, reason `DUPLICATE` | no | the duplicate row and the reason | Worker (own) · Chief otherwise |
| 5 | **Wrong display title** | **yes** — `document.title` is editable | unchanged | no | `before_state`/`after_state` of the title change | Worker · Chief |
| 6 | **Attachment classified as main letter** | n/a — role lives on the link | the `PRIMARY_LETTER` link → `REMOVED`, a new `ATTACHMENT` link created; the true letter gets the `PRIMARY_LETTER` link | no | both role changes | Worker (own, no dependents) · **Chief** otherwise |
| 7 | **Revised official letter uploaded as a new version instead of a new correspondence** | — | **the correction is at the workflow level, not the file level** — see §8.3 | — | — | **Chief** |

### 8.3 Case 7 in detail — the one that actually matters

A revised letter arrives from an authority. A clerk uploads it as **version 2** of the existing letter
document. That is wrong, and quietly so: the original letter still shows its pinned v1 (§4.4), but the
*document's* current version is now a different official act, the revised letter has no
`correspondence` row, no letter number, no date, and no `response` — and the official act it represents
is invisible to the workflow.

Correcting it:

1. The mistaken **v2** → `WITHDRAWN`, reason "revised letter — registered separately as correspondence
   №…". Its bytes are retained. In the same action, **v1 is reinstated** as the current version of the
   original letter document (`SUPERSEDED → ACTIVE`, §5.2): the same v1 row with its original upload
   facts — nothing is re-uploaded, and v2 stays visible in the history with its withdrawal reason.
2. A **new `correspondence`** is registered for the revised letter, with its own letter number, letter
   date and `received_at`, and `supersedes_correspondence_id` → the original letter where appropriate.
3. The revised file is uploaded as **v1 of a new `document`** and linked as that correspondence's
   `PRIMARY_LETTER`.
4. A **new `response`** is registered against the same request, with a `response_supersession` edge →
   the earlier response (`WORKFLOW.md` §4.3).
5. Any requirement raised by the earlier response is surfaced for review — never cascaded
   (`WORKFLOW.md` §6.4).

The lesson generalises: **a new official act is never a new version of an old file.** Versions model
*the same document re-issued*; correspondence models *acts*. The UI should make this hard to get wrong
(§19).

### 8.4 What correction never means

- Never a hard delete — no ordinary user can physically remove anything (`PROJECT.md` §29.6).
- Never an edit to bytes, a hash, a filename, an uploader or an upload time.
- Never removal of a link row — links are `REMOVED` by status, keeping *"this map used to be filed
  under that letter"* answerable.
- Never silent. Every action here writes an `audit_event` with `before_state` and `after_state`.
- Never a change to what an **issued** final result pinned. A pinned version is immune to every
  correction in this section; if the decision relied on the wrong file, the remedy is at the decision
  level — supersede the final result (`WORKFLOW.md` §8.6), not the evidence.

---

## 9. File safety

Uploaded files are **untrusted input** (`PROJECT.md` §23): they arrive from external authorities, from
USB sticks, from scanners, and occasionally from a compromised counterparty.

### 9.1 Five handling classes

A policy framework, **not a production allowlist** — that list is a business decision (§16.1, OQ-D3).

| Class | Meaning | Consequence |
|---|---|---|
| **A — Store** | accepted into the content store | metadata recorded, bytes retained |
| **B — Preview** | may be rendered by a future preview pipeline | only via §9.5; never in V1 |
| **C — Open inline** | may be served with a content type the browser renders in the application origin | **empty set in V1** — see §9.4 |
| **D — Download-only / quarantined** | stored and retrievable, but always as an attachment download, never rendered | the V1 default for everything |
| **E — Forbidden** | rejected at upload | requires a business decision to populate |

**V1 position: everything accepted is class D.** Authorised users download files and open them in their
own workstation applications (`PROJECT.md` §24). This is the smallest safe design that meets the
requirement, and it needs no scanning, sandboxing or conversion to be correct.

**The accepted families, confirmed 2026-09-18** (DECISIONS.md **ADR-042**, closing OQ-D3, OQ-D4, OQ-D5 and
OB-5). The department must be able to retain:

| Family | Notes |
|---|---|
| **PDF** | the common official format |
| **KMZ** | geographic data; a ZIP container, stored as opaque bytes and never extracted (§9.3) |
| **Word**, **Excel** | including **macro-enabled** workbooks and documents: authorities send them, so they are **accepted**, stored, visibly marked and download-only. OQ-D4 is answered "accept" |
| **AutoCAD** | `.dwg` / `.dxf`, stored as opaque bytes, never parsed. OQ-D5 is answered "yes, required" |
| **ArchiCAD** | required as a family. **The exact extension and MIME mapping is not yet confirmed** and is deliberately not invented here — until the department confirms it, an ArchiCAD file is stored as opaque bytes like any other |

Two rules come with that list, and they are the reason it is short:

1. **Class E stays empty.** Nothing is refused outright, and in particular **nothing is refused because the
   application cannot preview it** — storage and preview are separate concerns (§9.4, §9.5).
2. **Maximum 500 MB per file** (ADR-042, closing OQ-A5 / OB-7), enforced at the reverse proxy and in the
   application. At that size the upload protocol of §7 must **stream** to disk while hashing; a file is never
   buffered in memory (`ARCHITECTURE.md` §12.3).

### 9.2 Type detection

| Rule | |
|---|---|
| Type is determined from **content**, never from the extension or the client's declared type | an extension is a user-supplied string |
| Detection is **bounded signature matching** *(post-review clarification)*: a fixed, limited number of leading bytes compared against a table of known file signatures. It does not decompress, traverse internal structure, follow references, render or execute anything, and its cost does not depend on what the file contains (`SECURITY.md` §10.1) | detection is not parsing — parsing untrusted formats is exactly what §9 avoids |
| Where a signature identifies only a **container family** (OOXML and ODF documents are ZIP containers), the detected type is that family; a declared type or extension may refine it only when consistent with that family | a `.docx` is recognisable as a ZIP-family document without opening the ZIP |
| The detected type is stored in `document_version.mime_type` | frozen column |
| A mismatch between declared, extension and detected type is **recorded in the upload audit event** and surfaced in the UI | it is weak evidence of a mistake, and occasionally of an attack |
| A mismatch alone does **not** reject the upload | scanners and authorities produce mislabelled files constantly; rejecting would block legitimate official documents |

### 9.3 Format notes

| Format | Risk | V1 handling |
|---|---|---|
| **PDF** | may contain JavaScript, embedded files, external references | store; download-only; never rendered in the app origin |
| **DOCX / XLSX** | OOXML with external relationships | store; download-only |
| **Macro-enabled Office** (`.docm`, `.xlsm`) | executable macros | store **or** forbid — **business decision, OQ-D4.** If stored, always download-only and visibly marked |
| **Images** (PNG, JPEG, TIFF) | decoder vulnerabilities; huge dimensions | store; download-only; any future preview re-encodes rather than passes through |
| **DWG / DXF** | CAD; DXF is text, DWG is binary and parser-heavy | store as opaque bytes; no parsing. **Whether they are required at all is OQ-D5** |
| **ZIP and archives** | zip bombs, path traversal on extraction, hidden content | store as opaque bytes. **Never extracted or inspected by the application** — an archive is one file |
| **HTML** | scripts, forms, credential phishing in the app origin | store; **never** served inline; always download with a neutral content type |
| **SVG** | is an executable document — scripts, external fetches | store; **never** served inline. The most commonly underestimated format here |
| **Executables / scripts** (`.exe`, `.js`, `.bat`, `.ps1`, `.sh`) | obvious | **candidates for class E.** Business decision (OQ-D3); if stored, download-only with a prominent warning |

### 9.4 Why class C is empty in V1

Serving any user-supplied file inline from the application's own origin gives that file the
application's session context. One SVG or HTML file is enough to read another user's session.

Therefore, for every download in V1:

- served as an **attachment** with a safe, non-renderable content type;
- delivered from a **path that carries no authority** — authorisation is checked per request (§10);
- served with headers that prevent content-type sniffing and inline rendering;
- ideally from a **separate origin** dedicated to file delivery, so that even a rendering mistake cannot
  reach application session state. (Whether a second origin is practical on this LAN is an
  **ARCHITECTURE.md** decision, §16.2.)

### 9.5 Future preview architecture

Preview is **not required in V1** (`PROJECT.md` §24) and must not become a prerequisite. If it is ever
built, four conceptual constraints hold:

| # | Constraint |
|---|---|
| 1 | **No active content executes under the application origin.** Previews are rendered as inert images or as plain text, in a sandboxed context with no access to application session state |
| 2 | **Conversion runs unprivileged and isolated** — a separate, low-privilege worker with no database credentials, no write access to the content store, and no network; it receives bytes and returns bytes |
| 3 | **Preview artefacts are derivative cache, not evidence.** They are regenerable, excluded from evidence manifests and exports (§14), and may be deleted freely — the only thing in this system that may |
| 4 | **The original file remains authoritative** for every business purpose, always |

No software package is selected here. That is `DECISIONS.md` territory when the time comes.

### 9.6 Malware scanning

Not designed here — it belongs to `SECURITY.md`. Two constraints this model imposes on whatever is
chosen:

- Scanning must be **offline/on-premise**; no file, hash or metadata leaves the LAN
  (`PROJECT.md` §2, §25).
- A scan verdict is a **security fact, not a business state.** It does not change
  `document_version.status`, because the business record has not changed — the storage object has been
  judged dangerous. Whether a positive verdict should also be visible as a business state is
  **OQ-D6** (§16.1).

---

## 10. Access control

Document authorization is **derived from business context**, never from the document itself. There is no
such thing as a document permission in this system.

### 10.1 The rule

**Authorization applies to the requested `document_version`, not only to its document**
*(post-review — v1 authorised by document, so a user who could see v1 through one link could request
any other version of the same document)*:

```
exposes(L, v) =
        L.status = ACTIVE
    AND L.document_id = v.document_id
    AND (    L.document_version_id = v.id          # pinned: exactly that version
          OR L.document_version_id IS NULL )       # floating: home case only (§4.4) — the
                                                   #   document's versions, all of which entered there

may_access_version(user, v, via_link L) =
        user.status == ACTIVE
    AND exposes(L, v)
    AND can(user, VIEW, context_of(L))             # PERMISSIONS.md §14.2

visible_versions(user, d) = { v of d : EXISTS L such that may_access_version(user, v, L) }
may_see_document(user, d) = visible_versions(user, d) is not empty
```

A download is always requested **through a context the user can see, for a version that context's link
exposes**. The context is not decoration: it is what the check is performed against, and it is what the
audit event records.

**Everything version-level obeys the same rule** — not only downloads:

| Surface | Rule |
|---|---|
| Version history / revision list | lists only `visible_versions` — no count, numbering, gap or "newer version exists" indicator for anything else |
| Version metadata | `original_filename`, size, MIME type, `document_date`, uploader, upload time, status, withdrawal reason and integrity data only for exposed versions |
| `supersedes_version_id` | never followed to, or rendered for, a version that is not exposed |
| Document-level fields | title, kind, issuer, reference accompany any exposed version (§4.8) |
| Search | version-level facets match only exposed versions (§15.2) |
| Exports and print | contain only exposed versions (§14.3) |
| Download and print URLs | name the link and the version; the server re-evaluates `may_access_version` on every request |
| Audit views | an `UPLOAD` / `DOWNLOAD` event is shown under its own `case_id` scope and only for an exposed version |

### 10.2 A document ID is not a capability

> **A user cannot reach a restricted case's document by guessing or reusing an identifier.**

| Attack | Why it fails |
|---|---|
| Guessing a `document_id` | UUIDv7 is not enumerable in practice, **and** identity is not authority — the check in §10.1 still runs |
| Reusing a download URL seen elsewhere | every download re-authorises; a URL carries no grant and no signed token substitutes for the check |
| Requesting a document by hash | the hash is never an API input. It is storage-internal |
| Constructing a filesystem path | workstations have no access to the store (§6.6) |
| Requesting a version directly, skipping the document | the check is per version: the request must name a visible link that **exposes that version** (§10.1) |
| Seeing v1 through one link and requesting v2 of the same document — e.g. a version added in a restricted home case | refused: seeing a document's identity through one version grants nothing about its other versions. Only a visible link that exposes v2 authorises v2 |
| Relying on a link that has since been removed — including a wrong-case link | only `ACTIVE` links expose anything; a `REMOVED` link is history, not a grant. A wrong placement is corrected by removing it, not by leaving it active (§8.2) |
| Having *previously* had access via a since-revoked grant | authorisation is evaluated **now** (`PERMISSIONS.md` §27.3), not at session start |

### 10.3 Documents with several links

A document linked to several contexts (§4.5, §4.8) needs a rule that is safe in both directions:

| Question | Answer | Reason |
|---|---|---|
| May the user see the document at all? | if **any** `ACTIVE` link's context is visible to them | if they can already see the map as an attachment of an ordinary letter, a second link into a restricted case cannot retroactively hide it from them |
| **Which versions may they see?** | **only** versions exposed by an `ACTIVE` link whose context they may view (§10.1) — the union is taken per version, never per document | a pinned link into an ordinary case discloses its one version; it never discloses what the restricted home case adds later |
| Which links does the user see listed? | **only** those whose contexts they may view | otherwise the link list would reveal that a restricted case exists, roughly when, and what it concerns |
| Which context is the download authorised against? | the one the user requested it through | prevents "I can see it via context X" from silently licensing access through context Y |
| Does a restricted link restrict the document elsewhere? | **no** | restriction is a property of the case, not of bytes. A file that is legitimately public in case A does not become secret because it was also cited in restricted case B |

The consequence is stated in §4.8 and is worth repeating: **linking a document into another case is a
disclosure decision.** It is audited, and it is a Chief action (§8.2, case 2).

### 10.4 Aggregates, search and export

Documents obey `PERMISSIONS.md` §27.2 without exception: a restricted case's documents are **absent**
from search results, counts, dashboards and exports for users who cannot see the case. "3 documents
hidden" reveals that the documents exist. The same holds per version: a version not exposed to the user
is absent from every list, count, search match and export (§10.1).

### 10.5 TechAdmin

Per `PERMISSIONS.md` §18 and §19.3: the TechAdmin application role grants **no** access to business
document content — not to metadata screens, not to downloads, restricted or ordinary.

And the honest part, unchanged from `PERMISSIONS.md` §19.3:

> The OS administrator can read the content store directly from the filesystem, and the database
> administrator can read every metadata row. On a 13-person deployment this is very likely the same
> person. **No application-level rule prevents this**, and nothing in this document should be read as
> claiming otherwise.

What content addressing *does* contribute is modest but real: the store reveals nothing by its structure.
Paths carry no case number, no filename, no title and no organization, so browsing the directory yields
opaque hashes rather than a browsable dossier. That is obscurity, not access control, and it is worth
exactly what it costs — nothing.

The controls that matter are infrastructural and detective: no filesystem share (§6.6), backups not all
writable from the primary machine, and auditing of administrative access. They belong to
`SECURITY.md` and `ARCHITECTURE.md`.

---

## 11. Integrity verification

Because the bytes live outside the database, the two halves can drift. Drift must be **detected**, not
assumed away.

### 11.1 The five findings

| # | Finding | Detection | Classification | Automatic action | Needs an administrator |
|---|---|---|---|---|---|
| I1 | **Metadata row points at a missing object** | stat the path for each `document_version` | **CRITICAL** | none — nothing is modified | **Yes, immediately.** An evidence record with no evidence. Restore from backup |
| I2 | **Object exists with no metadata row** | enumerate the store, compare to `content_hash` values | **INFORMATIONAL** | none — **reported and retained**; V1 performs no physical garbage collection (§12.4) | No. Expected after a failed upload (§7.2) |
| I3 | **Stored size differs from `byte_size`** | stat vs column | **CRITICAL** | none | **Yes.** The object is not what the record says |
| I4 | **Re-hash differs from `content_hash`** | full re-read | **CRITICAL** — the most serious of all | none | **Yes.** Either corruption or tampering; content addressing means the path itself asserts the hash, so a mismatch is never benign |
| I5 | **Object unreadable** (permissions, I/O error, bad media) | read attempt | **WARNING**, escalating to **CRITICAL** if it persists | retry once | Yes if it persists — usually hardware |

### 11.2 What automatic means here

**Nothing is repaired, quarantined, re-hashed-into-place or deleted automatically.** The sweep is
read-only against both halves. It records findings and raises an alert; every remedy is an
administrator's decision.

This is deliberate: an automatic "fix" for I4 would mean either overwriting the metadata to match
corrupted bytes, or deleting the object — and both destroy the evidence that something went wrong.

### 11.3 Cadence

| Check | Scope | Frequency |
|---|---|---|
| Existence + size (I1, I3) | every `document_version` | cheap; frequently, e.g. nightly |
| Full re-hash (I4) | rolling subset, oldest `integrity_checked_at` first | expensive; continuous background sweep sized so the whole store is covered within a configured period |
| Orphan scan (I2) | the whole store | occasionally; informational only |
| Full verification | everything | **after every restore**, before the system is declared usable (§6.7) |

`document_version.integrity_checked_at` records the last successful verification — the frozen column
exists for exactly this.

### 11.4 On a critical finding

1. Record the finding (§13.2) and alert the administrator.
2. **Do not modify any business record.** The `document_version` stays `ACTIVE`; its business meaning
   has not changed, only its storage is broken.
3. Downloads of that version fail with an explicit integrity error — never a silent 404, and never
   corrupted bytes served as if they were fine.
4. The administrator restores the object from backup; because content is addressed by hash, a restored
   object is **verifiable**: it is either byte-identical and correct, or it is not the object.

Point 4 is the quiet payoff of content addressing. In a path-based store, "is this restored file the
right one?" is unanswerable. Here it is arithmetic.

---

## 12. Retention

### 12.1 Retention is indefinite *(decided 2026-09-18)*

The product owner has answered OB-8 (DECISIONS.md **ADR-043**, closing OQ-D1, OQ-D2 and Domain Model OQ-9):

> **Retention is indefinite. Files are never physically deleted, and there is no business hard delete and no
> physical purge — ever.**

> **Nothing is ever physically deleted except orphaned temporary files.**

That was the conservative V1 default; it is now the decision. Withdrawn, superseded and incorrect files stay
retained with their reasons, and the rest of this section stands unchanged — with two consequences worth
stating: storage grows monotonically, so **backup capacity is a standing operational requirement**
(`SECURITY.md` §14), and the integrity sweep (§11) is the control that must keep working for years.

### 12.2 Three separable concepts

Kept distinct so a future retention policy can be added without redesign:

| Concept | What it is | Exists in V1 | Reversible |
|---|---|---|---|
| **Business withdrawal** | a version or link is no longer in use: `WITHDRAWN`, `REMOVED`, `SUPERSEDED` | **yes** | yes — the record is intact; a new link or version restores use |
| **Logical archival** | a closed case's documents are marked cold; retrievable but excluded from default search and working views | **no** — not needed at this scale; the model permits adding it | yes |
| **Physical purge eligibility** | the object may be destroyed | **no** — nothing is ever eligible in V1 | **never** |

The essential point, frozen as consequence 5 in `DOMAIN_MODEL.md` §2.14: **business withdrawal never
implies physical purge.** They are separate decisions, with separate authority, and V1 implements only
the first.

### 12.3 What a future purge would require

Documented so that whoever implements it does not have to rediscover the hazards:

| # | Requirement |
|---|---|
| 1 | A stated retention policy with a legal basis, per document class (**OQ-D1**) |
| 2 | **Reference safety**: no object removed while **any** `document_version` in any status references its hash (§6.4) — the `storage_object` table with a maintained reference count becomes worthwhile here |
| 3 | **Legal hold** overriding retention for cases under dispute or audit |
| 4 | **Metadata survives purge**: the `document_version` row, its hash, size, filename and history remain. What is destroyed is bytes, never the record that they existed |
| 5 | Purge is an **administrative operation with business authorisation**, audited per object, never a user action and never a side effect of a business state change |
| 6 | Backups must be considered — an object purged from primary storage still exists in backups until those rotate out |

### 12.4 The only deletion V1 performs

Orphaned temporary files (§7.6): files in the temp directory, older than a configured threshold, never
renamed into the content store. They are by construction referenced by nothing.

**Objects already in the content store are never deleted in V1** — not when a version is withdrawn, not
when a document is voided, not when a case is cancelled, not when a link is removed.

**Physical garbage collection of the content store is disabled in V1** *(post-review, stated explicitly)*,
including for objects no metadata row references. Such orphans are **reported** (I2, §11.1) and
**retained**: at scan time an "orphan" may belong to an upload whose metadata is about to commit, and it
may be referenced by a database recovery point held in backup (§6.7) — deleting it could turn a valid
restore into a broken evidence record.

---

## 13. Audit requirements

Every action below writes an `audit_event` (`DOMAIN_MODEL.md` §2.18) using the **frozen** `action_code`
vocabulary. No enum is extended.

### 13.1 Required events

| Action | `action_code` | `entity_type` | Must also carry |
|---|---|---|---|
| Document created | `CREATE` | `document` | title, kind, issuing organization |
| **Version uploaded** | `UPLOAD` | `document_version` | **`document_hash`**, `original_filename`, `byte_size`, detected MIME, any declared/detected mismatch (§9.2) |
| Version superseded | `STATE_CHANGE` | `document_version` | `supersedes_version_id` on the new row |
| Version withdrawn | `WITHDRAW` | `document_version` | reason, actor |
| **Version reinstated** | `STATE_CHANGE` | `document_version` | **reason — mandatory**; the withdrawn version it follows; the withdrawal's `correlation_id` when done in the same action. No `UPLOAD` event — nothing was uploaded |
| Link created | `LINK` | `document_link` | role, target entity and its type, the pinned version (or floating, home case only — §4.4), `is_origin` |
| Link withdrawn | `UNLINK` | `document_link` | reason, actor |
| **Context corrected** | `UNLINK` + `LINK` under **one `correlation_id`** | `document_link` | both reasons — this is what makes a correction readable as one act rather than two unrelated events |
| Document metadata changed | `UPDATE` | `document` | `before_state` / `after_state` |
| Display title changed | `UPDATE` | `document` | old and new title (a `document` metadata change; called out because it is the most frequent) |
| Document withdrawn / voided | `WITHDRAW` / `VOID` | `document` | reason |
| **Download** | `DOWNLOAD` | `document_version` | `document_hash`, **the link/context it was requested through** (§10.1) |
| Bulk download or case export | `EXPORT` | `case` | scope, item count, hashes of what was exported (§14) |
| Print | `PRINT` | `document_version` | context |
| Denied attempt | `PERMISSION_DENIED` | the attempted object | attempted action and context |
| Evidence recorded / retracted | `LINK` / `UNLINK` | `requirement_evidence` | what it points at, pinned version (§4.6) |

### 13.2 Integrity findings

Findings are **job events**: `actor_kind = JOB` with no `actor_user_id`, because no person initiated
the sweep (`DOMAIN_MODEL.md` §2.18, amendment A-8), recorded as `UPDATE` on `document_version` (the
check updates `integrity_checked_at`), with the finding class and evidence in `after_state`, plus an
operational alert.

**Documenting the need, as required:** a dedicated `INTEGRITY_CHECK` or `INTEGRITY_FAILURE` action code
would make these findings a first-class, exact-match-queryable event class. That would be a **vocabulary
refinement**, explicitly permitted by `DOMAIN_MODEL.md` §12.6 — but it is **not required**: the design
above works with the frozen vocabulary, and this document does not assume the extension. Raised as
**OQ-D8** (§16.2) so it is a conscious choice at schema time.

### 13.3 Download auditing is not optional

Downloads are where confidential content actually leaves the application. A `DOWNLOAD` event without the
context it was requested through cannot answer *"who read the restricted case's documents?"* — which is
the question that will be asked after an incident.

### 13.4 Hashes in audit

`audit_event.document_hash` carries the version's `content_hash` on upload and download events (frozen
column). That single field lets an auditor tie an event to exact bytes years later, independently of
whether the file is still current.

Cryptographic chaining of audit events is **out of scope here** (`DOMAIN_MODEL.md` §2.18) and belongs to
`SECURITY.md`. Nothing in this document depends on it.

---

## 14. Case export and future portability

### 14.1 The goal

> A case must remain intelligible **after this application no longer exists**.

Not an integration feature. An insurance policy against the system being replaced, defunded, or simply
outliving its maintainers — which, for a municipal system with a 13-person department, is the expected
case rather than the pessimistic one.

### 14.2 Minimum future-proof structure

Conceptual; **not implemented in V1**:

```
case-2026-114/
  manifest.json              ← the machine-readable truth: every entity, relationship,
                               hash, timestamp, actor and state, in one self-describing file
  README.txt                 ← plain text: what this export is, when it was made,
                               how the folders relate, how to verify hashes
  summary.pdf                ← human-readable dossier: case, parties, chronology,
                               branches, requirements, decision  (renderable, not authoritative)
  files/
    <sha256>                 ← the original bytes, named by hash, exactly as stored
  index.csv                  ← one row per file: hash, original filename, title, kind,
                               which correspondence/requirement/result it belonged to, role,
                               version, uploader, upload time
```

### 14.3 Principles

| # | Principle |
|---|---|
| 1 | **Original bytes, unmodified.** No conversion, no re-encoding, no flattening. The original file is the evidence |
| 2 | **Self-describing.** `manifest.json` carries its own schema description; `README.txt` explains the layout in prose. An export that needs this repository to be understood has failed |
| 3 | **Verifiable.** Every file is named by and listed with its SHA-256, so any future reader can check the export is intact with a standard tool |
| 4 | **Relationships explicit.** "Which document belonged to which official letter, in what role, in what version" is answerable **from the export alone** — that is the §14.4 test |
| 5 | **Text where possible.** JSON and CSV over any proprietary format; UTF-8 throughout |
| 6 | **Authorised and audited.** An export is a bulk disclosure: `EXPORT` audit event, and it respects §10 — a user who cannot see the case cannot export it, and an export contains only the versions exposed to the exporting user through that case's links (§10.1): a document shared in from another case is exported as its pinned version only; a letter filed in another case is not exported with a response that references it (§4.8) |

### 14.4 The test it must pass

Given only the export folder and no software from this project, a competent person must be able to
answer: *which files arrived with letter №7/119 of 20.03.2026, in what roles, and which exact file
version satisfied the utility-map requirement?*

`index.csv` plus `manifest.json` answer it. That is the design target.

### 14.5 PDF/A is not assumed

`summary.pdf` is a **convenience rendering**, not the evidence, so long-term archival PDF conformance is
not justified for it. If the department ever requires archival-grade preservation of the *originals* —
a genuinely different and much larger commitment, involving format migration over decades — that is a
business decision (**OQ-D9**), not a formatting choice.

---

## 15. Search support

`PROJECT.md` §16 makes search a core feature. This section names the metadata that must exist for
documents to be findable. It does **not** design search, and it introduces **no OCR dependency**.

### 15.1 Document metadata available to search

| Facet | Source | Note |
|---|---|---|
| Display title | `document.title` | primary text target |
| Description | `document.description` | |
| Original filename | `document_version.original_filename` | people search for what the file was called |
| Document kind | `document.document_kind_id` | "show me the maps" |
| Link role | `document_link.document_link_role_id` | "the main letter", "the annexes" |
| Issuing organization | `document.issuing_organization_id` → `organization` + aliases | benefits from the AZ/RU alias handling in `DOMAIN_MODEL.md` §9.5 |
| Issuer's reference | `document.document_reference` | |
| Correspondence number | via the link → `correspondence.letter_number` / `registry_number` | a very common way people look for a file |
| Correspondence date | via the link → `letter_date`, `sent_at`, `received_at` | |
| Case | resolved through the link's context | subject to §10.4 filtering |
| Upload date (system time) | `document_version.uploaded_at` | |
| Business date | `document_version.document_date` | the date printed on the file — distinct from upload date, and the one users usually mean |
| Uploader | `document_version.uploaded_by_user_id` | |
| MIME type / size | `document_version` | "the big spreadsheet" |
| Version status | `document_version.status` | default to `ACTIVE`; withdrawn and superseded findable on request |

### 15.2 Constraints on whatever search is later built

- **Filter before aggregating** (§10.4, `PERMISSIONS.md` §27.2).
- **Search the current version by default**, with superseded and withdrawn versions available
  explicitly — a user searching for "the map" wants today's map, but an auditor wants all of them.
- **Version-level facets match only versions exposed to the searcher** (§10.1). A filename that exists
  only on a version the user cannot access must not make the document match — that match would disclose
  the filename.
- **No file-content indexing in V1.** No text extraction, no OCR, no parsing of untrusted file formats
  — parsing is exactly the attack surface §9 avoids. If content search is ever wanted it must run in
  the isolated pipeline of §9.5, offline, and it is **OQ-D10**.
- Matching technology stays open (`DOMAIN_MODEL.md` OQ-15) and belongs in `DECISIONS.md`.

---

## 16. Open questions

Separated the way the frozen documents separate them. **No answer is invented.**

### 16.1 Business decisions still needed

**OQ-D1 — CLOSED 2026-09-18: there is no retention period; retention is indefinite.** (DECISIONS.md
ADR-043, OB-8.) §12.3's purge machinery is therefore hypothetical and is not built.

**OQ-D2 — CLOSED 2026-09-18: no. Physical destruction of document bytes is never permitted.** (ADR-043,
Domain Model OQ-9.) `storage_object` and its reference count are not needed.

**OQ-D3 — CLOSED 2026-09-18: PDF, KMZ, Word, Excel, AutoCAD and ArchiCAD are accepted and retained; class
E stays empty.** (ADR-042, OB-5; §9.1.) Nothing is refused for lack of preview support.
**Still to confirm:** the exact ArchiCAD extension and MIME mapping — named as a family, not as a file list,
and deliberately not invented here.

**OQ-D4 — CLOSED 2026-09-18: yes, accept them.** (ADR-042.) Macro-enabled Office documents are stored,
visibly marked and download-only (class D) — never parsed or rendered server-side.

**OQ-D5 — CLOSED 2026-09-18: yes, AutoCAD drawings are required** (`.dwg` / `.dxf`, ADR-042). They remain
opaque bytes (§9.3); the answer matters for workstation tooling, not for this model.

**OQ-D6 — Should a malware finding be visible as a business state?**
Currently a scan verdict is a security fact that does not alter `document_version.status` (§9.6).
*Impact:* if it must be visible to caseworkers, `document_version.status` would need a new value — a
**change to the frozen model**, which is why it is raised rather than assumed.

**OQ-D7 — CLOSED (post-review): yes, and not as a default but as a rule.** Every placement on a
registered `correspondence` — incoming and outgoing, every role — is version-pinned (§4.4, L8), so a
letter's file composition is reproducible exactly as registered and a later upload can never change it.
Floating links survive only as non-historical working placements in the home case. Cross-case sharing is
an explicit pinned link, and a response in another case discloses nothing of the letter (§4.8). No new
column: the pin is the existing `document_version_id`.

**OQ-D9 — Is archival-grade preservation of originals (format migration over decades) required?**
Distinct from export (§14.5). *Impact:* a large, ongoing operational commitment if yes.

**OQ-D11 — Are scanned paper documents authoritative, or is the paper original?**
If the paper remains the legal original, the scan is a convenience copy and the physical archive
continues in parallel; if the scan is authoritative, the department's scanning process becomes part of
the evidential chain (resolution, completeness, who attests it). *Impact:* none on the model; a
significant impact on procedure, and on what the department can rely on in a dispute.

### 16.2 Implementation decisions safely deferred

| Question | Why it can wait | Owner |
|---|---|---|
| Exact filesystem and mount options | §6 depends only on atomic same-volume `rename` and `fsync` | `ARCHITECTURE.md` |
| Storage volume layout and capacity planning | `storage_volume_code` keeps roots relocatable (§6.5) | `ARCHITECTURE.md` |
| Virus-scanning approach and product | §9.6 fixes the constraints (offline, not a business state) | `SECURITY.md` |
| Preview tooling, if ever | §9.5 fixes the four architectural constraints | `DECISIONS.md` |
| Whether file delivery gets a separate origin | §9.4 states the reason to want one | `ARCHITECTURE.md` |
| Temp-file age threshold, integrity sweep cadence | §7.6, §11.3 fix the rules; the numbers are configuration | `ARCHITECTURE.md` |
| Backup product, media and rotation | §6.7 fixes the recovery-point invariant they must honour | `ARCHITECTURE.md` |
| **OQ-D8** — add an `INTEGRITY_CHECK` audit action code? | §13.2 works without it; adding it is a permitted vocabulary refinement | schema design |
| **OQ-D10** — file-content/OCR search | explicitly out of V1 (§15.2); would require the isolated pipeline of §9.5 | later phase |
| Export file format details (JSON schema shape, CSV dialect) | §14 fixes the principles and the test it must pass | later phase |

---

## 17. Risks

The five mistakes most likely to be made here by a future developer, in rough order of how much damage
they do.

**R1 — Collapsing `document`, `document_version` and `document_link` into one "attachment" table.**
*How it starts:* "we only ever have one version anyway, and every file belongs to exactly one letter."
*What breaks:* a revised file overwrites the original; the same map attached in three contexts becomes
three uploads with three hashes; "which file proved this requirement?" becomes unanswerable; version
pinning is impossible, so issued decisions silently change. Every guarantee in §1 depends on the three
staying separate.

**R2 — A single generic `is_deleted` / `is_active` flag across documents, versions and links.**
*How it starts:* four status columns look like duplication.
*What breaks:* the four distinct events in §5.4 collapse into "gone". A file withdrawn because it was
the wrong file becomes indistinguishable from a link removed because it was filed in the wrong place,
and from a version superseded by a better scan. Reason, authority and reporting meaning are all lost at
once, and no later migration can recover which was which.

**R3 — Treating a document identifier, a hash or a URL as authority to download.**
*How it starts:* a signed or "unguessable" download link, added for convenience, or a CDN-style cache.
*What breaks:* §10.2 in one step. A restricted case's documents leak to anyone who ever saw a link, and
because the check was skipped there is no `DOWNLOAD` audit event to show who read what. Authorisation is
per request, on the context, every time.

**R4 — Deleting a physical object when a `document_version` is withdrawn.**
*How it starts:* "the version is withdrawn, so the file is garbage — reclaim the space."
*What breaks:* content addressing means bytes are shared (§6.3). Withdrawing one version can destroy the
file that another document, another case, and an issued final result all still rely on. The damage is
silent until someone opens a decision from two years ago. Business withdrawal and physical deletion are
separate concepts (§12.2), and in V1 the second does not exist.

**R5 — Letting a revised official letter become a new version of an existing document.**
*How it starts:* it is the intuitive thing to do, and the UI makes it easy (§8.3).
*What breaks:* the revised letter has no `correspondence`, no letter number, no date, no `response` and
no place in the workflow. The original letter's attachment silently becomes a different document. The
supersession chain that `WORKFLOW.md` §4.3 depends on never forms — and the case's official history is
quietly wrong rather than visibly incomplete.

---

## 18. Final invariants

These are the commitments of this document. Any future change that breaks one of them requires a
decision record in `DECISIONS.md`.

| # | Invariant | Guaranteed by |
|---|---|---|
| **1** | A `document_version`'s bytes never change | §5.1; no operation replaces bytes; corrections create versions |
| **2** | A hash identifies **physical content**, not business meaning | §6.2; the path derives from content alone; meaning lives in links |
| **3** | Two logical documents may reference identical bytes | §6.3; `UNIQUE (document_id, content_hash)` is scoped per document |
| **4** | One correspondence may contain many documents | §4.2; one `PRIMARY_LETTER` plus unlimited attachments and annexes |
| **5** | One document may have many business links | §4.5; `document_link` is M:N to context, with roles and optional pins |
| **6** | Correcting a link never erases historical evidence | §8.2; links are `REMOVED` with a reason, never deleted |
| **7** | A final result can identify the **exact** versions that supported it | §4.7; `FINAL_RESULT_DOCUMENT` links are always version-pinned, taken at issue |
| **8** | A user cannot reach a restricted case's document — or any version not exposed to them — by direct identifier | §10.1–§10.2; authorisation is per request and per version, against a visible context |
| **9** | Original filename is metadata, never storage identity | §5.5, §6.2; the storage key is the hash and nothing else |
| **10** | Business withdrawal never physically deletes bytes | §12.2, §12.4; the only V1 deletion is orphaned temporary files |
| **11** | File storage stays consistent with PostgreSQL metadata | §6.7 recovery-point invariant; §7.2 bytes-first; §11 detection of drift; §12.4 no physical GC |
| **12** | No external or cloud storage is required, ever | §6.1, §20; local filesystem only, fully functional with no Internet |
| **13** | A registered letter's file composition never changes after the fact | §4.4, L8; every correspondence placement is version-pinned *(post-review)* |
| **14** | A floating link never exposes a version introduced under another case | §4.4, L10; floating links and new versions are confined to the home case *(post-review)* |

---

## 19. UI behaviour principles

Not screen designs — principles that follow from this model and would be expensive to retrofit.

| # | Principle | Why it follows from the model |
|---|---|---|
| 1 | **Context is inferred from where the user acts.** Dropping a file on a letter attaches it to that correspondence; dropping it on a requirement offers it as evidence | the link's context is the whole meaning of the upload (§4). Asking the user to pick a context afterwards invites the wrong one |
| 2 | **Never route users through a generic "Documents" area to attach a response file** | there is no such area in the model — a document with no business context cannot exist (L6) |
| 3 | **The main letter and its attachments appear as one group, in the letter's own order** | `PRIMARY_LETTER` + `ordinal` (§4.2). A flat list of six files loses which one is the letter |
| 4 | **Revision history is collapsed by default, one click to expand** | most users want the current version; auditors want all of them (§5.3) |
| 5 | **Withdrawn and superseded items are visible but visually secondary** — never hidden, never prominent | they are evidence, not clutter, and hiding them is how people conclude the system deleted something |
| 6 | **A withdrawn item always shows its reason and who withdrew it, inline** | the reason is the point of withdrawing rather than deleting (§8.4) |
| 7 | **Users never see hashes, paths, volume codes or version numbers as identifiers** | those are storage internals (§7.7). Users work with titles, letters and dates |
| 8 | **Uploading a revised official letter must be visibly different from uploading a new version** | R5 / §8.3 — the single most damaging easy mistake; the UI is where it is prevented |
| 9 | **A file's presence in more than one context is shown plainly** — "also evidence for R1" | §4.5; otherwise users duplicate files because they cannot see the link already exists |
| 10 | **Cross-case linking is presented as a disclosure decision, with a reason prompt, naming the one version being disclosed** | §4.8, §10.3 — it changes who can see the file, and it pins |
| 11 | **Download always states what is being downloaded and from which context** | matches the audit record (§13.3), and makes bulk actions self-evident |

---

## 20. Performance and scale

Thirteen users, one department, one server, one LAN.

| Do not introduce | Why |
|---|---|
| S3-compatible object stores, MinIO clusters, Ceph, SAN dependencies, distributed metadata services | `PROJECT.md` §26 forbids them without a demonstrated requirement, and §2 forbids cloud storage outright. None is remotely justified at this scale |
| Background conversion pipelines as a prerequisite | preview is explicitly not required (§9.5, `PROJECT.md` §24) |
| A separate metadata service | metadata is PostgreSQL rows |

**The intended direction is a local filesystem object store**, addressed by hash, on the same machine as
the application and the database, backed up per §6.7.

Realistic expectations: a municipal planning case carries tens of files; a department of thirteen
produces perhaps thousands of cases over its life. That is **tens of gigabytes**, not terabytes. The
two-level hash fan-out (§6.2) is sufficient by a wide margin, a full re-hash sweep is an overnight job at
worst, and nothing here needs to scale beyond one server.

The one real performance consideration is the **integrity sweep** (§11.3), which reads every byte. It is
sized by configuration and runs off-hours. Everything else is ordinary indexed queries over small tables.

---

## Appendix A — Self-review

| # | Question | Answer | Mechanism |
|---|---|---|---|
| 1 | Can one official letter contain five attachments? | **Yes** | §4.2 — one `PRIMARY_LETTER` link plus unlimited `ATTACHMENT`/`ANNEX` links, ordered by `ordinal` |
| 2 | Can the same map satisfy two requirements without two physical copies? | **Yes** | §4.5, §4.6 — two `requirement_evidence` rows (and their links) over one document, one version, one object |
| 3 | Can a revised map be uploaded without destroying the original? | **Yes** | §5.2 — v2 `ACTIVE`, v1 `SUPERSEDED`, both sets of bytes retained |
| 4 | Can a final result still point at v1 after v2 exists? | **Yes** | §4.7 — `FINAL_RESULT_DOCUMENT` links are version-pinned at issue and are immune to later versions (§8.4) |
| 5 | Can a wrong link be corrected without deleting history? | **Yes** | §8.2 cases 1, 2, 6 — link → `REMOVED` with reason, new link created, both queryable under one `correlation_id` |
| 6 | Can identical bytes exist under two logical documents? | **Yes** | §6.3 — the unique constraint is scoped per document, so cross-document sharing is legal and deliberate |
| 7 | Do interrupted and retried uploads converge safely? | **Yes** | §7.4 — incomplete uploads never rename; `(document_id, content_hash)` prevents phantom versions; a durable operation identifier (`ARCHITECTURE.md` §12.6) prevents a retried first upload creating a second document *(corrected post-review)* |
| 8 | Can missing or corrupted objects be detected? | **Yes** | §11.1 — five findings, three classified critical; full re-hash sweep with `integrity_checked_at` |
| 9 | Can restricted documents be downloaded only by authorised users? | **Yes** | §10.1–§10.3 — per-request, per-version authorisation against a visible context; identifiers carry no authority |
| 10 | Can the whole storage system operate with no Internet? | **Yes** | §6.1, §20 — local filesystem and local PostgreSQL only; no external dependency anywhere in this document |
| 11 | Can a future export reconstruct which document belonged to which official letter? | **Yes** | §14.2–§14.4 — `manifest.json` + `index.csv` carry role, version and correspondence for every file; §14.4 states the test |
| 12 | Does the model avoid exposing raw filesystem paths to users? | **Yes** | §7.7, §19 principle 7 — users work with titles and letters; hashes and paths are storage internals |

No answer is "no", so no revision was required by this review.

---

## Appendix B — Tension with the frozen documents

**No contradiction was found.** This document adds no entity, column, cardinality or invariant, and
`DOMAIN_MODEL.md`, `WORKFLOW.md` and `PERMISSIONS.md` are unmodified.

Two places where the frozen model left an operational rule **undefined** are settled here. Neither
required a model change; both are recorded so the resolution is visible rather than implicit:

| # | What was undefined | Resolution | Where |
|---|---|---|---|
| 1 | `requirement_evidence` and `document_link(requirement_id, REQUIREMENT_EVIDENCE)` both exist in the frozen model and are both used in its own examples, but their relationship was never stated | `requirement_evidence` is **authoritative** for fulfilment; the link is the **display placement**; for document evidence both are written in one transaction with the same pin, and retraction retires both | §4.6 |
| 2 | The brief asked for `MAP`, `DRAWING`, `SPREADSHEET` as link roles, but the frozen vocabulary places them in `document_kind` while `document_link_role` holds `PRIMARY_LETTER`, `ATTACHMENT`, `ANNEX`, … | **kind = what the file is** (on `document`); **role = why it is here** (on `document_link`). The requested values are kinds; `MAIN_LETTER` is the frozen `PRIMARY_LETTER` | §4.1 |

One open question (**OQ-D6**) *would* require a change to the frozen model if answered "yes" — a new
`document_version.status` value for a malware finding. It is raised, not acted on.

**Post-review (2026-09-17).** The independent review found that this document's v1 rules were not safe,
and they are corrected here: floating letter attachments could show a later file than the one registered
(§4.4, OQ-D7 closed); authorization by document exposed every version (§10.1); a response in another case
could surface the letter's files (§3.2, §4.8); "objects before database" did not make backups consistent
(§6.7); `(document_id, content_hash)` did not make first-upload retries idempotent (§7.4); detection was
described inconsistently with "no parsing" (§9.2); orphans were called "collectable" although V1 deletes
nothing (§12.4). None adds an entity or column; the domain side is `DOMAIN_MODEL.md` §12.7, A-5 and A-6.

---

*End of document. No application code, database migrations, API endpoints or UI components are defined
in or implied by this design.*
