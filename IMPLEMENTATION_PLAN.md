# Shelf — Implementation and Product Maturity Plan

> **Purpose:** An audit-first roadmap for making Shelf more reliable, polished, portable, and distinctive without rebuilding working features.
>
> **Critical rule:** Milestones below are hypotheses and audit areas, not assertions that the feature is missing. Repository evidence takes precedence over this document.

## Product north star

Shelf is a self-hosted home for a person's reading life: physical books, ebooks, audiobooks, and comics connected as one library, with cross-format progress, reading history, highlights, lending, imports, integrations, and user-controlled data.

Success means a reader can reliably:
1. Find or add a book without fighting metadata or duplicate records.
2. Open a supported format and resume where they left off.
3. Switch formats without losing the meaning of their progress.
4. Keep personal notes, highlights, and reading history private.
5. Lend and return physical copies with clear ownership and status.
6. Import, export, back up, restore, and move their library confidently.
7. Use Shelf on desktop and mobile, including supported offline workflows.
8. Run and upgrade Shelf as a self-hosted service without needing a mandatory cloud dependency.

## How to use this plan

- Start with **M0 Audit**. Do not skip it.
- Mark every item `Complete`, `Partial`, `Missing`, `Blocked`, or `Not applicable`.
- For each status, record evidence: file paths, test names, documented behavior, or reproducible findings.
- Do not duplicate a feature that already works.
- Implement one approved task at a time; do not attempt the whole plan in one pass.
- Re-rank priorities after the audit. Data safety and broken core workflows outrank new features.
- Keep this plan synchronized with the actual repository and update status only after verification.

## Status key

- `[ ]` Not started
- `[~]` In progress
- `[x]` Verified complete
- `[!]` Blocked or needs a decision

In the 2026-10-28 status pass over M1–M6, `[~]` means partly verified, with what's missing named, and `[!]` means it couldn't be verified here: it needs a person's judgement, real devices, or a large dataset.

## Status, 2026-10-28

- **M0:** complete.
- **Findings:** every finding from the M0 audit's top five and next tier is resolved, in follow-up tasks 1–19 and the same-day follow-ups below them.
- **Release:** **2.18.0** was released on 2026-10-28 (tag `v2.18.0` at `73f0499`). Images are `ghcr.io/doodersrage/shelf:2.18.0` and `latest`; release notes are at https://github.com/doodersrage/shelf/releases/tag/v2.18.0. Later fixes are under Unreleased in `CHANGELOG.md`.
- **Milestones:** each M1–M6 item has a status with evidence. Of the 54 items, 39 are `[x]`, 11 are `[~]`, and 4 are `[!]`. The `[~]` and `[!]` items are the remaining work. The code review of 2026-10-28 left known defects that need a design choice before fixing, each listed under its item. The most serious left is a P1: the book form can save stale reading dates. The other P1, an older tab or device overwriting a newer reading position, was fixed on 2026-10-29.
- **Accepted limits:** see each task. They include stale Kobo "Finished" pushes, app sessions and merges from before 2.18.0, PostgreSQL without `pg_dump` not copied offsite, restore undo, the `/alive` HEALTHCHECK, `pg_dump`'s password on its command line, and the unreachable xmldom advisory (no epubjs release fixes it; see M6 dependencies).

---

# M0 — Repository audit and baseline

**Goal:** Establish an accurate picture of the codebase and the five highest-impact remaining gaps before changing product behavior.

### Tasks

> **M0 status: complete (2026-10-28, commit `39f779c`, Shelf 2.17.1).** Full findings, evidence, and commands: [`docs/audits/implementation-audit.md`](docs/audits/implementation-audit.md).

- [x] Inspect `git status`, branch, recent commits, open TODO/FIXME markers, and existing local changes. *(Clean apart from the untracked `CLAUDE.md` and this plan; no TODO/FIXME markers.)*
- [x] Read `README.md`, `CHANGELOG.md`, `docs/`, deployment files, configuration references, and existing plans.
- [x] Map solution/projects, application layers, EF Core entities and relationships, migrations, background services, integrations, authentication, authorization, and UI component conventions. *(Audit §1.)*
- [x] Inventory unit, integration, migration, and browser tests; identify current CI gates and untested critical paths. *(Audit §1–2.)*
- [x] Run the documented build/test commands where environment permits. Record exact failures separately from product defects. *(Server tests: 398 passed. Chromium browser tests: 54 passed, 1 flaky failure that passed 3 of 3 times when rerun. Firefox, PostgreSQL and Docker were not runnable here.)*
- [x] Create a feature matrix for catalog, editions/formats, reader, audio player, progress sync, highlights/notes, reading journal, lending, multi-user permissions, imports/exports, integrations, offline/PWA, notifications, backups/restores, accessibility, localization, and deployment. *(Audit §2.)*
- [x] For every advertised capability, verify whether it works end-to-end—not just whether a page or service exists. *(This was done by code tracing and the existing tests. Runtime reproduction was limited to the test suites.)*
- [x] Identify the five highest-impact verified gaps and propose the single best next task with estimated scope and acceptance criteria. *(Audit §3–4.)*

### Baseline results

| Command | Result |
|---|---|
| `dotnet build -c Release` | Succeeded. Warning NU1902: MessagePack 2.5.302 in the development-only `Shelf.AppHost`. |
| `dotnet test -c Release` (SQLite) | 398 passed, 0 failed, 0 skipped. Some tests skip by an early `return;`, so this overstates coverage. |
| `npx playwright test --project=chromium` | 54 passed, 1 failed. `wayfinding.spec.mjs:4` is flaky: it passed 3 of 3 times when rerun alone. |
| Firefox, PostgreSQL, and Docker jobs | Not run. The Firefox build is the wrong revision, there is no PostgreSQL server, and the Docker socket is not accessible. |

### Five highest-impact verified gaps

| # | Priority | Gap | Evidence | Milestone |
|---|---|---|---|---|
| 1 | P0 (**fixed**) | The year card fetches reader-supplied cover addresses without the outbound guard (a server-side request forgery that can read internal addresses). | `YearCard.cs:37`, `Program.cs:233-237` | M1 |
| 2 | P0 (PostgreSQL) (**fixed; PostgreSQL run in CI only**) | Nightly PostgreSQL backups without `pg_dump` contain no books yet pass the restore drill. | `Backup.cs:170-178`, `ShelfDb.cs:36,170`, `BackupCheck.cs:115-120`, `Dockerfile:28` | M1 |
| 3 | P1 (**fixed**) | Audiobookshelf-app sessions survive revoking the API token and ignore read-only scope. | `AbsApi.cs:95,117,146,166-175`, `ApiTokens.cs:142-150` | M1 |
| 4 | P1 (**fixed in 4 parts; one Kobo limitation**) | A place can move backward: Kobo writes directly, Audiobookshelf and offline audio use last write wins, and a reread leaves the devices' old places. | `Kobo.cs:247-259`, `Rereads.cs:77-99`, `Kosync.cs:164`, `AbsApi.cs:669-801`, versus `reading.md:129` | M2 |
| 5 | P1 (**fixed**) | An unexpected exception in a background loop stops the whole server. | No `BackgroundServiceExceptionBehavior` set; narrow catches such as `Readwise.cs:285` | M1/M6 |

### Follow-up task 1: year-card SSRF (gap 1), done 2026-10-28, not yet committed

- [x] Year-card covers are fetched by a dedicated `year-card-covers` client behind `OutboundGuard.Handler(ReaderReach)`, with one try and no retries (`Program.cs`, `YearCard.cs`).
- [x] The body read stops at `CoverStore.MaxBytes`, and only JPEG, PNG, GIF, or WebP (`CoverStore.Kind`) is embedded. Anything else gets the spine drawing.
- [x] Regression tests in `WelcomeAndCardTests.cs`:
  - `A_cover_linked_from_this_machine_is_not_fetched_for_the_card` covers acceptance criterion 1: no request is made and nothing is embedded.
  - `With_local_addresses_allowed_…` covers criteria 2 and 3, plus a non-image check.
  - Both fail against the pre-fix code and pass with the fix.
- [x] `dotnet test -c Release`: 400 passed, 0 failed.
- [x] Docs updated: `CHANGELOG.md` (Unreleased, Security), `site/content/security.md`, `site/content/configuration.md`.
- Behavior change: remote covers that aren't JPEG, PNG, GIF, or WebP (for example SVG or AVIF) now show a spine on the card. Covers on private addresses need `Network:AllowPrivateAddresses`.
- **Next recommended:** gap 2, PostgreSQL backups that contain no books.

### Follow-up task 2: PostgreSQL backups without pg_dump (gap 2), done 2026-10-28, not yet committed

**Decision (by the user):** honest failure only. `pg_dump` is not added to the Docker image, because Ubuntu 24.04 ships pg_dump 16, which refuses the recommended postgres:17.

- [x] Without `pg_dump`, a snapshot no longer writes the one-reader `library.json`. It writes `NO-DATABASE.txt` explaining why, plus the files and covers (`Backup.cs`).
- [x] The drill fails both a `NO-DATABASE.txt` backup and an old-style `library.json` backup with a clear reason (`BackupCheck.cs`). The existing Server health item then shows the backup as one that would not restore.
- [x] `GET /admin/snapshot` returns 503 with that reason instead of a zip with no database (`AdminEndpoints.cs`).
- [x] The `pg_dump` path is read from configuration `Database:PgDump`, which the existing `Database__PgDump` environment variable maps to. The environment variable is still the fallback.
- [x] Tests in `ShelfCareTests.cs`:
  - `A_restore_drill_refuses_a_backup_without_the_database` runs on every database. It fails against the old drill code and passes with the new.
  - `Without_pg_dump_a_postgres_snapshot_says_it_holds_no_database` asserts the refusal on PostgreSQL and unchanged behavior on SQLite.
