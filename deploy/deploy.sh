#!/usr/bin/env bash
# Runs on the VPS as the deploy user. Expects IMAGE, TAG, GH_TOKEN, GH_ACTOR.
set -euo pipefail

cd /opt/cocorra/api

BACKUP_SCRIPT=/opt/cocorra/infra/backup.sh

[ -f .env ] || { echo "ERROR: .env missing"; exit 1; }
[ -f firebase-config.json ] || { echo "ERROR: firebase-config.json missing"; exit 1; }

# Checked before anything changes: the deploy must not continue without a database backup.
if [ ! -f "$BACKUP_SCRIPT" ]; then
  echo "ERROR: $BACKUP_SCRIPT is missing, so no pre-deploy database backup can be taken."
  echo "       Install it from deploy/backup.sh in the repository, then: chmod +x $BACKUP_SCRIPT"
  exit 1
fi
if [ ! -x "$BACKUP_SCRIPT" ]; then
  echo "ERROR: $BACKUP_SCRIPT is not executable. Fix: chmod +x $BACKUP_SCRIPT"
  exit 1
fi

# The container runs as the aspnet image's non-root user (UID 1654), which does not own the
# file, so it can only read it through the "other" read bit. Without it the API throws at
# startup reading the Firebase credential and every deploy fails its health check.
if (( (8#$(stat -c '%a' firebase-config.json) & 4) == 0 )); then
  echo "ERROR: firebase-config.json is not world-readable, so the container (UID 1654) cannot read it."
  echo "       Fix: chmod 644 /opt/cocorra/api/firebase-config.json"
  exit 1
fi

# .env records the version that is RUNNING and healthy. It is only rewritten after the new
# container passes its health check, so a failed deploy never becomes the next rollback target.
set_image() {
  sed -i '/^API_IMAGE=/d;/^IMAGE_TAG=/d' .env
  printf 'API_IMAGE=%s\nIMAGE_TAG=%s\n' "$1" "$2" >> .env
}

# Run docker compose against a specific image without touching .env: shell variables take
# precedence over .env for ${API_IMAGE}/${IMAGE_TAG} interpolation in docker-compose.yml.
compose_with() {
  local image=$1 tag=$2
  shift 2
  API_IMAGE="$image" IMAGE_TAG="$tag" docker compose "$@"
}

PREV_IMAGE=$(grep -oP '^API_IMAGE=\K.*' .env || true)
PREV_TAG=$(grep -oP '^IMAGE_TAG=\K.*' .env || true)
echo "Deploying $TAG (previous: ${PREV_TAG:-none})"

echo "==> Pre-deploy database backup"
# The backup script's output is shown as it runs and kept in a temp file; its final
# BACKUP_DB=<path> line names the database backup that the rollback restore steps refer to.
BACKUP_LOG=$(mktemp)
"$BACKUP_SCRIPT" | tee "$BACKUP_LOG"
BACKUP_DB=$(sed -n 's/^BACKUP_DB=//p' "$BACKUP_LOG" | tail -n 1)
rm -f "$BACKUP_LOG"
if [ -z "$BACKUP_DB" ] || [ ! -f "$BACKUP_DB" ]; then
  echo "ERROR: $BACKUP_SCRIPT did not report an existing backup file (expected a final line"
  echo "       BACKUP_DB=<path>). Install the current deploy/backup.sh from the repository."
  exit 1
fi
echo "    database backup: $BACKUP_DB"

echo "$GH_TOKEN" | docker login ghcr.io -u "$GH_ACTOR" --password-stdin
trap 'docker logout ghcr.io >/dev/null 2>&1 || true' EXIT

# A failed pull changes nothing: the old container keeps running and .env is untouched,
# so set -e simply stops the deploy here.
echo "==> Pulling new image"
compose_with "$IMAGE" "$TAG" pull

# Polls for up to 150s, but returns as soon as the outcome is known: "healthy", or
# "unhealthy" (start_period over and retries exhausted), or a crash loop (the restart
# policy restarting a container that exits at startup, e.g. a missing secret).
wait_healthy() {
  local state restarts health
  for i in $(seq 1 30); do
    read -r state restarts health < <(docker inspect \
      -f '{{.State.Status}} {{.RestartCount}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
      cocorra-api 2>/dev/null || echo "missing 0 none")
    case "$health" in
      healthy)   return 0 ;;
      unhealthy) echo "    container reported unhealthy"; return 1 ;;
    esac
    if [ "$restarts" -gt 0 ] || [ "$state" = "exited" ] || [ "$state" = "dead" ]; then
      echo "    container is not staying up (state=$state, restarts=$restarts)"
      return 1
    fi
    sleep 5
  done
  echo "    timed out waiting for a healthy status"
  return 1
}

# The database is never restored automatically: a restore discards every write made since the
# backup, so it is a manual decision (docs/MIGRATIONS.md). These are the exact steps for the
# backup taken by this deploy, printed whenever the deploy fails.
print_restore_steps() {
  local bak="${BACKUP_DB%.gz}"
  local start_previous
  if [ -n "$PREV_TAG" ]; then
    start_previous="API_IMAGE=$PREV_IMAGE IMAGE_TAG=$PREV_TAG docker compose up -d"
  else
    start_previous="# no previous version recorded: deploy a known-good commit instead"
  fi

  cat <<EOF
==> Database backup taken before this deploy: $BACKUP_DB
    The database was NOT restored. Restore it only if this release's migrations left a schema
    the previous version cannot use: restoring discards every write made since the backup.

    cd /opt/cocorra/api
    # 1. Stop the API so nothing writes during the restore
    docker stop cocorra-api
    # 2. Decompress (-k keeps the .gz)
    gunzip -kf $BACKUP_DB
    # 3. Copy it into the SQL Server container, readable by the mssql user
    docker cp $bak cocorra_db:/var/opt/mssql/backup/restore.bak
    docker exec -u 0 cocorra_db chmod 644 /var/opt/mssql/backup/restore.bak
    # 4. Restore over the current database
    #    (if this fails, run the same command with only "ALTER DATABASE [CocorraDb] SET MULTI_USER")
    docker exec cocorra_db sh -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "\$MSSQL_SA_PASSWORD" -C -b -Q "ALTER DATABASE [CocorraDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [CocorraDb] FROM DISK = N'\''/var/opt/mssql/backup/restore.bak'\'' WITH REPLACE, CHECKSUM; ALTER DATABASE [CocorraDb] SET MULTI_USER"'
    # 5. Start the previous version
    $start_previous
    # 6. Clean up
    rm $bak
    docker exec -u 0 cocorra_db rm /var/opt/mssql/backup/restore.bak
EOF
}

# From here on the old container may already be replaced, so every failure rolls back.
rollback() {
  echo "==> Deploy of $TAG FAILED. Last logs:"
  docker logs cocorra-api --tail 80 || true

  if [ -z "$PREV_TAG" ]; then
    echo "==> No previous version to roll back to."
  else
    echo "==> Rolling back to $PREV_TAG"
    compose_with "$PREV_IMAGE" "$PREV_TAG" pull || true
    if compose_with "$PREV_IMAGE" "$PREV_TAG" up -d && wait_healthy; then
      echo "==> Rollback OK: $PREV_TAG is running and healthy."
    else
      echo "==> ROLLBACK FAILED: $PREV_TAG is not healthy either. Manual intervention required."
      docker logs cocorra-api --tail 80 || true
    fi
  fi

  print_restore_steps
  exit 1
}

echo "==> Starting new container"
compose_with "$IMAGE" "$TAG" up -d || rollback

echo "==> Waiting for health check"
wait_healthy || rollback

set_image "$IMAGE" "$TAG"
echo "==> Deploy OK: $TAG"

# Remove old images of THIS repository only, keeping the KEEP_IMAGES most recently created
# tags (which include the one just deployed and, normally, the rollback target). Images of
# other stacks on the host are never touched. Failures here never fail the deploy.
KEEP_IMAGES=3
docker image ls "$IMAGE" --format '{{.CreatedAt}}|{{.Tag}}' \
  | grep -v '|<none>$' \
  | sort -r \
  | tail -n +$((KEEP_IMAGES + 1)) \
  | cut -d'|' -f2 \
  | while read -r old_tag; do
      [ "$old_tag" = "$TAG" ] && continue
      echo "    removing old image $IMAGE:$old_tag"
      docker image rm "$IMAGE:$old_tag" >/dev/null || true
    done || true
