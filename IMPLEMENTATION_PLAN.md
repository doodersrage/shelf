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
- New finding (P2): the reader's own **Sign out everywhere else** on Account (`TwoFactor.EndOtherSessionsAsync`) only deletes browser sessions. It doesn't rotate `Stamp`, so it leaves Audiobookshelf apps signed in, contrary to what its name suggests.
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
- Unchanged and still open in gap 4:
  - **Part 2:** "Read again" doesn't reset `KosyncPlaces`, `KoboEntries`, or `PositionReports`. A Kobo that says Finished can mark the reread finished.
  - **Part 3:** the Audiobookshelf apps and offline audio use last write wins.
  - **Part 4:** an Audiobookshelf `isFinished` PATCH resets the place to 0:00.
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
  - The pre-upgrade backup isn't drilled at startup, so Server health shows "not proved yet" until the next nightly backup.
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
  - Readwise send and import share one problem field, so a successful import clears a send's problem from the same round.
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
- Still not audited: the reader's settings and contents panels, the mini player, the bulk-edit bar, mobile width, and es/fr/de.

### Follow-up task 15: catalog lookups tell an outage from "not found" (audit P2), done 2026-10-28

- [x] `IBookLookup.CouldNotAsk` and `IBookSearch.CouldNotAsk` are default interface members, false, so test stand-ins are unaffected.
  - `OpenLibraryLookup`, `GoogleBooksLookup`, and `OpenLibrarySearch` set it on 429, 5xx, a timeout, a network error, or a Polly rejection. Google also counts 403 (its quota without a key). A 404 or an empty answer is a real "not found".
  - `CatalogLookup` reports it only when nothing was found and every catalog asked couldn't be (Google counts as not asked when turned off).
- [x] Where it shows: `/books/lookup` answers 503 with the reason, the add/edit form's lookup note explains it, and the Find page shows an error instead of "knows no book".
- [x] `OpenLibrarySearch` now also catches `Polly.ExecutionRejectedException`, which used to escape.
- [x] Tests: `GoogleBooksTests.A_lookup_tells_catalogs_out_of_reach_from_a_book_none_knows` and `BooksEndpointTests.Lookup_says_when_no_catalog_could_be_asked`. 2 new strings in es/fr/de.
- Not changed: the background "Fill in details" job (`DetailsFiller`) and scanning still treat an outage as no match. Their per-book notes could use the same flag.

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
- Not changed (still P3): the borrower's webhook and event feed include the owner's reading `Status` on BookLent and Returned (`Webhooks.cs:218-222`).

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

- [ ] Verify server-side authorization for every reader-owned record, file, API endpoint, shared link, lending action, and administrative action.
- [ ] Test privacy boundaries between readers: reading progress, notes, highlights, files, personal reading history, and account settings.
- [ ] Review account recovery, passkeys, two-step sign-in, OpenID Connect, API tokens, session handling, and security-sensitive logs against the current implementation.
- [ ] Test EF Core migrations against an existing populated database, not only a fresh install.
- [ ] Verify backup creation, retention, integrity checks, restore behavior, and recovery after an interrupted or partial backup.
- [ ] Confirm that exports and public share links omit secrets and private reader data.
- [ ] Exercise malformed imports, duplicate records, missing files, interrupted uploads, disk-full errors, and provider timeouts.
- [ ] Define conflict rules for progress and metadata arriving from multiple devices or integrations.
- [ ] Ensure external-service failure does not prevent unrelated local reading or catalog workflows.
- [ ] Add regression tests for every confirmed high-risk defect.

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

- [ ] Walk through add/import → open → resume → switch format → highlight/note → finish → reread.
- [ ] Verify position persistence on navigation, refresh, browser close, reconnect, and device change where supported.
- [ ] Confirm how positions map between EPUB, PDF, comics, audiobook tracks, and external devices; avoid pretending incompatible positions are directly equivalent.
- [ ] Test conflicts between browser, KOReader, Kobo, and other enabled sync sources.
- [ ] Verify reading status and completion dates when a reader rereads a book.
- [ ] Check reader usability: typography, line length, themes, progress controls, table of contents, search, bookmarks, notes, highlights, dictionary/read-aloud where supported, and return-to-reading behavior.
- [ ] Check audiobook playback across navigation, sleep timer, speed changes, track boundaries, lock-screen controls, and restored sessions where supported.
- [ ] Audit resume/recap experiences so they help readers recover context without exposing spoilers unexpectedly.
- [ ] Make empty, unsupported-file, corrupted-file, and failed-sync states understandable.
- [ ] Add browser regression tests for the most important reader and player journeys.

### Acceptance criteria