- [x] `dotnet test -c Release` (SQLite): 402 passed, 0 failed.
- [!] **Not verified here:** the PostgreSQL branch of the end-to-end test. No PostgreSQL server was available, and running downloaded PostgreSQL binaries was blocked. The CI `postgres` job runs it.
- [x] Docs: a new "On PostgreSQL" section in `backups.md` covering pg_dump, the backup failure, and restoring `shelf.sql`; `configuration.md`; and a `CHANGELOG.md` entry under Unreleased / Fixed.
- [x] Translations: 3 new strings in es/fr/de, machine-made like the rest. One string that was no longer used was removed.
- **Next recommended:** gap 3, Audiobookshelf-app sessions that outlive a revoked API token.

### Follow-up task 3: Audiobookshelf sessions tied to their API token (gap 3), done 2026-10-28, not yet committed

- [x] When an app signs in with an API token, its access and refresh tokens carry the token's id. Every API call, socket connection, and refresh checks that the token still exists for that reader (`AbsApi.cs`, `SessionOfAsync`). Password sign-ins are unchanged.
- [x] Sign-in refuses a read-only or area-limited token with a clear message, since the app must play and write progress (`AbsApi.cs` `Login`, new `ApiTokens.FindAsync`).
- [x] Tests in `AbsApiTests.cs`:
  - `An_app_signed_in_with_an_api_token_is_signed_out_when_the_token_is_removed` covers refusing read-only and narrow tokens, returning 401 on API calls and refresh after removal, and leaving password sessions unaffected. It fails against the old code.
  - The two-step test now uses a token that can change things.
- [x] `dotnet test -c Release`: 403 passed, 0 failed.
- [x] Docs: `devices.md`, the Sync page hint and its es/fr/de translations, and `CHANGELOG.md` (Unreleased / Security).
- Limitation: sessions created from a token before this change carry no token id, so removing the token doesn't end them. Changing the password, or an admin's **Sign out everywhere**, does.
- New finding (P2): the reader's own **Sign out everywhere else** didn't sign out Audiobookshelf apps. **Resolved in task 13.**
- **Next recommended:** gap 5, background loops that can stop the server.

### Follow-up task 4: background jobs can't stop the server (gap 5), done 2026-10-28, not yet committed

- [x] `HostOptions.BackgroundServiceExceptionBehavior = Ignore` (`Program.cs`) is the server-wide safety net: an error that escapes a job ends only that job.
- [x] Top-level catches in the periodic loops are widened to `when (!stoppingToken.IsCancellationRequested)`, so each loop logs the error and tries again next round:
  - Notices, SeriesWatch, Podcasts, Readwise, Hardcover, FolderImport, Automations, Federation, FileSweep (two catches), ReleaseWatch.
  - This filter also covers HTTP timeouts, which `ex is not OperationCanceledException` would have let through.
- [x] One reader's failure no longer skips the rest in Readwise `SendDueAsync`, Hardcover `SendDueAsync`, and the Federation hourly loop.
- [x] Queue workers (M4b, Calibre, DetailsFiller) survive a failed job. Calibre and details jobs are marked `Finished`, so they don't show as running forever, and a reader can start a new details fill.
- [x] Tests:
  - `BackgroundTests.A_background_job_that_fails_unexpectedly_stops_itself_not_the_server`
  - `ReadwiseTests.One_reader_s_readwise_failing_leaves_the_others_to_go`
  - Both fail against the old code.
- [x] `dotnet test -c Release`: 405 passed, 0 failed.
- [x] `CHANGELOG.md` (Unreleased / Fixed).
- Not in scope, still open (P2): Readwise and Hardcover sync failures are only logged. They aren't shown to the reader or on Server health, and `Heartbeats` still doesn't cover those services. The FreeBooks and Audiobookshelf per-item catches were left as they are; those queue loops already catch per item.
- **Next recommended:** gap 4, the sync paths that move a place backward. Split it into small tasks, starting with the Kobo forward-only rule.

### Follow-up task 5: Kobo place moves forward only (gap 4, part 1 of 4), done 2026-10-28

- [x] `Kobo.cs` `PutState` sends the chapter through `Lending.KeepPlaceAsync` with KOReader's rule: only a later chapter moves the place. Moving on clears the stale CFI, and the Kobo is noted in `PositionReports` as source "Kobo".
- [x] `KoboTests.A_kobo_that_is_behind_never_sends_the_reader_back` covers a backward report (place and CFI kept; the Kobo listed with `Kept=false`) and a forward report (chapter moved, CFI cleared, `Kept=true`). Against the old code it fails with chapter 0 still holding chapter 2's CFI.
- [x] Docs: `devices.md`, `reading.md` (the Kobo listed under "Where each device has you"), and `CHANGELOG.md`.
- The other parts of gap 4 are resolved in tasks 6 (reread reset), 8 (late audio places), and 7 (Audiobookshelf `isFinished`).
- **Next recommended:** part 2, the reread reset.

### Follow-up task 6: reread resets device state (gap 4, part 2 of 4), done 2026-10-28

- [x] `Rereads.ReadAgainAsync` now also does three things:
  - marks the reader's `KosyncPlaces` for the book with `KosyncPlace.BeforeReread` (-1, in the existing column, so no migration);
  - resets `KoboEntries` (Status and Bookmark cleared, `ModifiedAt` set to now, so the next Kobo sync sends the reading state);
  - removes the book's `PositionReports` for that reader.
- [x] `Kosync` PUT ignores an exact repeat of the place KOReader gave before the reread. Any other place moves the reread forward as usual. GET no longer returns the old end, because a chapter of -1 is never further.
- [x] `KosyncTests.A_book_read_again_starts_over_on_koreader_and_the_kobo`. Against the old code it fails at the first check: stale device places still listed. The KOReader checks that follow weren't reached on the old code; they follow from the traced GET and PUT logic.
- [x] Docs: `library.md` ("Read it again") and `CHANGELOG.md`.
- Limitation: a Kobo that pushes its old "Finished" before its next sync can still mark the reread finished. Guarding that needs comparing the Kobo's timestamps with the server's clock, so it's left for now.
- **Next recommended:** part 4 (small): an Audiobookshelf PATCH with `isFinished` and no `currentTime` resets the audio place to 0:00. Then part 3, the timestamp policy for Audiobookshelf and offline audio, which needs a design decision.

### Follow-up task 7: Audiobookshelf "finished" keeps the place (gap 4, part 4 of 4), done 2026-10-28

- [x] `AbsApi.KeepAsync` takes an optional time. A PATCH without `currentTime` marks the book finished and leaves the audio place alone, and an e-book-only book can now be finished this way. The other callers are unchanged.
- [x] `AbsApiTests.An_app_marking_a_book_finished_keeps_the_place`. Against the old code it fails with the place at (0, 0) instead of (1, 1).
- [x] `dotnet test -c Release`: 408 passed. `CHANGELOG.md` updated.
- Not changed: `isFinished:false` from the app still doesn't un-finish a book in Shelf. That is a policy choice, left as it is.
- **Remaining in gap 4:** part 3, the Audiobookshelf apps and offline audio using last write wins. This needs a decision on how to compare a device's time with the server's.

### Follow-up task 8: late audio positions move forward only (gap 4, part 3 of 4), done 2026-10-28

**Decision (by the user):** forward-only for positions sent late, with no reliance on device clocks. Live playback still moves the place both ways.

- [x] Shelf's player:
  - `listen.js` `catchUp` sends `replayed: true`.
  - `AudioPlaceRequest.Replayed` moves the place only if it is further (`AudioPlayback.Further`).
  - The place is reported as source Shelf / "listening offline", shown as **Listening offline**, a new string translated into es/fr/de.
- [x] Audiobookshelf apps: `/api/session/local` and `/api/session/local-all` use `KeepAsync(..., forwardOnly: true)`. `/session/{id}/sync` and PATCH progress (live) are unchanged. Listening time is still counted.
- [x] Tests:
  - `ListeningTests.A_place_sent_late_from_listening_offline_never_sends_the_listener_back` (old code: (0, 60) instead of (0, 180)).
  - `AbsApiTests.Sessions_an_app_played_offline_never_send_the_listener_back` (old code: track 0 instead of 2).
- [x] Docs: `audiobooks.md` and `CHANGELOG.md`.
- **Gap 4 is now addressed in all four parts.** The Kobo stale-"Finished" limitation from task 6 remains.

### Follow-up task 9: backup before startup migrations (next tier, P1), done 2026-10-28

- [x] `BackupSchedule.BeforeMigratingAsync`, called from `Program.cs` before `MigrateAsync`. It runs only when the database has applied migrations and others pending, so a fresh install or a normal restart takes none.
- [x] It writes `shelf-backup-<time>-before-upgrade.zip` (database and covers, using `Backup.WriteSnapshotAsync`) into the backups folder, where it is listed and rotated like the nightly backups.
- [x] If the backup fails, startup stops with exit code 1 and an explanation, leaving the database untouched. `Backup:BeforeMigrating=false` turns the backup off. On PostgreSQL without `pg_dump`, a warning is logged and the upgrade continues.
- [x] `UpgradeTests.A_shelf_from_the_version_before_is_backed_up_then_upgraded_with_its_books` also closes the audit's "no populated-upgrade test" gap. It:
  - builds a real shelf with a reader and a book;
  - rolls it back one migration with `IMigrator`;
  - restarts it and checks for one backup that holds the pre-upgrade migration and the book, and that the upgraded shelf still has the book;
  - checks that the off switch takes no backup;
  - checks that a blocked folder leaves the database un-migrated, with the message.
  - Without the startup hook it fails at "one backup". It is SQLite-only and returns early on PostgreSQL.
