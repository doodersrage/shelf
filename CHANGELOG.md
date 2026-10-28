# Changelog

Every release of Shelf, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [semantic versioning](https://semver.org).

## [Unreleased]

### Security

- **A year's card keeps to the internet**: the covers drawn into a year's card, fetched from the addresses books' covers were linked from, went to any address, this machine and its network included, and whatever answered was drawn into the card, so a reader could use it to read what a service inside the network said. They are now held to the same rule as podcast feeds: the open internet only, unless `Network:AllowPrivateAddresses` is set; only a JPEG, PNG, GIF, or WebP of a cover's size is drawn in, read no further than that, and anything else gets a spine.

## [2.17.1] - 2026-10-15

### Fixed

- **Not found suggests on PostgreSQL too**: on a shelf using PostgreSQL, the page for an address with nothing at it was sent before its suggestions were found, so it offered only the search; they are now found before the page is drawn.

## [2.17.0] - 2026-10-15

### Added

- **A page for addresses with nothing at them**: instead of a blank page, a browser is shown where it may have meant to go: a page whose address is a letter or two off (`/libary`), your books, authors, series, places, and collections matching the address's words, as Go to finds them, and the book at `/book/42`; with a search of the library filled in from the address, and links to Home, Library, Trash & undo, and Help. Signed out, it offers to sign in and says nothing of any shelf. It answers 404, and the API and anything not asking for a page keep their plain 404.

## [2.16.2] - 2026-10-15

### Fixed

- **Big open shelves open**: another reader's open shelf is searched, sorted, and shown as your library is, as covers, a list, or a compact table, 60 books at a time: by title, author, series, or ISBN, sorted by author, title, series, year, or the most recently added, narrowed to those not lent out or with an e-book or audiobook, and to an author or a series from their names (never by the owner's statuses, stars, or tags), reading only the page shown from the database, where before every book was read and drawn at once and a shelf of 80,000 timed out. A reader's shelf on a linked Shelf is shown the same way, and asked for a page at a time and for the one book asked to borrow; a linked Shelf from before sends its whole shelf as it did, and the page is cut from it here.

## [2.16.1] - 2026-10-15

### Fixed

- **Turnstile refusals say why**: a form Turnstile turns down, while the widget shows its tick, is logged with the reason and shown on **Server health** until a check passes: a token made on a host the shelf doesn't expect (with the host it came from and the ones expected), a secret key Cloudflare doesn't know, a token made for another form, one expired or used already, or Cloudflare out of reach. Before, Server health said only that Turnstile was on.

## [2.16.0] - 2026-10-15

### Added

