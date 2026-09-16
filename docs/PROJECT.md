# PROJECT SPEC v1

## 1. Project Purpose

The system is a closed internal application for a municipal urban-planning department.

It is designed to manage incoming official requests from government bodies and track the complete
lifecycle of each case until a final result is issued.

The system replaces fragmented paper folders and manual tracking with a structured, searchable,
auditable digital workflow.

This is NOT a generic file archive.

This is a:

**Case Management + Interagency Workflow + Document Management System**

The central object of the system is a **Case**.

---

## 2. Deployment Environment

The system will be used by approximately 13 employees.

It must operate exclusively inside the organization's local network.

Requirements:

* Local LAN only
* No cloud infrastructure
* No SaaS backend
* No external database
* No external authentication dependency
* No external API required for normal operation
* No cloud storage
* No telemetry leaving the organization
* No confidential data leaving the local infrastructure
* No Internet dependency for core functionality
* Full functionality must remain available when Internet access is completely unavailable

Target topology:

```
13 workstations
↓
Local LAN
↓
Local application server
↓
Application + PostgreSQL + document storage + audit logs
```

The system should preferably be accessed through a browser on the local network.

Example:

```
https://cases.internal.example
```

The exact internal hostname will be configured during deployment.

Do NOT use `.local`.

---

## 3. Core Business Concept

A government authority sends an official request to the department.

Examples may include:

* request related to allocation of land;
* review of a proposed land use;
* urban-planning approval;
* request for an official opinion;
* request for information;
* other municipal planning matters.

The department registers the incoming correspondence and creates a Case.

The department then sends official requests to other competent authorities.

Examples may include:

* architectural authority;
* emergency authority;
* property authority;
* utility authority;
* infrastructure authority;
* other government bodies.

These authorities may:

* approve;
* reject;
* issue an opinion;
* request additional documents;
* request additional information;
* impose requirements;
* refer the matter to another competent authority.

A response may therefore create a new dependency.

Example:

```
Incoming Case
↓
Request to Architecture Authority
↓
Architecture Authority requests utility communication map
↓
Department sends additional request to Utility Authority
↓
Utility Authority provides map
↓
Requirement is fulfilled
↓
Architecture Authority gives final opinion
```

The workflow is therefore NOT strictly linear.

It must support chains and nested dependencies.

---

## 4. Primary Objective

At any moment, an authorized employee or manager must be able to open a Case and understand within
seconds:

* who submitted the original request;
* what was requested;
* when it was received;
* who is responsible for the Case;
* which authorities were contacted;
* what was sent to each authority;
* what each authority responded;
* which additional requirements appeared;
* why those requirements appeared;
* what additional requests were created;
* which documents support each event;
* what is currently blocking the Case;
* what the department is currently waiting for;
* what final result was reached;
* which documents prove the final result.

---

## 5. Core Domain Entities

### 5.1 Case

A Case represents one complete administrative dossier initiated by an incoming request.

Minimum conceptual fields:

* Case ID
* Internal case number
* Title / subject
* Requesting organization
* Incoming correspondence
* Responsible employee
* Created date
* Business date
* Lifecycle status
* Current progress
* Restricted flag
* Final result
* Closed date
* Notes

The Case itself is NOT a document.

The Case is the container for the complete workflow.

### 5.2 Organization

Government bodies and other organizations must exist as structured master-data entities.

Do NOT store organization names as unrestricted free text in each Case.

Organization fields may include:

* ID
* Official name
* Short name
* Organization type
* Alternative names / aliases
* Active / inactive status

Aliases are important because the same organization may appear under different spellings or
abbreviations.

The same Organization entity may participate in different roles:

* original requester;
* recipient of an outgoing request;
* sender of a response;
* authority responsible for additional information.

### 5.3 Correspondence

Correspondence represents an official incoming or outgoing communication.

A Correspondence record is different from a Document.

One correspondence item may contain multiple files.

Fields may include:

