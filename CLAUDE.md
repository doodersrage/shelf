# CLAUDE.md — Shelf

## Project purpose

Shelf is a self-hosted personal library for physical books, ebooks, audiobooks, and comics. It combines catalog management, reading and listening, cross-format progress, highlights and notes, lending, shared readers, imports/exports, integrations, and self-hosted data ownership.

The product's core promise is: **one book across formats, with the reader's data and files kept under their control**.

## Technology and repository

The public project documentation describes this stack:
- .NET 10 / ASP.NET Core
- Blazor
- Entity Framework Core
- SQLite
- Docker
- .NET server tests and Playwright browser tests

Use the repository itself as the source of truth. Confirm actual projects, package versions, commands, architecture, and conventions before changing code.

## Non-negotiable working rules

1. **Audit before implementation.** Read the README, docs, `CHANGELOG.md`, solution/project files, migrations, test suites, CI workflows, and existing plans. Search for existing implementations before proposing or adding a feature.
2. **Do not duplicate working functionality.** For each proposed capability, classify it as `Complete`, `Partial`, `Missing`, `Blocked`, or `Not applicable`, and cite the relevant files/tests.
3. **Do not rewrite architecture without evidence.** Prefer the smallest coherent change that fits the existing patterns. Do not add a dependency or service unless the benefit is concrete and documented.
4. **Protect user data.** Treat library records, files, highlights, notes, reading history, accounts, and backups as valuable user-owned data. Avoid destructive migrations and irreversible operations.
5. **Preserve self-hosting.** Core library, reading, listening, and data portability should not silently acquire a mandatory cloud dependency. Optional integrations must remain optional unless the project already explicitly specifies otherwise.
6. **Respect privacy boundaries.** A reader must not see another reader's private notes, highlights, progress, account details, or files unless the product explicitly grants that access. Verify authorization on the server, not only in the UI.
7. **No fake completion.** Never mark a task complete because a UI exists. Confirm the underlying behavior, persistence, authorization, error handling, and appropriate automated tests.
8. **Work in small, reviewable increments.** Complete one approved task or milestone at a time. Avoid unrelated refactors and feature creep.
9. **Never claim tests passed unless they ran.** Record exact commands, results, and any tests that could not be run.
10. **Do not overwrite user work.** Inspect `git status` and the diff before editing. Preserve unrelated changes and do not reset, checkout, or clean user files.
11. **Use repository conventions.** Follow existing naming, dependency injection, error handling, logging, validation, localization, UI component, and test patterns.
12. **Keep documentation accurate.** Update user-facing docs, configuration references, migration notes, and release notes when behavior changes.

## Recommended workflow for every task

1. Inspect `git status --short` and the current branch. Do not discard local modifications.
2. Read the relevant implementation, tests, documentation, and recent history.
3. State the user problem and the evidence that the current implementation does not fully solve it.
4. Define a narrow implementation plan and explicit acceptance criteria.
5. Make the smallest end-to-end change.
6. Add or update tests for the behavior and regression risk.
7. Run the narrowest relevant tests, then broader checks where practical.
8. Review the diff for security, privacy, data loss, accessibility, localization, performance, and accidental scope.
9. Update documentation and the implementation plan with actual results.
10. Report files changed, behavior delivered, commands run, results, limitations, and recommended next step.

## Product principles

- **Reader-first:** opening, resuming, and switching between formats should be obvious and reliable.
- **One title, many formats:** do not create duplicate reading histories when physical, ebook, and audiobook formats represent the same work.
- **Progress must be trustworthy:** define how position, completion, rereads, and external-device sync interact. Avoid silent overwrites.
- **Private by default:** make ownership and sharing boundaries explicit.
- **Portable by design:** provide understandable exports and reliable backups/restores without leaking credentials.
- **Useful, not noisy:** prioritize actionable information over dashboard clutter, social feeds, or generic AI features.
- **Accessible and responsive:** keyboard, touch, screen-reader, reduced-motion, contrast, and narrow-screen behavior matter.
- **Explain failures:** distinguish an empty result from a provider outage, authentication error, rate limit, unsupported format, or malformed import.
- **Optional integrations stay optional:** failures in an external service should not break unrelated core workflows.
- **Localization is part of the feature:** use existing localization infrastructure; do not hard-code new user-facing strings when a translation mechanism exists.

## Quality gates

Before calling a task done, check applicable items:
- Correctness and persistence
- Authentication and server-side authorization
- Multi-user privacy and tenant/reader boundaries
- Migration and existing-data compatibility
- Failure, retry, cancellation, and empty states
- Keyboard and screen-reader usability
- Responsive mobile and desktop behavior
- Localization and date/time/number formatting
- Performance on large libraries or files
- Tests and regression coverage
- Documentation and changelog
- Docker/self-hosted deployment compatibility

## Data and migration safety

- Review EF Core migrations and model configuration before changing persistent data.
- Prefer additive, reversible migrations when practical.
- Never silently delete, merge, or rewrite user records during import or deduplication.
- For destructive or ambiguous operations, provide a preview, explicit confirmation, and a recoverable path where feasible.
- Test old-database startup/upgrade behavior when changing schema.
- Backups must be validated for completeness and restore usability; a successful archive creation alone is insufficient.
- Never put passwords, tokens, API keys, private notes, or private book files into logs or public share outputs.

## Integration safety

For KOReader, Kobo, Audiobookshelf, Readwise, Hardcover, email delivery, public-domain catalogs, or other integrations:
- Inspect current sync direction, identity mapping, conflict rules, retries, and rate-limit handling.
- Make sync idempotent where possible.
- Do not overwrite newer local progress or metadata without a defined conflict policy.
- Surface actionable sync status and errors.
- Test behavior when a provider is unavailable or credentials are invalid.
- Keep provider-specific code isolated according to existing project architecture.

## Testing expectations

Use the commands documented in the repository. Public docs currently describe `dotnet test` and Playwright tests under `tests/e2e`; verify the actual scripts and prerequisites before relying on them.

Add tests at the right level:
- Unit tests for rules, validation, mapping, and conflict resolution.
- Integration tests for persistence, authorization, imports, exports, and migrations.
- Browser tests for critical reader, player, lending, and account workflows.
- Regression tests for every confirmed bug when practical.

Do not make tests brittle by asserting implementation details when observable behavior is sufficient.

## Scope control and priorities

Use this order when prioritizing verified gaps:
- **P0 — Critical:** data loss/corruption, authorization or privacy breach, security vulnerability, broken upgrade/restore, or a core workflow that cannot complete.
- **P1 — High:** core reading/listening/progress/sync/import/lending workflow is unreliable or materially confusing.
- **P2 — Medium:** important discoverability, accessibility, portability, performance, or library-management friction.
- **P3 — Later:** differentiators and enhancements that are not needed to make existing promises reliable.

Do not implement a speculative feature merely because it appears in `IMPLEMENTATION_PLAN.md`. First verify the problem, the current code, and whether the feature is still relevant.

## Completion report format

At the end of a task, report:
1. **Outcome** — what changed and why.
2. **Files changed** — important files and migrations.
3. **Verification** — exact commands and pass/fail results.
4. **Risks/limitations** — anything not verified or needing manual testing.
5. **Plan status** — update milestone/task status honestly.
6. **Next step** — one recommended next task, not a large unapproved batch.

## Initial instruction

When beginning work from the implementation plan, perform M0 first. Do not immediately start implementing every roadmap item. Produce an evidence-backed audit, identify the five highest-impact verified gaps, recommend the single best next task, and wait for approval before making broad changes.