- **Help in the app**: a **Help** page, under **Settings** and at the foot of the menu (and found by Go to as "help", "docs", or "manual"), lists the guides in plain words and says who looks after the shelf; a **?** beside the name of many pages opens the guide to that page; **Server health** links each fix to how to make it; and the sign-in page links to help with signing in. `DocsUrl` points them at a shelf's own copy of the guides.
- **[Features](https://doodersrage.github.io/shelf/features.html)**: a page of the documentation showing everything Shelf does, part by part, with screenshots, and screenshots in the guides beside what they describe: the library, Go to, the compact table, the reader, the player, loans, book clubs, adding books, devices, notifications and their emails, accounts, Shelf admin, invitations, and Server health.

- **More from Audiobookshelf**: an import brings, if you like, your bookmarks, your listening history (so Insights, goals, and the streak include it), the days you started and finished each book, and your collections and playlists as collections, besides where you stopped; and all of it for books already on your shelf, without their files coming again. An admin of both can bring every Audiobookshelf user's books and reading to the shelf of the reader they are here, matched by email or name, only the books each started or every book, inviting those not here yet.

### Fixed

- **"This page's buttons cannot reach Shelf" when nothing was wrong**: the banner is for a live connection that never comes up (an extension or privacy setting blocking it). It also showed when a working connection dropped and was slow to come back, as after a laptop sleeps, a phone changes networks, or a tab sits in the background, and then stayed even once the connection was back. Now a dropped connection is left to Blazor's own reconnecting bar, a connection that comes up late takes the banner away, and time a tab spends hidden doesn't count.

## [2.15.0] - 2026-10-15

### Added

- **Cloudflare Turnstile** on signing in, signing up, and asking for a password reset, once `Turnstile:SiteKey` and `Turnstile:SecretKey` are set from a widget of your own. Each token is checked with Cloudflare by the server, for its own form and the shelf's own host names (`Turnstile:Hostnames`, or the host of `Email:PublicAddress`); a check that fails or cannot finish is refused. **Server health** says whether it is on.

- **Shelf admin**, the old Readers page in tabs: **Readers**, **Backups**, **System**, and **Activity**.
  - **Invitations**: invite people by email, up to fifty at once; each link makes an account with that address, confirmed, even when sign-up is closed. Send again, or withdraw.
  - **Who may sign up**: anyone, only people invited, or no one new, chosen on the page.
  - **A list of readers** to search and sort, with each one's address, whether it is confirmed, two-step sign-in, books, and when they were last seen; and for each, **Rename**, **change their address**, **Sign out everywhere**, and **Suspend**, which stops every way of signing in and keeps their shelf.
  - **Background work** to run now, a test email with the server's answer, and an **announcement** at the top of every page, and by email if you like.

- **More from your book clubs**: members hear when the club moves to its next book, when a meeting is called off, a day and an hour before each meeting, and when they are taken out of a club or it ends.
- **An ask turned down** is told to whoever asked, so they are not left waiting.
- **Emails about your account**: a changed email address (told to the old one), a changed or reset password, two-step sign-in turned off, a new passkey, and a sign-in from a device not seen before each come by email, whatever you chose to hear about, with what to do if it wasn't you.
- **For admins**: a new reader signing up, and anything going wrong on **Server health**, each told once, under a new kind on **Notifications**.

### Changed

- **Emails look the part**: every email Shelf sends, from password resets and address confirmations to notifications, series news, and Send to Kindle, comes in the shelf's own colours under its mark, with the subject as a heading, links as buttons, and why it came set apart at the foot. A plain-text copy goes with each, for mail apps that prefer it.

## [2.14.2] - 2026-10-15

### Fixed

- **Email with a named sender**: `Email:From` written with a name, such as `Shelf <shelf@example.org>` or `"My Shelf" <shelf@example.org>`, stopped every email from going; it now sends, under that name.
- An email that does not go is logged with the mail server's answer, and **Server health** shows it ("The last email did not go: …") until one goes, rather than saying email is set up.
- A mail server that refuses an email no longer breaks signing up, asking for a password reset, changing an address, or **Send a test email**, which now says what the server answered.

## [2.14.1] - 2026-10-15

### Added

- **Notifications by email**: with a mail server set up, notifications go to the address a reader signs in with, for every reader until they untick **Email me at …** under **Notifications**, and for the kinds they chose. The daily loan email is now one of them.
- **Confirmed addresses**: with a mail server and `Email:PublicAddress`, a new or changed address is the reader's only once they open the link sent to it, so no one can take an address that isn't theirs. A reader can sign in with it while it waits, and **Send the link again** is on **Account**.
- **A kinder audiobook player**: after a pause the book picks up a few seconds back, more the longer it was away (5 to 20 seconds, never before the chapter's start; off under **Player settings**); a move to another chapter, along the scrubber, or to a bookmark can be taken back with **Back to …** for ten seconds; and the sleep timer fades the sound out over its last half minute.
- Where two readers share a name, the lists for lending, shelf checks, and book clubs tell them apart with a hint of each address, such as `s…m@example.org`.

### Changed

- **Sign in with your email address.** Making an account asks for an email address, which no two readers may share; names may now repeat, and other readers still see only the name. Accounts made before keep signing in by name until their reader adds an address, and an address already given for reminders works for signing in from the first start. A child's account still signs in by name, which no other reader may have. A name only one reader has still works on the sign-in page, for KOReader's progress sync, and for Audiobookshelf apps. `--reset-password` and `--make-admin` take an email address or a name.
- An address can be changed on **Account** to one no other reader has, but no longer removed.

### Removed

- The separate daily reminder email and its checkbox: loans due and overdue come with the other notifications, by email among them.

## [2.14.0] - 2026-10-14

### Added

- **Compact library**: a third way to see the library, beside covers and the list: a table, one line a book, holding every book that matches with only the lines in view drawn, so 70,000 books scroll as easily as 70. **Columns** chooses what it shows beside the title and author (status, series, year, format, place, rating, pages, added, finished, on loan), kept for next time; `?view=compact` opens it.
- **Keyboard shortcuts**: "?" lists every key Shelf answers to, anywhere, the reader and player included; so does **Keyboard shortcuts** at the foot of the menu.

### Changed

- **One box to search or go to**: the menu's search box and Go to are one. Typing there opens Go to, whose first answer, and Enter, searches the library, unless what was typed is the very name of a page or book; the arrows reach the pages and books it finds. Without scripts it still searches the library.
- In the covers and the list, books far off screen are no longer laid out or painted until they come near, so a long scroll stays quick.

## [2.13.0] - 2026-10-14

### Added

- **Go to…** (Ctrl K, ⌘ K, or "/"): any page by its name or what it's for ("goodreads", "passkeys", "dark"), and any of your books, authors, series, places, and collections by a few letters, chosen with the arrow keys. Also `GET /books/jump?q=`.
- **Add & import**: every way to bring books in on one page, a book at a time, a shelf at a time, from another app, or something new. The add-book form and a new shelf's welcome lead there, and the welcome now offers scanning your shelves.
- **A menu of your own**: under Account, **Menu** leaves out the pages you don't use; Go to still finds them.
- **Undo, there and then**: a change to many books, from the library or a scanning run, is undone from the message that reports it, without going to Trash & undo.
- **Recent places in Go to**: before anything is typed, the pages and books opened lately on this device come first.
- **Changing many books** in the library's list can now put them at a place, and choose every book the filter matches (up to a thousand), not only those shown.
- **Add on a phone** opens a short list: scan books, type a title or ISBN, or more ways to add.

### Changed

- **The menu, regrouped** around what you do: Your books, Your reading, Discover, Insights, Your copies, Settings, and Admin, in place of one long Browse.
- **A book's page in tabs**: Reading, Details, Your copy, and History. **Account in tabs**: You, Sign-in & security, Connections, and Notifications. Links to a part of either page open the tab it's in, and `?tab=` chooses one; after uploading a file, cover, or picture, the book opens on Your copy. Only the tab shown is drawn, so the pages open faster.
- Nine pages that only show things (years, a year, reading DNA, what to read next, the map, the calendar, shelves, condition, recommenders) are plain pages, with no live connection to start or lose.
- The same message, done or a problem, on every page that reports one.
- The library's **Show more** is pressed for you as you scroll near it, so a big library reads as one list; so is a place's.
- Seven more pages work as plain pages, their buttons as forms: Authors (Follow), a shared shelf and a linked one (Ask to borrow), Book clubs, Collections, the Reference desk, and Server health.
- Reading DNA, What to read next, a year's review, and Your collection over time show their heading and a placeholder at once, and the rest when it's worked out.
- **Recent activity** on Account is under Sign-in & security; a book kept for offline reading says so beside the button, wherever the tab.

### Fixed

- Compiler warnings: a buffer allocated on the stack inside a loop when reading a WAV file's length, and two never-used fields on Up next.

## [2.12.3] - 2026-10-14

### Added

- **Spines found are put in their place**: books a photo of the spines finds that aren't noted at the run's place are ticked, and one button notes them there together (ticking them off in a check), as a change undone at once. Untick any the spines were misread as. Also `POST /books/scans/{id}/place`.
- **Spines not in the catalog, added**: **Find it** beside a line read but matching none of your books looks it up in Open Library; in an adding run, **Add to this run** puts it on the run's list with the barcodes read, and elsewhere **Add it** opens the book form with the run's place filled in. The book form takes `location` in its address.

### Changed

- Only lines read clearly, from a turn of the photo about as clear as the best, are offered as not in the catalog, so text read upside down no longer is; nor is a line that names a book already found. The list is open rather than folded away.

## [2.12.2] - 2026-10-14

### Security

- System.Security.Cryptography.Pkcs, which the email library brings in, is 10.0.13, past [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx) (high) and [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) (moderate) in 10.0.0.

## [2.12.1] - 2026-10-14

### Added

- **Reading spines with the live camera**: while the camera is running in a scanning run, **Read the spines in view** reads the titles on the shelf it shows and matches them to your catalog, so a shelf can be read a stretch at a time without leaving the page. Each reading adds to what was found before. The live camera asks for a sharper picture, and takes a full still where the browser can.

### Changed

- The scanning page says that the live camera reads barcodes as they come and spines only when asked, as walking along a shelf with it running found nothing until a barcode was shown.

## [2.12.0] - 2026-10-14

### Added

- **Places**: rooms, the bookcases in them, their shelves, and boxes, nested as deep as you like and moved by dragging one onto another, a copy's place following. Places your books already name become places at a word. A place for a time, such as a storage unit, keeps where each copy usually lives, and puts it back. Each place has a page of its copies, and a label to print with a QR code that opens it.
- **Moving house**: numbered boxes with where each is going, packed by scanning each book as it goes in, with a label of what's inside; unpacked by scanning them out, when they go where the box was going and any that didn't arrive are named.
- **Your collection over time**: copies that came, were finished, and left each year, how many were kept, what they cost and brought back; what waits to be read by age, format, subject, and series; books never opened; books read again and again. A copy can be let go (donated, sold, given away, discarded, lost), keeping its record and reading, and a price paid noted. Goals for reading what was already on the shelf, and for letting copies go.
- **Up next**: a short queue in your order, dragged into place; holds until a day or for a mood; a book in a series waiting for the one before it. Finishing a book offers the next of its series, then the queue.
- **Where each device has you**: each source's last place in a book (Shelf's reader and player, offline reading, KOReader, the Audiobookshelf apps), and a choice of the right one, back or forward, with the choices kept.
- **Search your own words**: notes, reviews, quotes, highlights, the reading log, the journal, and the reference desk, searched beside the books' own text, narrowed by kind, status, and author.
- **Rules**: when a book is added, started, finished, read again, lent, or back, or a loan is overdue, and the book is as asked, tag it, put it in a collection, queue it, note its place, or tell you. A dry run first; each run listed and undone like any change; rules never set off rules.
- **Server health**, for admins: backups and their proof, copies elsewhere, stored files, disk space, imports, webhooks, linked shelves, email, OCR, and each background job, what needs you first with what to do.
- **Who sees what**, for admins: what one reader can see and why, their tokens, what anyone with their links sees, and why they can or can't see a given book.
- **Ask your shelf**: questions in everyday words answered from your own books, notes, and quotes, with links. With an Anthropic key, the question alone may be read by the model; nothing from the shelf is sent.
- **Downloads that prove themselves**: every file in a download listed with its SHA-256 in `manifest.json`, with `SCHEMA.md` describing each file, and a restore says if any file no longer matches. Tidy up names files in formats that may not last, or locked by a shop.
- **Your library's future**: who is to have which books and a word for whoever looks after them, kept sealed and private, printed or downloaded as a list. A note of wishes, not a will.
- **Tidy up** finds paper copies with no ISBN, series books with no number, books with no author, page counts that look wrong, and publishers written more than one way.
- **A newer Shelf**: admins are told at the top of each page when a new release is out, with what's new and how to update, until they update or say not now. `Updates:Check` turns it off.

### Fixed

- **Goodreads imports find more audiobooks and lecture courses already on the shelf**: a title with a series in front ("The Modern Scholar: Heavens Above: …"), one by the same author whose words are all in the other's, a long title the same word for word whoever Goodreads says wrote it, and a narrator or co-author Goodreads lists under Additional Authors. One volume of a course is never taken for another.

## [2.11.1] - 2026-10-14

### Fixed

- **Goodreads, StoryGraph, and spreadsheet imports find books already on your shelf in any edition**: by ISBN, or by author and title however written, so an audiobook with its own ISBN, "(Unabridged)" in its title, a series before its title, or its narrator beside its author is no longer added a second time. A book found this way takes the status (when it is still want to read), dates, rating, review, and tags it doesn't have yet from the file, and the result says how many were filled in.

## [2.11.0] - 2026-10-14

### Added

- **Shelf photos**: in a scanning run, photograph the spines; their titles are read on the server with Tesseract and matched to your catalog, a misread letter forgiven, and in a shelf check the books found are ticked off. Lines that match nothing link to a search.
- **Reading DNA**: the subjects and authors you return to, what each year was about, what you read more and less of lately, what you own but leave alone, and books long waiting that fit you, each with its reasons. Worked out from your own shelf when you look, and kept nowhere.
- **A calendar**: books due back either way, your clubs' meetings, collections to read by a day, and your goals' year end, only ever what is yours or shared with you. The subscribed calendar now carries all of it, meetings at their moment.
- **A reference desk**: questions you look into across books, with notes, quotations, and links each citing a book and where in it; a bibliography in Chicago, APA, or MLA; and a research packet as Markdown.
- **Words and passages**: keep a word from the reader's look-up with its meaning and sentence, practise a few at a time as they come due, and download them as flashcards; keep passages beside their translations.
- **Import recipes**: bring in a CSV from any app, choosing which column holds what, what its statuses mean, and how it writes dates and tags, with the first rows shown first; keep it as a recipe for next time.
- **A sandbox**: a token for a shelf of sample books, to try a connector, script, or webhook on before it is given yours. No one else sees it, and it lends to no one.

### Fixed

- A spreadsheet import where two books share a tag not yet on the shelf no longer fails.


## [2.10.0] - 2026-10-13

### Added

- **A trash**: a deleted book waits 30 days with everything that was part of it and its files, and comes back as it was, under its own id; so does a deleted quote, reading session, or highlight. Merging duplicates sends the other entry there too, so a wrong merge can be taken back. **Trash & undo** is under Settings; `Trash:Days` sets how long.
- **Undo for changes to many books**: an edit to a selection, books deleted together, an import from a backup or a spreadsheet, books scanned in, details filled in, and the changes made under Tidy up are each kept as one, and undone as one. Undoing leaves alone anything changed by hand since.
- **A book's history**: every change to its details for a year, what it was and what it became, each one able to be put back.
- **Scanning a pile of books**: **Scan books** keeps the camera open and reads barcodes one after another, then adds them all at once, a book at a time from the catalogs. **Checking a shelf** goes along a place and says what is there, what is noted elsewhere, and what is missing, and can tag the missing. A run is kept, to take up again on any device.
- **More lending**: renewals, the state a copy came back in and a note on the loan, a record of every loan after it ends, readers waiting in line for a book with the first told when it comes back, how many days ahead a loan counts as due soon, and **QR labels** to stick inside copies that open a page to lend or mark back the copy.
- **Backups proved**: each nightly backup is read back, its database opened and checked, and brought up to the running version before it is sent anywhere; Readers shows what each would restore. A reader can **bring one book back from a nightly backup**, with its notes, log, and files, without rolling back anything else.
- **A weekly file check**: every stored e-book, audiobook, cover, and picture is read through and compared with what it was when saved; a missing, damaged, or unreadable file shows on Tidy up, beside the newest backup proved to restore.
- **Details looked over before they change**: Tidy up can ask Open Library and Google Books apart, for books missing details or for every book, and lists each difference with what each says and where they disagree, to choose from.
- **Duplicates by likeness**: titles alike but for a subtitle, an edition, or a word or two, by the same author however written, with how sure each group is; **These are different books** keeps a pair from being shown again.
- **A download in plain formats**: a reader's zip now has the books as a spreadsheet, their collections, the loans of their books, and what they marked in borrowed books, with a README describing each file. Only their own records, never another reader's.


## [2.9.0] - 2026-10-13

### Added

- **Goals of your own**, on Insights beside the books, pages, and hours: a number of books with a tag, by an author, in a series, or by authors new to you. Each shows the books that count, rereads among them, and says gently what would see it done in the weeks left, never that you're behind.
- **An event feed**: everything a webhook is sent is kept for you too, the latest 1,000, at `GET /books/events`, for a script that would rather ask than be told. It works with webhooks turned off.
- **API tokens kept to parts of the shelf**: the library, files, the journal, book clubs, or the event feed, chosen when the token is made. A call outside them is refused.

### Fixed

- A club meeting's time is the moment meant in the zone it was arranged in: each member sees it in their own time, and notifications name the zone. In 2.8.0 a time was kept as typed and taken as UTC; a meeting arranged then may be off by your distance from UTC, and arranging it again puts it right.
- A book added as finished on an earlier day no longer gets today as its start, after its finish; the journal leaves such starts out for books added before.

## [2.8.0] - 2026-10-13

### Added

- **Reading a book again**: **Read it again** on a finished book puts that read by, with its dates and stars, and starts the book over. Every read is kept, listed under the reading log with the sessions, highlights, and quotes from its days and a few words of your own on what changed. Each finish counts in its own year, on Insights, Years, the year's review, the goal, and the public page.
- **A journal**: your reading life by the day, in the sidebar. The books begun and finished, sessions and their notes, highlights, quotes, thoughts on earlier reads, and entries of your own, about a book or about where you were. Read it a year at a time, search every year, download a year as Markdown, or print it.
- **Connections between books**: link a book to another edition or translation, to one that inspired it, one that answers it, one that makes a contrast, or one to read beside it. Editions are listed under the title with what tells them apart and the one you read, and **Map** draws the links around a book.
- **Collections with notes and plans**: a note on each book in a collection, shown on its shared page too; and a day to read the books by, with whether they fit your own pace and, when they don't, a day that would or how many books to leave out.
- **A copy's history**: who had it before you, whether the author signed it, and up to 20 pictures of it.
- **Club meetings**: the club's starter arranges meetings with a book, how far to read, and what to talk about; members send questions, kept between them and the one who runs it until the meeting begins; and a summary of what was said. A post can be marked a spoiler, folded until opened and kept out of notifications.
- Webhooks for `book.reread` and `club.meeting`, and the API for all of the above. A backup carries earlier reads, the journal, and links between books.

## [2.7.3] - 2026-10-12

### Added

- A download from your indexers can be **cancelled** while it's sent or downloading, from the book's page or from Wanted: it's taken out of Deluge, qBittorrent, or SABnzbd with what it had downloaded so far. Handy for one that has stalled. The book stays on your want list, but isn't looked for again on its own, since the same release would most likely be chosen; search for it from its page and pick a better one.

## [2.7.2] - 2026-10-12

### Fixed

- **Insights** opens on a big shelf with many tags: each tag's books were counted with a look of its own through the shelf, seconds on tens of thousands of books with a few hundred tags. They're now counted together, and the page reads only the few details of each book its figures need.
- Home, Library, and What to read next read only the details of each book they show, rather than everything, such as an audiobook's chapter list, so a big shelf opens noticeably quicker.

## [2.7.1] - 2026-10-12

### Fixed

- **Insights** opens on a big shelf again: with 72,000 books it took 17 seconds to draw, twice over, and gave up; it now takes about a second and a half. A book's reading sessions, tags, and the like are now read in a query of their own rather than joined to the books, which SQLite does many times faster on a large shelf.
- Home and Reading are about three times quicker on a big shelf, and What to read next twice as quick: the books you're reading are put in order without a list of every one of them in the query, and a suggestion's reason is worked out only for those shown.

## [2.7.0] - 2026-10-12

### Added

- **Needs you**, at the top of Home, names what wants you now, and is only there when something does: a book you lent that's overdue, a borrowed one due back, a reader asking to borrow, new books in series you follow or by authors you follow. On Home it stands in for the loans banner every other page has.
- **Arrange Home** hides any of Home's sections and puts them in the order you like, kept for you on every device.
- **Connections**, at the top of Devices, shows how KOReader, a Kobo, Audiobookshelf, Readwise, Hardcover, and another shelf each stand: off, waiting for the first sync, working (and when it was last heard from, from which device), or needing a look, and why. A connection set up with nothing new isn't shown as trouble.
- When the device loses the shelf, a line across the top of the page says so, with the way to the books kept for reading offline, instead of pages that just don't load.

### Changed

- On a book with an e-book and a recording, the one you used last leads, the other beside it to go back to, and the page shows where you are in each, when you last read and listened, and that each keeps its own place.
- A book's page says when it's kept on this device to read or listen to offline, rather than only inside **More**.
- Lending a book says what the other reader gets: its files with a place and highlights of their own, never your notes, highlights, review, or reading log.
- The library's address keeps up with its filters and sort, so opening a book and going back finds the same books, as far down as you were, and a filtered view can be bookmarked. Each filter not shown as a chip, from an author's or series' link or under **More filters**, is a chip of its own, with **×** to take it away.
- **Keep this one** on Duplicates now says first what merging will bring across from the others and which of their files it will delete, and asks before it does.

### Fixed

- **Wanted** no longer lists audiobooks when Shelf fetches e-books (or e-books when it fetches audiobooks): a book you have, or have marked, in the other format isn't missing a file.

## [2.6.0] - 2026-10-12

### Changed

- The reader is friendlier to use. **☰** opens the book's contents, with the chapter being read marked, in place of a cramped list in the bar. **Aa** opens the text settings as taps rather than lists: the page's colors as swatches, the size a step at a time with **A−** and **A+** (now up to 200%), and the rest as rows of buttons; on a phone it, and the **⋯** panel, come up from the bottom. Esc or a tap on the dimmed page closes whichever is open. The bottom bar's line names the chapter and fits a phone.

### Fixed

- Chapters are named as the book's own table of contents names them. Before, a chapter took its file's title, which many books set to the book's title in every file, and which wasn't read at all in a file with a doctype (most EPUBs), so chapters showed as file names.

## [2.5.1] - 2026-10-11

### Changed

- The page-turning reader always fills the browser window, as Audiobookshelf's does, with its bars over the book instead of the page's around it: back, the title, the chapters, **Aa**, and **⋯** at the top; the page buttons and where you are, with the slider, at the bottom. A tap in the middle of the page shows or hides them. **⋯** opens a panel with the audiobook, Listen along, read aloud, the recap, this chapter's highlights, and the browser's own full screen; a selected passage gets a sheet over the page.
- **Full screen** and **F** now take only the browser's own full screen, since the reader already fills the window; Esc leaves it.
- A borrowed book's title in the reader's bar says it is lent to you, as the classic reader and the player do.

## [2.5.0] - 2026-10-11

### Added

- A full-screen reader for EPUBs, as Audiobookshelf's: **Full screen** or **F** gives the book the whole screen (the browser's own full screen too, where it allows), and a tap in the middle of the page shows or hides the bars: the way out, the chapters, the text settings, and where in the book, with a slider to go anywhere in it. Remembered on each device.