* ID
* Case ID
* related Request ID if applicable
* direction: INCOMING / OUTGOING
* sender organization
* recipient organization
* external letter number
* internal registry number
* letter date
* received date
* sent date
* subject
* description
* parent correspondence if applicable
* created by
* recorded at
* occurred at

`occurred_at` represents when the event actually happened.

`recorded_at` represents when it was entered into the system.

These must remain separate.

### 5.4 Request

A Request represents an official request sent by the department to another authority.

A Request belongs to a Case.

A Request may also exist because another authority created a requirement.

Example:

```
Architecture Authority response
↓
Requirement: obtain utility map
↓
New Request to Utility Authority
```

Fields may include:

* ID
* Case ID
* target organization
* source Requirement ID
* request subject
* outgoing Correspondence
* status
* responsible employee
* sent date
* expected response date
* closed date

### 5.5 Response

A Response represents an answer received from an authority regarding a Request.

Responses must NOT simply overwrite one status field.

Authorities may send multiple letters over time.

Therefore responses should be versioned or represented as separate immutable events.

Possible classification:

* Approval
* Rejection
* Conditional
* Information request
* Additional requirement
* Opinion
* Clarification
* Other

A newer response may supersede or modify an earlier response.

The original history must remain visible.

### 5.6 Requirement

A Requirement represents something that must happen before another authority can issue its final
opinion or before the Case can progress.

Example:

> "Provide utility communication map."

A Requirement must record:

* ID
* Case ID
* source Response
* requesting organization
* description
* responsible employee
* external responsible organization if applicable
* created date
* deadline if known
* status
* evidence documents
* completion information
* related child Request if one was created

Recommended lifecycle:

* OPEN
* IN_PROGRESS
* FULFILLED
* WAIVED
* VOID
* FAILED

Do NOT force every irrelevant requirement to be marked FULFILLED.

Example:

If a requirement becomes irrelevant because another authority's opinion removes the need for it, it
may be marked VOID with a reason.

### 5.7 Document

A Document represents an actual file.

Examples:

* PDF
* DOCX
* XLSX
* scanned image
* map
* technical drawing
* attachment
* official letter
* supporting certificate

Documents must not exist merely in a generic Case folder.

Each document should be linked to its real context.

Possible contexts include:

* initial incoming correspondence;
* outgoing Request;
* incoming Response;
* Requirement;
* final decision;
* supporting attachment.

A single correspondence record may have:

* main letter;
* attachment 1;
* attachment 2;
* map;
* spreadsheet;
* additional document.

---

## 6. Document Storage Rules

Document bytes should be stored on the local server filesystem.

Document metadata should be stored in PostgreSQL.

Do NOT store large document files directly as database BLOBs unless a future architectural decision
explicitly changes this.

Recommended concept:

PostgreSQL:

* document metadata;
* ownership;
* relationships;
* version;
* hashes;
* audit references.

Filesystem:

* actual immutable file objects.

Files should be content-addressed using a cryptographic hash such as SHA-256.

Example conceptual storage:

```
/documents/ab/cd/<sha256>
```

Original filename remains metadata.

Two documents may have the same filename without conflict.

---

## 7. Document Immutability

Uploaded document versions must never be silently overwritten.

If a user uploads a revised file:

Version 1 remains.

Version 2 is added.

The system shows which version is current.

No ordinary hard-delete functionality should exist.

Incorrect documents should normally be:

* withdrawn;
* superseded;
* re-linked;
* marked incorrect;

with:

* reason;
* user;
* timestamp;
* audit record.

---

## 8. Request and Response Document Structure

Every Request should expose its own document context.

Example:

Request → Architecture Authority

Outgoing:

* official_request.pdf
* attachment.xlsx

Incoming:

* architecture_response.pdf
* scheme.pdf
* map.pdf

If the incoming response creates a Requirement:

Requirement:
"Utility communications map required."

That Requirement may create:

Request → Utility Authority

Outgoing:

* utility_request.pdf

Incoming:

