#!/usr/bin/env bash
# Runs on the VPS as the deploy user. Expects IMAGE, TAG, GH_TOKEN, GH_ACTOR.
set -euo pipefail

cd /opt/cocorra/api

[ -f .env ] || { echo "ERROR: .env missing"; exit 1; }
[ -f firebase-config.json ] || { echo "ERROR: firebase-config.json missing"; exit 1; }

set_image() {
  sed -i '/^API_IMAGE=/d;/^IMAGE_TAG=/d' .env
  printf 'API_IMAGE=%s\nIMAGE_TAG=%s\n' "$1" "$2" >> .env
}

PREV_IMAGE=$(grep -oP '^API_IMAGE=\K.*' .env || true)
PREV_TAG=$(grep -oP '^IMAGE_TAG=\K.*' .env || true)
echo "Deploying $TAG (previous: ${PREV_TAG:-none})"

echo "==> Pre-deploy database backup"
/opt/cocorra/infra/backup.sh

echo "$GH_TOKEN" | docker login ghcr.io -u "$GH_ACTOR" --password-stdin
trap 'docker logout ghcr.io >/dev/null 2>&1 || true' EXIT

echo "==> Pulling new image"
set_image "$IMAGE" "$TAG"
docker compose pull

echo "==> Starting new container"
docker compose up -d

echo "==> Waiting for health check"
for i in $(seq 1 30); do
  STATUS=$(docker inspect -f '{{.State.Health.Status}}' cocorra-api 2>/dev/null || echo unknown)
  if [ "$STATUS" = "healthy" ]; then
    echo "==> Deploy OK: $TAG"
    docker image prune -af --filter "until=168h" >/dev/null || true
    exit 0
  fi
  sleep 5
done

echo "==> Health check FAILED. Last logs:"
docker logs cocorra-api --tail 80 || true

if [ -n "$PREV_TAG" ]; then
  echo "==> Rolling back to $PREV_TAG"
  set_image "$PREV_IMAGE" "$PREV_TAG"
  docker compose pull
  docker compose up -d
else
  echo "==> No previous version to roll back to."
fi
exit 1