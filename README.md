# Shelf

A small personal library on .NET 10. It tracks books you want, are reading, or have finished.

## Run

```bash
dotnet run --project src/Shelf.Api
```

Open [http://localhost:5041](http://localhost:5041).

| URL | What you get |
| --- | --- |
| `/` | The book list, as a Blazor page. Add a book or change its status there. |
| `/books` | The same list as JSON |
| `/openapi/v1.json` | The OpenAPI document, in Development |

The first Development run creates `shelf.db` next to the project and adds one sample book. `src/Shelf.Api/Shelf.Api.http` has requests for creating, updating, and deleting books.

## Tests

```bash
dotnet test
```

## Arch Linux

The `dotnet-sdk` package does not include ASP.NET Core. Install it before building:

```bash
sudo pacman -S aspnet-runtime aspnet-targeting-pack
```
