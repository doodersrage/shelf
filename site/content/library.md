---
title: Your library
nav: Your library
description: The catalog, statuses, the library's views and filters, changing many books at once, covers, and the reading log.
---

# Your library

**Home** is the front page: the book you were last in leads, with a button straight back into it, then your shelf at a glance (reading, want to read, finished this year against your goal, on loan), the other books you're reading, what to read next, what you added lately, and what you finished. **Library** holds every book on your shelf, as covers or a compact list, with search, chips for each status and for books on loan, e-books, and audiobooks, and an order that's kept for next time. On a cover, the status is a menu: change it there without opening the book. **Reading** gathers the books you have open, each a tap from your place, with your reading log of the last weeks and what you noted on the way.

When you mark a book finished, its page offers a rating, a quote to keep, and the next book from your read-next list. Take any of them, or dismiss it.

## Statuses

Every book is one of four, each shown with a different small shape as well as its word, so status never depends on color:

| Status | Means |
| --- | --- |
| **Want to read** | On the list. Mark one **Read next** to bring it to the front. |
| **Reading** | Underway. Moving a book here fills in a blank start date. |
| **Finished** | Done. Fills in a blank finish date too, and counts toward the year's goal. |
| **Abandoned** | Put down. **Pick it up again** moves it back to Reading. |

Can't choose what to read next? `/books/pick` picks one book from the want list.

## The catalog

A book's details hold as much or as little as you like:

- **Title, subtitle, author, and year**, with the original title of a translation and its translator, and the narrator of an audiobook (search finds narrators too).
- **Series and number**, so the next volume is offered from the book's page.
- **ISBN, publisher, language, page count, and format** (hardcover, paperback, e-book, or audiobook).
- **About your copy:** where it sits, its condition from fine to poor, how it arrived (bought, a gift, or found), the date, any inscription written in the front, and who recommended it.
- **Your own words:** a rating, a review, notes, and tags.

