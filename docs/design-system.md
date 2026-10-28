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

Measured contrast on the canvas: ink 12.9:1, green 10.2:1, muted 4.5:1, danger 8.2:1; in dark mode, text 12.9:1, secondary 8.9:1, accent 5.8:1. Tan on paper is 2:1, which is why it never carries text. Muted on sunken is 4.1:1, short of AA for small text: on a sunken background, such as a chosen option, small secondary text takes ink instead. Check any new pairing against WCAG AA before using it.

## Type

- **Lora**: page headings, section headings, book titles in cards and on the book page, quotes, the reading passage, and milestones.
- **Inter**: navigation, labels, fields, buttons, filters, badges, metadata, and statistics.
- The wordmark is a lowercase **shelf** in Inter Bold, in green, beside a plain three-spine mark.

## Layout and navigation

- **Wide screens:** a slim sticky sidebar holds the wordmark, search, a solid **Add a book**, the five main places with their icons, then three groups that fold away. Content sits in a column up to 62rem.
- **Phones (under 52rem):** a top bar with the wordmark and a **Menu** drawer holding the same sidebar, plus a bottom bar for Home, Library, Add (a solid round button, the one action among places), Reading, and Insights. No page scrolls sideways.

| Main | Browse (folds) | Your copies (folds) | Settings (folds) |
| --- | --- | --- | --- |
| Home, Library, Reading, Loans, Insights | Want to read, Finished, saved searches, Collections, Series, Authors, Podcasts, Journal, Calendar, Reference desk, Words and passages, Quotes & highlights, Search inside books, What to read next, Reading DNA, Book clubs, Free books, Years | Shelves, Places, Scan books, QR labels, Condition, Recommenders, Duplicates, Tidy up | Account, Activity, Backup & restore, Trash & undo, Devices, Offline reading, Readers (admins) |

A folded group opens by itself on one of its own pages, and Browse while there are saved searches or series news. The current link is marked with `aria-current="page"`. The library's views share `/library` and differ by `?status=`, so their links are matched on that query. Addresses from before 2.0 (`/?status=…`) redirect to `/library`.

## Components

| Component | Rule |
| --- | --- |
| Book cover | `BookCover`: always 2:3, a small radius and a soft shadow. With no cover, a green spine with the title, hidden from screen readers since the title is beside it. |
| Book card | Cover, Lora title, author and year, then badges. The title is the only link, and it stretches over the card. |
| Buttons | A form's submit button and `.primary` are solid green. Everything else is a quiet outline. `.danger` is a red outline. Toggles use `aria-pressed`. |
| Status badge | `StatusBadge`: the words *Want to read*, *Reading*, *Finished*, or *Abandoned*, each with a different small shape (empty ring, half ring, full dot, diamond), so status never depends on color. *On loan* is a tan-tinted badge with a square. |
| Search and filters | Search and sort stay visible, with a row of chips for each status in use and On loan, E-book, and Audiobook; one status at a time, the others toggles. Loved and Read next sit under **More filters**, with a count of how many are on. **Clear filters** appears whenever anything narrows the list. The order chosen is kept for the reader. On a phone the chips scroll sideways in one row. |
| Menu | `details.menu`: the rarer actions under a quiet **More**, opening a small panel below it. |
| Forms | A visible label on every field, errors in red beside the form, a green *Saved.* after saving. |
| Panels | A surface with a rule border and a 12px radius groups one kind of information. Settings pages put each setting in its own panel, and fields stop at 40rem however wide the page is. |
| Empty states | `EmptyState`: what belongs here, and one next step. |
| Destructive actions | Say what will be lost, then ask again. Account and reader deletion also name what happens to loans. |
| Notice | One line under the top bar for overdue loans, books due soon, and waiting asks, linking to Loans. |

## Screen patterns

