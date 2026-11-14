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
"$BACKUP_SCRIPT"

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

# From here on the old container may already be replaced, so every failure rolls back.
rollback() {
  echo "==> Deploy of $TAG FAILED. Last logs:"
  docker logs cocorra-api --tail 80 || true

  if [ -z "$PREV_TAG" ]; then
    echo "==> No previous version to roll back to."
    exit 1
  fi

  echo "==> Rolling back to $PREV_TAG"
  compose_with "$PREV_IMAGE" "$PREV_TAG" pull || true
  if compose_with "$PREV_IMAGE" "$PREV_TAG" up -d && wait_healthy; then
    echo "==> Rollback OK: $PREV_TAG is running and healthy."
  else
    echo "==> ROLLBACK FAILED: $PREV_TAG is not healthy either. Manual intervention required."
    docker logs cocorra-api --tail 80 || true
  fi
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
