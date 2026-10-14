---
title: Backups
description: Nightly copies, full backups with files, restoring, an admin's snapshot of the whole server, and health checks.
---

# Backups

Shelf keeps three kinds of copy, from quick to complete.

## Nightly copies

Each night after 03:00 UTC, Shelf saves a copy of its database in a `backups` folder beside it (`/data/backups` in Docker), keeping the last seven. An admin sees them on **Readers**, can download any of them, and can take one at once with **Back up now**.

Each one is proved as soon as it's taken, before it's sent anywhere: every entry is read back against its checksum, the database inside is opened on its own and passes SQLite's integrity check, and a copy is brought up to the running version, as a restore would have to. **Readers** shows what each would restore (how many books and readers) or why it wouldn't, and a backup taken before 2.10 can be checked there. **Tidy up** shows every reader the newest backup proved this way.

E-books and audiobooks never change once saved, so they are left out of the nightly copy by default; back up their folders alongside, or set `Backup:IncludeFiles` to `true`. Cover pictures are small and always included. [Configuration](configuration.md#nightly-backups) has the settings.

### Off the server

A backup on the same disk as the shelf does not help if that disk fails. Set `Backup:CopyTo` to a folder on another disk or a mounted network share, or the `Backup:S3` settings to a bucket in S3-compatible storage (Amazon S3, Backblaze B2, Wasabi, Cloudflare R2, MinIO), and each night's backup is copied there too, keeping as many as the backups folder. **Readers** shows where copies go and whether the last one arrived; a copy that fails never stops the backup itself. See [Configuration](configuration.md#nightly-backups).

## A reader's full backup

On **Backup & restore**, **Download everything** gives a zip of your shelf: the catalog, notes, reviews, quotes, reading log, highlights, e-books, audiobooks, and cover pictures. **download JSON** gives just the catalog, without files.

The zip also carries your records in plainer forms, with a `README.md` saying what each file is: `books.csv`, a row a book, for a spreadsheet; `collections.json`, with the note on each book; `loans.json`, every loan of your books; and `borrowed-highlights.json`, what you marked in books others lent you. Only your own records are in it. Each reader on a shelf downloads their own, and nothing of another reader's is ever in it.

Every file in it is listed in `manifest.json` with its size and SHA-256, and `SCHEMA.md` says what each file holds, field by field, so the download can be checked and read without Shelf. Restoring one holds it against its manifest and says if any file was damaged or changed since.

To restore one, here or on another shelf, upload it on the same page. Books already on the shelf are skipped, so restoring twice does no harm.

Your quotes, highlights, notes, and reviews also download as Markdown, one file a book, for a notes app.

## Bringing back one book

A book deleted longer ago than the trash keeps things can come back from a nightly copy without rolling back anything else. On **Backup & restore**, under **Bring back a book from a nightly backup**, choose a backup, search for the title or author if you like, and **Look**: the books of yours in it that aren't on your shelf now are listed. **Bring back** restores one with its notes, reading log, quotes, highlights, and tags, under its old id when it's free; its files come from the shelf if they're still there, or from the backup if it has them. Every reader can look for their own books this way, never anyone else's.

This works with a shelf kept in SQLite, whose backups hold the database itself.

## Checking files

Once a week (`FileCheck:Days`), Shelf reads every stored e-book, audiobook, cover, and picture of a copy through and compares it with its SHA-256 from when it was first seen. Stored files are never written again, so one whose bytes differ has been damaged. A file a book names that isn't there, or can't be read, is noted too. Each reader sees their own books' problems under **Tidy up**, **Files**. The first check runs ten minutes after the shelf starts, and an admin can start one at any time.

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