* utility_response.pdf
* communications_map.dwg

The complete chain must remain visible.

---

## 9. Workflow Model

The workflow must support dependencies.

It must NOT assume:

```
Incoming → Requests → Responses → Final Decision
```

as a fixed linear sequence.

Instead:

```
Case
↓
Request
↓
Response
↓
Requirement
↓
Child Request
↓
Response
↓
Requirement fulfilled
↓
Parent workflow continues
```

Requests may exist in parallel.

Example:

```
Case
├── Architecture Request
├── Emergency Authority Request
└── Property Authority Request
```

Each branch can progress independently.

---

## 10. Case Lifecycle

The stored Case lifecycle should remain intentionally small.

Recommended:

```
REGISTERED
↓
ACTIVE
↕
ON_HOLD
↓
CLOSED
```

Additional terminal state:

```
CANCELLED
```

Reopening must be supported.

Do NOT create dozens of manually maintained Case statuses.

Instead, the system should derive the operational status from active work.

Example:

```
Case status:
ACTIVE

Derived progress:
"Waiting for response from Architecture Authority"
```

or:

```
"2 of 3 authority responses received · 1 open requirement"
```

or:

```
"Waiting for utility communication map"
```

or:

```
"Ready for final decision"
```

This derived progress should appear prominently in the UI.

---

## 11. Responsible Employees

Cases must support assignment.

At minimum:

* primary responsible employee;
* assignment history;
* reassignment;
* temporary coverage during absence.

Assignments must not be overwritten without history.

Example:

```
Case assigned:
Worker A
01.09.2026 – 20.09.2026

Reassigned:
Worker B
from 21.09.2026
```

---

## 12. Users and Roles

Approximately 13 users.

Initial roles:

### Worker

Can:

* view normal department Cases;
* create Cases if permitted;
* register correspondence;
* create Requests;
* register Responses;
* upload Documents;
* create Requirements;
* update work assigned to them;
* add comments and notes.

### Chief

Includes Worker permissions plus:

* assign and reassign Cases;
* manage departmental workflow;
* waive or void Requirements;
* close and reopen Cases;
* correct certain structured records;
* approve workflow steps where required;
* access operational reports.

### Head

Includes Chief permissions plus:

* high-level override;
* final approval where required;
* access all Cases;
* access administrative reports;
* approve exceptional actions.

### TechAdmin

Responsible for:

* user accounts;
* server/application configuration;
* backups;
* deployment;
* technical maintenance.

TechAdmin should NOT automatically receive business authority.

Technical administration and business approval are separate concepts.

---

## 13. Visibility Model

Default assumption for V1:

Employees in the department can view normal Cases.

Authority to modify or approve differs by role.

A Case may be marked:

```
RESTRICTED
```

Restricted Cases may be visible only to:

* assigned users;
* Chief;
* Head;
* specifically authorized users.

This is preferred over building a complex per-Case ACL matrix in V1.

---

## 14. Authentication

The final authentication method depends on the organization's infrastructure.

Possible:

* local application accounts;
* Active Directory integration if an internal AD already exists.

The application must NOT depend on external/cloud identity services.

Accounts must be deactivated, never physically deleted.

Historical audit entries must always retain the original user identity.

---

## 15. Audit Log

Important actions must generate immutable audit events.

Examples:

* Case created
* Case reassigned
* Correspondence registered
* Document uploaded
* Document withdrawn
* Request created
* Response registered
* Requirement created
* Requirement fulfilled
* Requirement waived
* Case closed
* Case reopened
* user role changed

An audit event should record:

* event sequence;
* actor;
* action;
* target entity;
* timestamp;
* occurred date if relevant;
* previous values;
* new values;
* related document hash when relevant.

Ordinary application users must not be able to modify audit history.

---

## 16. Search

Search is a core feature, not an optional enhancement.

Users should be able to find Cases using combinations of:

* Case number
* requesting organization
* contacted organization
* incoming letter number
* outgoing letter number
* correspondence date
* Case subject
* responsible employee
* status
* document metadata
* year
* requirement status
* final decision / final response number

