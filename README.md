# Shelf

A personal library on .NET 10. It keeps the catalog, the reading log, the quotes, loans, and a yearly goal, looks up an ISBN, suggests one book from the want list, and can back the whole shelf up as JSON.

## Run

```bash
dotnet run --project Shelf.AppHost --launch-profile http
```

That starts the API and a dashboard with its logs, traces, and the SQLite file. The dashboard address is printed when the AppHost starts. The `http` profile is there because the local HTTPS development certificate is not fully trusted. The `https` profile is the one to use once `dotnet dev-certs https --trust` succeeds.

To run the API on its own, use `dotnet run --project src/Shelf.Api` and open [http://localhost:5041](http://localhost:5041).

| URL | What you get |
| --- | --- |
| `/` | The library. Search, filter, sort, add a book, or change its status. Covers line the top, and anything being read is listed first. |
| `/library/{id}` | One book. Edit the catalog, log a reading session, keep quotes, open the next volume, or delete it. |
| `/quotes` | Every quote, with the book it came from. Search the words, the title, or the author. |
| `/authors` | Every author, with how many of their books are on the shelf. |
| `/series` | Each series, in reading order. |
| `/places` | Where the books sit. |
| `/recommenders` | Who suggested the books. |
| `/years` | Finished books, grouped by the year they were finished. |
| `/loans` | Who currently has a book, and which loans are overdue. |
| `/stats` | Counts, the yearly goal, a reading streak, a month of reading, books finished this year, recent sessions, tags, and a JSON backup. |
| `/books` | The library as JSON. Filter with `q`, `status`, `tag`, `author`, `series`, `place`, `recommendedBy`, `loanedTo`, `loved`, `loaned`, `format`, and `sort` (`title`, `author`, `series`, `year`, `rating`, `added`). |
| `/books/{id}` | One book as JSON, including tags, quotes, and sessions. |
| `/books/export` | The shelf as a JSON backup. `POST /books/import` restores one, skipping books already on the shelf. |
| `/books/lookup?isbn=` | Title, author, year, pages, publisher, language, and cover from Open Library. `?title=` and `?author=` look the book up without an ISBN. `POST /books/{id}/enrich` fills the empty catalog fields. |
| `/books/pick` | One book from the want list for today. `?status=Reading` picks from another status. |
| `/books/authors` | Authors and how many of their books are on the shelf. |
| `/books/series` | Each series, in reading order. |
| `/books/places` | Where the books sit. |
| `/books/recommenders` | Who suggested the books. |
| `/books/calendar` | The days with a reading session. `?year=` and `?month=` pick another month. |
| `/books/years` | Finished books, grouped by the year they were finished. |
| `/books/loans` | Who currently has a book, and how many of those loans are overdue. |
| `/books/{id}/return` | Clears a loan. |
| `/books/stats` | The same summary as JSON. |
| `/settings` | The yearly goal, as JSON. |
| `/openapi/v1.json` | The OpenAPI document, in Development. |

Putting a book into Reading or Finished fills a blank start date, and Finished also fills a blank finish date. A translation can keep its original title. An inscription is the note written in the front of a copy. Tags are stored in lowercase, and an ISBN can be typed with or without hyphens. Deleting the last book that uses a tag removes that tag.

The app applies EF Core migrations on startup. That creates `shelf.db` next to the project, and the first Development run adds one sample book. `src/Shelf.Api/Shelf.Api.http` has requests for creating, updating, quoting, and deleting books.

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