Give an ISBN, or a title and author, and **Look up** fills the empty fields from [Open Library](https://openlibrary.org), cover included, whether you are adding a book or editing one. When Open Library has no match, or leaves the pages, publisher, year, or cover blank, [Google Books](https://books.google.com) fills those in; it knows many newer and non-English books Open Library does not. Open Library's answer always comes first. In the library, **Fill empty details** looks up every book with gaps, in the background, a book at a time with a pause between them so the catalogs are asked politely (`Details:PauseMilliseconds`, 1200 by default): leave the page and it carries on, and **Activity** shows how far it has got. Neither touches anything you wrote.

Tags are kept in lowercase, and an ISBN can be typed with or without hyphens.

## Views and filters

The library shows covers or a list, and remembers your choice. Search, status, and sort are always in view; more filters (author, series, tag, place, format, loved, on loan, recommended by) sit under **More filters**, and **Clear filters** appears whenever something narrows the list.

Pages gathered from the catalog give other ways in: **Authors**, **Series** in reading order, **Places** where books sit, **Condition**, **Recommenders**, and **Years** of finished books.

### Your year in books

Each year on **Years**, and the current one on **Insights**, links to a review of it: how many books you finished, their pages, the hours you spent reading and listening, how many authors (and how many were new to you), your goal, a wall of the covers in the order you finished them, a month-by-month chart with your busiest month, the first, last, longest, and shortest books, your formats and most read tags, the books you loved or gave five stars, and the quotes and highlights you kept, with one to remember the year by.

### Saved searches

A view you come back to, such as *loved, want to read, newest first*, can be kept: set the filters and order, choose **Save this search**, and give it a name. It joins **Browse** in the sidebar, and opens with the same filters and order every time. **Forget this search**, on the open search, removes it. Saved searches are yours alone; every filtered view is also a plain address you can bookmark.

### Collections

A collection is a list you make by hand, in the order you choose: a book club's year, summer reading, books to lend a friend. Unlike a saved search, it holds exactly the books you put in it. Make one on **Collections** in the sidebar; then, on a book's page, choose it under **Collections** and **Add**. On the collection's page, **↑** and **↓** change the order and **×** takes a book out; **Rename or describe** gives it a name and a few words about it. Deleting a collection leaves its books on your shelf, and deleting a book takes it out of every collection.

### Sharing a reading list

A saved search or a collection can be shared with anyone, signed in or not: open it and choose **Share by link**, then copy the address shown. The page behind it lists the books as the search finds them at that moment, with their covers, authors, years, series, and your stars; never your notes, reviews, quotes, loans, places, or files. Search engines are asked not to index it. **Stop sharing** ends the link at once, as does forgetting the search; sharing again makes a new link.

### New books in your series

On **Series**, tick **Tell me when a new book comes out in a series I am reading**. Once a day Shelf asks Open Library about each series you are reading or have finished a book of, sending only the series and author names. A book in the series that is newer than yours and not on your shelf appears under **New in your series**, and the sidebar's **Series** shows how many are waiting. **Want to read** adds it to the want list, numbered after the last one you have; **Dismiss** lets it go. Either way, the same book is never offered twice. **Look now** asks straight away. With reminders by email turned on under **Account**, new books found by the daily look come by email too, each book once.

Box sets and collections are left out. Open Library knows the series of many books but not all, so a book it has not filed under the series is missed; the series name on your books should match Open Library's (with or without a leading *The*).

## What to read next

**What to read next**, under Discover & reflect, suggests books from what's already here, without sending anything anywhere:

- **Next in your series:** for each series you're reading or have finished a book of, the next one on your want-to-read list. Later books wait their turn.
- **New in your series:** books the daily look found (see above), to add.
- **From your want-to-read list:** your queue first, then books by authors whose books you loved or gave four or five stars, then books whose tags (subjects) you share with those, each with why.
- **On other readers' open shelves:** books you don't have, by authors and on subjects you love, to ask to borrow.

The more you rate, love, and tag, the better they get.

## A public page

**Share a public page**, under **Account**, makes a page anyone with its address can see: the books you're reading, the ones you finished lately with their stars, and your count for the year. Notes, reviews, loans, and everything else stay private. It comes with:

- a **widget** to put on a website or blog, as an `<iframe>` (the code is on the Account page), showing what you're reading now;
- a **feed** of books you finish, for a feed reader;
- the same as JSON, at `/public/<address>/shelf.json`.

Turning it off ends the address; turning it on again makes a new one.

## Tidy up

Under **Your copies**, **Tidy up** puts a shelf brought in from files and other servers in order. Nothing changes until you choose it:

- **Covers and details**: how many books have no cover, and how many lack a year, pages, publisher, or language. **Find covers** looks up a cover for each book with none (and changes nothing else); **Fill in all missing details** fills every gap. Both run in the background, a book at a time.
- **Authors written more than one way**: "Le Guin, Ursula K.", "Ursula K Le Guin", and "URSULA K. LE GUIN" are the same person. Each group lists its spellings with how many books use each; choose the one to keep, or use the most common spelling for every group at once.
- **Series inside titles**: titles like *The Tombs of Atuan (Earthsea Cycle, #2)* or *Discworld 08 - Guards! Guards!* name their series. Taking it out leaves the title alone and sets the series and number, so the book shows with the rest of its series.

## Activity

**Activity**, under **Settings**, lists everything Shelf is doing for you in the background: imports, details being filled in, books brought by keeping in step, and the slower work on single books (read-along transcripts, reading scanned PDFs, making an M4B). Each running job shows its progress and can be stopped; finished ones stay listed for a while, folded to a line with what they did.

## Duplicates

Under **Your copies**, **Duplicates** lists books that look like the same book: the same ISBN, or the same title and author (case and punctuation aside). Choose **Keep this one** on the entry to keep, and the others merge into it:

- its empty details are filled from them, the further status wins (finished over reading over wanting to read), with the earliest start and the latest finish;
- tags, notes, reviews, quotes, highlights, the reading log, time spent reading and listening, and collections come across;
- an e-book, audiobook, or cover picture it has none of comes across with everything that belongs to it. Where both have a file of one kind, the one kept stays, and the other's goes, with the bookmarks in its recording.

A book out on loan can't be merged away until it's back.

## Changing many books at once

In the list view, tick books to change their status, add or remove a tag, mark them loved, or delete them, up to a thousand at a time.

## Covers

A book's cover is, in order of preference:

1. **A picture kept on the shelf.** Upload one under **Files** on the book's page (JPEG, PNG, GIF, or WebP, up to 10 MB). Art embedded in an audiobook, or a cover from an import, is kept this way when the book has no cover yet.
2. **A cover address** in the details, which Open Library fills in.
3. **The cover inside the e-book**, or a comic's first page.

With none of these, the book shows a plain green spine with its title.

## The reading log

Log a session from a book's page: the date, the pages you read from and to, and a note. Sessions feed **Insights**: the year's goals and how you are doing, books finished each month, a year of the days you read and listened, the authors, kinds of book, and series you return to, milestones (your first finished book, the tenth, ten thousand pages, the longest book, the year's goal reached), counts, a calendar of reading days, and recent sessions.

The year can have up to three goals, set under **Set goals** in the year's panel on **Insights**: books to finish, pages to read (counting the pages of the books you finish), and hours to spend reading and listening (from the reader's and the player's own count of your time). Leave one at 0 to go without it; each one set shows how far along you are, on **Insights** and in the year's review.

## Quotes

Keep a quote on a book's page, with the page it is on. **Quotes & highlights** gathers every quote and every passage highlighted in the reader, with the books they came from, and searches their words, your notes, the titles, and the authors. Download them all [as Markdown](reading.md#exporting-highlights-and-notes).
