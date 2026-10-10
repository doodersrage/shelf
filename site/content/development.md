---
title: Development
description: Build Shelf from source, run its tests, change the database, follow the design system, and release a version.
---

# Development

Shelf is a .NET 10 app: ASP.NET Core minimal APIs and Blazor pages over EF Core and SQLite, with a little plain JavaScript for the readers and the player. Contributions are welcome on [GitHub](https://github.com/doodersrage/shelf).

## The pieces

| Folder | What it holds |
| --- | --- |
| `src/Shelf.Api` | The app: `Books` (the library, files, reading, lending, imports), `Readers` (accounts, sign-in, passkeys, the activity log), `Components` (the Blazor pages), `Data` (the database and migrations), and `wwwroot` (styles, scripts, fonts, and the vendored PDF.js and JSZip). |
| `src/Shelf.ServiceDefaults` | Health checks, telemetry, and resilience, shared with the app host. |
| `Shelf.AppHost` | An Aspire host that runs the app with a dashboard. |
| `tests/Shelf.Api.Tests` | Server tests with xUnit, against the real app on a throwaway database. |
| `tests/e2e` | Browser tests with Playwright. |
| `site` | This documentation. |

## Running it

```bash
dotnet run --project src/Shelf.Api                      # http://localhost:5041
dotnet run --project Shelf.AppHost --launch-profile http  # with the Aspire dashboard
```

The first run in Development adds one sample book, which goes to the first account. `src/Shelf.Api/Shelf.Api.http` has requests to try against it.

## Tests

```bash
dotnet test
```

The browser tests start Shelf on an empty data folder and drive it in Chromium: lending, passkeys, two-step sign-in, uploads, imports, search, both readers, OCR, read aloud, the player, comics, offline reading with highlights, and an accessibility audit of every page in both themes.

```bash
cd tests/e2e
npm ci
npx playwright install chromium   # or set CHROMIUM_PATH to a Chromium already installed
npx playwright test
```

CI runs both on every push and pull request, along with a check that the vendored front-end files match their pinned versions and a Docker build.

## Changing the database

After changing the model in `ShelfDb`, add a migration named for the change:

```bash
dotnet tool restore
dotnet ef migrations add AddSomething --project src/Shelf.Api
```

Shelf applies migrations when it starts. `dotnet ef database update --project src/Shelf.Api` applies them without starting it.

## The design system

[`docs/design-system.md`](https://github.com/doodersrage/shelf/blob/main/docs/design-system.md) describes the look: color, type, and spacing tokens, the self-hosted Inter and Lora fonts, navigation, and the rules for covers, buttons, status labels, empty states, and dark mode. The tokens at the top of `wwwroot/app.css` are the only place colors, type, and spacing are set; this site uses the same ones.

## Dependencies

Dependabot proposes updates to the NuGet packages, the GitHub Actions, the Docker images, and the npm packages for the vendored files, the browser tests, and this site. PDF.js, JSZip, and the fonts are copied into `wwwroot` from pinned npm packages; [`docs/maintenance.md`](https://github.com/doodersrage/shelf/blob/main/docs/maintenance.md) covers updating them.

## Releasing

Shelf follows [semantic versioning](https://semver.org). The version is set once, in `Directory.Build.props`, and the build stamps the commit beside it.

1. Bump `VersionPrefix` in `Directory.Build.props`.
2. Add a section for the version to `CHANGELOG.md`.
3. Commit, push, and tag:

```bash
git tag v1.3.0 && git push origin v1.3.0
```

The release workflow checks the tag against `Directory.Build.props`, runs the tests, publishes `ghcr.io/doodersrage/shelf:1.3.0` and `:latest`, and makes a GitHub release from the changelog entry.

## This documentation

The site is built from Markdown in `site/content`, with the changelog taken from `CHANGELOG.md`:

```bash
cd site
npm ci
npm run build        # into site/_site
npm run serve        # build, then serve it at http://localhost:8000
```

A workflow publishes it to GitHub Pages on every push to `main` that changes it.