- **Home:** the book last read or listened to leads, in one card with its cover, progress, and a way straight back in (*Resume reading*, *Resume listening*, or *Update progress* for paper). Then four tiles (reading, want to read, finished this year against the goal, on loan), the other open books, read next, recently added as a sideways row of covers, and recently finished.
- **Library:** every book in a cover grid, each with its stars and a status menu drawn as a badge, or a compact list with status, love, read-next, and return controls.
- **Reading:** the open books, each with its ways back in, the read-next queue, the last weeks of reading and listening, and the latest quotes and highlights.
- **Book:** the cover, title, author, series, status, and progress lead, with the likeliest next step solid (*Read*, *Listen*, *Start reading*, or *Update progress*), then the status change, **Edit**, and **More** for the rest. Below it, panels for the reading log, quotes, highlights, files, lending, details (a summary of what is filled in, with the form folded away), collections, and removal. Marking a book finished offers, once and dismissibly, a rating, a quote, and the next book.
- **Insights:** the year's goals and a finished-per-month chart first (the goal form folded once set), then a year of days read and listened as a grid with its sentence beside it, what you return to (authors, kinds, series), milestones, the counts, the month of reading, and recent sessions. Reaching the goal is noted once, quietly; a run of days is told, never asked for.
- **Reader and player:** the sidebar, reminders, and bottom bar step aside for a slim bar with the wordmark and a way back. The reader's own row holds the book's title as the way back and an **Aa Text settings** menu: page (as the device, light, sepia, or dark), typeface (the book's own, serif, or sans), text size, line spacing, layout, and reading width. They are kept per reader and applied to the chapter. The place is restored without asking.
- **Look:** light or dark follows the device unless the reader chose one in Account; the choice sets `data-theme` on the page, and the dark tokens apply under it.
- **PDFs:** drawn by PDF.js (self-hosted in `wwwroot/lib/pdfjs`, with eval and XFA turned off) one page at a time, with selectable text. The text size setting zooms from *Fit the width*; spacing and width do not apply, because a PDF keeps its own layout. Arrow keys and Page Up/Down turn pages, and the page is kept like an EPUB chapter. Selecting text on a page opens the same highlight and note panel as an EPUB; a PDF highlight is stored with its page in place of the chapter, found again by its words and the words around them, and painted in tan over the page. A scanned page gets the same treatment from OCR: the server reads it in the background (Tesseract, through `Books/Ocr.cs`), and the viewer lays each word over the picture at its box, with a one-line note above the page saying whether its words were read, are being read, or cannot be.
- **Library view:** Covers or List is remembered for each reader; a `?view=` link still wins.
- **Uploads:** e-book, audiobook, and backup forms carry `data-upload`. `wwwroot/upload.js` sends them with a progress bar and an honest status line, then follows the server's redirect to the result. Without the script they post the ordinary way.
- **Bulk changes:** the list view's checkboxes bring up a sticky bar for status, tags, loved, and delete; delete asks again and says what goes.
- **Search inside books:** results group by book, each match shown in context with the words marked in tan, and an *Open here* link into the reader.
- **Player:** speed and a sleep timer sit under the audio controls, in the same settings row as the reader's text settings; the timer says when it will stop, and says so again when it has.
- **Backup & restore:** downloading is a solid green action that changes nothing. Restoring is a quiet action, says plainly that it only adds books, and reports exactly what it did.
- **Trash & undo:** deleting says where things go and for how long; bringing back is a quiet button, letting go for good is a red outline, and emptying the trash asks again. An undo for many books says what it will and won't set back before it does it.
- **Scanning:** the camera sits in the page, not a dialog, with the last code read said under it in words and announced politely; typing an ISBN is always beside it.

## Accessibility

- A skip link to the page content, and visible focus rings in the brand color.
- `prefers-reduced-motion` turns off animation and transitions.
- `prefers-color-scheme: dark` switches every token; covers are checked in both themes.
- Reading status, loans, and overdue dates are always in words.
- Progress bars carry an `aria-label` with the page numbers.