- [x] Docs: `install.md` (Updating), `backups.md`, `configuration.md` (`Backup:BeforeMigrating`), and `CHANGELOG.md` (Unreleased / Added).
- Limits:
  - ~~The pre-upgrade backup isn't drilled at startup.~~ Resolved later the same day: `BackupSchedule.DrillUpgradeBackupsAsync` proves unproved `-before-upgrade.zip` backups in the background when the service starts, even with nightly backups off. `UpgradeTests` waits for a passing verdict.
  - A pre-upgrade backup taken before the nightly hour counts as that day's backup.
  - The startup-exit path is tested through `BeforeMigratingAsync` directly, not through a full host start.

### Follow-up task 10: backup retention after the drill (next tier, P2), done 2026-10-28

- [x] `BackupSchedule.TakeAsync` now:
  - drills the new backup first, then prunes with `ToLetGo`, which keeps the newest `Keep` backups plus the newest one that passed its drill;
  - sends to `CopyTo`/S3 only when the drill passes;
  - deletes the `.partial` file when writing fails.
- [x] `BackupCheck.VerdictAt(path)` reads a backup's verdict without the schedule.
- [x] `OffsiteBackupTests.The_last_backup_that_restores_is_kept_however_many_fail_after_it` unit-tests the choice. An end-to-end run of failing drills can't be produced on SQLite, where a fresh backup always passes.
- [x] Docs: `backups.md` and `CHANGELOG.md`.
- Trade-off: on PostgreSQL without `pg_dump`, nightly backups (which now fail their drill) are no longer copied offsite, including their covers and, with `Backup:IncludeFiles`, their files.

### Follow-up task 11: Readwise and Hardcover sync problems shown to the reader (P2), done 2026-10-28

- [x] New `SyncProblem` (Refused, Busy, Unreachable, Failed, Unsealed), stored on `Reader` as `ReadwiseProblem`/`ReadwiseProblemAt` and `HardcoverProblem`/`HardcoverProblemAt`. The migrations (`SyncProblems`, `PostgresSyncProblems`) add four nullable columns and nothing else.
- [x] Each client gains `DiagnoseAsync(token)` (401/403 refused, 429 busy, 5xx or timeout unreachable; Hardcover also treats a GraphQL `errors` reply as refused). It is called after a failed send or import.
- [x] Problems are recorded by sends and imports, by an unexpected error in the hourly round, and when a token can't be unsealed. A success clears them.
- [x] Connections shows **Needs a look** with the time and the reason. The Account sections show the same on load, and a manual send or fetch that fails gives the specific reason.
- [x] Hardcover send diagnoses before looking books up. An outage or a revoked token no longer marks books "not on Hardcover" (`HardcoverState`), which stopped them from being looked up again.
- [x] Tests:
  - `ReadwiseTests.Readwise_trouble_is_told_to_the_reader_until_a_trade_goes_right` (refused, unsealed, then cleared; Connections and Account).
  - `HardcoverTests.Hardcover_trouble_is_told_and_books_are_not_given_up_on`.
  - 6 new strings in es/fr/de; 3 obsolete ones removed.
- [x] Docs: `devices.md` and `CHANGELOG.md`.
- Limits:
  - ~~Readwise send and import share one problem field.~~ Resolved later the same day: in the hourly round, a fetch problem is put back if the send that follows succeeds. `ReadwiseTests.Trouble_bringing_highlights_is_kept_when_sending_goes_right` fails against the old code.
  - The PostgreSQL migration runs only in CI.

### Follow-up task 12: child accounts read shared books with their own place and marks (audit P2), done 2026-10-28

- [x] `OpenBook` gains `Shared` (set only by `Lending.OpenAsync` through `Family.SharedBookAsync`).
- [x] `Lending.Marks` and `Lending.Bookmarks` accept `Shared` as well as a current loan. They still require `ReaderId == me`, so a child sees only their own marks and the guardian's stay hidden.
- [x] New `Lending.StillOpenAsync` (lent, or still shared) replaces the borrower-only checks in `KeepPlaceAsync` and in Listen's add-bookmark.
- [x] `FamilyTests.A_child_keeps_their_own_place_and_highlights_in_a_shared_book`. Against the old code, the child's own highlight is saved but their list comes back empty. The test also covers place separation, deleting a highlight, and the book closing when unshared.
- [x] `CHANGELOG.md`. `lending.md` already promised this behavior.

### Follow-up task 13: "Sign out everywhere else" signs out the Audiobookshelf apps (P2, found in task 3), done 2026-10-28

- [x] New `Reader.AppStamp` column (migrations `AppStamp` and `PostgresAppStamp`, one nullable column). It is rotated by `TwoFactor.EndOtherSessionsAsync`.
- [x] App tokens now carry `id|stamp|apiTokenId|appStamp`, and `SessionOfAsync` checks the app stamp. Older 2- and 3-part tokens are still accepted. They end on a password change, but this button doesn't reach them until the app signs in again.
- [x] Rotating `Stamp` instead would also sign out the browser where the button was pressed, because a Blazor circuit can't reissue its cookie.
- [x] `AbsApiTests.Signing_out_everywhere_else_signs_out_the_apps` (without the rotation: the app stays OK). `dotnet test -c Release`: 416 passed.
- [x] Docs: `security.md` and `CHANGELOG.md` (Security).

### Follow-up task 14: accessibility audit of signed-out pages and dialogs (audit P2), done 2026-10-28

- [x] `accessibility.spec.mjs` has a shared `audit()` helper and a second test. It runs axe (WCAG 2.1 AA and best practices) in both themes on `/signin`, `/signup`, `/forgot`, `/reset`, a shared list, an unknown shared link, and a 404 page, all signed out; and with Go to (Ctrl+K, with results) and the keyboard shortcuts dialog open.
- [x] It found and fixed one real violation: Go to's chosen option rendered `.jump-kind` and `.jump-detail` in muted on sunken at 4.1:1. They now use ink (11.7:1) when the option is chosen or hovered (`app.css`). `docs/design-system.md` records the pairing.
- [x] Chromium: both accessibility tests and the wayfinding spec pass (5 tests). Firefox runs in CI only.
- [x] Later the same day: two more tests in `accessibility.spec.mjs`.
  - One audits the reader's Contents and Text settings, the mini player while playing, and the bulk-edit bar, at phone width (390×844) in both themes.
  - The other audits `/`, `/library`, a book page, `/account`, `/stats`, `/backup`, `/signin`, and `/signup` in es-ES, fr-FR, and de-DE. It checks that `<html lang>` matches, so each page really renders in that language.
  - Both pass in Chromium with no new violations. Firefox runs in CI.

### Follow-up task 15: catalog lookups tell an outage from "not found" (audit P2), done 2026-10-28

- [x] `IBookLookup.CouldNotAsk` and `IBookSearch.CouldNotAsk` are default interface members, false, so test stand-ins are unaffected.
  - `OpenLibraryLookup`, `GoogleBooksLookup`, and `OpenLibrarySearch` set it on 429, 5xx, a timeout, a network error, or a Polly rejection. Google also counts 403 (its quota without a key). A 404 or an empty answer is a real "not found".
  - `CatalogLookup` reports it only when nothing was found and every catalog asked couldn't be (Google counts as not asked when turned off).
- [x] Where it shows: `/books/lookup` answers 503 with the reason, the add/edit form's lookup note explains it, and the Find page shows an error instead of "knows no book".
- [x] `OpenLibrarySearch` now also catches `Polly.ExecutionRejectedException`, which used to escape.
- [x] Tests: `GoogleBooksTests.A_lookup_tells_catalogs_out_of_reach_from_a_book_none_knows` and `BooksEndpointTests.Lookup_says_when_no_catalog_could_be_asked`. 2 new strings in es/fr/de.
- [x] Later the same day: `DetailsFiller` marks a book `Failed` ("The catalogs did not answer.") when `CouldNotAsk` is set, instead of `Skipped` ("Nothing matched."). `TidyTests.Details_say_when_the_catalogs_could_not_be_asked` fails against the old code.
- [x] Later the same day: `Scanning.AddAsync` counts unreachable codes apart (`ScanAdded.Unreachable`) and leaves them on the list, and the Scan page says so (1 new string). `ICatalogSources.CouldNotAsk` makes **Review details** mark such a book `Failed`. Tests: `KeepingTests.A_scan_tells_catalogs_out_of_reach_from_a_book_they_dont_know` (fails against the old code) and the review case in `GoogleBooksTests`.

### Follow-up task 16: a reader's zip restores collections and past loans (audit P2), done 2026-10-28

