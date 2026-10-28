# Shelf — M0 implementation audit

- **Date:** 2026-10-28
- **Commit audited:** `39f779c` (Shelf 2.17.1), branch `main`
- **Method:** read-only code tracing across five areas (authorization/privacy; backups/migrations/export; progress sync; tests/CI/localization/accessibility; integrations/self-hosting), plus local build and test runs. No product code was changed.
- **Evidence labels:** **Re-verified** means the lead auditor read the cited code directly. **Traced** means an area auditor traced the code path and cited it, but it was not independently re-read. Neither label means a runtime reproduction unless it says so.

## 1. Baseline

### Working tree

`git status --short` before the audit showed only the untracked `CLAUDE.md` and `IMPLEMENTATION_PLAN.md`. Test runs left the tree unchanged. There are no `TODO`/`FIXME`/`HACK` markers in `src/`, `tests/Shelf.Api.Tests`, or `tests/e2e/specs`.

### Repository map

| Part | Contents |
|---|---|
| `Shelf.slnx` | `src/Shelf.Api` (the whole app: API, Blazor UI, background services), `src/Shelf.ServiceDefaults` (resilience, health, telemetry), `Shelf.AppHost` (Aspire, development only), `tests/Shelf.Api.Tests` |
| `src/Shelf.Api/Books/` | 130 feature files (catalog, reader, audio, sync, imports, backups, integrations) |
| `src/Shelf.Api/Readers/` | 20 files: accounts, passkeys, two-step, OIDC, API tokens, family, admin |
| `src/Shelf.Api/Data/` | `ShelfDb` (78 `DbSet`s), 82 SQLite migrations, 18 PostgreSQL migrations (a consolidated `Initial` plus one twin for each later SQLite migration) |
| Tenancy | One shared database. `ShelfDb.ReaderId` (`ShelfDb.cs:36`) feeds per-reader query filters (for example `Book`: `OwnerId == ReaderId`, `ShelfDb.cs:170`). Unfiltered entities rely on explicit predicates. |
| Localization | `Localization/Words.cs` (`T()`/`Say()` keyed on English text) and `es/fr/de.json`; browser strings in `wwwroot/words.js` |
| CI (`.github/workflows/ci.yml`) | `test` (SQLite), `postgres` (Postgres 17), `browser` (Playwright in Chromium and Firefox, with axe), `vendored`, `docker` (amd64 and emulated arm64 smoke tests). None of them uses `continue-on-error`. |

### Commands run

The environment has the .NET SDK 10.0.401 at `~/.dotnet` (not on `PATH`), Node 24, and Playwright 1.63. Docker is installed, but this session's user cannot reach its socket. There is no PostgreSQL server.

| Command | Result |
|---|---|
| `dotnet restore && dotnet build --no-restore -c Release` | **Succeeded.** Warning NU1902: `MessagePack` 2.5.302 has a moderate advisory (GHSA-qhrr-8q5h-9q3h). It enters only through `Shelf.AppHost`, the development-only Aspire host, not the shipped image. |
| `dotnet test --no-build -c Release` (SQLite) | **398 passed, 0 failed, 0 skipped** (1 min 5 s). |
| `npx playwright test --project=chromium` (in `tests/e2e`) | **54 passed, 1 failed** (6.3 min). The failure was `wayfinding.spec.mjs:4`, "quick jump…": the `Keyboard shortcuts` dialog was not visible within 15 s. |
| `npx playwright test specs/wayfinding.spec.mjs --project=chromium --repeat-each=3` | **9 passed.** The failure above is a timing flake under full-suite load, not a product defect. |

**Not run here:**
- **Firefox browser tests.** Playwright 1.63 needs Firefox revision 1543, but only 1538 is cached, and PulseAudio (which the Firefox audio specs need) is not installed.
- **The PostgreSQL test job.** No server is available.
- **The Docker image build and smoke test.** The Docker socket is not accessible.

