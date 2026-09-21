#!/usr/bin/env bash
#
# RCS restore test. Proves a recovery point is real by rebuilding it somewhere else and checking the bytes.
#
#   /opt/rcs/deploy/backup/rcs-restore-test.sh /srv/rcs-pilot/backups/20260921T101500Z
#
# It NEVER touches the live pilot database or the live object store: it creates a throw-away database
# (rcs_restore_test_<stamp>) and reads the backed-up objects read-only. The live system keeps running throughout.
#
# What a pass means (ADR-021, docs/PILOT_DEPLOYMENT.md):
#   1. the dump restores into an empty database;
#   2. the application's own schema check accepts it;
#   3. every document version in that database has its object present, of the recorded size and SHA-256;
#   4. the case data is readable.
# Previews are not checked: they are derived and are regenerated after a restore.

set -euo pipefail
umask 077
# Secrets files load LAST in RCS and could silently override the throw-away target with the live database.
# Use pgpass/peer authentication for this command; never inherit a live secrets file.
unset RCS_SECRETS_FILE
export PGCONNECT_TIMEOUT=10

POINT="${1:?usage: rcs-restore-test.sh <recovery-point-directory>}"
APPLICATION_DIR="${RCS_APP_DIR:-/opt/rcs}"
ADMIN_USER="${RCS_RESTORE_ADMIN:-postgres}"
# Where PostgreSQL is reached for the throw-away database: the local Unix socket by default, because that is how the
# pilot server talks to its own PostgreSQL. Override only where peer authentication is unavailable.
PGHOST_VALUE="${RCS_PG_HOST:-/var/run/postgresql}"

[ -f "$POINT/database.dump" ] || { echo "no database.dump in '$POINT'" >&2; exit 2; }
[ -d "$POINT/objects" ] || { echo "no objects/ in '$POINT'" >&2; exit 2; }

STAMP="$(date -u +%Y%m%d%H%M%S)"
TEST_DB="${RCS_RESTORE_TEST_DB:-rcs_restore_test_${STAMP}_$$}"
[[ "$TEST_DB" =~ ^rcs_restore_test_[a-z0-9_]{1,40}$ ]] || { echo "only explicitly named rcs_restore_test_* databases are allowed" >&2; exit 2; }
POINT="$(realpath -e "$POINT")"
( cd "$POINT" && sha256sum --check objects.sha256 )
TEMP_ROOT="$(mktemp -d -t rcs-restore-test.XXXXXXXX)"
trap 'rmdir -- "$TEMP_ROOT" 2>/dev/null || true' EXIT
echo "restore target database: $TEST_DB (throw-away; the pilot database is untouched)"

createdb --host="$PGHOST_VALUE" --username="$ADMIN_USER" --owner=rcs_migrate --template=template0 --encoding=UTF8 \
         --locale-provider=builtin --builtin-locale=C.UTF-8 "$TEST_DB"

echo "  [1/4] restoring the dump"
pg_restore --exit-on-error --host="$PGHOST_VALUE" --username="$ADMIN_USER" --dbname="$TEST_DB" --no-owner --role=rcs_migrate "$POINT/database.dump"

echo "  [2/4] schema compatibility"
ConnectionStrings__Runtime="Host=$PGHOST_VALUE;Database=$TEST_DB;Username=rcs_app" \
DOTNET_ENVIRONMENT=Pilot \
    "$APPLICATION_DIR/Rcs.Web" check-schema

echo "  [3/4] document integrity against the backed-up objects (existence, size, SHA-256)"
ConnectionStrings__Runtime="Host=$PGHOST_VALUE;Database=$TEST_DB;Username=rcs_app" \
Rcs__Storage__RootPath="$POINT/objects" \
Rcs__Storage__TempPath="$TEMP_ROOT" \
Rcs__Preview__Enabled=false \
DOTNET_ENVIRONMENT=Pilot \
    "$APPLICATION_DIR/Rcs.Web" verify-documents --rehash

echo "  [4/4] case data is readable"
psql --host="$PGHOST_VALUE" --username="$ADMIN_USER" --dbname="$TEST_DB" --set=ON_ERROR_STOP=1 --tuples-only --no-align --command \
    "SELECT 'cases=' || (SELECT count(*) FROM rcs.case_record) || ' documents=' || (SELECT count(*) FROM rcs.document_version) || ' users=' || (SELECT count(*) FROM rcs.app_user);"

echo
echo "RESTORE TEST PASSED for $POINT"
echo "Drop the throw-away database when you are finished inspecting it:"
echo "    dropdb --host=$PGHOST_VALUE --username=$ADMIN_USER $TEST_DB"