## [2.4.1] - 2026-10-11

### Security

- Addresses readers give the server to fetch (a podcast's feed and its episodes, an Audiobookshelf to import from when the reader isn't an admin) can no longer reach the server itself, the local network, or a cloud's metadata service. `Network:AllowPrivateAddresses` allows the local network. Notices and webhooks may still reach the local network, never the metadata service.
- A password reset link always goes to `Email:PublicAddress`; without it, no reset email is sent, where before the link went to whatever host the request named, so a forged request could send a reader a working link to someone else's site.
- Every answer carries `X-Content-Type-Options: nosniff`, a referrer policy, and, except the public page and its widget, a rule against being shown in another site's frame.
- `Hosting:KnownProxies` limits which addresses forwarded headers are trusted from.

### Fixed

- A child's account no longer has new books by followed authors fetched for it.
- Opening a search on **Find new books** from its address asks Open Library once, not twice.
- The daily look for followed authors' books notes the activity log only when it finds one.

### Changed

- The upload limits have a section of their own in the configuration docs, with the Docker form and the reverse proxy note.

## [2.4.0] - 2026-10-11

### Added

- **Find new books**: search Open Library for a book you don't have by title, author, or ISBN, and put it on your want list, ready to fetch.
- Follow an author, and their new books are offered once a day, or wanted and fetched straight away. The sidebar counts what's waiting.
- Hardcover, once connected, fills in what Open Library and Google Books leave blank when details are looked up: above all a book's series and number. `Hardcover:LookupToken` asks it for every reader.
- KOReader's place in an EPUB is traded with Shelf's reader to the word, both ways, instead of by the chapter.

