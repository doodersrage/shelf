# Changelog

Every release of Shelf, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [semantic versioning](https://semver.org).

## [Unreleased]

## [1.0.0] - 2026-10-09

The first versioned release.

### The library

- A catalog with series, translators, original titles, inscriptions, places, condition, how a copy arrived, who recommended it, ratings, reviews, notes, and tags.
- Covers or a compact list, with search, status, sort, and filters; the choice of view is kept for each reader.
- Reading now, read next, and recently finished lead the library.
- A book's page leads with its cover and one next step, then the reading log, lending, quotes, highlights, files, and details.
- Change many books at once: status, tags, loved, or delete.
- Catalog details filled from Open Library by ISBN, or by title and author.
- Import a library from a Goodreads or StoryGraph CSV export.

### Reading

- Read EPUBs and PDFs in the app, back where you stopped, at your own text size; EPUBs also take line spacing and width.
- Highlight a passage and keep a note with it, in an EPUB or a PDF.
- Scanned PDFs are read with OCR (Tesseract and Poppler), so their words can be selected and highlighted.
- Search the words inside your books.
- Play audiobooks, from one file or a zip of tracks, with a speed kept for each reader and a sleep timer.
- A reading log, a yearly goal, a monthly chart, a calendar of reading days, and a streak.
- Quotes and highlights gathered on one page.

### Readers and lending

- Accounts, each with a shelf of their own; the first reader keeps the books from before there were accounts and is the admin.
- Lend to another reader, who can read and listen with a place and notes of their own; ask to borrow from a reader's open shelf.
- Reminders in the app, and by email when a mail server is set, for overdue loans, books due soon, and waiting asks.
- Password resets by email, by an admin, or from the command line.

### Devices and backups

- Trade e-books and audiobooks with another shelf using a device key, keeping the furthest place and passage notes.
- An OPDS catalog for e-reader apps such as KOReader.
- Full backups as a zip with every file, JSON backups, and an admin's snapshot of the whole server.

### Running it

- A Docker image with OCR included, keeping everything under `/data`.
- Sign-in keys kept beside the database, HTTPS redirects, and support for a reverse proxy.
- A design system with light and dark themes, self-hosted fonts, an app icon, and a web app manifest.
- CI on every push, and releases published from version tags.

[Unreleased]: https://github.com/doodersrage/shelf/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/doodersrage/shelf/releases/tag/v1.0.0
