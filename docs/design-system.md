# Shelf design system

A quiet home for your reading life: warm paper surfaces, deep library green, the books' own covers, and restrained editorial type. Management stays fast and plain; the reading carries the character.

This is the working version of the Shelf design guide. Update it when a token, a component, or the navigation changes.

## Principles

1. **Book-first.** Covers and titles are the color and interest. Chrome stays calm.
2. **Warm, not nostalgic.** Paper neutrals and Lora where it earns its place; Inter for everything you scan or press.
3. **Quietly capable.** Fast and clear, never an admin panel.
4. **Personal by default.** Reading history, notes, highlights, and lending are separate kinds of information and look it.
5. **Accessible everywhere.** Keyboard, touch, small screens, contrast, and dark mode are part of every screen.

## Where it lives

| What | Where |
| --- | --- |
| Tokens and every style | `src/Shelf.Api/wwwroot/app.css`. The tokens at the top are the only place colors, type, spacing, and radii are set. |
| Fonts | `src/Shelf.Api/wwwroot/fonts`, self-hosted Inter and Lora variable fonts under the SIL Open Font License. No page calls out to a font service. |
| App frame | `Components/Layout/MainLayout.razor`: sidebar, phone top bar and drawer, bottom bar, reminder notice. |
| Shared pieces | `Components/Shared`: `StatusBadge`, `BookCover`, `EmptyState`. |

## Tokens

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `--color-brand` | `#263E38` library green | `#86A995` | Identity, primary actions, current navigation, progress. |
| `--color-on-brand` | `#F5F1E8` | `#202824` | Text on a brand fill. |
| `--color-canvas` | `#F5F1E8` warm paper | `#202824` | Page background. |
| `--color-surface` | `#FFFDF9` | `#2B342F` | Panels, cards, fields. |
| `--color-sunken` | `#ECE6D9` | `#1A211E` | Badges, tracks, cover placeholders. |
| `--color-accent` | `#D5A46A` leather tan | same | Small accents only: quote rules, the notice dot, loan badges. Never text. |
| `--color-ink` | `#252A28` | `#F1EDE4` | Main text. |
| `--color-muted` | `#66716B` | `#C0C9C1` | Secondary text and metadata. |
| `--color-rule` | `#DDD5C6` | `#3C4842` | Dividers and panel borders. |
| `--color-control` | `#79837D` | `#6F7B73` | Field and button borders, at 3:1 or better against the canvas. |
| `--color-danger` | `#8A1F11` | `#F0B4A8` | Destructive actions and errors. |

Space runs `--space-1` (4px) to `--space-12` (48px). Radii are `--radius-card` 12px, `--radius-control` 8px, and `--radius-pill`. Type sizes run `--text-xs` to `--text-2xl`. Controls are at least `--touch` (2.5rem) tall.

Measured contrast on the canvas: ink 12.9:1, green 10.2:1, muted 4.5:1, danger 8.2:1; in dark mode, text 12.9:1, secondary 8.9:1, accent 5.8:1. Tan on paper is 2:1, which is why it never carries text. Check any new pairing against WCAG AA before using it.

## Type

- **Lora**: page headings, section headings, book titles in cards and on the book page, quotes, the reading passage, and milestones.
- **Inter**: navigation, labels, fields, buttons, filters, badges, metadata, and statistics.
- The wordmark is a lowercase **shelf** in Inter Bold, in green, beside a plain three-spine mark.

## Layout and navigation

- **Wide screens:** a slim sticky sidebar holds the wordmark, search, a solid **Add a book**, then the groups below. Content sits in a column up to 62rem.
- **Phones (under 52rem):** a top bar with the wordmark and a **Menu** drawer holding the same sidebar, plus a bottom bar for Library, Reading, Add, Loans, and Stats. No page scrolls sideways.

| Your library | Discover & reflect | Your copies | Settings |
| --- | --- | --- | --- |
| Library, Reading, Want to read, Finished, Loans, Shelves | Authors, Series, Quotes, Stats, Years | Places, Condition, Recommenders | Account, Backup & restore, Devices, Readers (admins) |

The current link is marked with `aria-current="page"`. The library's views share a path and differ by `?status=`, so their links are matched on that query.

## Components

| Component | Rule |
| --- | --- |
| Book cover | `BookCover`: always 2:3, a small radius and a soft shadow. With no cover, a green spine with the title, hidden from screen readers since the title is beside it. |
| Book card | Cover, Lora title, author and year, then badges. The title is the only link, and it stretches over the card. |
| Buttons | A form's submit button and `.primary` are solid green. Everything else is a quiet outline. `.danger` is a red outline. Toggles use `aria-pressed`. |
| Status badge | `StatusBadge`: the words *Want to read*, *Reading*, *Finished*, or *Abandoned*, each with a different small shape (empty ring, half ring, full dot, diamond), so status never depends on color. *On loan* is a tan-tinted badge with a square. |
| Search and filters | Search, status, and sort stay visible. The rest sits under **More filters**, with a count of how many are on. **Clear filters** appears whenever anything narrows the list. |
| Forms | A visible label on every field, errors in red beside the form, a green *Saved.* after saving. |
| Panels | A surface with a rule border and a 12px radius groups one kind of information. Settings pages put each setting in its own panel, and fields stop at 40rem however wide the page is. |
| Empty states | `EmptyState`: what belongs here, and one next step. |
| Destructive actions | Say what will be lost, then ask again. Account and reader deletion also name what happens to loans. |
| Notice | One line under the top bar for overdue loans, books due soon, and waiting asks, linking to Loans. |

## Screen patterns

- **Library:** Reading now, Read next (or one pick from the want list), and Recently finished lead the page while nothing is filtered. Below them, all books in a cover grid, or a compact list with status, love, read-next, and return controls.
- **Book:** the cover, title, author, series, and status lead, with one direct next step: *Start reading*, then *Mark finished*. Below it, separate panels for the reading log, lending, quotes, highlights, files, details (folded away), and removal.
- **Stats:** the year's goal and a finished-per-month chart come first, then the counts, the month of reading, and recent sessions. Reaching the goal is noted once, quietly.
- **Reader and player:** the sidebar, reminders, and bottom bar step aside for a slim bar with the wordmark and a way back. The chapter sits in a calm frame, with text size, line spacing, and reading width under **Text settings**. They are kept per reader and applied to the chapter, which also follows dark mode. The place is restored without asking.
- **PDFs:** drawn by PDF.js (self-hosted in `wwwroot/lib/pdfjs`, with eval and XFA turned off) one page at a time, with selectable text. The text size setting zooms from *Fit the width*; spacing and width do not apply, because a PDF keeps its own layout. Arrow keys and Page Up/Down turn pages, and the page is kept like an EPUB chapter.
- **Library view:** Covers or List is remembered for each reader; a `?view=` link still wins.
- **Uploads:** e-book, audiobook, and backup forms carry `data-upload`. `wwwroot/upload.js` sends them with a progress bar and an honest status line, then follows the server's redirect to the result. Without the script they post the ordinary way.
- **Backup & restore:** downloading is a solid green action that changes nothing. Restoring is a quiet action, says plainly that it only adds books, and reports exactly what it did.

## Accessibility

- A skip link to the page content, and visible focus rings in the brand color.
- `prefers-reduced-motion` turns off animation and transitions.
- `prefers-color-scheme: dark` switches every token; covers are checked in both themes.
- Reading status, loans, and overdue dates are always in words.
- Progress bars carry an `aria-label` with the page numbers.
