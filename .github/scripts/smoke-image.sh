#!/usr/bin/env bash
# Starts a built Shelf image and checks that it answers, and that every script and stylesheet the sign-in page
# links to is served. A page whose _framework/blazor.web.js is missing still renders, but none of its buttons
# work, and only a running image shows it. Usage: smoke-image.sh <image> [platform, such as linux/arm64]
set -euo pipefail
image="$1"
name="shelf-smoke-$$"
docker run -d --name "$name" ${2:+--platform "$2"} -p 18080:8080 "$image" > /dev/null
trap 'docker logs "$name" > smoke.log 2>&1 || true; docker rm -f "$name" > /dev/null 2>&1 || true' EXIT

# Under emulation, an arm64 image takes a while to start.
for _ in $(seq 1 150); do
  if curl -fsS http://localhost:18080/alive > /dev/null 2>&1; then break; fi
  sleep 2
done
curl -fsS http://localhost:18080/alive > /dev/null

page="$(curl -fsS http://localhost:18080/signin)"
assets="$(grep -oE '(src|href)="[^"#?]+\.(js|mjs|css)"' <<< "$page" | sed -E 's/^(src|href)="//; s/"$//' | sort -u)"
grep -q 'blazor.web' <<< "$assets" || { echo "The sign-in page does not load blazor.web.js." >&2; exit 1; }

failed=0
while read -r asset; do
  status="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:18080/${asset#/}")"
  echo "$status $asset"
  [ "$status" = 200 ] || failed=1
done <<< "$assets"
exit "$failed"
