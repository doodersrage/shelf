# Shelf

A personal library on .NET 10. It keeps the catalog, the reading log, the quotes, loans, and a yearly goal, looks up an ISBN, suggests one book from the want list, and can back the whole shelf up as JSON. Each reader signs in to a shelf of their own and can lend books to the other readers.

## Run

```bash
dotnet run --project Shelf.AppHost --launch-profile http
```

That starts the API and a dashboard with its logs, traces, and the SQLite file. The dashboard address is printed when the AppHost starts. The `http` profile is there because the local HTTPS development certificate is not fully trusted. The `https` profile is the one to use once `dotnet dev-certs https --trust` succeeds.

To run the API on its own, use `dotnet run --project src/Shelf.Api` and open [http://localhost:5041](http://localhost:5041).

## Readers

Every page and API call needs a signed-in reader. The first visit goes to `/signin`, and `/signup` makes an account with a name and a password of at least eight characters. The first account keeps the books and the yearly goal that were on the shelf before there were accounts. Each later reader starts with an empty shelf, and no reader sees another's books, quotes, highlights, or reading log.

A book can be lent to another reader from its page. It stays on the owner's shelf as a loan to that reader, and shows under Borrowed on the borrower's Loans page until either of them marks it returned. Typing a different name into the book's Loaned to field turns it back into an ordinary loan.

Set `Accounts:AllowSignUp` to `false` to stop new accounts once the first one exists. Sign-in and sign-up allow ten attempts a minute from one address, which `Accounts:SignInsPerMinute` changes.

| URL | What you get |
| --- | --- |
| `/signin`, `/signup` | Sign in, or make an account. |
| `/account` | Change the password. |
| `/` | The library. Search, filter, sort, add a book, or change its status. Covers line the top, anything being read is listed first, and want-list books can be marked to read next. An uploaded EPUB, PDF, or audiobook can be opened from its card. |
| `/library/{id}` | One book. Edit the catalog, log a reading session, keep quotes, note how the copy arrived and what condition it is in, upload an EPUB, PDF, or audiobook, open the next volume, or delete it. |
| `/library/{id}/read` | Read that book's EPUB or PDF. In an EPUB, select a passage to highlight it and leave a note. |
| `/library/{id}/listen` | Play that book's audiobook. A zip of tracks becomes the track list, and playback resumes where it stopped. |
| `/sync` | Trade e-books and audiobooks with another shelf. The furthest stopping place, and notes on a passage, come along. Make a key here for the other shelf, and enter the key it made for you. |
| `/quotes` | Every quote, with the book it came from. Search the words, the title, or the author. |
| `/authors` | Every author, with how many of their books are on the shelf. |
| `/series` | Each series, in reading order. |
| `/places` | Where the books sit. |
| `/copies` | Copies grouped by condition, from fine to poor. |
| `/recommenders` | Who suggested the books. |
| `/years` | Finished books, grouped by the year they were finished. |
| `/loans` | Who currently has a book, which loans are overdue, and the books other readers have lent you. |
| `/stats` | Counts, the yearly goal, a reading streak, a month of reading, books finished this year, recent sessions, tags, and a JSON backup. |
| `/books` | The library as JSON. Filter with `q`, `status`, `tag`, `author`, `series`, `place`, `recommendedBy`, `loanedTo`, `loved`, `loaned`, `format`, and `sort` (`title`, `author`, `series`, `year`, `rating`, `added`). |
| `/books/{id}` | One book as JSON, including tags, quotes, and sessions. |
| `/books/export` | The shelf as a JSON backup. `POST /books/import` restores one, skipping books already on the shelf. |
| `/books/lookup?isbn=` | Title, author, year, pages, publisher, language, and cover from Open Library. `?title=` and `?author=` look the book up without an ISBN. `POST /books/{id}/enrich` fills the empty catalog fields. |
| `/books/pick` | One book from the want list for today. `?status=Reading` picks from another status. |
| `/books/authors` | Authors and how many of their books are on the shelf. |
| `/books/series` | Each series, in reading order. |
| `/books/places` | Where the books sit. |
| `/books/copies` | Copies grouped by condition, from fine to poor. |
| `/books/recommenders` | Who suggested the books. |
| `/books/calendar` | The days with a reading session. `?year=` and `?month=` pick another month. |
| `/books/years` | Finished books, grouped by the year they were finished. |
| `/books/loans` | Who currently has a book, and how many of those loans are overdue. |
| `/books/{id}/return` | Clears a loan. |
| `/books/{id}/lend` | `POST {"readerId": 2, "dueOn": "2026-11-01"}` lends the book to another reader. `/readers` lists the other readers. |
| `/books/borrowed` | The books other readers have lent you. `POST /books/borrowed/{id}/return` gives one back. |
| `/books/{id}/ebook` | `POST` an EPUB or PDF (up to 80 MB). `DELETE` removes it. `/books/{id}/ebook/file` returns the file, and an EPUB's chapters are at `/books/{id}/ebook/chapters/{index}`. |
| `/books/{id}/audio` | `POST` an audio file or a zip of tracks (up to 1 GB): mp3, m4a, m4b, aac, ogg, opus, wav, or flac. `DELETE` removes it. `/books/{id}/audio/tracks/{index}` returns one track. |
| `/books/sync` | The e-books and audiobooks another shelf can take, with the place each one stopped. `GET /books/sync/{key}/ebook` and `GET /books/sync/{key}/audio` return a file. `PUT /books/sync/{key}/progress` keeps the furthest place. Another shelf calls these with `Authorization: Bearer <key>`, using a key made on `/sync`. A key reaches only these sync calls, and only its own reader's books. |
| `/books/stats` | The same summary as JSON. |
| `/settings` | The yearly goal, as JSON. |
| `/openapi/v1.json` | The OpenAPI document, in Development. |

Putting a book into Reading or Finished fills a blank start date, and Finished also fills a blank finish date. A translation can keep its original title. An inscription is the note written in the front of a copy. A copy can be fine, good, fair, or poor. An EPUB or PDF uploaded for a book stays with that copy and opens in the reader. In an EPUB, a selected passage can be highlighted and kept with a note. An audiobook can be one recording or a zip of tracks, and the player remembers the place it stopped. The files themselves are not part of the JSON backup. Two shelves can trade those files from Devices: the furthest place is kept, and a passage note comes with the e-book. Each side needs the key the other one made, and the key goes over the wire with every request, so use an `https://` address for a shelf outside your own network. Tags are stored in lowercase, and an ISBN can be typed with or without hyphens. Deleting the last book that uses a tag removes that tag.

The app applies EF Core migrations on startup. That creates `shelf.db` next to the project, and the first Development run adds one sample book, which goes to the first account. Sign-in cookies are protected with ASP.NET Core Data Protection, whose keys are kept in the user profile by default. `src/Shelf.Api/Shelf.Api.http` has requests for creating, updating, quoting, lending, and deleting books. They need the `shelf` sign-in cookie from a browser.

After changing the model in `ShelfDb`, add a migration whose name describes that change. For the library expansion, that was:

```bash
dotnet tool restore
dotnet ef migrations add ExpandLibrary --project src/Shelf.Api
```

The next run applies the new migration. `dotnet ef database update --project src/Shelf.Api` applies it without starting the site.

## Tests

```bash
dotnet test
```

## Arch Linux

The `dotnet-sdk` package does not include ASP.NET Core. Install it before building:

```bash
sudo pacman -S aspnet-runtime aspnet-targeting-pack
```