Azerbaijani and Russian naming variations must be considered.

Organization aliases should improve retrieval.

Search should provide filters/facets.

Example:

```
Year: 2026
Organization: Architecture Authority
Responsible: Worker A
Status: Active
```

---

## 17. Case Screen

The Case page is the most important UI in the entire system.

It should answer:

> "What happened in this Case?"

without forcing the user to navigate many separate pages.

Recommended structure:

**HEADER**

* Case number
* requester
* subject
* responsible employee
* lifecycle state
* derived progress
* created / received dates

**WORKFLOW**

* original incoming correspondence
* outgoing requests
* responses
* requirements
* child requests
* conclusions
* final result

**DOCUMENTS**

Displayed inside their actual workflow context.

**ACTIVITY**

Chronological activity history.

---

## 18. Dependency Visualization

The Case page should make dependencies understandable.

Conceptual example:

```
Original Request
│
├── Architecture Authority
│   ├── Response: additional information required
│   └── Requirement: Utility map
│       └── Utility Authority
│           └── Response received
│
├── Emergency Authority
│   └── Approval received
│
└── Property Authority
    └── Response pending
```

The UI does not necessarily need a complex graphical node editor.

A structured expandable tree/timeline may be sufficient.

Simplicity is preferred.

---

## 19. Dashboard

Worker dashboard:

* My active Cases
* Waiting for me
* Waiting for external response
* Open Requirements
* Overdue items
* Recently updated Cases

Chief dashboard:

* total active Cases
* unassigned Cases
* Cases by responsible employee
* overdue Requests
* overdue Requirements
* Cases ready for closure
* Cases with no activity for N days

Avoid excessive BI/dashboard complexity in V1.

---

## 20. Backup and Disaster Recovery

Backups are part of the product architecture.

At least:

Primary server

Second local machine or backup target that pulls backups

Offline backup copy that the primary server cannot modify

A mirror is NOT a backup.

The application should expose basic backup health information.

Example:

```
Last backup:
15.09.2026 22:00

Last verified restore:
14.09.2026

Status:
Healthy
```

The exact backup architecture will be finalized during infrastructure design.

---

## 21. Concurrency

Multiple employees may work on the same Case.

The system must prevent silent overwrites.

Use optimistic concurrency or equivalent.

If Worker A edits an entity and Worker B has changed it since A opened it:

the system must not silently overwrite B's change.

Conflict must be detected and shown.

---

## 22. File Upload Reliability

Uploads must be atomic.

Conceptual process:

```
upload to temporary location
↓
calculate hash
↓
ensure file completed
↓
move to immutable storage
↓
commit metadata
↓
create audit event
```

Retries must not create accidental duplicate records.

---

## 23. Security Principles

The system is LAN-only, but LAN-only does NOT mean secure by default.

Security assumptions:

* uploaded files are untrusted;
* employees can make mistakes;
* malware may enter through USB or external documents;
* administrator access is powerful;
* backups must not all be writable from the primary machine;
* direct filesystem shares containing system documents should be avoided.

Workstations should access confidential documents through the application rather than directly
browsing the storage directory.

---

## 24. External File Safety

Potentially active document formats must be treated carefully.

Do not execute or automatically render arbitrary uploaded HTML, SVG, scripts or macro-enabled Office
files inside the primary application context.

Document preview/conversion, if later implemented, should run in an isolated environment and must not
become a prerequisite for V1.

V1 may simply allow authorized users to download/open supported files using their local workstation
applications.

---

## 25. Offline Requirement

Production deployment must not secretly depend on Internet access.

The final release must include everything required to operate.

Avoid production dependencies on:

* CDN
* Google Fonts
* external JS libraries loaded at runtime
* cloud authentication
* remote license validation
* telemetry
* cloud error tracking
* cloud OCR
* cloud AI
* external DNS
* Internet-hosted package registries