## [2.3.0] - 2026-10-11

### Changed

- EPUBs open in a page-turning reader built on epub.js, as Audiobookshelf's is: the whole book in one view, turned by tapping, swiping, or the keys across chapters, with the page and how far through the book alongside. New readers start in pages; the classic chapter-at-a-time reader is a link away, and **Listen along** uses it.

### Added

- The place in an e-book is kept to the word (an EPUB CFI) as you turn pages, so it opens where you stopped on any device and screen. Audiobookshelf's phone apps share it both ways.

### Fixed

- A long chapter no longer reopens at its start: the place within it was only ever kept for the visit.

## [2.2.0] - 2026-10-11

### Added

- Finding and fetching books, as Sonarr does for shows: search your indexers (Jackett or Prowlarr) from a book's page, send the release to Deluge, qBittorrent, or SABnzbd, and the finished file lands on the book. **Wanted** lists want-to-read books with no file and can look for them all, or every day. An admin sets it up under **Downloads**; it is off until then.

## [2.1.1] - 2026-10-11

### Added

- The largest audiobook and e-book can be set: `Uploads:MaxAudioMegabytes` (1024 unless set) and `Uploads:MaxEbookMegabytes` (80 unless set). The pages quote whatever is set.

## [2.1.0] - 2026-10-11

