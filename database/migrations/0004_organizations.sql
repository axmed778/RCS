-- description: Organization master data and aliases
--
-- RCS migration 0004 - organizations (DOMAIN_MODEL.md sections 2.1, 2.2).
--
-- One row per real party; the role a party plays is implied by the column that references it. The
-- department itself is the single row flagged is_own_organization. Deactivated, never deleted.
--
-- Normalized search columns (ARCHITECTURE.md 13.4) are not added until PS-1 / DEF-09 are decided;
-- no uniqueness is placed on names, because name equality is collation- and normalization-dependent.

CREATE TABLE rcs.organization (
    id                       uuid        NOT NULL,
    official_name            text        NOT NULL,
    short_name               text,
    organization_type_id     uuid        NOT NULL,
    registration_code        text,
    parent_organization_id   uuid,
    is_own_organization      boolean     NOT NULL DEFAULT false,
    postal_address           text,
    email                    text,
    phone                    text,
    is_active                boolean     NOT NULL DEFAULT true,
    deactivated_at           timestamptz,
    deactivated_by_user_id   uuid,
    deactivation_reason_note text,
    notes                    text,
    created_at               timestamptz NOT NULL DEFAULT now(),
    created_by_user_id       uuid,
    updated_at               timestamptz,
    updated_by_user_id       uuid,
    row_version              integer     NOT NULL DEFAULT 1,
    CONSTRAINT organization_pk PRIMARY KEY (id),
    CONSTRAINT organization_registration_code_uq UNIQUE (registration_code),
    CONSTRAINT organization_official_name_present CHECK (btrim(official_name) <> ''),
    CONSTRAINT organization_not_own_parent CHECK (parent_organization_id IS NULL OR parent_organization_id <> id),
    CONSTRAINT organization_deactivation_recorded CHECK (is_active OR deactivated_at IS NOT NULL),
    CONSTRAINT organization_row_version_positive CHECK (row_version > 0),
    CONSTRAINT organization_type_fk FOREIGN KEY (organization_type_id) REFERENCES rcs.organization_type (id),
    CONSTRAINT organization_parent_fk FOREIGN KEY (parent_organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT organization_deactivated_by_fk FOREIGN KEY (deactivated_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT organization_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT organization_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

-- Exactly one row may be the department itself.
CREATE UNIQUE INDEX organization_one_own_uq ON rcs.organization (is_own_organization) WHERE is_own_organization;
CREATE INDEX organization_type_idx ON rcs.organization (organization_type_id);
CREATE INDEX organization_parent_idx ON rcs.organization (parent_organization_id);

-- Former names, abbreviations, transliterations and misspellings (Azerbaijani and Russian variants).
-- An alias never migrates to another organization.
CREATE TABLE rcs.organization_alias (
    id                 uuid        NOT NULL,
    organization_id    uuid        NOT NULL,
    alias              text        NOT NULL,
    alias_type         text        NOT NULL,
    alias_language     text,
    valid_from         date,
    valid_to           date,
    is_active          boolean     NOT NULL DEFAULT true,
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by_user_id uuid,
    updated_at         timestamptz,
    updated_by_user_id uuid,
    row_version        integer     NOT NULL DEFAULT 1,
    CONSTRAINT organization_alias_pk PRIMARY KEY (id),
    CONSTRAINT organization_alias_present CHECK (btrim(alias) <> ''),
    CONSTRAINT organization_alias_type_valid CHECK (alias_type IN ('FORMER_NAME', 'ABBREVIATION', 'TRANSLITERATION', 'MISSPELLING')),
    CONSTRAINT organization_alias_language_format CHECK (alias_language IS NULL OR alias_language ~ '^[a-z]{2,3}$'),
    CONSTRAINT organization_alias_validity CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from),
    CONSTRAINT organization_alias_organization_fk FOREIGN KEY (organization_id) REFERENCES rcs.organization (id),
    CONSTRAINT organization_alias_created_by_fk FOREIGN KEY (created_by_user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT organization_alias_updated_by_fk FOREIGN KEY (updated_by_user_id) REFERENCES rcs.app_user (id)
);

CREATE INDEX organization_alias_organization_idx ON rcs.organization_alias (organization_id);

GRANT SELECT, INSERT, UPDATE ON rcs.organization, rcs.organization_alias TO rcs_app;
