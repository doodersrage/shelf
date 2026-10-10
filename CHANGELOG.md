# Changelog

Every release of Shelf, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [semantic versioning](https://semver.org).

## [Unreleased]

### Added

- Uploading an e-book or audiobook that is already on another of your books asks first: keep both, or don't add it. `keepBoth=true` on the upload skips the question.
- Adding a book that another reader has on an open shelf offers to ask to borrow it instead, and a book's Files panel does the same when an open copy has a file yours lacks.

## [1.1.0] - 2026-10-10

### Added

- Two-step sign-in with an authenticator app, with ten single-use recovery codes, and a list of signed-in devices that can each be signed out.
- Offline reading: keep an e-book on a device, read it when the shelf cannot be reached, and have the place sent back later. `GET` and `PUT /books/{id}/place` read and move a reader's place.
- Audiobook chapters, read from the marks inside an `.m4b` or `.m4a`, and bookmarks with a note.
- Nightly database backups, keeping the last seven, listed and downloadable from Readers.
- `/health` and `/alive` in every environment, and a health check in the Docker image.
- Dates written the way each reader's region writes them, from the browser or a choice on the account.
- `skip` and `take` on `/books`, with the total in `X-Total-Count`.
- Browser tests in CI, covering every interactive feature and an accessibility audit in both themes.
- Dependabot, and a pinned, checked copy of PDF.js, the fonts, and JSZip.

### Changed

- Search inside books uses a full-text index: places come back best match first, accents and case do not matter, and the last word can be partial.
- The library loads only what it shows and draws sixty books at a time.
- Email goes out through MailKit; `Email:Security` chooses the connection.
- An admin's new password also turns off two-step sign-in. Everyone signs in once more after updating, since sign-ins are now sessions.

### Fixed

- The bulk tag box and the account's email box now count what is typed straight away, instead of once the field loses focus.

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

[Unreleased]: https://github.com/doodersrage/shelf/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/doodersrage/shelf/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/doodersrage/shelf/releases/tag/v1.0.0