- [x] `Backup.RestoreShelfAsync` now maps each book in the download to its book on this shelf, using the new `BookRules.ImportKey`, which matches books the way `ImportAsync` skips them. This covers both books just added and books skipped as already present.
- [x] Collections are restored by name (case-insensitive), or created through `Collections.CreateAsync`, then filled with `AddAsync`, `NoteAsync`, and `PlanAsync`. An existing collection keeps its books, notes, and read-by date, and gains what it lacks.
- [x] Loans: only returned loans are restored, deduplicated on (book, borrower, lent on). An open loan comes back as the book's own loan from `shelf.json`.
- [x] `RestoreResult` gains `Collections` and `Loans`. The redirect, the audit detail, and the Backup page report them (1 new string in es/fr/de). The zip's README and `backups.md` say what comes back. `borrowed-highlights.json` is export-only.
- [x] `ShelfCareTests.A_readers_zip_brings_back_their_collections_and_past_loans` checks order, note, read-by date, a returned loan, and that a second restore duplicates nothing.
- Limit: restored collections and loans aren't part of the import's single undo batch, so undoing the restore doesn't remove them. I didn't check what happens to a collection's entries for books the undo removes.

### Follow-up task 17: owners' tags no longer reach other readers (audit P3, privacy), done 2026-10-28

- [x] `NextReads.BorrowableAsync` suggests open-shelf books by loved authors only, with an empty `Subjects`. It reads id, title, author, cover, and owner only, not tags.
- [x] `AbsApi.BooksAsync` and `OpenAsync` clear `Tags` on books the reader doesn't own (lent or shared) before building the app's JSON (genres and tags).
- [x] Tests: `NextReadsTests` gains a lender book tagged as the reader loves, by an unloved author, which must not be suggested; and `AbsApiTests.A_lent_book_reaches_the_borrowers_app_without_the_owners_tags`. Both fail against the old code.
- The webhook and event-feed status leak noted here is **resolved in task 19**.

### Follow-up task 18: a merge can be taken apart (audit P2; decision by the user: full unmerge), done 2026-10-28

- [x] `Merging.MergeAsync` builds a `MergeReceipt` (`Unmerging.cs`) as it goes:
  - row ids moved, per table (quotes, highlights, sessions, audio bookmarks, KOReader places, past reads, copy photos, journal entries, OCR scans, book texts, and listening and reading days);
  - links moved and removed, and collections whose place moved;
  - days whose time was added into the kept book's day;
  - the kept book's changed properties (before and after, from the EF change tracker), tags added, and stored files taken.
  - It is stored as JSON on the merged-away book's `TrashItem.MergeReceipt` (new nullable column; migrations `MergeReceipts` and `PostgresMergeReceipts`).
- [x] `Trash.RestoreManyAsync` runs `Unmerging.UnmergeAsync` after restoring such a book, inside the same transaction:
  - each row goes back only if it is still on the kept book;
  - each kept detail reverts only if it still equals what the merge left;
  - day totals are split again;
  - files go back only once the kept book has let them go.
- [x] `MergingTests.Restoring_a_merged_away_book_takes_back_what_was_its_own` covers file, tags, quote, highlight, collection place, reading time, and kept details reverted, with one detail edited after the merge left as the reader set it. With the restore hook off, it fails.
- [x] Docs: `library.md` (merging) and `CHANGELOG.md`.
- Limits: merges made before this release have no receipt and still restore as before. Unmerging into a kept book that has since been deleted brings the merged-away book back with only what it kept.

### Follow-up task 19: loan events omit the owner's reading status for the borrower (audit P3, privacy), done 2026-10-28

- [x] `WebhookBook.Status` is now nullable. `Webhooks` sends `book.lent` and `book.returned` to the other reader with `Status = null`, in both the webhook and the event feed. The owner's copy is unchanged.
- [x] `WebhookTests.A_loan_reaches_the_borrower_without_the_owners_reading_of_it` (with the old payload, it fails). Docs: `notifications.md` and `CHANGELOG.md`.
- **With this, every finding in the M0 audit's top five and next tier is addressed.** What remains is the limits recorded under each task.

---

# M1 — Reliability, security, and data integrity

**Goal:** Protect user data and make core behavior predictable before adding more product surface.

> **Evidence-based priorities (M0):**
> 1. P0: the year-card SSRF (gap 1).
> 2. P0: PostgreSQL backups that contain no books (gap 2).
> 3. P1: Audiobookshelf sessions that outlive their API token (gap 3).
> 4. P1: background loops that can stop the server (gap 5).
> 5. P1: no backup before migrating, and no test upgrading a populated database.
> 6. P2: retention runs before the backup drill; integration failures show as "no results"; `backups.md` doesn't say what is lost without `keys`.
>
> **Verified sound** (no work needed beyond tests):
> - per-reader query filters and the `IgnoreQueryFilters` predicates
> - borrower privacy (`ReadersTests.cs:351`)
> - SQLite backup and restore (`RestoreTests.cs:32`)
> - soft delete and trash
> - secrets stored hashed or sealed
> - the `OutboundGuard` design

### Audit and candidate tasks

- [x] Verify server-side authorization for every reader-owned record, file, API endpoint, shared link, lending action, and administrative action.
  - *Status 2026-10-28:* M0 audit §1 (query filters plus about 120 `IgnoreQueryFilters` sites traced). Fixed: API-token sessions (task 3) and app sign-out (task 13). Tested in ReadersTests, FamilyTests, AbsApiTests. Later the same day: `ReadersTests.No_route_of_a_book_opens_it_to_a_reader_it_isnt_lent_to` asks every GET and DELETE `/books/{id}…` route (more than 40) as a stranger and expects refusal. It found `GET /books/{id}/photos` answering 200 with an empty list (no data leaked, since photos are owner-filtered); that now answers 404.
- [x] Test privacy boundaries between readers: reading progress, notes, highlights, files, personal reading history, and account settings.
  - *Status 2026-10-28:* ReadersTests borrower test; FamilyTests (task 12); tags and status leaks fixed (tasks 17 and 19).
- [x] Review account recovery, passkeys, two-step sign-in, OpenID Connect, API tokens, session handling, and security-sensitive logs against the current implementation.
  - *Status 2026-10-28:* Tokens, two-step, and sessions reviewed in the audit; token and app-session defects fixed (tasks 3 and 13). Passkeys and OIDC were reviewed by reading the code only; no new findings.
  - *Reviewed 2026-10-28 by reading the code; the findings were checked and the fixes tested (`dotnet test`, 446 passed).*
    - Sound: password hashing and rehash; reset links (256-bit, hashed, used once, one hour, no Host-header links); the two-step secret sealed, codes compared in constant time, recovery codes hashed and single-use; passkey origin, RP ID, and user verification; OpenID Connect with PKCE, readers matched by issuer and subject and never by email; API tokens and device keys random, hashed, and revocable; cookies HttpOnly, SameSite=Lax, and checked on every request; antiforgery; open redirects; admin authorization.
    - **Fixed:**
      - The Audiobookshelf-app `/login` had no rate limit (P1): `AbsApiTests.The_apps_sign_in_has_the_same_limit_on_guesses`.
      - `/two-factor/start` turned two-step sign-in off without a code (P2). Start, confirm, disable, and remove now share the sign-in limit, and a wrong code is audited: `FeatureTests.Two_step_sign_in_asks_for_a_code…`.
      - Open Blazor pages outlived a revoked sign-in (P2): `ShelfRevalidation` rechecks every minute, and `IsAdminAsync` now refuses a suspended admin: `FeatureTests.An_open_page_loses_a_sign_in_that_has_ended`.
      - Reset links survived a new password or email (P3): `FeatureTests.A_forgotten_password_is_reset_from_an_emailed_link`.
    - **Open, each needing a design choice:**
      - P2: adding a passkey or API token asks for no fresh proof, and a password change leaves passkeys, tokens, and device keys working.
      - P2: wrong codes are limited per address, not per account.
      - P3: a code can be reused within its 90-second window; sign-in timing and sign-up reveal which addresses have accounts; changing the email asks no password and tells the old address nothing; an admin's sensitive actions ask for no fresh sign-in; passkey challenges aren't single-use on the server; there is no breached-password check; the audit log can be flooded and doesn't log successful sign-ins.
- [x] Test EF Core migrations against an existing populated database, not only a fresh install.
  - *Status 2026-10-28:* `UpgradeTests` rolls a populated shelf back one migration and upgrades it (task 9).
- [x] Verify backup creation, retention, integrity checks, restore behavior, and recovery after an interrupted or partial backup.
  - *Status 2026-10-28:* RestoreTests, OffsiteBackupTests, and ShelfCareTests drill tests; PostgreSQL honest failure (task 2); retention after the drill (task 10); `.partial` cleanup; pre-update backup and its drill (task 9).
- [x] Confirm that exports and public share links omit secrets and private reader data.
  - *Status 2026-10-28:* Audit §2 (PortableExport scoped to the reader, secrets sealed). Shared lists are covered by CollectionTests and the signed-out accessibility test.