**Tests that report a pass without testing anything.** Some server tests skip by an early `return;` instead of a declared skip, so they count as passed even though nothing was checked:
- `MoveToPostgresTests.cs:16` returns early when there is no PostgreSQL server.
- `M4bTests.cs:19` returns early when ffmpeg is missing.
- `ShelfLifeTests.cs:274` returns early when ImageMagick is missing.
- Backup/restore tests return early when running on PostgreSQL: `RestoreTests.cs:35`, `FeatureTests.cs:497`, `ShelfCareTests.cs:346`, `TimeMachineTests.cs:344`.

So "0 skipped" overstates the coverage.

## 2. Feature matrix

| Area | Status | Evidence |
|---|---|---|
| Catalog, series, editions, bulk edit, saved searches | Complete | `BooksEndpointTests` (63 tests), `SavedSearchTests`, `library.spec.mjs` (7) |
| One book, many formats | Complete | Single `Book` row holds the paper, e-book and audio places; history per book, not per format (`Book.cs`, `Sessions`, `PastReads`); `CrossoverTests` |
| E-book/PDF/comic reader | Complete | `reading.spec.mjs` (13), `EpubContentsTests`, `ComicTests`, `OcrTests` |
| Audiobook player | Complete | `listening.spec.mjs` (8), `ListeningTests`, `AudioChapterTests` |
| **Progress across devices and sync** | **Partial** | Browser, offline e-book and KOReader paths only move forward (`Lending.cs:278-305`, `Kosync.cs:161-174`; `FeatureTests.cs:918`). Kobo, Audiobookshelf-app, offline-audio and reread paths can move a place backward; see gap 4. |
| Highlights and notes | Complete, with one exception | Borrower separation is tested (`ReadersTests.cs:351`). Child accounts get no highlights or place of their own on shared books (`Lending.cs:137,151,165,228`), traced. |
| Reading journal, goals, year review | Complete (lightly tested) | `ReadingLifeTests`, `YearReviewTests`, `InsightsTests` |
| Lending and asking to borrow | Complete | `ReadersTests.cs:312,330,351`, `ShelfCareTests.cs:139`, `Asking.cs` predicates |
| **Multi-user authorization** | **Partial** | Query filters and about 120 `IgnoreQueryFilters` sites carry explicit ownership predicates (traced). One confirmed defect: Audiobookshelf-app sessions outlive revoked API tokens (gap 3). There is no systematic sweep testing that another reader's ids are refused. |
| Imports | Complete | Added, skipped and failed counts (`ImportJobs.cs:12-67`, `Calibre.cs:180-321`); re-runs are idempotent; refused files go to `.not-added` (`FolderImport.cs:206`) |
| Exports | Partial | Exports hold only the exporting reader's data and no credentials (`PortableExport.cs:27-50`). Restoring a reader's zip ignores `collections.json`, `loans.json` and `borrowed-highlights.json` (traced). |
| **Backups and restore (SQLite)** | Complete | `VACUUM INTO` (`Backup.cs:183`); `.partial` file then rename; drill with CRC, `integrity_check` and a migrated copy (`BackupCheck.cs:84-111`); `RestoreTests.cs:32` restores into a fresh folder and checks records and files |
| **Backups and restore (PostgreSQL)** | **Missing in practice** | Nightly backups can contain zero books yet pass the drill (gap 2, re-verified) |
| Migrations and upgrade | Partial | Applied automatically at startup (`Program.cs:364`) with no backup first. No test upgrades a populated older database. Model drift is guarded: `PendingModelChangesWarning` is not suppressed. |
| Integrations (Readwise, Hardcover, ABS, Kobo, KOReader, Calibre, Gutenberg/LibriVox, Kindle email) | Partial | Each has tests. Failures are collapsed into "nothing found" (`Readwise.cs:43-113`, `OpenLibraryLookup.cs:28-33`, `Federation.cs:113-125`). Readwise and Hardcover record no sync status (traced). |
| **Outbound network safety** | **Partial** | `OutboundGuard` is sound, with connect-time checks and protection against DNS rebinding and redirects. The year-card cover fetch bypasses it (gap 1, re-verified). |
| Background services | Partial | About 23 hosted services. Many catch only narrow exception types, and `BackgroundServiceExceptionBehavior` is never set, so the .NET default `StopHost` applies (gap 5, re-verified). |
| Offline/PWA | Partial | Offline e-book reading is tested (`offline.spec.mjs`, Chromium only by design). Offline audio replay overwrites newer listening (gap 4). |
| Notifications and webhooks | Complete | `NoticeTests`, `WebhookTests` (2). Plausible risk: POSTs are retried, so a webhook can be delivered twice. |
| Accessibility | Partial | axe (WCAG 2.1 AA plus best practices) on about 70 pages, in light and dark themes (`accessibility.spec.mjs`). Not audited: signed-out and public pages (`/signin`, `/shared/{token}`, …), open dialogs and menus, mobile width, and es/fr/de. |
| Localization | Complete | 3,364 keys in each of es, fr and de, none missing or empty. `TranslationTests` enforce key parity, matching placeholders, no unused keys, and no interpolated keys. Dates follow the reader's region (`FeatureTests.cs:707-770`). |
| Self-hosted deployment | Complete | Non-root via `setpriv`, PUID/PGID, write probe (`docker-entrypoint.sh`); no mandatory cloud, CDN or telemetry dependency; CI smoke-tests both architectures. Minor: the HEALTHCHECK uses `/alive`, so a broken database still looks healthy. |
| Secrets at rest | Complete | API, Kobo, kosync and invitation tokens are hashed. Integration secrets are sealed with Data Protection. No secrets appear in logs (grep). |

