#!/usr/bin/env bash
# Starts a built Shelf image and checks that it answers, and that every script and stylesheet the sign-in page
# links to is served. A page whose _framework/blazor.web.js is missing still renders, but none of its buttons
# work, and only a running image shows it. Usage: smoke-image.sh <image> [platform, such as linux/arm64]
set -euo pipefail
image="$1"
name="shelf-smoke-$$"
# /data is a folder from the host, as Unraid, TrueNAS, and most NAS setups give it, owned by someone else: the image
# has to make it its own before Shelf can write there.
data="$(mktemp -d)"
docker run -d --name "$name" ${2:+--platform "$2"} -p 18080:8080 -v "$data:/data" "$image" > /dev/null
trap 'docker logs "$name" > smoke.log 2>&1 || true; docker rm -f "$name" > /dev/null 2>&1 || true; sudo rm -rf "$data" 2>/dev/null || rm -rf "$data"' EXIT

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
# The folder is now the container user's alone, so the runner looks in as root.
owner="$(sudo stat -c '%u' "$data/shelf.db" 2>/dev/null || echo none)"
echo "shelf.db in the host folder, owned by $owner"
[ "$owner" = 1654 ] || failed=1

# A shelf copied in by root, into a folder that is already the container's: the files inside are taken over too.
docker stop "$name" > /dev/null
sudo chown root:root "$data/shelf.db"
sudo chmod 600 "$data/shelf.db"
docker start "$name" > /dev/null
for _ in $(seq 1 150); do
  if curl -fsS http://localhost:18080/alive > /dev/null 2>&1; then break; fi
  sleep 2
done
if curl -fsS http://localhost:18080/alive > /dev/null; then
  echo "A shelf copied in by root opens"
else
  echo "A shelf copied in by root does not open" >&2
  failed=1
fi

# A Docker volume copied whole, its files one folder down: Shelf says where they are and stops.
nested="$(mktemp -d)"
sudo mkdir "$nested/_data"
sudo cp "$data/shelf.db" "$nested/_data/shelf.db"
docker run --rm ${2:+--platform "$2"} -v "$nested:/data" "$image" > nested.log 2>&1 && nested_status=0 || nested_status=$?
sudo rm -rf "$nested" 2>/dev/null || true
if [ "$nested_status" != 0 ] && grep -q "_data" nested.log; then
  echo "A volume copied with its _data folder is pointed out"
else
  echo "A volume copied with its _data folder was not pointed out (exit $nested_status):" >&2
  cat nested.log >&2
  failed=1
fi
exit "$failed"
