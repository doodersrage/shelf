#!/bin/sh
# Starts Shelf as an ordinary user. Started as root (the default), it first makes /data that user's, which a folder
# mounted from the host (Unraid's appdata, a NAS share) often is not, then drops to PUID:PGID (1654, the image's
# own app user, unless set). Started with --user, it runs as that user and changes nothing.
set -e

if [ "$(id -u)" != "0" ]; then
    exec "$@"
fi

uid="${PUID:-1654}"
gid="${PGID:-1654}"

mkdir -p /data
# When /data, or anything in its top two levels (the database, the keys, the folders of books), is someone else's,
# as files copied in by root are; a library already right is not walked on every start.
if [ "$(stat -c '%u:%g' /data)" != "$uid:$gid" ] \
    || [ -n "$(find /data -maxdepth 2 \( ! -user "$uid" -o ! -group "$gid" \) -print -quit 2>/dev/null)" ]; then
    echo "Making /data belong to $uid:$gid."
    chown -R "$uid:$gid" /data || echo "Could not change who owns everything in /data; continuing."
fi

# A folder chown cannot fix: read-only, a network share that ignores owners, or SELinux without :z on the mount.
if ! setpriv --reuid="$uid" --regid="$gid" --clear-groups sh -c 'probe=/data/.shelf-write-check; : > "$probe" && rm -f "$probe"' 2>/dev/null; then
    echo "Shelf cannot write to /data as $uid:$gid. Check the mount is not read-only (:ro), add :z to it on SELinux hosts" \
        "(Fedora, RHEL), and keep the database on a local disk rather than a network share."
    exit 1
fi

# A home the user can write to, wherever its id came from.
if [ "$uid" != "1654" ]; then
    export HOME=/tmp
fi

exec setpriv --reuid="$uid" --regid="$gid" --clear-groups "$@"