## 3. The five highest-impact verified gaps

### Gap 1 — P0: Server-side request forgery through the year-card cover fetch (re-verified)

- **Evidence:**
  - `GET /books/years/{year}/card.svg` (`YearCard.cs:20`) fetches each finished book's `CoverUrl`.
  - It uses the `FreeCatalog` HTTP client (`YearCard.cs:37`), which is registered without `OutboundGuard` (`Program.cs:233-237`).
  - It base64-embeds whatever comes back into the SVG it returns (`YearCard.cs:54`, `DataUri`).
- **Who and what:** any signed-in reader, including invited and child accounts, sets `CoverUrl` (the only check is the http/https scheme, `BookRules.cs:59-61`).
- **Effect:**
  - A reader can read responses from `localhost`, LAN services, or cloud metadata (`169.254.169.254`). This works even with `Network:AllowPrivateAddresses=false`, and redirects are followed.
  - When `Content-Length` is missing, the read is uncapped.
- **Not affected:** this is the only server-side fetch of a reader-supplied cover address. The other `FreeCatalog` callers (`Availability.cs:30`, `FreeBooks.cs:190`) use fixed catalog hosts.

### Gap 2 — P0 for PostgreSQL installs: nightly backups can hold no books and still pass the drill (re-verified)

> **Status (2026-10-28):** fixed by failing honestly. See `IMPLEMENTATION_PLAN.md`, "Follow-up task 2". The PostgreSQL branch is verified in CI only.

- **Evidence:**
  - Without `pg_dump`, `Backup.WriteSnapshotAsync` falls back to `library.json` from `ExportWithBooksAsync` (`Backup.cs:170-178`).
  - That export queries `db.Books` through the per-reader filter (`ShelfDb.cs:170`).
  - The nightly job uses a scope with no HTTP user (`BackupSchedule.cs:68-71`), so `ShelfReader.Id` is null and `ReaderId` is 0 (`ShelfReader.cs:12`, `ShelfDb.cs:36`). The export therefore holds no books.
  - A manual download holds only the admin's own books, and never other readers, loans or collections.
  - The official image installs no `pg_dump` (`Dockerfile:28`).
  - The drill accepts a `library.json` with any number of books, including 0 (`BackupCheck.cs:115-120`). It checks a `shelf.sql` only for pg_dump's closing line.
