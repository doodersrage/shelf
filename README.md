# Shelf

A small personal library on .NET 10. It tracks books you want, are reading, or have finished.

## Run

```bash
dotnet run --project Shelf.AppHost --launch-profile http
```

That starts the API and a dashboard with its logs, traces, and the SQLite file. The dashboard address is printed when the AppHost starts. The `http` profile is there because the local HTTPS development certificate is not fully trusted. The `https` profile is the one to use once `dotnet dev-certs https --trust` succeeds.

To run the API on its own, use `dotnet run --project src/Shelf.Api` and open [http://localhost:5041](http://localhost:5041).

| URL | What you get |
| --- | --- |
| `/` | The book list, as a Blazor page. Add a book or change its status there. |
| `/books` | The same list as JSON |
| `/openapi/v1.json` | The OpenAPI document, in Development |

The app applies EF Core migrations on startup. That creates `shelf.db` next to the project, and the first Development run adds one sample book. `src/Shelf.Api/Shelf.Api.http` has requests for creating, updating, and deleting books.

After changing the model in `ShelfDb`, add a migration whose name describes that change. For a new year column, that would be:

```bash
dotnet tool restore
dotnet ef migrations add AddYear --project src/Shelf.Api
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
