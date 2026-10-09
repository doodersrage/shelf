# Shelf

A personal library on .NET 10. It tracks books you want, are reading, have finished, or have set aside, along with tags, notes, loans, reading progress, and quotes.

## Run

```bash
dotnet run --project Shelf.AppHost --launch-profile http
```

That starts the API and a dashboard with its logs, traces, and the SQLite file. The dashboard address is printed when the AppHost starts. The `http` profile is there because the local HTTPS development certificate is not fully trusted. The `https` profile is the one to use once `dotnet dev-certs https --trust` succeeds.

To run the API on its own, use `dotnet run --project src/Shelf.Api` and open [http://localhost:5041](http://localhost:5041).

| URL | What you get |
| --- | --- |
| `/` | The library. Search, filter by status or tag, sort, add a book, or change its status. |
| `/library/{id}` | One book. Edit it, track the current page, keep quotes, or delete it. |
| `/stats` | Counts, pages read, books finished this year, and tags. |
| `/books` | The library as JSON. Filter with `q`, `status`, `tag`, and `sort` (`title`, `author`, `year`, `rating`, `added`). |
| `/books/{id}` | One book as JSON, including its tags and quotes. |
| `/books/stats` | The same summary as JSON. |
| `/openapi/v1.json` | The OpenAPI document, in Development. |

Putting a book into Reading or Finished fills a blank start date, and Finished also fills a blank finish date. Tags are stored in lowercase, and an ISBN can be typed with or without hyphens. Deleting the last book that uses a tag removes that tag.

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
