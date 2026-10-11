# Shelf

[![CI](https://github.com/doodersrage/shelf/actions/workflows/ci.yml/badge.svg)](https://github.com/doodersrage/shelf/actions/workflows/ci.yml)

**[Read the documentation](https://doodersrage.github.io/shelf/)**: installing, every feature, configuration, and the API.

A personal library you run yourself. Shelf keeps the books you own on paper, your e-books, and your audiobooks together, one book at a time: the catalog, where you stopped in each format, what you highlighted, your reading log and yearly goal, and who has borrowed which copy. Everyone on your server gets a shelf of their own and can lend to the others.

![Home: the book last read with a button back into it, the shelf at a glance, and the other open books](docs/images/library.png)

| | |
| --- | --- |
| ![Every book on the shelf as a grid of covers, with filter chips, stars, and a status menu on each](docs/images/covers.png) | ![A book's page, led by its cover, status, and next step](docs/images/book.png) |
| **The whole shelf** as covers or a compact list, with search, filters, and saved searches. | **A book's page** leads with the cover and one next step, then the reading log, lending, quotes, highlights, and files. |
| ![The e-book reader filling the window, a highlighted passage, and the text settings open from the bar over the book](docs/images/reader.png) | ![The audiobook player with chapters, a bookmark, and a button to continue in the e-book](docs/images/listen.png) |
| **The reader** fills the window with the book, its bars a tap away, and keeps your place, highlights, notes, and text settings. | **The player** plays many tracks as one book, with chapters, bookmarks, speed, and a sleep timer, and keeps playing in a mini player as you browse. |
| ![Insights with the year's goal and books finished each month](docs/images/stats.png) | ![Loans, with a book lent out and another reader asking to borrow](docs/images/loans.png) |
| **Insights** lead with the year's goal, then a year of reading, favourites, and milestones. | **Loans** between readers, and asks to borrow from an open shelf. |
| ![A reading list shared by link, as someone signed out sees it](docs/images/shared.png) | ![Home in dark mode](docs/images/dark.png) |
| **A reading list** shared by link with anyone, showing only the books. | **Dark mode** follows the system, or a choice in Account. |

<p align="center"><img src="docs/images/phone.png" alt="Home on a phone, with the bottom bar" width="300" /></p>

The screenshots show public-domain books, with covers drawn for the demo. `npm run screenshots` in `tests/e2e` makes them again.

## Why Shelf

Good self-hosted book apps already exist, and each is built around one kind of book:

- **[Calibre-Web](https://github.com/janeczku/calibre-web)** is a web front end to a Calibre library. It is the better choice if Calibre is where you edit metadata and convert files. It keeps e-books only, in Calibre's database.
- **[Audiobookshelf](https://www.audiobookshelf.org)** is an audiobook and podcast server with phone apps that download for offline listening. It is the better choice if audiobooks are all you keep. E-books come second there, and paper books not at all; its phone apps can sign in to Shelf too.
- **Goodreads and StoryGraph** keep a reading log and a yearly goal well, but on someone else's server, and they hold none of your files.

Shelf is for a library that is all of these at once, kept on your own server:

- **One book, every format.** A paperback, its EPUB, and its audiobook are the same book, with one reading log. **Continue in the e-book** and **Continue in the audiobook** carry your place across.
- **Paper books count.** Where each copy sits, its condition, who recommended it, an inscription, and loans to friends, with due dates and reminders.
- **Readers lend to each other.** A borrower reads or listens with their own place and highlights, and the owner's notes stay private.
- **Come as you are.** Shelf brings in Calibre libraries, Audiobookshelf servers, Goodreads and StoryGraph exports, folders of files, and free public-domain books. Your own data goes out as JSON, Markdown notes, or one zip with every file.

It has no phone apps of its own: on a phone it is the website, added to the home screen, with offline reading and listening for kept books. For listening, Audiobookshelf's apps sign in to Shelf as to their own server, with downloads, Android Auto, and CarPlay.

## What it does

- **[A real catalog](https://doodersrage.github.io/shelf/library.html):** series, editions, translators, narrators, places, condition, ratings, reviews, and tags. Fill it from an ISBN, typed, scanned with a phone's camera, or shared from another app. Saved searches, hand-made collections, reading lists shared by link, new books in your series, suggestions of what to read next, and duplicates merged in a click.
- **[Reading](https://doodersrage.github.io/shelf/reading.html):** EPUBs, PDFs, comics (manga too), and Kindle files in the browser, scrolled or turned page by page, with highlights, notes, a dictionary, read aloud, a recap when you come back, search inside every book, OCR for scanned PDFs, and offline reading.
- **[Audiobooks](https://doodersrage.github.io/shelf/audiobooks.html):** one file or a folder of tracks played as one book, with chapters, bookmarks, speeds from 0.5× to 3×, a sleep timer, lock-screen controls, and a mini player that keeps going while you browse. Narrator, series, cover, and chapter names from Audible, or chapters by hand; read-along, which marks each word of the e-book as the audiobook reads it; search inside recordings; one M4B from many tracks; podcasts in the same player; and Audiobookshelf's phone apps, with downloads, Android Auto, and CarPlay.
- **[Readers and lending](https://doodersrage.github.io/shelf/lending.html):** a shelf for each reader, children's accounts, loans with due dates, open shelves to ask to borrow from, linked shelves on a friend's server, book clubs, and reminders.
- **[Notifications](https://doodersrage.github.io/shelf/notifications.html):** on your phone or computer, or by ntfy or Gotify, when a book is lent to you or asked for, a loan is due, a series has a new book, or your club talks; and webhooks for Home Assistant, Discord, and Slack.
- **[Bringing books in](https://doodersrage.github.io/shelf/adding.html):** files, an import folder, Calibre, Audiobookshelf, Goodreads, StoryGraph, and Project Gutenberg and LibriVox.
- **[Devices and services](https://doodersrage.github.io/shelf/devices.html):** Kobo e-readers sync with Shelf as with their own store; KOReader downloads from its catalog and keeps its place in step; KOReader's reading statistics count too; Kindles get books by email and their highlights come across; highlights go to and come from Readwise, and reading status both ways with Hardcover.
- **[Security](https://doodersrage.github.io/shelf/security.html):** passkeys, two-step sign-in, single sign-on through OpenID Connect, an activity log, and [API tokens](https://doodersrage.github.io/shelf/api.html#api-tokens) for scripts and Home Assistant.
- **[Backups](https://doodersrage.github.io/shelf/backups.html):** nightly snapshots, copies to another folder or S3-compatible storage, and a restore drill in the tests.
- **Your year:** goals for books, pages, and hours, time spent reading and listening, and a review of each year in books.
- **In four languages:** English, Spanish, French, and German, chosen by each reader. The translations were made by machine and want a native speaker's eye; [docs/translating.md](docs/translating.md) shows how to help.

## Install

With Docker, on amd64 or arm64, keeping everything in one volume:

```bash
docker run -d --name shelf -p 8080:8080 -v shelf-data:/data \
  --restart unless-stopped ghcr.io/doodersrage/shelf:latest
```

Open [http://localhost:8080](http://localhost:8080) and make the first account; it becomes the admin. `docker-compose.yml` has the same with the settings worth knowing, commented out. There are templates for [Unraid, TrueNAS, and CasaOS](deploy/), and the [install guide](https://doodersrage.github.io/shelf/install.html) covers those, running from source, updating, and putting Shelf on the internet safely.

## Development

Shelf is .NET 10: ASP.NET Core with Blazor, EF Core on SQLite, and an Aspire AppHost for local runs.

```bash
dotnet run --project Shelf.AppHost --launch-profile http   # the app, with a dashboard of its logs
dotnet test                                               # the server tests
cd tests/e2e && npm ci && npx playwright test             # the browser tests, in Chromium and Firefox
```

[Development](https://doodersrage.github.io/shelf/development.html) covers the pieces, migrations, the design system, dependencies, and releases. [CHANGELOG.md](CHANGELOG.md) records each release.

## Licence

Shelf is free software under the [GNU Affero General Public License, version 3](LICENSE). You may use, study, share, and change it. If you change it and let other people use your changed copy, over a network as well as by handing it to them, you must offer them its source under the same licence. The **Source code** link at the foot of every page points at this repository; a changed copy sets `SourceUrl` to the address of its own source.

Shelf serves some files written by others, under their own licences: [PDF.js](https://github.com/mozilla/pdf.js) (Apache-2.0), [JSZip](https://github.com/Stuk/jszip) (MIT, chosen from MIT or GPL-3.0), [barcode-detector](https://github.com/Sec-ant/barcode-detector) and [zxing-wasm](https://github.com/Sec-ant/zxing-wasm) (MIT, built from [ZXing-C++](https://github.com/zxing-cpp/zxing-cpp), Apache-2.0), and the [Inter](https://github.com/rsms/inter) and [Lora](https://github.com/cyrealtype/Lora-Cyrillic) fonts (SIL Open Font License 1.1, whose texts are in `wwwroot/fonts`). The .NET packages it uses are under MIT or Apache-2.0 licences.