All frontend assets must be bundled locally.

---

## 26. Architecture Direction

For approximately 13 users, prefer a simple architecture.

Recommended direction:

One modular monolith application

PostgreSQL

Local filesystem object storage

Background worker if needed

Reverse proxy

Do NOT introduce without a demonstrated requirement:

* Kubernetes
* microservices
* Redis
* Kafka
* RabbitMQ
* Elasticsearch
* cloud storage
* distributed services
* automatic HA cluster

The system should be boring, maintainable and recoverable.

---

## 27. Initial Technical Direction

Exact stack is NOT yet locked.

Any proposed stack must satisfy:

* long-term maintainability;
* offline deployment;
* simple local installation;
* straightforward backup;
* good PostgreSQL support;
* strong authorization model;
* local file handling;
* maintainability by another developer years later.

Claude Code and Codex must NOT select or change the primary stack independently.

Stack decisions must first be recorded in `/docs/DECISIONS.md`.

---

## 28. Source of Truth

The `/docs` directory is the authoritative business and architectural reference.

Planned documents:

**PROJECT.md**

* overall product definition

**DOMAIN_MODEL.md**

* entities and relationships

**WORKFLOW.md**

* Case / Request / Requirement workflow

**PERMISSIONS.md**

* users, roles and authorization

**DOCUMENT_MODEL.md**

* document storage, versions and correspondence relationships

**SECURITY.md**

* security requirements and threat model

**ARCHITECTURE.md**

* application and infrastructure architecture

**DECISIONS.md**

* Architecture Decision Records / important decisions

---

## 29. AI Development Rule

Claude Code and Codex must follow these rules:

1. Do not invent business rules that contradict `/docs`.
2. Do not silently change the domain model.
3. Do not introduce a new infrastructure dependency without documenting why.
4. Do not add any cloud dependency.
5. Do not add external telemetry.
6. Do not implement hard delete for business records unless explicitly approved.
7. Do not overwrite document versions.
8. Do not flatten Request → Response → Requirement dependencies into a generic document folder.
9. If a requirement is ambiguous, record it as an open design question instead of guessing.
10. Architectural changes must be documented in `DECISIONS.md`.

---

## 30. Development Strategy

The project should be implemented incrementally.

### Phase 1 — Domain Model

Design:

* Case
* Organization
* Correspondence
* Request
* Response
* Requirement
* Document
* Assignment
* User
* Role
* AuditEvent

No production UI implementation before the relationships are reviewed.

### Phase 2 — Workflow + Permissions

Define:

* state transitions;
* Request dependencies;
* Requirement lifecycle;
* Case closure rules;
* authorization.

### Phase 3 — Case Workspace

Build the primary Case screen.

Focus on:

* clarity;
* document upload;
* Requests;
* Responses;
* Requirements;
* workflow history.

### Phase 4 — Search

Implement robust Case retrieval.

### Phase 5 — Dashboard

Worker and Chief operational lists.

### Phase 6 — Backup / Operations

Finalize deployment, recovery and support tooling.

---

## 31. V1 Non-Goals

Do NOT build in V1 unless requirements change:

* mobile application;
* Internet access;
* remote access;
* public portal;
* email auto-ingestion;
* cloud OCR;
* AI analysis of documents;
* configurable workflow designer;
* complex role designer;
* microservices;
* real-time chat;
* BI platform;
* Elasticsearch;
* electronic signature infrastructure;
* multi-department support;
* automated integration with external government systems.

---

## 32. Definition of Success

The system succeeds when a department employee can open any registered Case and reliably answer:

1. What was requested?
2. Who requested it?
3. Who is responsible?
4. Which authorities were contacted?
5. What documents were sent?
6. What answers were received?
7. What additional requirements appeared?
8. Why did those requirements appear?
9. What actions were taken to satisfy them?
10. What are we waiting for right now?
11. What was the final result?
12. Which documents prove the complete history?

The system must make these answers easier, faster and more reliable than the current physical-paper
process.