- Core reading/listening tasks are completable on supported browsers and screen sizes.
- Progress survives expected lifecycle events and has defined conflict behavior.
- Format switching is clear about what can and cannot be synchronized.
- Critical reader/player paths have automated regression coverage.

---

# M3 — Library management and portability

**Goal:** Keep a growing library accurate, maintainable, and easy to move.

### Audit and candidate tasks

- [ ] Verify duplicate detection and merge behavior across ISBNs, editions, translations, narrators, and different formats of the same work.
- [ ] Ensure merge previews explain which fields, files, relationships, reading records, notes, and copies will be preserved.
- [ ] Audit bulk editing, saved searches, collections, tags, series, locations, editions, physical copies, and metadata cleanup.
- [ ] Verify import previews, source mapping, duplicate handling, retry behavior, and partial-failure reporting.
- [ ] Verify export completeness for catalog records, editions/formats, physical copies, reading history, highlights/notes, lending records, collections, and files where applicable.
- [ ] Distinguish portable user-data export from full operational backup, and document what each includes or excludes.
- [ ] Test moving a library to another Shelf instance without losing file associations or private data boundaries.
- [ ] Make file-missing and storage-migration states discoverable and recoverable.
- [ ] Add safeguards and tests around destructive merges, deletions, and bulk actions.

### Acceptance criteria

- Import/export operations report what was added, skipped, updated, or rejected and why.
- Duplicate merges are reviewable and preserve selected records and relationships.
- A documented migration/portability path is tested with a representative library.
- Destructive operations have appropriate confirmation and recovery safeguards.

---

# M4 — Personal reading intelligence

**Goal:** Make reading history and library insights useful without turning Shelf into a noisy social network or a generic AI chatbot.

### Audit and candidate tasks

- [ ] Verify that the reading journal correctly represents sessions, dates, rereads, reading time, audiobook time, and imported activity.
- [ ] Check yearly goals and summaries for consistency when records are edited, deleted, imported, or synced.
- [ ] Ensure charts and milestones explain their definitions and handle incomplete data gracefully.
- [ ] Review current recommendations and “what to read next” behavior for transparency and usefulness.
- [ ] If a meaningful gap remains, consider a **Read Next from My Library** flow that uses owned/wishlisted books and explainable reasons (for example, continuing a series, matching a saved collection, or revisiting an unfinished book).
- [ ] Consider a **lifetime reading journal** view that connects dates, books, rereads, notes, and milestones—only if the existing journal does not already satisfy this.
- [ ] Avoid adding a generic social feed, generic AI chatbot, or opaque recommendation engine unless clear user evidence supports it.

### Acceptance criteria

- Stats agree with underlying records and documented definitions.
- Recommendations are explainable, dismissible, and based on available data.
- Historical views remain useful for readers with sparse or imported records.
- New insights do not require a mandatory external AI service.

---

# M5 — Mobile, offline, accessibility, and localization

**Goal:** Make the web-first experience comfortable and dependable across devices.

### Audit and candidate tasks

- [ ] Test narrow phones, tablets, desktop, landscape orientation, touch targets, and virtual keyboard interactions.
- [ ] Verify PWA installation and offline behavior against documented capabilities; distinguish cached app shell from genuinely available offline books/audio.
- [ ] Test offline progress, queued actions if supported, conflict resolution, and recovery after reconnection.
- [ ] Check keyboard-only navigation, visible focus, semantic headings, form labels, dialog behavior, contrast, reduced motion, and screen-reader announcements.
- [ ] Verify reader/player controls and critical workflows do not rely on hover.
- [ ] Review English, Spanish, French, and German coverage; check pluralization, date/time/number formatting, overflow, and fallback behavior.
- [ ] Ensure new user-facing text follows the existing localization system.
- [ ] Add focused accessibility and responsive regression checks where feasible.

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

- [ ] Test clean Docker installation on supported architectures and documented volume/permission configurations.
- [ ] Test upgrades from supported previous versions with persistent data.
- [ ] Verify startup diagnostics, health checks, graceful shutdown, database locking/error handling, and useful logs.
- [ ] Review configuration defaults and documentation for reverse proxies, HTTPS, email, storage, and optional integrations.
- [ ] Ensure secrets are not printed in logs or accidentally exposed in diagnostics.
- [ ] Check performance with a realistically large catalog, many reading records, and large ebook/audiobook files.
- [ ] Review dependency updates and security advisories using repository-supported tooling.
- [ ] Ensure CI runs relevant build, test, migration, and browser checks.
- [ ] Verify release notes, upgrade notes, troubleshooting documentation, and backup-before-upgrade guidance.
- [ ] Run the documented restore drill as part of release verification where feasible.

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
