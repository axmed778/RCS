-- description: Users, the four conceptual roles and temporal role grants
--
-- RCS migration 0002 - identity (DOMAIN_MODEL.md sections 2.3, 2.4; PERMISSIONS.md section 25).
--
-- Physical names: the domain entity "user" is the table app_user, because USER is a reserved word in
-- PostgreSQL and quoted identifiers are not used (DECISIONS.md ADR-037). Foreign keys keep the domain
-- names (*_user_id).
--
-- Authentication secrets (password hashes, lockout counters, sessions) are deliberately NOT here: they
-- belong in a separate table owned by the authentication module (DOMAIN_MODEL.md section 2.3,
-- SECURITY.md section 6.2), which is not built yet.

CREATE TABLE rcs.app_user (
    id                       uuid        NOT NULL,
    username                 text        NOT NULL,
    employee_number          text,
    full_name                text        NOT NULL,
    display_name             text        NOT NULL,
    job_title                text,
    email_internal           text,
    phone_internal           text,
    status                   text        NOT NULL,
    auth_source              text        NOT NULL,
    directory_identifier     text,
    deactivated_at           timestamptz,
    deactivated_by_user_id   uuid,
    deactivation_reason_note text,
    created_at               timestamptz NOT NULL DEFAULT now(),
    created_by_user_id       uuid,
    updated_at               timestamptz,
    updated_by_user_id       uuid,
    row_version              integer     NOT NULL DEFAULT 1,
    CONSTRAINT app_user_pk PRIMARY KEY (id),
    CONSTRAINT app_user_username_uq UNIQUE (username),
    CONSTRAINT app_user_employee_number_uq UNIQUE (employee_number),
    CONSTRAINT app_user_username_format CHECK (username ~ '^[a-z0-9][a-z0-9._-]*$'),
    CONSTRAINT app_user_names_present CHECK (btrim(full_name) <> '' AND btrim(display_name) <> ''),
    CONSTRAINT app_user_status_valid CHECK (status IN ('ACTIVE', 'SUSPENDED', 'DEACTIVATED')),
    CONSTRAINT app_user_auth_source_valid CHECK (auth_source IN ('LOCAL', 'ACTIVE_DIRECTORY')),
    CONSTRAINT app_user_deactivation_recorded CHECK (status <> 'DEACTIVATED' OR deactivated_at IS NOT NULL),
    CONSTRAINT app_user_row_version_positive CHECK (row_version > 0),
    CONSTRAINT app_user_deactivated_by_fk FOREIGN KEY (deactivated_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT app_user_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT app_user_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

COMMENT ON TABLE rcs.app_user IS
    'Domain entity "user" (DOMAIN_MODEL.md 2.3). Deactivated, never deleted. No credentials here.';

-- Conceptual roles only; no permission engine (PERMISSIONS.md section 14.3). A closed set seeded here.
CREATE TABLE rcs.role (
    id          uuid     NOT NULL,
    code        text     NOT NULL,
    name        text     NOT NULL,
    description text,
    rank        smallint NOT NULL,
    is_active   boolean  NOT NULL DEFAULT true,
    CONSTRAINT role_pk PRIMARY KEY (id),
    CONSTRAINT role_code_uq UNIQUE (code),
    CONSTRAINT role_code_valid CHECK (code IN ('WORKER', 'CHIEF', 'HEAD', 'TECH_ADMIN'))
);

INSERT INTO rcs.role (id, code, name, description, rank) VALUES
    ('01995c00-0001-7000-8000-000000000001', 'WORKER', 'Worker', 'Does the case work (PERMISSIONS.md section 15).', 10),
    ('01995c00-0001-7000-8000-000000000002', 'CHIEF', 'Chief', 'Operational authority over the department''s work (PERMISSIONS.md section 16).', 20),
    ('01995c00-0001-7000-8000-000000000003', 'HEAD', 'Head', 'Highest business authority (PERMISSIONS.md section 17).', 30),
    ('01995c00-0001-7000-8000-000000000004', 'TECH_ADMIN', 'Technical administrator', 'Administers the system; no business authority (PERMISSIONS.md section 18).', 0);

-- Temporal grants, so audit can answer "what role did this person hold when they acted?".
-- Revocation closes the window (valid_until) and records who and why; rows are never deleted.
CREATE TABLE rcs.user_role (
    id                     uuid        NOT NULL,
    user_id                uuid        NOT NULL,
    role_id                uuid        NOT NULL,
    valid_from             timestamptz NOT NULL,
    valid_until            timestamptz,
    granted_by_user_id     uuid,
    revoked_by_user_id     uuid,
    revocation_reason_note text,
    recorded_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT user_role_pk PRIMARY KEY (id),
    CONSTRAINT user_role_window_valid CHECK (valid_until IS NULL OR valid_until > valid_from),
    CONSTRAINT user_role_revocation_recorded CHECK (revoked_by_user_id IS NULL OR valid_until IS NOT NULL),
    CONSTRAINT user_role_user_fk FOREIGN KEY (user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT user_role_role_fk FOREIGN KEY (role_id) REFERENCES rcs.role (id),
    CONSTRAINT user_role_granted_by_fk FOREIGN KEY (granted_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT user_role_revoked_by_fk FOREIGN KEY (revoked_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX user_role_user_idx ON rcs.user_role (user_id);

-- Runtime privileges. No DELETE on any business table (DECISIONS.md ADR-030).
GRANT SELECT ON rcs.role TO rcs_app;
GRANT SELECT, INSERT, UPDATE ON rcs.app_user TO rcs_app;
GRANT SELECT, INSERT, UPDATE ON rcs.user_role TO rcs_app;
