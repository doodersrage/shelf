---
title: Backups
description: Nightly copies, full backups with files, restoring, an admin's snapshot of the whole server, and health checks.
---

# Backups

Shelf keeps three kinds of copy, from quick to complete.

## Nightly copies

Each night after 03:00 UTC, Shelf saves a copy of its database in a `backups` folder beside it (`/data/backups` in Docker), keeping the last seven. An admin sees them on **Readers**, can download any of them, and can take one at once with **Back up now**.

E-books and audiobooks never change once saved, so they are left out of the nightly copy by default; back up their folders alongside, or set `Backup:IncludeFiles` to `true`. Cover pictures are small and always included. [Configuration](configuration.md#nightly-backups) has the settings.

### Off the server

A backup on the same disk as the shelf does not help if that disk fails. Set `Backup:CopyTo` to a folder on another disk or a mounted network share, or the `Backup:S3` settings to a bucket in S3-compatible storage (Amazon S3, Backblaze B2, Wasabi, Cloudflare R2, MinIO), and each night's backup is copied there too, keeping as many as the backups folder. **Readers** shows where copies go and whether the last one arrived; a copy that fails never stops the backup itself. See [Configuration](configuration.md#nightly-backups).

## A reader's full backup

On **Backup & restore**, **Download everything** gives a zip of your shelf: the catalog, notes, reviews, quotes, reading log, highlights, e-books, audiobooks, and cover pictures. **download JSON** gives just the catalog, without files.

To restore one, here or on another shelf, upload it on the same page. Books already on the shelf are skipped, so restoring twice does no harm.

Your quotes, highlights, notes, and reviews also download as Markdown, one file a book, for a notes app.

## An admin's snapshot

On **Readers**, **Download a snapshot** gives the whole server: the database, every e-book, audiobook, and cover picture, for every reader. It's the one to take before a big update or a move.

To restore a snapshot:

1. Stop the shelf.
2. Unpack the zip where the data lives: `shelf.db`, `ebooks`, `audio`, and `covers` (in Docker, the `/data` volume).
3. Start the shelf again.

Readers sign in again afterwards unless the old `keys` folder is put back too.

Shelf's tests carry out this restore on every change: they fill a shelf, download a snapshot, unpack it into an empty folder, start a second shelf on it, and check that every reader, book, file, cover, highlight, quote, and reading session came back, both with the `keys` folder and without it.

> [!WARNING]
> The `keys` folder seals sign-ins and two-step secrets. Keep a copy of it somewhere private. Restored without it, everyone signs in again, and a reader with two-step sign-in needs a recovery code, a passkey, or an admin's new password to get in.

## Health checks

`/health` answers `Healthy` when the app and its database are up, and `/alive` when the app is. Both answer without signing in, with a single word. The Docker image checks `/alive` itself, and they suit an uptime monitor too.
