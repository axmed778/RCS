#!/usr/bin/env bash
#
# RCS pilot backup. One recovery point = database + originals + configuration + release identity.
#
#   sudo -u rcs-backup /opt/rcs/deploy/backup/rcs-backup.sh /srv/rcs-pilot/backups
#
# ORDER MATTERS, and it is the whole reason this is a script rather than a cron line with pg_dump in it.
# The object store is append-only (ADR-043: bytes are never deleted or rewritten) and the upload protocol publishes
# an object BEFORE it commits the metadata that refers to it (DOCUMENT_MODEL.md 7.2). Therefore:
#
#     dump the database FIRST, copy the objects SECOND
#
# gives a recovery point where every object the dump references is present in the object copy — the validity rule of
# ADR-021. Doing it the other way round can produce a dump that names an object the copy does not contain.
#
# Preview artifacts are NOT backed up: they are derived cache and are regenerated after a restore (ADR-044).
#
# This script never deletes anything. Pruning old recovery points is a deliberate, separate decision.

set -euo pipefail
umask 077

DESTINATION_ROOT="${1:-/srv/rcs-pilot/backups}"
OBJECT_ROOT="${RCS_OBJECT_ROOT:-/srv/rcs-pilot/objects}"
CONFIG_DIR="${RCS_CONFIG_DIR:-/etc/rcs}"
APPLICATION_DIR="${RCS_APP_DIR:-/opt/rcs}"
DATABASE="${RCS_DATABASE:-rcs_pilot}"
PGUSER_BACKUP="${RCS_BACKUP_USER:-rcs_backup}"

# Refuse to run against anything that is not a real, absolute destination. No variable reaches a delete: this script
# only ever creates a new timestamped directory and writes inside it (filesystem safety rule).
case "$DESTINATION_ROOT" in
    /|/home|/srv|/etc|/opt|/var|"") echo "refusing to use '$DESTINATION_ROOT' as a backup root" >&2; exit 2 ;;
    /*) ;;
    *) echo "backup root must be an absolute path" >&2; exit 2 ;;
esac
[ -d "$OBJECT_ROOT" ] || { echo "object store '$OBJECT_ROOT' not found" >&2; exit 2; }

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET="$DESTINATION_ROOT/$STAMP"
mkdir -p "$DESTINATION_ROOT"
mkdir "$TARGET"
chmod 0700 "$TARGET"
echo "recovery point: $TARGET"

# 1. Database first.
echo "  [1/4] database $DATABASE"
pg_dump --username="$PGUSER_BACKUP" --dbname="$DATABASE" --format=custom --compress=6 \
        --file="$TARGET/database.dump"

# 2. Originals second, so the set is a superset of what the dump references. Append-only, so a plain copy is
#    consistent; --link-dest against the previous recovery point keeps repeat backups cheap. No --delete, ever.
echo "  [2/4] document objects from $OBJECT_ROOT"
PREVIOUS="$(find "$DESTINATION_ROOT" -maxdepth 1 -mindepth 1 -type d -name '20*' ! -name "$STAMP" | sort | tail -1 || true)"
if [ -n "$PREVIOUS" ] && [ -d "$PREVIOUS/objects" ]; then
    rsync -a --link-dest="$PREVIOUS/objects" "$OBJECT_ROOT/" "$TARGET/objects/"
else
    rsync -a "$OBJECT_ROOT/" "$TARGET/objects/"
fi

# 3. Configuration needed to rebuild the service — without the secrets file, which holds database passwords.
echo "  [3/4] configuration (excluding secrets)"
mkdir -p "$TARGET/config"
if [ -d "$CONFIG_DIR" ]; then
    find "$CONFIG_DIR" -maxdepth 1 -type f -name 'appsettings.Pilot.json' -exec cp -p {} "$TARGET/config/" \;
fi
if [ -f "$APPLICATION_DIR/appsettings.Pilot.json" ]; then
    cp -p "$APPLICATION_DIR/appsettings.Pilot.json" "$TARGET/config/"
fi
for unit in /etc/systemd/system/rcs.service /etc/nginx/sites-available/rcs; do
    [ -f "$unit" ] && cp -p "$unit" "$TARGET/config/" || true
done

# 4. Release identity, so a restore can be matched with the code that wrote it.
echo "  [4/4] release identity"
{
    echo "taken_at=$STAMP"
    echo "database=$DATABASE"
    echo "object_root=$OBJECT_ROOT"
    echo "host=$(hostname)"
    # The schema this release expects, read from the shipped configuration. A restore is only valid against a release
    # that expects the same number, and rcs-restore-test.sh proves it with the application's own check.
    if [ -f "$APPLICATION_DIR/appsettings.json" ]; then
        echo "schema_expected=$(grep -o '"ExpectedSchemaVersion"[^,]*' "$APPLICATION_DIR/appsettings.json" | grep -o '[0-9]\+')"
    fi
    if [ -f "$APPLICATION_DIR/RELEASE" ]; then cat "$APPLICATION_DIR/RELEASE"; fi
} > "$TARGET/RECOVERY_POINT"

# A checksum manifest makes the copy verifiable later without the application.
( cd "$TARGET" && find objects -type f -print0 | sort -z | xargs -0 -r sha256sum > objects.sha256
  sha256sum database.dump >> objects.sha256
  sha256sum --check objects.sha256 )

echo "done: $TARGET"
echo "A recovery point is only valid once a restore test has passed (docs/PILOT_DEPLOYMENT.md): restore into a"
echo "separate database, start the application against it, and run 'Rcs.Web verify-documents --rehash'."