- [x] Exercise malformed imports, duplicate records, missing files, interrupted uploads, disk-full errors, and provider timeouts.
  - *Status 2026-10-28:* Imports report added, skipped, and failed, and re-runs are idempotent (audit). Provider timeouts are now told apart (tasks 15 and 11). Disk-full is covered only for backups (task 9), not for uploads.
  - *Reviewed 2026-10-28 by reading the code; the findings were checked and the fixes tested (`dotnet test`, 446 passed).*
    - Sound: outbound timeouts on every client; backups written `.partial`, then drilled; temporary folders cleaned up; no duplicates on re-import; CSV fills only empty fields; Calibre and the import folder explain refusals.
    - **Fixed:**
      - The old file was deleted before saving a replacement (P1): `EbookEndpoints`/`AudioEndpoints.AttachAsync` and `Remove`.
      - A zip restore that failed partway said "nothing was added" with the books already saved (P1). Now files are restored per book and the lost ones counted: `ShelfCareTests.A_full_backup_carries_files…`.
      - One bad import-folder item stopped every scan (P1): `FolderImportTests.A_broken_zip_is_set_aside_and_the_rest_still_come_in`.
      - A corrupt audiobook zip failed the upload (P2): `BooksEndpointTests.A_broken_audiobook_zip_is_refused`.
      - A cut-off e-book write left a part-file (P2).
      - Restore dropped refused files without saying so (P2).
    - **Open:**
      - P2: a slow linked shelf can hold the Shelves and Loans pages, with a 1 h timeout.
      - P2: failed linked-shelf and Shelf-to-Shelf downloads, and a cancelled full-library download, leave temporary files.
      - P2: CSV rows with no title or author aren't counted apart from duplicates; one unexpected error stops the Audiobookshelf importer until restart; tracks past 200 are dropped silently.
      - P3: an unclosed quote swallows the rest of a CSV; bulk and folder imports say "unsupported" for a damaged file.
- [x] Define conflict rules for progress and metadata arriving from multiple devices or integrations.
  - *Status 2026-10-28:* Forward-only for devices and late replays, live playback both ways, reread resets (tasks 5–8). Documented in `reading.md` and `audiobooks.md`.
- [x] Ensure external-service failure does not prevent unrelated local reading or catalog workflows.
  - *Status 2026-10-28:* Background jobs isolated (task 4); catalog and sync outages surfaced (tasks 11 and 15).
- [x] Add regression tests for every confirmed high-risk defect.
  - *Status 2026-10-28:* Each task in this plan adds a test that fails against the old code.

### Acceptance criteria

- No known P0 security, privacy, data-loss, or restore defect remains unresolved without an explicit documented decision.
- Existing databases upgrade safely through supported migration paths.
- Authorization is enforced server-side and covered by tests.
- Backup/restore is verified by restoring into a clean environment and checking representative records and files.
- Failures are visible and actionable; destructive operations are not silent.

---

# M2 — Reading and listening experience

**Goal:** Make Shelf's defining promise—one book, multiple formats, one coherent reading life—reliable and easy to understand.

> **Evidence-based priorities (M0):** gap 4, splitting it into small tasks:
> 1. Route Kobo writes through `Lending.KeepPlaceAsync` so they only move forward.
> 2. Make "Read again" reset the stored KOReader, Kobo, and position-report state.
> 3. Compare Audiobookshelf and offline audio writes by when they were played.
> 4. Fix the Audiobookshelf `isFinished` request that resets the place to 0:00.
>
> Also P2: child accounts get no place or highlights of their own on shared books.
>
> **Verified sound:** one history per book; forward-only offline e-book and KOReader places; read-along mapping.

### Audit and candidate tasks

- [~] Walk through add/import → open → resume → switch format → highlight/note → finish → reread.
  - *Status 2026-10-28:* Covered piecewise by `reading.spec`, `listening.spec`, KosyncTests, and ReadingLifeTests. Nobody did a manual end-to-end walk-through in this session.
- [~] Verify position persistence on navigation, refresh, browser close, reconnect, and device change where supported.
  - *Status 2026-10-28:* The offline, mini player, and resume specs pass. Browser close and device change are covered only through the sync tests.
  - *Reviewed 2026-10-28 by reading the code; the findings were checked and the fixes tested (`dotnet test`, 446 passed).*
    - Sound: the audio player saves on `pagehide` with a local copy and forward-only replay; reader time flushes on hide; KOReader sync is forward-only.
    - **Fixed:** a page turn within a second of closing the reader was dropped (P3, `epub-reader.js` `close()`).
    - **Fixed 2026-10-29 (P1): a stale tab or device no longer writes its older place over a newer one.**
      - Rule: a save from Shelf's reader or player names the place it last knew. The save goes through only if the shelf still holds that place, so no clock or migration is needed. `Lending.KeepPlaceAsync` takes an `onlyIf` check.
      - Reader (EPUB, PDF, comics, classic): when the place has moved, page turns stop saving and a notice asks the reader to choose. **Go there** reopens the book at the newer place; **Keep my place here** saves this page anyway. Inside the page-turning reader the notice sits in its dialog, so screen readers reach it.
      - Player: `PUT /books/{id}/audio/place` takes `from`. When the place has moved, the answer is 409 with the newer place, and the time listened still counts. The player checks as it starts playing, goes to the newer place, says so, and offers **Back to** for a minute. A save without `from` (offline replay, other clients) behaves as before. A save of the very place already kept is never refused: CI caught "Continue in the e-book" saving the audio place on the server just before the player's own save of the same moment.
      - Tests: `KeepingTests.A_place_moved_elsewhere_turns_away_a_move_that_only_holds_if_it_had_not`, `ListeningTests.A_player_left_open_cannot_send_back_a_place_moved_on_elsewhere`, and two browser tests (`reading.spec`: "a reader left open…"; `listening.spec`: "a player left open…").
    - **Still open:**
      - P2: EPUB and PDF page turns made while the connection is down are lost: there's no local queue, no `pagehide` flush, and no resend on reconnect. The reader now notices the dropped connection's effect only as a stale place, not as lost turns.
      - P3: every browser reports as one device ("Shelf's reader"), so the Positions page shows one row for all of them.
      - P3: a place queued offline before a reread can jump the reread forward.
- [x] Confirm how positions map between EPUB, PDF, comics, audiobook tracks, and external devices; avoid pretending incompatible positions are directly equivalent.
  - *Status 2026-10-28:* Audit: read-along when present, otherwise chapter pins with a proportional estimate (`Crossover.cs`). P3: the UI doesn't flag an estimate.
- [x] Test conflicts between browser, KOReader, Kobo, and other enabled sync sources.
  - *Status 2026-10-28:* Tasks 5–8 with tests.
- [x] Verify reading status and completion dates when a reader rereads a book.
  - *Status 2026-10-28:* ReadingLifeTests, plus the reread device reset (task 6). Limit: a Kobo can still send a stale "Finished".
- [!] Check reader usability: typography, line length, themes, progress controls, table of contents, search, bookmarks, notes, highlights, dictionary/read-aloud where supported, and return-to-reading behavior.
  - *Status 2026-10-28:* Needs a person to judge. The automated specs (`reading.spec`, 13 tests) pass.
- [x] Check audiobook playback across navigation, sleep timer, speed changes, track boundaries, lock-screen controls, and restored sessions where supported.
  - *Status 2026-10-28:* `listening.spec` (8 tests: speed, sleep timer, tracks, lock screen, mini player) passes in both browsers in CI.
- [!] Audit resume/recap experiences so they help readers recover context without exposing spoilers unexpectedly.
  - *Status 2026-10-28:* Not reviewed in this session. RecapTests exist.
- [~] Make empty, unsupported-file, corrupted-file, and failed-sync states understandable.
  - *Status 2026-10-28:* Failed sync is now told (task 11); catalog outages are told (task 15).
  - *Reviewed later the same day:* an unsupported type was already refused ("Choose an EPUB, a PDF, a comic…"). A **damaged** file was not: a cut-short EPUB, a `.pdf` that isn't one, or an empty `.cbz` was saved with "The e-book is on the shelf.", and only failed in the reader (a broken PDF said nothing at all).
    - Fixed: `EbookStore.LooksWhole` checks a PDF for its `%PDF-` header and an EPUB or CBZ for a zip that opens with entries. A damaged file is deleted and refused with `EbookSaveStatus.Damaged`, and the book page says "That file looks damaged or incomplete…". Imports count it as a file that failed.
    - Tests: `BooksEndpointTests.A_damaged_ebook_is_turned_away_as_damaged`. `StorageTests` now uploads real zips.
    - Not covered: a file that opens as a zip or carries a PDF header but is damaged inside still reaches the reader, which says it could not be opened (EPUB and comics) and offers the download.
- [x] Add browser regression tests for the most important reader and player journeys.
  - *Status 2026-10-28:* The existing specs, plus the accessibility tests for the reader panels and mini player (task 14).

### Acceptance criteria

- Core reading/listening tasks are completable on supported browsers and screen sizes.
- Progress survives expected lifecycle events and has defined conflict behavior.
- Format switching is clear about what can and cannot be synchronized.
- Critical reader/player paths have automated regression coverage.

---

# M3 — Library management and portability

**Goal:** Keep a growing library accurate, maintainable, and easy to move.

### Audit and candidate tasks

- [x] Verify duplicate detection and merge behavior across ISBNs, editions, translations, narrators, and different formats of the same work.
  - *Status 2026-10-28:* MergingTests (detection, merge, unmerge in task 18).
- [x] Ensure merge previews explain which fields, files, relationships, reading records, notes, and copies will be preserved.
  - *Status 2026-10-28:* A merge preview exists (`library.md`, "says what merging will do"), and restoring now reverses a merge (task 18).
- [x] Audit bulk editing, saved searches, collections, tags, series, locations, editions, physical copies, and metadata cleanup.
  - *Status 2026-10-28:* Tests exist (BooksEndpointTests, SavedSearchTests, CollectionTests, `library.spec`); bulk bar accessibility audited (task 14). No new review beyond that.
  - *Reviewed 2026-10-28 by reading the code; the findings were checked and the fixes tested (`dotnet test`, 446 passed).*
    - Sound: bulk edit, collections, shared lists, saved searches, tags, places, merge (with receipt and unmerge), detail proposals, and trash are all owner-scoped, batched for undo, and confirmed where destructive.
    - **Fixed:**
      - Tidy's author spellings went around history, so they couldn't be undone despite the page's promise (P1): `TidyTests.Spellings_become_one…`.
      - A place's destination wasn't checked against the reader's own places, so another reader could block its removal (P2): `KeepingTests.A_destination_is_one_of_your_own_places`.
    - **Open:** P3: bulk "add tag" counts books at the tag limit as changed.
