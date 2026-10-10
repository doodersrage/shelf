# Changelog

Every release of Shelf, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [semantic versioning](https://semver.org).

## [Unreleased]

### Added

- API tokens, for scripts and home dashboards such as Home Assistant: make one on Account and send it as `Authorization: Bearer shelf_…`. A token opens the books API, never the account or the admin pages, and can be read-only. The API docs have a Home Assistant sensor to start from.
- Series alerts: turn them on under Series, and once a day Shelf asks Open Library about the series you are reading. A newer book that is not on your shelf shows under New in your series, with a count beside Series in the sidebar, and goes to the want list in one click.
- Share a reading list: a saved search can be shared by link with anyone, signed in or not. The page shows the books' covers, authors, years, series, and your stars, never notes, reviews, or loans; Stop sharing ends the link.

## [1.4.1] - 2026-10-10

### Added

- Templates for Unraid, TrueNAS SCALE, and CasaOS in `deploy/`, and a section in the install guide for each.

### Changed

- The Docker image starts as root just long enough to make `/data` belong to the user Shelf runs as, then switches to it: `PUID` and `PGID`, 1654 unless set. A folder from the host now works as `/data` without a `chown`. Starting with `--user` works as before.

### Fixed

- A page whose live connection cannot start, because an extension or a privacy setting blocks it, now says so in a banner with the usual causes, instead of looking fine while its buttons do nothing. Shelf starts Blazor itself to watch for this, and marks the connection's state on the page as `data-live`.
- A browser that refuses an audiobook's lock-screen details no longer leaves the player's buttons dead.
- The library's Reading now and Read next show at most eight books, the latest started first, with a link to the rest. With hundreds marked as reading (after a Goodreads import, say), the page had grown to many times its size.
- After a restore without the `keys` folder, a reader with two-step sign-in can get in with a recovery code, and turn two-step off with another, as the docs say. Before, the code was refused whenever the authenticator secret could not be unsealed.

## [1.4.0] - 2026-10-10

### Added

- Scan the barcode: on the book form, a phone's camera reads the ISBN from the back of a book and looks it up; over plain http it takes a photo instead. The barcode is read in the browser, by its own reader or by ZXing served from the shelf.
- Lock-screen controls for audiobooks: a phone's lock screen, notifications, headphones, and car show the chapter, the book, its author, and cover, with play and pause, skips of 15 and 30 seconds, scrubbing, and previous and next chapter.
- An import folder: set `Import:Folder`, and books dropped into a reader's folder inside it are added on their own, once they stop changing. A folder of tracks is one audiobook; added files move into `.imported`, and ones Shelf cannot take into `.not-added`.
- Single sign-on through an OpenID Connect provider such as Authentik, Authelia, Keycloak, or Pocket ID (`Oidc:*`). Readers already here connect from Account; new people get an account while sign-ups are open. Readers are matched by the provider's subject, never by email.
- Bring books from a Calibre library on the server: each book's best file, cover, series, tags, publisher, rating, ISBN, language, year, and description, read straight from `metadata.db` without changing it. Books with no usable file come in as catalog entries.
- Send to Kindle: with a mail server set up, a reader saves their Kindle's address on Account, and an EPUB or PDF of their own goes to it by email from the book's page.
- Off-site backups: each night's backup can be copied to a second folder (`Backup:CopyTo`) and to S3-compatible storage such as Amazon S3, Backblaze B2, Wasabi, Cloudflare R2, or MinIO (`Backup:S3:*`), keeping as many as the backups folder. Readers shows how the last copy went.
- Highlights from KOReader: upload the JSON file from KOReader's Export highlights on Devices, and each highlight lands in its chapter (or page) with its note. Books are matched by title or file name, and highlights already here are skipped.
- Saved searches: a filtered and sorted library view can be saved under a name, and opens again from the sidebar. Sort and the More filters checkboxes are now part of the library's address, so any view can be bookmarked too.
- Google Books as a second source for Look up and Fill empty details: asked when Open Library has no match or leaves the pages, publisher, year, or cover blank (`Lookup:GoogleBooks`, with an optional `Lookup:GoogleBooksKey`).
- Docker images for arm64 as well as amd64: a Raspberry Pi 4 or 5, most NAS boxes, and Apple silicon. CI starts the arm64 image too.

### Changed

- PDF.js 6, JSZip 3.10.2, and the Inter and Lora fonts 5.3.0 are served from the shelf; Fido2, MailKit, QRCoder, and OpenTelemetry are updated.

### Fixed

- Each search area on a page (the sidebar's, the library filters', Free books', and Search inside books') has a name of its own for screen readers.

## [1.3.1] - 2026-10-10

### Added

- Shelf is free software under the GNU AGPL-3.0. A Source code link sits beside the version at the foot of every page; `SourceUrl` points it at a changed copy's own source.

### Fixed

- The Docker image could not make any page interactive: it left out `_framework/blazor.web.js`, so buttons such as Connect on the Audiobookshelf import, About this book, Add to shelf, and others handled on the page did nothing. Every image before this one is affected; running from source was not. The image's build now restores with the whole source in place, and CI and releases start the image and check that a page's scripts load before anything is published. After pulling the new image, reload Shelf once in the browser.
- A new release's styles and translations reached a browser that had Shelf open before only once the offline helper itself changed. They now come from the shelf whenever it answers, with the copies on the device kept for offline reading.
- Connecting to an Audiobookshelf address that never answers says it timed out, instead of that it did not answer as Audiobookshelf.
- About this book on Free books could leave the whole page unresponsive when Project Gutenberg or LibriVox was slow: a timeout from the retry layer every outgoing request goes through was not caught, and it ended the page's connection. The same gap is closed for Open Library look-ups, shelf-to-shelf sync, free-book downloads, and the Audiobookshelf import.
- Gutenberg and LibriVox requests may take up to 45 seconds an attempt, and Audiobookshelf up to two minutes, instead of the standard ten, so a busy catalog or a large library still answers.
- About this book works before the page's live connection is up, or without one: it opens the details from the server instead.

## [1.3.0] - 2026-10-10

### Added

- Shelf speaks Spanish, French, and German as well as English. Each reader gets their browser's language or picks one on Account, separately from the region dates follow; emails go in the reader's language. The translations await review by native speakers.
- Free books can be browsed: Gutenberg's most read books and its categories, LibriVox's newest recordings and its genres, a page at a time. About this book shows a book's summary or description, subjects, and more.
- Bring books from Audiobookshelf: audio tracks in order, e-books, covers, series, narrators, genres, and where you stopped, from an API key or a user name and password.
- Cover art: upload a picture of your own for any book, and an audiobook's embedded art becomes its cover when it has none. Kept covers travel in backups.
- Highlights while offline: a kept book brings its highlights, and passages highlighted or removed offline are sent to the shelf when it answers again. Offline PDF pages gain a text layer for selecting words.
- Passkeys: sign in with a phone's or laptop's fingerprint, face, or PIN, or a password manager, instead of the password. A passkey counts as both steps of two-step sign-in. An admin's password reset also removes a reader's passkeys.

### Changed

- Adding books from files moved to `POST /books/import/files`, so it no longer shares an address with restoring a JSON backup at `POST /books/import`.

### Fixed

- A book with no cover keeps whole words on its small spine, and a very long title wraps instead of running out of its card.

## [1.2.0] - 2026-10-10

### Added

- Free books: search Project Gutenberg and LibriVox, and add a public-domain e-book or audiobook to the shelf with its file. Downloads run in the background; `FreeBooks:Enabled` turns it off.
- Add books from files, as many at once as you like: each EPUB, PDF, comic, Kindle file, zip of tracks, or album of audio files becomes a book named from what the file says, and a matching book already on the shelf takes the file instead. `POST /books/import`.
- KOReader progress sync: Shelf answers KOReader's sync plugin at `/kosync`, so a place reached on an e-reader comes back, and the other way round. Devices makes the KOReader password.
- Comics (CBZ) read page by page. Kindle files (MOBI, AZW3) are turned into EPUBs when Calibre is installed; `--build-arg CALIBRE=true` builds it into the Docker image.
- Read aloud: the reader speaks an EPUB or PDF with the browser's voices, going on into the next chapter or page.
- Quotes, highlights, notes, and reviews download as Markdown, a book at a time or all together, for Obsidian or any notes app.
- An activity log of account changes, failed sign-ins, restores, and backups, for the admin on Readers and for each reader on Account.
- Uploading a file that is already on another of your books asks first: keep both, or don't add it. `keepBoth=true` skips the question.
- Adding a book that another reader has on an open shelf offers to ask to borrow it instead, and a book's Files panel does the same when an open copy has a file yours lacks.
- An EPUB's own cover, or a comic's first page, is shown for a book with no cover address.

### Changed

- After updating, Shelf looks inside existing e-books once in the background, for their covers and the names KOReader gives them.

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

[Unreleased]: https://github.com/doodersrage/shelf/compare/v1.4.1...HEAD
[1.4.1]: https://github.com/doodersrage/shelf/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/doodersrage/shelf/compare/v1.3.1...v1.4.0
[1.3.1]: https://github.com/doodersrage/shelf/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/doodersrage/shelf/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/doodersrage/shelf/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/doodersrage/shelf/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/doodersrage/shelf/releases/tag/v1.0.0
