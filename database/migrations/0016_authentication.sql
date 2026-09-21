-- description: Local authentication - password credentials, revocable server-side sessions and sign-in throttling
--
-- RCS migration 0016 - authentication (SECURITY.md sections 6, 8; DECISIONS.md ADR-033; PERMISSIONS.md section 25).
--
-- 0002 deliberately kept credentials out of app_user so user rows stay freely readable for display. This migration
-- adds the two tables the authentication module owns:
--   user_credential  one current local password per user: the encoded Argon2id hash, the cost parameters it was
--                    produced with (so they can be reviewed and upgraded), and the per-account throttling counters.
--   user_session     server-side, revocable sessions. ADR-033 chose these over self-contained tokens precisely so
--                    that suspending a user can revoke access immediately (SECURITY.md section 6.5, invariant 5).
--
-- Nothing here is reversible: no column stores a password, a password hint, or anything from which a password could
-- be recovered. The CHECK on password_hash refuses anything that is not an Argon2id PHC string, so a plaintext or
-- unsalted digest cannot physically be stored by any code path, including a mistaken one.

CREATE TABLE rcs.user_credential (
    -- One current credential per user; history is not kept, because a previous password hash has no business value
    -- and every extra copy is another place a hash can leak from (SECURITY.md 6.2).
    user_id              uuid        NOT NULL,
    algorithm            text        NOT NULL,
    -- The exact cost this hash was produced with, recorded so it can be reviewed later and raised deliberately.
    parameters           text        NOT NULL,
    -- PHC string: $argon2id$v=19$m=...,t=...,p=...$<base64 salt>$<base64 hash>. The salt lives inside it.
    password_hash        text        NOT NULL,
    -- A credential the user must replace before doing anything else: first sign-in and every administrative reset.
    must_change          boolean     NOT NULL DEFAULT true,
    -- An administrative reset (SECURITY.md 6.7): single-use and short-lived.
    is_temporary         boolean     NOT NULL DEFAULT false,
    expires_at           timestamptz,
    set_at               timestamptz NOT NULL,
    set_by_user_id       uuid        NOT NULL,
    failed_attempt_count integer     NOT NULL DEFAULT 0,
    locked_until         timestamptz,
    last_failed_at       timestamptz,
    last_success_at      timestamptz,
    updated_at           timestamptz,
    row_version          integer     NOT NULL DEFAULT 1,
    CONSTRAINT user_credential_pk PRIMARY KEY (user_id),
    CONSTRAINT user_credential_algorithm_valid CHECK (algorithm = 'ARGON2ID'),
    -- Memory-hard hashing, per ADR-033 and SECURITY.md 6.2. A bare digest or a plaintext value cannot match this.
    CONSTRAINT user_credential_hash_encoded CHECK (password_hash ~ '^\$argon2id\$v=19\$m=[0-9]+,t=[0-9]+,p=[0-9]+\$[A-Za-z0-9+/=]{16,}\$[A-Za-z0-9+/=]{16,}$'),
    CONSTRAINT user_credential_parameters_format CHECK (parameters ~ '^m=[0-9]+;t=[0-9]+;p=[0-9]+$'),
    CONSTRAINT user_credential_temporary_expires CHECK (NOT is_temporary OR (expires_at IS NOT NULL AND must_change)),
    CONSTRAINT user_credential_attempts_valid CHECK (failed_attempt_count >= 0),
    CONSTRAINT user_credential_row_version_positive CHECK (row_version > 0),
    CONSTRAINT user_credential_user_fk FOREIGN KEY (user_id) REFERENCES rcs.app_user (id),
    CONSTRAINT user_credential_set_by_fk FOREIGN KEY (set_by_user_id) REFERENCES rcs.app_user (id)
);

COMMENT ON TABLE rcs.user_credential IS
    'Local password credentials (SECURITY.md 6.2). Argon2id only; no plaintext, no reversible form, no history.';

CREATE TABLE rcs.user_session (
    id             uuid        NOT NULL,
    user_id        uuid        NOT NULL,
    created_at     timestamptz NOT NULL,
    last_seen_at   timestamptz NOT NULL,
    -- Absolute end of the session; the idle timeout is applied against last_seen_at by the application.
    expires_at     timestamptz NOT NULL,
    revoked_at     timestamptz,
    revocation_reason text,
    -- Where the session was established from, for the sign-in audit trail. Never a credential.
    client_host    text,
    row_version    integer     NOT NULL DEFAULT 1,
    CONSTRAINT user_session_pk PRIMARY KEY (id),
    CONSTRAINT user_session_window_valid CHECK (expires_at > created_at AND last_seen_at >= created_at),
    CONSTRAINT user_session_revocation_recorded CHECK ((revoked_at IS NULL) = (revocation_reason IS NULL)),
    CONSTRAINT user_session_revocation_reason_valid CHECK (
        revocation_reason IS NULL OR revocation_reason IN ('SIGNED_OUT', 'PASSWORD_CHANGED', 'ACCOUNT_SUSPENDED', 'ADMINISTRATIVE')),
    CONSTRAINT user_session_row_version_positive CHECK (row_version > 0),
    CONSTRAINT user_session_user_fk FOREIGN KEY (user_id) REFERENCES rcs.app_user (id)
);

COMMENT ON TABLE rcs.user_session IS
    'Server-side revocable sessions (ADR-033, SECURITY.md 8.2): suspending a user ends access at once, not at expiry.';

-- The sessions of one user: revoking every session of an account must stay cheap, because it happens on suspension,
-- on password change and on an administrative reset.
CREATE INDEX user_session_user_idx ON rcs.user_session (user_id) WHERE revoked_at IS NULL;
CREATE INDEX user_session_expiry_idx ON rcs.user_session (expires_at) WHERE revoked_at IS NULL;

-- The runtime role signs people in and out, throttles attempts, and replaces a password. It may never delete a
-- credential or a session row, and never rewrites who set a credential or when a session began.
GRANT SELECT, INSERT ON rcs.user_credential TO rcs_app;
GRANT UPDATE (algorithm, parameters, password_hash, must_change, is_temporary, expires_at, set_at, set_by_user_id,
              failed_attempt_count, locked_until, last_failed_at, last_success_at, updated_at, row_version)
    ON rcs.user_credential TO rcs_app;
GRANT SELECT, INSERT ON rcs.user_session TO rcs_app;
GRANT UPDATE (last_seen_at, expires_at, revoked_at, revocation_reason, row_version) ON rcs.user_session TO rcs_app;
