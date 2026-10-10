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
# Only when the top of /data is someone else's, so a large library is not walked on every start.
if [ "$(stat -c '%u:%g' /data)" != "$uid:$gid" ]; then
    echo "Making /data belong to $uid:$gid."
    chown -R "$uid:$gid" /data
fi

# A home the user can write to, wherever its id came from.
if [ "$uid" != "1654" ]; then
    export HOME=/tmp
fi

exec setpriv --reuid="$uid" --regid="$gid" --clear-groups "$@"
