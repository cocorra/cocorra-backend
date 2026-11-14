#!/usr/bin/env bash
# Runs on the VPS as the deploy user. Expects IMAGE, TAG, GH_TOKEN, GH_ACTOR.
set -euo pipefail

cd /opt/cocorra/api

[ -f .env ] || { echo "ERROR: .env missing"; exit 1; }
[ -f firebase-config.json ] || { echo "ERROR: firebase-config.json missing"; exit 1; }

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
/opt/cocorra/infra/backup.sh

echo "$GH_TOKEN" | docker login ghcr.io -u "$GH_ACTOR" --password-stdin
trap 'docker logout ghcr.io >/dev/null 2>&1 || true' EXIT

# A failed pull changes nothing: the old container keeps running and .env is untouched,
# so set -e simply stops the deploy here.
echo "==> Pulling new image"
compose_with "$IMAGE" "$TAG" pull

wait_healthy() {
  for i in $(seq 1 30); do
    STATUS=$(docker inspect -f '{{.State.Health.Status}}' cocorra-api 2>/dev/null || echo unknown)
    if [ "$STATUS" = "healthy" ]; then
      return 0
    fi
    sleep 5
  done
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
  compose_with "$PREV_IMAGE" "$PREV_TAG" up -d || true
  exit 1
}

echo "==> Starting new container"
compose_with "$IMAGE" "$TAG" up -d || rollback

echo "==> Waiting for health check"
wait_healthy || rollback

set_image "$IMAGE" "$TAG"
echo "==> Deploy OK: $TAG"
docker image prune -af --filter "until=168h" >/dev/null || true