### Added

- Keep in step with Audiobookshelf: connected with an API key, Shelf looks at a library once a day and brings across books added to it since, never the same one twice. **Check now** and **Stop keeping in step** are on the Audiobookshelf page.
- **Tidy up**, under Your copies: authors written more than one way made one, series taken out of titles like *The Tombs of Atuan (Earthsea Cycle, #2)*, and covers and missing details looked for in the background.
- **Activity**, under Settings: imports, details being filled in, and other background work in one place, with progress, a way to stop each job, and what finished lately.
- A new shelf opens on a welcome that asks where your books are now, with a card for each way to bring them: Audiobookshelf, Calibre, Goodreads or StoryGraph, files, by hand, and free books.
- **Where to get it**, on the page of a book with no e-book: a free e-book from Project Gutenberg or audiobook from LibriVox, added to that book in one step; reading or borrowing it from Open Library; and links to find it at a library near you (WorldCat) or borrow it with Libby. `Availability:Enabled` follows `FreeBooks:Enabled` unless set.
- **Share my year**, on a year in books: a picture of the year, its numbers, its covers, the longest book, and a favourite quote, to post or send, or save where sharing is not offered.

### Changed

- A scanned barcode's ISBN shows at once, while the catalogs are asked about the rest; before, it waited for their answer.
- **Fill empty details** runs in the background, a book at a time with a polite pause, rather than in the open page, so a shelf of thousands can take its hours; its progress shows on Activity and on every page.

## [2.0.1] - 2026-10-11

### Fixed

- A large Audiobookshelf library lists quickly, with a count as it comes in, where before a library of thousands showed nothing for minutes and could give up. Telling which books are already on the shelf no longer compares every book with every other.
- The list of books to choose from draws only the rows in view, so thousands scroll easily, and it can be narrowed by title or author, or to what's new, audiobooks, or e-books.

### Added

- Imports from Audiobookshelf and Calibre show how far they have come: a progress bar, counts, the book in hand, about how long is left, the last few brought, and any that couldn't be. Every other page shows a line with the progress while one runs. **Stop after this book** ends one early.

## [2.0.0] - 2026-10-11

Shelf 2.0 reorganizes what was there rather than adding more: a front page to pick up from, a library led by its covers, a book page with fewer buttons, a quieter reader, and insights that tell the year's story.

### Changed

- **Home** is the front page: the book you were last in leads, with a button straight back into it, then your shelf at a glance, the other open books, read next, recently added, and recently finished.
- **The library moved to `/library`.** Addresses from before (`/?status=Reading`, saved searches, bookmarks) redirect there.
- The sidebar has five main places, Home, Library, Reading, Loans, and Insights; the rest folds into **Browse**, **Your copies**, and **Settings**, each opening by itself on its own pages. The phone's bottom bar is Home, Library, Add, Reading, and Insights.
- The library's status menu became a row of chips (each status, on loan, e-book, audiobook), with counts; the chosen order is kept for next time.
- A book's page leads with the likeliest next step (Read, Listen, Start reading, or Update progress), then the status change, **Edit**, and **More** for the rarer actions. Its details read as a summary, with the form folded away, and its sections run reading log, quotes, highlights, files and lending, then details.
- The reader's settings fold into an **Aa Text settings** menu, so the page starts with the text; the line by the chapter list says which chapter of how many. Read aloud shows its speed and voice once it is reading.
- **Stats** is now **Insights** (at `/insights`, and still `/stats`), with the goal form folded once goals are set.

### Added

- **Reading**: the books you have open, each a tap from your place, the read-next queue, your reading and listening of the last weeks, and your latest quotes and highlights.
- On each cover in the library, its stars and its status as a menu, to change without opening the book.
- After marking a book finished, an offer you can dismiss: rate it, keep a quote, or start the next book.
- The reader's page in light, sepia, or dark, and a serif or sans serif typeface in place of the book's.
- Light or dark for all of Shelf, under **Look** on **Account**, in place of following the device.
- On Insights: a year of the days you read and listened, the authors, kinds of book, and series you return to, and milestones such as your first finished book, the hundredth, and ten thousand pages.
- A book looked up by ISBN shows the cover and details found, to check before adding it.

### Fixed

- A shelf moved from a Docker volume into a folder starts: the image takes over the files copied in, not only the folder, whoever copied them.
- When Shelf can't open its database, it says why at start in place of SQLite's "unable to open database file": the folder is missing or can't be written to, or the files are one folder down in a copied volume's `_data`.

## [1.9.0] - 2026-10-11

### Added

- Audiobookshelf's phone apps sign in to Shelf as to an Audiobookshelf server: your audiobooks and e-books listed, streamed, or downloaded for offline listening, with Android Auto and CarPlay, and the place, bookmarks, and time listened kept in step. With two-step sign-in, an API token is the password.
- Search inside audiobooks: a transcript, made by the read-along's speech-to-text with or without an EPUB, puts what a recording says in the search, and a hit opens the player at that moment.
- Edit an audiobook's chapters by hand: names and start times, across one file or many tracks.
- One M4B from many tracks, with chapters, details, and cover, made by ffmpeg: to download, or to keep in place of the tracks.
- Kobos get Kobo's own kind of EPUB (KEPUB), made from yours, for page numbers, time left in the chapter, and reading statistics. `Kobo:Kepub` turns it off.
- Better comics: CBR, CB7, and CBT files are repacked as CBZ, manga reads right to left (on by itself when its ComicInfo.xml says so), two pages at a time is a choice, and comics can be matched on ComicVine with a key an admin sets.
- Previously in this book: a recap of what has happened up to your place, and nothing after, written by a model an admin chooses (`Recap:ApiKey`), for e-books and for audiobooks with a transcript.
- A calendar of your loans' due dates to subscribe to, and a public page of what you're reading, with a widget for a website and a feed of books finished.
- Storage for admins: the space each reader's files take, by kind, and a limit that refuses files past it.
- PostgreSQL as the database, if wanted (`Database:Provider`), with `--move-to-postgres` to bring an existing shelf across.

## [1.8.0] - 2026-10-11

### Added

- Match an audiobook on Audible: search by title and author, or ASIN, in any Audible store, and take its narrator, series, publisher, year, and language, its cover, and its chapter names, which the player then shows across one file or many tracks.
- Notifications: on your phone or computer by the browser's push, or by ntfy or Gotify, when someone lends you a book or asks to borrow one, a loan is due or overdue, your series have new books, or there's news in your book clubs. Choose the kinds under Account.
- Webhooks: each reader's books added, started, finished, lent, and returned, asks to borrow, club posts, and new series books posted to addresses of their own, as signed JSON or a line for Discord or Slack.
- Hardcover both ways: what you change on Hardcover comes back to books not changed here since, and books only on Hardcover can be added.
- Readwise both ways: highlights made on a Kindle, Kobo, Apple Books, and elsewhere come from Readwise onto the books they're from, every hour if you like.
- KOReader's reading statistics: bring statistics.sqlite3 in on Devices, and the time spent on each page counts as reading time by day, in Stats, the year in review, and the hours goal.
- Children's accounts: a reader makes accounts for their children, who keep their own shelf and read the collections their guardian shares, but can't lend, share links, or connect services, and are kept out of other readers' lists.
- What to read next: the next books in your series, your want-to-read list ranked by the authors and subjects you love, and books on open shelves you might borrow.
- Read-along: a book with an EPUB and an audiobook can be matched word by word by a speech-to-text service of your choice (`ReadAlong:Url`). **Listen along** in the reader plays the audiobook and marks each word as it's read, and switching formats keeps the place to the word.
- Podcasts: follow podcasts by search, feed, or OPML; the newest episodes are kept downloaded, and they play in the audiobook player with their place kept.
- Linked shelves: two Shelf servers linked by their admins with a one-time code, whose readers see each other's open shelves and borrow from them. A book lent across goes as a copy while it is out.

### Changed

- Hardcover sends a book's reading dates as a new read only when they change, so a read isn't counted twice, and what was sent is kept in a way that no longer depends on the reader's language.
- The Docker image takes `--build-arg FFMPEG=true`, for read-along with a speech-to-text service that limits uploads.

## [1.7.0] - 2026-10-11

### Added

- Collections: lists you make by hand, in the order you choose, for a book club, summer reading, or books to lend. Add a book from its page, reorder and describe a collection on its own page, and share it by link like a saved search.
- Reading time: the reader counts time spent with a book open while someone is reading it, and **Stats** shows it beside listening time. It keeps the reading streak too.
- Your year in books: a review of each year, linked from Years and Stats, with the year's books, pages, hours, new authors, busiest month, standouts, favourites, and the words you kept.
- New books in your series come by email, each once, to readers with series alerts and email reminders turned on.
- Highlights from a Kindle: bring its My Clippings.txt in on Devices. Highlights land in an EPUB's chapters with their notes, or as quotes with their pages on other books, each once.
- Duplicates: books with the same ISBN, or the same title and author, are listed together, and keeping one merges the others into it: details, status, notes, quotes, highlights, reading log, time spent, collections, and the files it lacks.
- Pages in the EPUB reader: under Text settings, Layout lays a chapter out a screen at a time, turned by tapping the sides, swiping, the arrow keys, or the wheel, and on into the next chapter.
- Look up a word: select a word or short phrase in the reader for its definitions from Wiktionary and a summary from Wikipedia, in the book's language. `Lookup:Dictionary` turns it off.
- Share into Shelf: with Shelf on an Android home screen, share a book from any app and the add form opens with its ISBN looked up, or its title and author.
- Pages and hours goals beside the books goal: pages to read and hours to spend reading and listening each year, with progress on Stats and in the year's review.
- Book clubs: readers on the same shelf read together. Start a club and invite readers; members add books, pick the one being read, talk about each, and put one on their own shelf in a click.
- Readwise: connect it on Account with your access token, then send your highlights and quotes there, all at once or every hour as you make them.
- Hardcover: connect it on Account with your API token, and each book's status, stars, and reading dates go to your Hardcover account as they change.
- Kobo sync: a Kobo e-reader takes your EPUBs (all, or one collection) from Shelf as if from its own store, downloads them over Wi-Fi, and sends its place and finished books back. Set it up on Devices.

## [1.6.0] - 2026-10-11

### Added

- A new audiobook player. A book in many tracks plays as one recording, straight from one track into the next, with the time gone and left in the chapter, a bar for the whole book with a mark at each chapter, and the time left at your speed. Big play and skip buttons, skips you choose (5 to 60 seconds), speeds from 0.5× to 3× in steps of 0.05, a sleep timer of 5 to 90 minutes or the end of the chapter with 5 more minutes, volume, a list of chapters to jump to, and keyboard keys.
- The mini player: leave the player and the book plays on in a bar at the foot of every page, and waits there, paused, after a reload. **Play while you browse** on a book's page, and a play button on books being read, start a book there without leaving the page.
- Shelf reads how long each track runs from the file itself (MP3, M4B and M4A, Ogg and Opus, FLAC, WAV). `GET /books/{id}/audio/plan`, `PUT /books/{id}/audio/place`, and `PUT /books/audio/speed` are the player's own calls, open to scripts too.
- Time spent listening, by day: **Stats** shows today, this week, and this year, the last two weeks, and the books most listened to. A day of listening counts toward the reading streak.
- Playing an audiobook to its end marks the book finished.
- **Reading now** in the library shows how long is left of an audiobook.
- Listening offline: **Keep for listening offline** saves an audiobook's tracks in the browser. It then plays from the device, and with no connection the offline page has a player for it; the place and the time listened go back to the shelf when it answers.
- A book's narrator: a field of its own, shown on the book's page and in the player, and found by search. The Audiobookshelf import fills it, and narrators an earlier import put in the notes move across.

### Changed

- The player keeps your place on the shelf as you listen, every 15 seconds and whenever you pause, seek, or leave, straight from the browser.

## [1.5.0] - 2026-10-10

### Added

- API tokens, for scripts and home dashboards such as Home Assistant: make one on Account and send it as `Authorization: Bearer shelf_…`. A token opens the books API, never the account or the admin pages, and can be read-only. The API docs have a Home Assistant sensor to start from.
- Series alerts: turn them on under Series, and once a day Shelf asks Open Library about the series you are reading. A newer book that is not on your shelf shows under New in your series, with a count beside Series in the sidebar, and goes to the want list in one click.
- Share a reading list: a saved search can be shared by link with anyone, signed in or not. The page shows the books' covers, authors, years, series, and your stars, never notes, reviews, or loans; Stop sharing ends the link.
- Switch between the audiobook and the e-book at the same point: **Continue in the e-book** on the player, and **Continue in the audiobook** in the reader, for a book with both an EPUB and a recording. Chapters named alike are lined up; elsewhere it goes by how far through each you are.

### Changed

- The README is shorter, with fresh screenshots (the player and a shared list among them), a section on why Shelf rather than Calibre-Web or Audiobookshelf, and links into the documentation for the rest. `npm run screenshots` in `tests/e2e` remakes the pictures.
- A page's heading no longer shows a focus ring when the page opens.

### Fixed

- The service worker no longer answers for a chapter's frame inside the reader, so a frame cut short as the page changes is not taken for the shelf being offline.

## [1.4.1] - 2026-10-10

### Added

- Templates for Unraid, TrueNAS SCALE, and CasaOS in `deploy/`, and a section in the install guide for each.

### Changed

- The Docker image starts as root just long enough to make `/data` belong to the user Shelf runs as, then switches to it: `PUID` and `PGID`, 1654 unless set. A folder from the host now works as `/data` without a `chown`. Starting with `--user` works as before.

### Fixed

- A page whose live connection cannot start, because an extension or a privacy setting blocks it, now says so in a banner with the usual causes, instead of looking fine while its buttons do nothing. Shelf starts Blazor itself to watch for this, and marks the connection's state on the page as `data-live`.
- A browser that refuses an audiobook's lock-screen details no longer leaves the player's buttons dead.
- The library's Reading now and Read next show at most eight books, the latest started first, with a link to the rest. With hundreds marked as reading (after a Goodreads import, say), the page had grown to many times its size.
- After a restore without the `keys` folder, a reader with two-step sign-in can get in with a recovery code, and turn two-step off with another, as the docs say. Before, the code was refused whenever the authenticator secret could not be unsealed.

## [1.4.0] - 2026-10-10

### Added

- Scan the barcode: on the book form, a phone's camera reads the ISBN from the back of a book and looks it up; over plain http it takes a photo instead. The barcode is read in the browser, by its own reader or by ZXing served from the shelf.
- Lock-screen controls for audiobooks: a phone's lock screen, notifications, headphones, and car show the chapter, the book, its author, and cover, with play and pause, skips of 15 and 30 seconds, scrubbing, and previous and next chapter.
- An import folder: set `Import:Folder`, and books dropped into a reader's folder inside it are added on their own, once they stop changing. A folder of tracks is one audiobook; added files move into `.imported`, and ones Shelf cannot take into `.not-added`.
- Single sign-on through an OpenID Connect provider such as Authentik, Authelia, Keycloak, or Pocket ID (`Oidc:*`). Readers already here connect from Account; new people get an account while sign-ups are open. Readers are matched by the provider's subject, never by email.
- Bring books from a Calibre library on the server: each book's best file, cover, series, tags, publisher, rating, ISBN, language, year, and description, read straight from `metadata.db` without changing it. Books with no usable file come in as catalog entries.
- Send to Kindle: with a mail server set up, a reader saves their Kindle's address on Account, and an EPUB or PDF of their own goes to it by email from the book's page.
- Off-site backups: each night's backup can be copied to a second folder (`Backup:CopyTo`) and to S3-compatible storage such as Amazon S3, Backblaze B2, Wasabi, Cloudflare R2, or MinIO (`Backup:S3:*`), keeping as many as the backups folder. Readers shows how the last copy went.
- Highlights from KOReader: upload the JSON file from KOReader's Export highlights on Devices, and each highlight lands in its chapter (or page) with its note. Books are matched by title or file name, and highlights already here are skipped.
- Saved searches: a filtered and sorted library view can be saved under a name, and opens again from the sidebar. Sort and the More filters checkboxes are now part of the library's address, so any view can be bookmarked too.
- Google Books as a second source for Look up and Fill empty details: asked when Open Library has no match or leaves the pages, publisher, year, or cover blank (`Lookup:GoogleBooks`, with an optional `Lookup:GoogleBooksKey`).
- Docker images for arm64 as well as amd64: a Raspberry Pi 4 or 5, most NAS boxes, and Apple silicon. CI starts the arm64 image too.

### Changed

- PDF.js 6, JSZip 3.10.2, and the Inter and Lora fonts 5.3.0 are served from the shelf; Fido2, MailKit, QRCoder, and OpenTelemetry are updated.

### Fixed

- Each search area on a page (the sidebar's, the library filters', Free books', and Search inside books') has a name of its own for screen readers.

## [1.3.1] - 2026-10-10

### Added

- Shelf is free software under the GNU AGPL-3.0. A Source code link sits beside the version at the foot of every page; `SourceUrl` points it at a changed copy's own source.

### Fixed

- The Docker image could not make any page interactive: it left out `_framework/blazor.web.js`, so buttons such as Connect on the Audiobookshelf import, About this book, Add to shelf, and others handled on the page did nothing. Every image before this one is affected; running from source was not. The image's build now restores with the whole source in place, and CI and releases start the image and check that a page's scripts load before anything is published. After pulling the new image, reload Shelf once in the browser.
- A new release's styles and translations reached a browser that had Shelf open before only once the offline helper itself changed. They now come from the shelf whenever it answers, with the copies on the device kept for offline reading.
- Connecting to an Audiobookshelf address that never answers says it timed out, instead of that it did not answer as Audiobookshelf.
- About this book on Free books could leave the whole page unresponsive when Project Gutenberg or LibriVox was slow: a timeout from the retry layer every outgoing request goes through was not caught, and it ended the page's connection. The same gap is closed for Open Library look-ups, shelf-to-shelf sync, free-book downloads, and the Audiobookshelf import.
- Gutenberg and LibriVox requests may take up to 45 seconds an attempt, and Audiobookshelf up to two minutes, instead of the standard ten, so a busy catalog or a large library still answers.
- About this book works before the page's live connection is up, or without one: it opens the details from the server instead.

## [1.3.0] - 2026-10-10

### Added

- Shelf speaks Spanish, French, and German as well as English. Each reader gets their browser's language or picks one on Account, separately from the region dates follow; emails go in the reader's language. The translations await review by native speakers.
- Free books can be browsed: Gutenberg's most read books and its categories, LibriVox's newest recordings and its genres, a page at a time. About this book shows a book's summary or description, subjects, and more.
- Bring books from Audiobookshelf: audio tracks in order, e-books, covers, series, narrators, genres, and where you stopped, from an API key or a user name and password.
- Cover art: upload a picture of your own for any book, and an audiobook's embedded art becomes its cover when it has none. Kept covers travel in backups.
- Highlights while offline: a kept book brings its highlights, and passages highlighted or removed offline are sent to the shelf when it answers again. Offline PDF pages gain a text layer for selecting words.
- Passkeys: sign in with a phone's or laptop's fingerprint, face, or PIN, or a password manager, instead of the password. A passkey counts as both steps of two-step sign-in. An admin's password reset also removes a reader's passkeys.

### Changed

- Adding books from files moved to `POST /books/import/files`, so it no longer shares an address with restoring a JSON backup at `POST /books/import`.

### Fixed

- A book with no cover keeps whole words on its small spine, and a very long title wraps instead of running out of its card.

## [1.2.0] - 2026-10-10

### Added

- Free books: search Project Gutenberg and LibriVox, and add a public-domain e-book or audiobook to the shelf with its file. Downloads run in the background; `FreeBooks:Enabled` turns it off.
- Add books from files, as many at once as you like: each EPUB, PDF, comic, Kindle file, zip of tracks, or album of audio files becomes a book named from what the file says, and a matching book already on the shelf takes the file instead. `POST /books/import`.
- KOReader progress sync: Shelf answers KOReader's sync plugin at `/kosync`, so a place reached on an e-reader comes back, and the other way round. Devices makes the KOReader password.
- Comics (CBZ) read page by page. Kindle files (MOBI, AZW3) are turned into EPUBs when Calibre is installed; `--build-arg CALIBRE=true` builds it into the Docker image.
- Read aloud: the reader speaks an EPUB or PDF with the browser's voices, going on into the next chapter or page.
- Quotes, highlights, notes, and reviews download as Markdown, a book at a time or all together, for Obsidian or any notes app.
- An activity log of account changes, failed sign-ins, restores, and backups, for the admin on Readers and for each reader on Account.
- Uploading a file that is already on another of your books asks first: keep both, or don't add it. `keepBoth=true` skips the question.
- Adding a book that another reader has on an open shelf offers to ask to borrow it instead, and a book's Files panel does the same when an open copy has a file yours lacks.
- An EPUB's own cover, or a comic's first page, is shown for a book with no cover address.

### Changed

- After updating, Shelf looks inside existing e-books once in the background, for their covers and the names KOReader gives them.

## [1.1.0] - 2026-10-10

### Added

- Two-step sign-in with an authenticator app, with ten single-use recovery codes, and a list of signed-in devices that can each be signed out.
- Offline reading: keep an e-book on a device, read it when the shelf cannot be reached, and have the place sent back later. `GET` and `PUT /books/{id}/place` read and move a reader's place.
- Audiobook chapters, read from the marks inside an `.m4b` or `.m4a`, and bookmarks with a note.
- Nightly database backups, keeping the last seven, listed and downloadable from Readers.
- `/health` and `/alive` in every environment, and a health check in the Docker image.
- Dates written the way each reader's region writes them, from the browser or a choice on the account.
- `skip` and `take` on `/books`, with the total in `X-Total-Count`.
- Browser tests in CI, covering every interactive feature and an accessibility audit in both themes.
- Dependabot, and a pinned, checked copy of PDF.js, the fonts, and JSZip.

### Changed

- Search inside books uses a full-text index: places come back best match first, accents and case do not matter, and the last word can be partial.
- The library loads only what it shows and draws sixty books at a time.
- Email goes out through MailKit; `Email:Security` chooses the connection.
- An admin's new password also turns off two-step sign-in. Everyone signs in once more after updating, since sign-ins are now sessions.

### Fixed

- The bulk tag box and the account's email box now count what is typed straight away, instead of once the field loses focus.

## [1.0.0] - 2026-10-09

The first versioned release.

### The library

- A catalog with series, translators, original titles, inscriptions, places, condition, how a copy arrived, who recommended it, ratings, reviews, notes, and tags.
- Covers or a compact list, with search, status, sort, and filters; the choice of view is kept for each reader.
- Reading now, read next, and recently finished lead the library.
- A book's page leads with its cover and one next step, then the reading log, lending, quotes, highlights, files, and details.
- Change many books at once: status, tags, loved, or delete.
- Catalog details filled from Open Library by ISBN, or by title and author.
- Import a library from a Goodreads or StoryGraph CSV export.

### Reading

- Read EPUBs and PDFs in the app, back where you stopped, at your own text size; EPUBs also take line spacing and width.
- Highlight a passage and keep a note with it, in an EPUB or a PDF.
- Scanned PDFs are read with OCR (Tesseract and Poppler), so their words can be selected and highlighted.
- Search the words inside your books.
- Play audiobooks, from one file or a zip of tracks, with a speed kept for each reader and a sleep timer.
- A reading log, a yearly goal, a monthly chart, a calendar of reading days, and a streak.
- Quotes and highlights gathered on one page.

### Readers and lending

- Accounts, each with a shelf of their own; the first reader keeps the books from before there were accounts and is the admin.
- Lend to another reader, who can read and listen with a place and notes of their own; ask to borrow from a reader's open shelf.
- Reminders in the app, and by email when a mail server is set, for overdue loans, books due soon, and waiting asks.
- Password resets by email, by an admin, or from the command line.

### Devices and backups

- Trade e-books and audiobooks with another shelf using a device key, keeping the furthest place and passage notes.
- An OPDS catalog for e-reader apps such as KOReader.
- Full backups as a zip with every file, JSON backups, and an admin's snapshot of the whole server.

### Running it

- A Docker image with OCR included, keeping everything under `/data`.
- Sign-in keys kept beside the database, HTTPS redirects, and support for a reverse proxy.
- A design system with light and dark themes, self-hosted fonts, an app icon, and a web app manifest.
- CI on every push, and releases published from version tags.

[Unreleased]: https://github.com/doodersrage/shelf/compare/v2.17.1...HEAD
[2.17.1]: https://github.com/doodersrage/shelf/compare/v2.17.0...v2.17.1
[2.17.0]: https://github.com/doodersrage/shelf/compare/v2.16.2...v2.17.0
[2.16.2]: https://github.com/doodersrage/shelf/compare/v2.16.1...v2.16.2
[2.16.1]: https://github.com/doodersrage/shelf/compare/v2.16.0...v2.16.1
[2.16.0]: https://github.com/doodersrage/shelf/compare/v2.15.0...v2.16.0
[2.15.0]: https://github.com/doodersrage/shelf/compare/v2.14.2...v2.15.0
[2.14.2]: https://github.com/doodersrage/shelf/compare/v2.14.1...v2.14.2
[2.14.1]: https://github.com/doodersrage/shelf/compare/v2.14.0...v2.14.1
[2.14.0]: https://github.com/doodersrage/shelf/compare/v2.13.0...v2.14.0
[2.13.0]: https://github.com/doodersrage/shelf/compare/v2.12.3...v2.13.0
[2.12.3]: https://github.com/doodersrage/shelf/compare/v2.12.2...v2.12.3
[2.12.2]: https://github.com/doodersrage/shelf/compare/v2.12.1...v2.12.2
[2.12.1]: https://github.com/doodersrage/shelf/compare/v2.12.0...v2.12.1
[2.12.0]: https://github.com/doodersrage/shelf/compare/v2.11.1...v2.12.0
[2.11.1]: https://github.com/doodersrage/shelf/compare/v2.11.0...v2.11.1
[2.11.0]: https://github.com/doodersrage/shelf/compare/v2.10.0...v2.11.0
[2.10.0]: https://github.com/doodersrage/shelf/compare/v2.9.0...v2.10.0
[2.9.0]: https://github.com/doodersrage/shelf/compare/v2.8.0...v2.9.0
[2.8.0]: https://github.com/doodersrage/shelf/compare/v2.7.3...v2.8.0
[2.7.3]: https://github.com/doodersrage/shelf/compare/v2.7.2...v2.7.3
[2.7.2]: https://github.com/doodersrage/shelf/compare/v2.7.1...v2.7.2
[2.7.1]: https://github.com/doodersrage/shelf/compare/v2.7.0...v2.7.1
[2.7.0]: https://github.com/doodersrage/shelf/compare/v2.6.0...v2.7.0
[2.6.0]: https://github.com/doodersrage/shelf/compare/v2.5.1...v2.6.0
[2.5.1]: https://github.com/doodersrage/shelf/compare/v2.5.0...v2.5.1
[2.5.0]: https://github.com/doodersrage/shelf/compare/v2.4.1...v2.5.0
[2.4.1]: https://github.com/doodersrage/shelf/compare/v2.4.0...v2.4.1
[2.4.0]: https://github.com/doodersrage/shelf/compare/v2.3.0...v2.4.0
[2.3.0]: https://github.com/doodersrage/shelf/compare/v2.2.0...v2.3.0
[2.2.0]: https://github.com/doodersrage/shelf/compare/v2.1.1...v2.2.0
[2.1.1]: https://github.com/doodersrage/shelf/compare/v2.1.0...v2.1.1
[2.1.0]: https://github.com/doodersrage/shelf/compare/v2.0.1...v2.1.0
[2.0.1]: https://github.com/doodersrage/shelf/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/doodersrage/shelf/compare/v1.9.0...v2.0.0
[1.9.0]: https://github.com/doodersrage/shelf/compare/v1.8.0...v1.9.0
[1.8.0]: https://github.com/doodersrage/shelf/compare/v1.7.0...v1.8.0
[1.7.0]: https://github.com/doodersrage/shelf/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/doodersrage/shelf/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/doodersrage/shelf/compare/v1.4.1...v1.5.0
[1.4.1]: https://github.com/doodersrage/shelf/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/doodersrage/shelf/compare/v1.3.1...v1.4.0
[1.3.1]: https://github.com/doodersrage/shelf/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/doodersrage/shelf/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/doodersrage/shelf/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/doodersrage/shelf/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/doodersrage/shelf/releases/tag/v1.0.0