- [x] Verify import previews, source mapping, duplicate handling, retry behavior, and partial-failure reporting.
  - *Status 2026-10-28:* Audit §5: ImportJobs and Calibre report added, skipped, and failed; re-runs are idempotent.
- [x] Verify export completeness for catalog records, editions/formats, physical copies, reading history, highlights/notes, lending records, collections, and files where applicable.
  - *Status 2026-10-28:* Portable zip contents traced (audit); collections and loans now round-trip (task 16). Borrowed highlights are export-only by design.
- [x] Distinguish portable user-data export from full operational backup, and document what each includes or excludes.
  - *Status 2026-10-28:* `backups.md` describes the reader's zip, the admin's snapshot, nightly copies, and PostgreSQL separately (tasks 2 and 16).
- [x] Test moving a library to another Shelf instance without losing file associations or private data boundaries.
  - *Status 2026-10-28:* RestoreTests (snapshot into a fresh folder) and `A_readers_zip_brings_back_their_collections_and_past_loans`.
- [~] Make file-missing and storage-migration states discoverable and recoverable.
  - *Status 2026-10-28:* The FileCheck weekly sweep exists and is tested (audit). No new work.
  - *Reviewed 2026-10-28 by reading the code; the findings were checked and the fixes tested (`dotnet test`, 446 passed).* There is no storage-migration feature, so that part is not applicable. Kobo, OPDS, KOReader, and the Audiobookshelf API answer 404 for a missing file.
    - **Open (P2):** a missing EPUB says "could not be opened", like a damaged one, with no "the file is missing" wording or upload link; re-uploading on the book page does work. Calibre gives the same note for a missing file as for an unsupported one.
- [x] Add safeguards and tests around destructive merges, deletions, and bulk actions.
  - *Status 2026-10-28:* Trash with undo (audit); merges are reversible (task 18).

### Calibre gaps (asked for 2026-10-29)

The user asked for what Calibre has and Shelf lacks, in this order, one at a time:

- [x] **Details written into downloaded EPUBs** (2026-10-29). `EpubDetails` writes title, authors, series (Calibre's and EPUB 3's way), publisher, language, year, ISBN, and the shelf's cover into a cached copy (`EpubDetailsStore`, `ebooks/with-details`), remade when they change. It's used by the new `GET /books/{id}/ebook/download` (linked on the book page) and Send to Kindle. Notes, review, rating, and tags are never written. The stored file is untouched, so OPDS, the reader, and KOReader's digest matching are unaffected. Backups now leave out `kepub/` and `with-details/`. Tests: `EpubDetailsTests` (EPUB 2 and 3 packages, language codes, the download and its privacy).
  - Not done: a cover with no `CoverImage` (an address only) isn't written; the Kobo download isn't stamped (Kobo takes details from sync); OPDS isn't stamped, which would break KOReader's file-based matching.
- [x] **Fields of your own** (2026-10-29). Additive migration (`CustomFields`, `CustomValues`, both providers). A reader's fields are filtered by owner, values by the book's owner. Values are kept in one form: invariant numbers, ISO dates, `yes`/`no`. There's a `/fields` page, the book edit form, the details list (each value links to a library filter), library search and `field`/`value` filters (the page, `GET /books`, and shared lists), the JSON export/import and `books.csv`, merges (a value the kept book lacks comes across, and is undone with the unmerge), and the API. Tests: `CustomFieldsTests` (6, including privacy, validation, export/import, merge) and `library.spec` "a field of one's own…".
  - Not done: sorting the library by a field; a field's kind can't be changed; Calibre's custom columns aren't brought in by the Calibre import yet.
