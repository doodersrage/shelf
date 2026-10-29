---
title: Backups
description: Nightly copies, full backups with files, restoring, an admin's snapshot of the whole server, and health checks.
---

# Backups

Shelf keeps three kinds of copy, from quick to complete.

## Nightly copies

Each night after 03:00 UTC, Shelf saves a copy of its database in a `backups` folder beside it (`/data/backups` in Docker), keeping the last seven, and always the newest one that was proved to restore, however many after it weren't. Old copies go only once the new one has been proved. An admin sees them on **Readers**, can download any of them, and can take one at once with **Back up now**.

Each one is proved as soon as it's taken, before it's sent anywhere: every entry is read back against its checksum, the database inside is opened on its own and passes SQLite's integrity check, and a copy is brought up to the running version, as a restore would have to. **Readers** shows what each would restore (how many books and readers) or why it wouldn't, and a backup taken before 2.10 can be checked there. **Tidy up** shows every reader the newest backup proved this way.

Before a new version of Shelf changes the database, it takes one more, named `…-before-upgrade.zip`, with the database as it was; it is listed and kept as the nightly ones are, and **Readers** can check that it restores. It isn't checked on its own at startup, so the update isn't held up. If it can't be taken, Shelf leaves the database alone and stops, saying why (`Backup:BeforeMigrating` turns this off).

E-books and audiobooks never change once saved, so they are left out of the nightly copy by default; back up their folders alongside, or set `Backup:IncludeFiles` to `true`. Cover pictures are small and always included. [Configuration](configuration.md#nightly-backups) has the settings.

### Off the server

A backup on the same disk as the shelf does not help if that disk fails. Set `Backup:CopyTo` to a folder on another disk or a mounted network share, or the `Backup:S3` settings to a bucket in S3-compatible storage (Amazon S3, Backblaze B2, Wasabi, Cloudflare R2, MinIO), and each night's backup is copied there too, keeping as many as the backups folder. **Readers** shows where copies go and whether the last one arrived; a copy that fails never stops the backup itself. Only a backup proved to restore is copied there, so one that wouldn't never pushes out a good copy elsewhere. See [Configuration](configuration.md#nightly-backups).

## A reader's full backup

On **Backup & restore**, **Download everything** gives a zip of your shelf: the catalog, notes, reviews, quotes, reading log, highlights, e-books, audiobooks, and cover pictures. **download JSON** gives just the catalog, without files.

**download as folders** gives your books' files laid out as Calibre lays out its library: a folder for each author, a folder for each of their books named `Title (id)`, and in it every format of the e-book named for the book (the EPUB with Shelf's details written in, as Calibre takes a book's title and authors from its files), the cover, the audiobook's tracks under `audiobook/`, and a `metadata.opf` with the title, authors, series, rating, tags, ISBN, publisher, language, and year. Calibre reads it with **Add books › Add from folders and sub-folders › one book per folder**, and so can other programs that read OPF. It's for moving to other programs, not a backup: notes, reviews, reading history, and highlights stay in **Download everything**, and Shelf doesn't restore from it.

The zip also carries your records in plainer forms, with a `README.md` saying what each file is: `books.csv`, a row a book, for a spreadsheet; `collections.json`, with the note on each book; `loans.json`, every loan of your books; and `borrowed-highlights.json`, what you marked in books others lent you. Only your own records are in it. Each reader on a shelf downloads their own, and nothing of another reader's is ever in it.

Every file in it is listed in `manifest.json` with its size and SHA-256, and `SCHEMA.md` says what each file holds, field by field, so the download can be checked and read without Shelf. Restoring one holds it against its manifest and says if any file was damaged or changed since.

To restore one, here or on another shelf, upload it on the same page. Your collections come back too, with each book's note, their order, and the day to read them by, as do the past loans of your books; a collection of the same name already here gains the books it lacks. Books already on the shelf are skipped, and so are books and loans a collection or book already has, so restoring twice does no harm. What you marked in books you borrowed stays in the download for you, but isn't brought back, as those books are other readers'.

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

## On PostgreSQL

On a shelf kept in [PostgreSQL](configuration.md#postgresql), nightly copies and snapshots hold `shelf.sql`, PostgreSQL's own copy of the database made by `pg_dump`, in place of `shelf.db`. The Docker image doesn't include `pg_dump`, and it must be the same major version as the database server or newer: put one on the server's path, or set `Database__PgDump` to where it is.

Without it, there is no copy of the database to keep. A nightly copy then holds the files and covers with a `NO-DATABASE.txt` saying so, and fails its check, which **Readers** and **Server health** show; **Download a snapshot** is refused rather than given without the database. Backups taken that way before this release held a library as JSON instead, with one reader's books at most and often none; they are now shown as backups that would not restore.

Either way, back the database up with PostgreSQL's own tools as well, for example from the database's own container, whose `pg_dump` always matches it:

```bash
docker exec db pg_dump -U shelf --no-owner shelf > shelf.sql
```

To restore: stop the shelf, load `shelf.sql` into an empty database (`psql -U shelf -d shelf -f shelf.sql`), unpack `ebooks`, `audio`, and `covers` where the data lives, and start the shelf again.

## Health checks

`/health` answers `Healthy` when the app and its database are up, and `/alive` when the app is. Both answer without signing in, with a single word. The Docker image checks `/health` itself, so a shelf that can't reach its database shows as unhealthy, and they suit an uptime monitor too.