- **No tests:** every backup/restore test returns early on PostgreSQL.
- **Documentation:** `configuration.md:94` advises backing up with PostgreSQL's own tools as well, but `backups.md` never mentions PostgreSQL.
- **Effect:** an admin sees verified nightly backups that cannot restore their library.
- **Not reproduced at runtime:** no PostgreSQL server was available.

### Gap 3 — P1: Audiobookshelf-app sessions outlive a revoked API token and ignore its scope (re-verified)

> **Status (2026-10-28):** fixed for new sessions. See `IMPLEMENTATION_PLAN.md`, "Follow-up task 3". Sessions from before the fix end on a password change.

- **Evidence:**
  - `AbsApi` login accepts any `shelf_` API token (`AbsApi.cs:146`), including read-only or area-limited ones.
  - It issues access and refresh tokens carrying only `readerId|Stamp` (`AbsApi.cs:95`), checked at `AbsApi.cs:117`.
  - `ApiTokens.RemoveAsync` deletes the token row but does not rotate `Stamp` (`ApiTokens.cs:142-150`).
  - `/auth/refresh` reissues the token pair indefinitely (`AbsApi.cs:166-175`).
- **Effect:**
  - Revoking a token does not sign the app out.
  - A token made read-only can still write progress and bookmarks through these sessions.
- **Tests:** `AbsApiTests.cs:127` signs in with a `canChange:false` token and does not assert any restriction.

### Gap 4 — P1: Progress can move backward or be clobbered, contradicting the docs

The docs promise: "A place moves only forward by itself, so a device that is behind never sends you back" (`site/content/reading.md:129`).

- **Kobo** (re-verified): `Kobo.cs:247-259` assigns `tracked.EbookChapter` straight from the device bookmark, with no check that it is further on. It also bypasses `Lending.KeepPlaceAsync` and its position report. On a forward move, the stale `EbookCfi` is left behind, and the web reader opens at the CFI (traced: `epub-reader.js:454`).
- **Reread** (re-verified): `Rereads.ReadAgainAsync` (`Rereads.cs:77-99`) clears only the place on `Book`. It leaves `KosyncPlaces`, `KoboEntries` and `PositionReports` alone. After "Read again", the Book place is null, so KOReader's next push of its old end position counts as "further" (`Kosync.cs:164`). Kobo is still told "Finished" (traced: `Kobo.cs:420`).
- **Audiobookshelf apps and offline audio** (traced): `AbsApi.cs:669-801` and the offline audio replay (`listen.js:270-285` → `AudioPlayback`) apply last write wins with no client time. Offline sessions replayed on reconnect overwrite newer listening. A PATCH of `isFinished:true` with no `currentTime` resets the audio place to 0:00 (`AbsApi.cs:795`).
- **No tests** cover a backward Kobo move, a stale replay, or a sync after a reread.

### Gap 5 — P1: One unexpected exception in a background loop can stop the whole server (re-verified configuration; trigger traced)

> **Status (2026-10-28):** fixed. See `IMPLEMENTATION_PLAN.md`, "Follow-up task 4". Showing sync status to readers is still open.

- **Configuration:** `HostOptions.BackgroundServiceExceptionBehavior` is not set anywhere, so .NET's default `StopHost` applies.
- **Narrow catches:** many loops catch only narrow types. For example, `ReadwiseSync` catches only `DbUpdateException or InvalidOperationException or HttpRequestException` (`Readwise.cs:285`). The same pattern is reported at `Hardcover.cs:533`, `SeriesWatch.cs:111`, `Automations.cs:650`, `Notices.cs:722`, `Audiobookshelf.cs:509` and `Federation.cs:833`.
- **Effect:** any other exception (a `SqliteException` "database is locked", an `IOException`, a `JsonException`) escapes `ExecuteAsync` and shuts the server down.
- **Related (traced):**
  - Readwise's per-reader loop has no per-reader catch (`Readwise.cs:301-316`), so one reader's failure skips the rest that hour.
  - Readwise and Hardcover record no visible sync status, and `Heartbeats` covers only six services.

### Next tier (verified or traced, lower priority)