- [x] **Several formats per book** (2026-10-29). Not a rewrite of the 204 uses of the main e-book: `Book.EbookStoredName` stays the main e-book for the reader, places, KOReader, Kobo, and recaps, and a new `EbookFormats` table (additive migration, both providers) holds the others, one per format, kept as they came (`EbookStore.SaveAsync(keepAsIs)`, Kindle files checked for `BOOKMOBI`). Covered: upload, list, download, remove, and make-main (place reset, the old main kept); OPDS acquisition links; storage totals; the file check; the trash (rows captured by the generic payload, files purged only when nothing names them); account removal; the full export (`books/NNNN/formats/<format>/`) and restore; merges (the other's formats and a differently-formatted main e-book come across, and unmerge gives them back). Tests: `EbookFormatsTests` (3, including OPDS, trash, export/restore, merge/unmerge) and `library.spec` "another format…".
  - Not done: the restore drill (`BackupCheck`) checks main files only; Shelf-to-Shelf trading and linked shelves carry the main e-book only; removing a format asks no confirmation (as removing the main file doesn't).
- [x] **Convert on demand** (2026-10-29). `EbookFormats.ConvertAsync` has Calibre's `ebook-convert` (`Ebooks:Convert`) make an EPUB, AZW3, MOBI, or PDF from the book's EPUB, or its main e-book, and keeps it as another format through the same checks as an upload. One conversion at a time, five minutes each, in a temp folder that's always removed. The buttons show only when the converter answers `--version` (asked once per process). There's also `POST /books/{id}/ebook/formats/convert`. Test: `EbookFormatsTests.Calibre_makes_another_format_from_the_books_own`, with a stand-in converter (CI has no Calibre).
  - Not verified: a real Calibre conversion. Calibre isn't installed here or in CI.
- [x] **A readable export** (2026-10-29). `GET /books/export/folders` (`FolderExport`), linked on the Backup page: `Author/Title (id)/` with every format named for the book, the cover, the audiobook under `audiobook/`, and an OPF 2.0 `metadata.opf` with Calibre's series and rating fields. Only the reader's own books, and no notes. The stored layout is unchanged. Test: `FolderExportTests`.
  - Not verified: adding the folders to a real Calibre (none installed here).
### Audiobookshelf gaps (asked for 2026-10-29)

The user asked for what Audiobookshelf has and Shelf lacks, all of it, one at a time:

- [x] **Details written into audio files** (2026-10-29). `GET /books/{id}/audio/download` (`AudioTagging`): ffmpeg `-c:a copy` writes title/album/artist/album artist/composer (narrator)/date/grouping (series)/track and the cover into temp copies (one file as itself with the player's chapters for MP4 kinds; tracks as a numbered zip). Stored files untouched; without ffmpeg the files go as stored. Also: temp downloads now send a `filename*` so any title is a safe header. Test: `AudioTaggingTests` (real ffmpeg, read back with ffprobe; CI has ffmpeg).
- [x] **Playlists** (2026-10-29). `Playlists`/`PlaylistItems` (additive migration, both providers), private by owner. `/playlists` and `/playlists/{id}` (reorder, take out, rename, remove with confirmation), an `AddToPlaylist` control on the book page (recordings) and podcast episodes, and the API. The player plays a queue through (`listen.js` `shelf-queue`, gone things passed over). Covered by the trash (generic), merges/unmerge, the full export (`playlists.json`) and restore. Tests: `PlaylistsTests` (2), `listening.spec` "a playlist plays…".
  - Found and fixed on the way: two simultaneous saves of a new book's place made the second fail with a 500 (`Positions.NoteAsync` unique row; test `Places_saved_at_the_same_moment_all_go_through` fails without the fix); a book played to its end had its old place sent over the start "finished" gave it (the player now knows the reset, drops the waiting seek save, and doesn't save twice at the end).
- [x] **A private podcast feed for an audiobook** (2026-10-29). `AudioFeeds` (additive migration, both providers): one per book, a SHA-256 of a 192-bit secret kept, shown once, replaced or stopped from the book page (`AudioFeedControl`) or the API. `/listen-feed/{secret}/feed.xml` is RSS 2.0 with iTunes serial ordering and Podlove chapters for a single file; tracks (ranges) and cover are under the same secret; nothing else is reachable. It refuses a suspended or child owner, or a book no longer theirs or without a recording. Track titles in the feed and in tagged downloads lose their file extension. Test: `AudioFeedsTests`.
- [x] **Casting** (2026-10-29). The browser's Remote Playback API (and Safari's AirPlay picker), not Google's Cast SDK, so nothing is loaded from elsewhere. The button shows only when the browser reports a device. While connected the player swaps to `/cast/{signed}/{track}` addresses (`CastLinks`: time-limited data protection, 12 h, reader + book + stored name, checked again on each fetch). Books only; saved podcast episodes aren't cast. Test: `CastLinksTests`.
  - Not verified: casting to a real Chromecast or AirPlay device. None here, and headless browsers report none, so the button's appearance and the hand-over are untested.
- [x] **Listening sessions** (2026-10-29). `ListeningSessions` (additive migration, both providers), the listener's own (filtered by reader, no admin view, to keep the privacy rule). Recorded with listening time from the web player (device from the user agent, browser and system only), offline time sent later, and the Audiobookshelf apps. A device reporting within 15 minutes continues the sitting. `/sessions` (all or `?book=`), linked from the book page; `GET /books/listening/sessions`; `listening-sessions.json` in the full export (not restored); merges move them. Test: `ListeningSessionsTests`.
  - Limits: times are in UTC, as reading days are (the known P2); sessions can't be edited or deleted.
- [x] **Narrators and sharing by link** (2026-10-29). `/narrators` (`Narrators`, names split as authors are) and a `narrator` filter in the library, `GET /books`, and shared lists; each name on the book page links there. Sharing uses the feed's secret: `/listen-feed/{secret}` is a plain HTML page with a player per track (no script), and the address can expire (`AudioFeed.ExpiresAt`, additive column). Tests: `NarratorsTests`, `AudioFeedsTests`.
- To ask the user about: shared libraries with permissions, and files read in place. Both change Shelf's model.

- Not planned (reasons given to the user): USB device management (a web server can't reach a cable; OPDS, Kobo sync, and Send to Kindle cover it), news downloads, an e-book editor, and plugins (other people's code on a shared server).

### Acceptance criteria

- Import/export operations report what was added, skipped, updated, or rejected and why.
- Duplicate merges are reviewable and preserve selected records and relationships.
- A documented migration/portability path is tested with a representative library.
- Destructive operations have appropriate confirmation and recovery safeguards.

---

# M4 — Personal reading intelligence

**Goal:** Make reading history and library insights useful without turning Shelf into a noisy social network or a generic AI chatbot.

### Audit and candidate tasks

- [~] Verify that the reading journal correctly represents sessions, dates, rereads, reading time, audiobook time, and imported activity.
  - *Status 2026-10-28:* ReadingLifeTests, YearReviewTests, InsightsTests, and the Audiobookshelf listening-history import. Not re-reviewed.
  - *Reviewed 2026-10-28 by reading the code; the findings were checked and the fixes tested (`dotnet test`, 446 passed).*
    - **Fixed:** an earlier read with no finish date counted the same sessions as the next read (P3): `ReadingLifeTests.An_earlier_read_with_no_end_ends_where_the_next_began`.
    - **Open:**
      - P2, needs a decision: every day is a UTC day. Finishes, sessions, and highlights after about 19:00 in the Americas land on the next day, or the next year. Proposed: the browser sends its local date, or a per-reader time zone.
      - P3: the journal leaves out listening and reader time.
      - Unsure: trashing a book removes a former borrower's own listening days and highlights on it.
- [~] Check yearly goals and summaries for consistency when records are edited, deleted, imported, or synced.
  - *Status 2026-10-28:* Covered by existing tests; not re-reviewed.
  - *Reviewed 2026-10-28 by reading the code; the findings were checked and the fixes tested (`dotnet test`, 446 passed).* Year counts agree across stats, years, the year review, and Home; trash and restore are consistent.
    - **Fixed:**
      - Imports stamped undated "read" books as finished today (P1): `FeatureTests.A_goodreads_export_comes_in…` now has an undated row.
      - The progress bar ignored e-book progress (P2): `ReadingLifeTests.Progress_counts_the_place_in_the_ebook`.
    - **Open:**
      - P1, needs a decision: the book form saves the reading status and dates as they were when the page opened, so saving an old tab can undo a finish made elsewhere, or count a reread twice. Proposed: write reading fields only when changed in the form, and refuse when they changed underneath.
      - P2: merging two finished entries keeps one finish and drops the other's year; unmerge restores it.
- [!] Ensure charts and milestones explain their definitions and handle incomplete data gracefully.
  - *Status 2026-10-28:* Needs a person to judge.
- [x] Review current recommendations and “what to read next” behavior for transparency and usefulness.
  - *Status 2026-10-28:* Reviewed for privacy (task 17). Each suggestion carries its reason (`NextReason`, `Because`).
- [x] If a meaningful gap remains, consider a **Read Next from My Library** flow that uses owned/wishlisted books and explainable reasons (for example, continuing a series, matching a saved collection, or revisiting an unfinished book).
  - *Status 2026-10-28:* Not needed: `NextReads` already offers series, loved authors, subjects, queue, and open shelves, each with a reason.
- [x] Consider a **lifetime reading journal** view that connects dates, books, rereads, notes, and milestones—only if the existing journal does not already satisfy this.
  - *Status 2026-10-28:* Not needed: Journal and Each read already exist (`library.md`).
- [x] Avoid adding a generic social feed, generic AI chatbot, or opaque recommendation engine unless clear user evidence supports it.
  - *Status 2026-10-28:* None was added.

### Acceptance criteria

- Stats agree with underlying records and documented definitions.
- Recommendations are explainable, dismissible, and based on available data.
- Historical views remain useful for readers with sparse or imported records.
- New insights do not require a mandatory external AI service.

---

# M5 — Mobile, offline, accessibility, and localization

**Goal:** Make the web-first experience comfortable and dependable across devices.

### Audit and candidate tasks

- [~] Test narrow phones, tablets, desktop, landscape orientation, touch targets, and virtual keyboard interactions.
  - *Status 2026-10-28:* Phone-width accessibility audit (task 14) and the phone specs in `getting-around.spec`. Tablet, landscape, and virtual keyboard not tested.
- [x] Verify PWA installation and offline behavior against documented capabilities; distinguish cached app shell from genuinely available offline books/audio.
  - *Status 2026-10-28:* `offline.spec` (kept book, kept audiobook, offline highlights) runs in CI. Install itself is a browser action, not tested.
- [x] Test offline progress, queued actions if supported, conflict resolution, and recovery after reconnection.
  - *Status 2026-10-28:* `offline.spec`, plus the forward-only replay rule (task 8).
- [~] Check keyboard-only navigation, visible focus, semantic headings, form labels, dialog behavior, contrast, reduced motion, and screen-reader announcements.
  - *Status 2026-10-28:* axe (WCAG 2.1 AA) on about 85 states in both themes (task 14), plus keyboard specs. No screen-reader testing with a real assistive technology.
- [~] Verify reader/player controls and critical workflows do not rely on hover.
  - *Status 2026-10-28:* Phone-width (touch) audits pass. No explicit hover-only check.
- [x] Review English, Spanish, French, and German coverage; check pluralization, date/time/number formatting, overflow, and fallback behavior.
  - *Status 2026-10-28:* TranslationTests enforce key parity and placeholders; es/fr/de pages are audited with `lang` checked (task 14).
- [x] Ensure new user-facing text follows the existing localization system.
  - *Status 2026-10-28:* Every new string in this session is in es/fr/de; TranslationTests pass.
- [x] Add focused accessibility and responsive regression checks where feasible.
  - *Status 2026-10-28:* Four accessibility tests in `accessibility.spec.mjs` (task 14).

### Acceptance criteria

- Critical workflows remain usable on supported mobile and desktop browsers.
- Offline claims match tested behavior.
- Keyboard and screen-reader users can complete key tasks.
- Missing translations and layout regressions are documented and prioritized.

---

# M6 — Self-hosting and release readiness

**Goal:** Make Shelf straightforward to install, update, troubleshoot, and recover.

> **Evidence-based priorities (M0):**
> - P1: take a backup before migrations run at startup.
> - P3: the HEALTHCHECK uses `/alive` and doesn't check the database; `release.yml` doesn't require CI to pass; the MessagePack advisory in `Shelf.AppHost`.
>
> **Verified sound:** non-root image, PUID/PGID handling, no mandatory cloud dependency, CI smoke tests on both architectures.

### Audit and candidate tasks

- [~] Test clean Docker installation on supported architectures and documented volume/permission configurations.
  - *Status 2026-10-28:* CI builds and smoke-tests amd64 and arm64 on every push; 2.18.0 was published and its image smoke-tested by `release.yml`. Volume and permission setups were not tested by hand.
- [~] Test upgrades from supported previous versions with persistent data.
  - *Status 2026-10-28:* `UpgradeTests` covers one migration step on SQLite. A multi-version upgrade on PostgreSQL wasn't tested.
- [x] Verify startup diagnostics, health checks, graceful shutdown, database locking/error handling, and useful logs.
  - *Status 2026-10-28:* Server health reviewed (audit). Later the same day: the Docker HEALTHCHECK uses `/health` (app plus database), so a lost database shows unhealthy.
  - *Shutdown and SQLite locking reviewed later the same day, on the 50,000-book scratch library:*
    - Load: 16 clients for 30 s (half adding reading sessions, half opening Home, Reading, Quotes and stats) made 11,186 writes and 778 page loads, all succeeding, with no `database is locked` in the log. Writes: median 3 ms, slowest 1.9 s. Pages: median 239 ms, slowest 2.4 s. Microsoft.Data.Sqlite retries a busy database for up to the 30 s command timeout, so contention queues rather than failing.
    - WAL journal mode was tried for comparison and was no faster (pages median 298 ms), since the time is server CPU, not locking. It stays off: WAL is unsafe on network-mounted volumes, and backups already use `VACUUM INTO`, which is consistent in either mode.
    - SIGTERM during that load (what `docker stop` sends) stopped the server in 0.45 s, with no failed requests in the log and `PRAGMA integrity_check` = ok. That is well inside Docker's 10 s grace period.
    - A backup interrupted by a stop is written to `.partial` and renamed only when finished (`BackupSchedule.cs`), so it is never taken for a complete one.
- [x] Review configuration defaults and documentation for reverse proxies, HTTPS, email, storage, and optional integrations.
  - *Status 2026-10-28:* Documentation for the changed settings was updated (`Backup:BeforeMigrating`, `Database:PgDump`, `Network:AllowPrivateAddresses`).
  - *Proxy, HTTPS, and email reviewed later the same day* against `Program.cs` and `Email.cs`:
    - `server.md` and `configuration.md` match the code: `BehindProxy`, `KnownProxies`, HSTS and the HTTPS redirect, the nginx WebSocket lines, and every `Email:` setting. The one exception is the older `Email:Ssl`, which is still read when `Email:Security` is unset and is left undocumented on purpose.
    - **Found and fixed (security):** an unreadable `Hosting:KnownProxies` entry was skipped silently. With none left, forwarded headers were trusted from any address, so a direct request could spoof its IP past the sign-in limit. Now startup stops and names the entry. Test: `FeatureTests.A_proxy_address_that_cant_be_read_stops_the_start`, which also checks that a valid list still starts.
    - Storage and optional-integration settings were later checked key by key against the code: every documented key is read, and every default matches. `Recap:Url`, `Backup:S3:MultipartAboveBytes`, the watchers' pauses, and `Email:Ssl` were added, and the stray `Ask:UseModel` row was put back in its table.
- [x] Ensure secrets are not printed in logs or accidentally exposed in diagnostics.
  - *Status 2026-10-28:* Audit §3: tokens hashed or sealed; grep found no secrets in log calls. Later the same day: `pg_dump` gets the password in `PGPASSWORD`, not on its command line (`Tools.RunAsync` takes an environment; `ShelfCareTests.A_tool_is_given_its_password_in_its_environment`).
- [x] Check performance with a realistically large catalog, many reading records, and large ebook/audiobook files.
  - *Status 2026-10-28:* Measured on a local Release build with SQLite. The data: one reader with 50,000 books, 100,000 sessions, 25,000 quotes, and 100,000 tag links, plus a second reader's open shelf of 30,000 books. Each page was requested once to warm up, then three times; the median is reported.
  - **Fine:** every page and API except one answers in about a second or less. Slowest: `/books?take=60` 1.0 s, `/stats` 0.7 s, `/tidy` 0.65 s, `/library` 0.55 s.
  - **P1 found and fixed: `/journal` took about 81 s and sent 13.7 MB.** My first diagnosis was wrong. I blamed drawing the page, but paging alone left it at 77 s. The real cause was `Journal.YearAsync` filtering quotes and highlights by id: EF Core 10 turns `ids.Contains(x.Id)` into one SQL parameter per id (`IN (@kept1 … @kept25000)`), and building that command took about 76 s.
    - Fixes: the journal reads passages in one query and filters by time in memory. The page draws 300 items at a time with Show more and Show all, and printing shows the whole year. Result: **about 250 ms and 82 KB**.
    - Wider fix: `UseParameterizedCollectionMode(ParameterTranslationMode.Parameter)` on both providers (`Program.cs`), so the 52 `ids.Contains` queries send one JSON or array parameter. Without it, more than 32,766 ids fails outright (`SQLite Error 1: 'too many SQL variables'`, reproduced by `ReadingLifeTests.A_query_by_tens_of_thousands_of_ids_runs`).
    - Tests: `ReadingLifeTests.A_long_year_of_reading_opens_a_page_at_a_time` and the 40,000-id query test.
  - ~~**P3:** some pages send very large HTML.~~ Fixed later the same day:
    - `/quotes` went from 8 MB to 89 KB (200 at a time), `/series` from 3 MB to 86 KB (100 at a time), `/reading` from 2.2 MB to 39 KB (24 open books, then a link), and `/years` from 1.4 MB to 180 KB (60 a year, then the year's page).
    - `/years/2024` takes 334 ms.
    - Test: `ReadingLifeTests.Long_lists_open_a_page_at_a_time`.
  - Large e-book and audiobook files weren't measured.
  - *Large files, checked later the same day on the same scratch server:*
    - A 273 MB, three-hour AAC audiobook uploaded in 0.35 s on localhost, with server memory up about 10 MB (it streams to disk). Its play plan answers in 38 ms; range requests at the start and the very end take under 10 ms each (206).
    - An EPUB with 500 chapters and 43 MB of text (13.6 MB zipped) uploaded in 0.04 s. Its book page opens in 0.5 s and the reader in 0.8 s, chapter 500 included. Search takes 1.1 s.
    - Peak server memory over the whole run: about 400 MB.
    - Not measured: network upload speed and the browser's own memory for such a book.
- [x] Review dependency updates and security advisories using repository-supported tooling.
  - *Status 2026-10-28:* 2026-10-28: `dotnet list package --vulnerable` lists only MessagePack 2.5.302 (moderate), in the development-only `Shelf.AppHost`, from Aspire. `npm audit`: tests/e2e and site are clean. Vendored `epubjs` 0.3.93 bundles `@xmldom/xmldom`, which has a high advisory, but epubjs uses it only when there is no native `DOMParser` or `forceXMLDom` is passed, and nothing passes it. So it isn't reachable in the browser. All open Dependabot PRs were merged after CI.
  - *Rechecked later the same day; the way out recorded above was wrong.* `npm audit`'s "fix", epubjs 0.4.2, is a **2018 release, older than 0.3.93**; it's offered only because it predates the xmldom dependency. 0.3.93 is epubjs's newest stable release, and after it there are only 0.5.0 alphas. So no update, from Dependabot or anywhere else, removes the advisory.
    - Reachability, rechecked in the served `wwwroot/lib/epubjs/epub.min.js`: its parse helper is `s = "undefined" == typeof DOMParser || force ? xmldom.DOMParser : DOMParser`. Every supported browser has `DOMParser`, and `epub-reader.js` never forces xmldom, so the bundled xmldom code never runs.
    - Kept as an accepted limit. Look again if epubjs publishes a stable release past 0.3.93, or if Shelf ever parses EPUBs with epubjs outside a browser.
- [x] Ensure CI runs relevant build, test, migration, and browser checks.
  - *Status 2026-10-28:* CI runs SQLite, PostgreSQL, Chromium and Firefox, vendored files, and Docker on amd64 and arm64. The populated-upgrade test was added. ~~P3: `release.yml` runs only the SQLite tests itself.~~ Fixed later the same day: before publishing, `release.yml` waits for CI's run on the tagged commit and stops unless it passed (`gh run watch --exit-status`). Checked by hand: it passes for `21b36ad` (2.19.0) and stops for `c49f4c4`, whose Firefox test failed.
- [x] Verify release notes, upgrade notes, troubleshooting documentation, and backup-before-upgrade guidance.
  - *Status 2026-10-28:* 2.18.0 was released with its CHANGELOG section. Backup-before-upgrade is automatic and documented (`install.md`, `backups.md`).
- [x] Run the documented restore drill as part of release verification where feasible.
  - *Status 2026-10-28:* RestoreTests ran in the 2.18.0 release run's `dotnet test`. The nightly drill is in BackupCheck.

### Acceptance criteria

- A fresh install and a supported upgrade are repeatable using documented instructions.
- Core CI gates pass or failures are explicitly documented.
- Health checks and logs help diagnose common deployment failures without leaking secrets.
- Release notes clearly communicate changes, migration risks, and recovery guidance.

---

# Cross-cutting quality requirements

Apply these to every milestone:

- **Correctness:** define expected behavior and edge cases before coding.
- **Privacy:** verify authorization at the server boundary.
- **Data safety:** protect migrations, imports, merges, deletions, and backups.
- **Resilience:** handle missing files, unavailable integrations, retries, cancellation, and partial failure.
- **Accessibility:** use semantic UI and test keyboard operation.
- **Localization:** follow the current resource/string conventions.
- **Performance:** avoid unnecessary full-library queries, repeated file reads, and expensive rendering.
- **Observability:** provide actionable errors without leaking private content or secrets.
- **Maintainability:** reuse existing abstractions; avoid speculative generalization.
- **Documentation:** update user-facing documentation whenever behavior or configuration changes.

# Prioritization rubric

| Priority | Definition | Examples |
|---|---|---|
| P0 | Critical safety or correctness issue | Data loss, privacy breach, broken restore, severe authorization flaw |
| P1 | Core promise is unreliable | Progress corruption, broken reader/player flow, unsafe import |
| P2 | Material friction or quality gap | Poor mobile workflow, inaccessible control, confusing duplicate resolution |
| P3 | Optional differentiation | New insights, extra views, nonessential integrations |

Estimate each verified task by impact, frequency, risk, confidence, and effort. Prefer small changes that eliminate recurring friction or prevent irreversible harm.

# Task proposal template

For each task Claude proposes, include:

- **Problem:** specific user-facing issue.
- **Evidence:** files, tests, reproduction steps, or documentation.
- **Current behavior:** what happens now.
- **Desired behavior:** what should happen instead.
- **Scope:** what will and will not change.
- **Data/security implications:** migrations, permissions, files, sync, and privacy.
- **Acceptance criteria:** observable and testable outcomes.
- **Tests:** exact unit/integration/browser coverage.
- **Documentation:** docs/changelog/configuration updates.
- **Effort and risk:** small/medium/large with rationale.

# First Claude Code prompt

Read `CLAUDE.md` and this plan. Start with M0 only.

1. Inspect the working tree and repository structure; preserve all existing changes.
2. Read the README, changelog, docs, project files, migrations, test suites, CI, and existing implementation plans.
3. Run the documented build/test commands that are available in this environment and record exact outcomes.
4. Build an evidence-backed feature matrix using `Complete`, `Partial`, `Missing`, `Blocked`, and `Not applicable`.
5. Identify the five highest-impact verified gaps, ranked by P0–P3, and explain why each matters.
6. Recommend one next task with a narrow scope, acceptance criteria, test plan, and risk assessment.
7. Update the audit document and this plan with findings. Do not begin broad implementation or claim untested features are complete.
8. Stop and present the findings for approval before starting M1 or implementing a broad set of changes.