- **P1:**
  - No backup is taken before startup migrations (`Program.cs:364`).
  - No test upgrades a populated older database. The nightly backup drill migrates a copy, which helps but is not a regression test.
- **P2:**
  - Backup retention and offsite pruning run before the new backup's drill (`BackupSchedule.cs:77-86`, `OffsiteBackup.cs:128-136`), so failing backups can rotate out good ones.
  - Child accounts can't keep a place or highlights on shared books.
  - Provider failures are shown as "no results".
  - A reader's portable zip does not restore collections or loans.
  - Signed-out pages and open dialogs are not covered by axe.
  - `backups.md` doesn't say that integration tokens are lost without the `keys` folder. The comment at `Program.cs:36-37` is wrong about this.
  - Merges cannot be fully undone.
- **P3:**
  - Owners' tags reach other readers through `NextReads.cs:126-141` and the ABS API, against the "never tags" rule.
  - Failed `.partial` backups are never cleaned up.
  - `pg_dump` receives the password on its command line.
  - `release.yml` doesn't require the full CI run to pass.
  - The flaky `wayfinding.spec.mjs:4`.
  - The MessagePack advisory in `Shelf.AppHost`.

## 4. Recommended next task: close the year-card SSRF (Gap 1)

> **Status (2026-10-28):** implemented as specified, with regression tests; 400 server tests pass. See `IMPLEMENTATION_PLAN.md`, "Follow-up task 1".

It is the only P0 that affects the default SQLite install. It is small, isolated, and easy to test, and the fix reuses the guard that already protects podcasts and reader-supplied Audiobookshelf addresses.

- **Problem:** any signed-in reader can make the server fetch internal addresses and read the replies through their year card.
- **Current behavior:** `YearCard.CoversAsync` fetches `CoverUrl` with the unguarded `FreeCatalog` client, follows redirects, and reads an uncapped body when no length is given.
- **Desired behavior:**
  - Cover fetches for the card go through `OutboundGuard.Handler(OutboundGuard.ReaderReach(configuration))`, so they honor `Network:AllowPrivateAddresses` like the other reader-supplied addresses.
  - The body read stops at `CoverStore.MaxBytes`.
  - Only image content is embedded.
  - A refused or oversized fetch falls back to the existing spine drawing.
- **Scope:**
  - Register a guarded named HTTP client for cover fetches (in `Program.cs`, next to the existing guarded clients) and use it in `YearCard.cs`.
  - No schema, UI or text changes.
  - This does not touch the admin-configured, unguarded clients (federation, Prowlarr, admin ABS).
- **Data and security:** no migration and no stored-data change. It narrows outbound reach only.
- **Acceptance criteria:**
  1. With default settings, a finished book whose `CoverUrl` points at a loopback listener gets a spine on its card. The listener's bytes are absent from the SVG, and the listener receives no request.
  2. With `Network:AllowPrivateAddresses=true`, the same cover is embedded, so the existing behavior is kept for LAN users who opt in.
  3. A response larger than `CoverStore.MaxBytes`, sent without `Content-Length`, is not embedded, and the card still renders.
  4. A public cover URL still embeds as before. The existing `WelcomeAndCardTests` pass.
- **Tests:**
  - New cases in `tests/Shelf.Api.Tests/WelcomeAndCardTests.cs` using a local `HttpListener` or Kestrel stand-in, for criteria 1–3.
  - Then the full `dotnet test`.
- **Documentation:** a `CHANGELOG.md` entry under *Fixed / Security*. If `security.md` lists what the outbound guard covers, add the year card to that list.
- **Effort and risk:** small (two files plus tests). The risk is low. The only behavior change is for covers hosted on private addresses, which then need the existing opt-in.

**Suggested order after that:**
1. Gap 2 (PostgreSQL backup: export with filters off or require `pg_dump`, fail the drill on an empty library, document restore).
2. Gap 3 (tie ABS sessions to their API token).
3. Gap 5 (resilient background loops).
4. Gap 4 (route device writes through `KeepPlaceAsync` with explicit rules, and reset device state on reread). Gap 4 is the largest and should be split into several tasks.
