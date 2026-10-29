---
title: API
description: Shelf's HTTP API: signing in from a script, and every endpoint for books, files, reading, lending, importing, accounts, and devices.
---

# API

Everything Shelf's pages do goes through the same HTTP API, which answers in JSON. Every call works on the signed-in reader's own shelf, signed in by an [API token](#api-tokens) or the browser's cookie. In Development, the OpenAPI document is at `/openapi/v1.json`.

## API tokens

A script or a home dashboard signs in with an API token. Make one on **Account**, under **API tokens**: name it after what will use it, and tick **Let it change things** if it needs more than reading. The token is shown once. Send it with every request:

```bash
curl -s -H "Authorization: Bearer $SHELF_TOKEN" https://shelf.example.org/books?status=Reading
```

A token opens the calls on this page under `/books`, `/settings`, and `/readers`. Under **What it opens**, a token can be kept to some of them: the library (books, reading, lending, collections, goals, and settings), files (e-books, recordings, covers, pictures of a copy, backups, and the file check), the journal, book clubs, and the event feed. Leave every box clear for all of them; a call outside a token's parts is refused with 403, and `/version` is open to every token. Send `"areas": ["journal", "events"]` to make one through the API. It never opens the account (passwords, keys, other tokens, the sandbox) or the admin pages, so a token that leaks cannot take the account over; remove it on **Account** and it stops working at once. A read-only token is refused anything but `GET`, with 403. Account shows when each token was last used.

### Trying a connector on a sandbox

On **Account**, **Sandbox**, **Make a sandbox token** gives a token for a shelf of a dozen sample books instead of yours: a connector, a script, or a webhook can be tried on it, writing included, before it is given the real one. Everything under `/books` works the same there, webhooks and the event feed too, but it is a reader of its own that no one signs in as: no other reader sees it, it lends to and asks no one, and it is in no club. **Put the samples back as they were** resets it and its tokens go on working; **Remove the sandbox** ends it and them.

### Home Assistant

A [REST sensor](https://www.home-assistant.io/integrations/sensor.rest/) shows what you are reading and how the year's goal is going. Keep the token in `secrets.yaml` as `shelf_token: "Bearer shelf_…"`, then:

```yaml
rest:
  - resource: https://shelf.example.org/books/stats
    headers:
      Authorization: !secret shelf_token
    scan_interval: 3600
    sensor:
      - name: Books reading
        value_template: "{{ value_json.reading }}"
      - name: Books finished this year
        value_template: "{{ value_json.finishedThisYear }}"
        json_attributes:
          - yearlyGoal
          - pagesRead
```

## Signing in from a script without a token

A script can also sign in with the cookie the browser uses, posting the sign-in form with the anti-forgery token the page carries. This doesn't work with [Turnstile](configuration.md#cloudflare-turnstile) on; use an API token then.

```bash
site=https://shelf.example.org
jar=$(mktemp)
token=$(curl -s -c "$jar" "$site/signin" \
  | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | sed 's/.*value="//; s/"$//')
curl -s -b "$jar" -c "$jar" -o /dev/null "$site/account/signin" \
  --data-urlencode "email=tenar@example.org" --data-urlencode "password=$SHELF_PASSWORD" \
  --data-urlencode "__RequestVerificationToken=$token"

curl -s -b "$jar" "$site/books?status=Reading"
```

A reader with two-step sign-in is asked for a code next, at `/account/signin/code`. The [device key](devices.md#the-device-key) is for e-reader catalogs and shelf-to-shelf sync only, not for this API.

> [!TIP]
> Requests that change something take JSON, with `Content-Type: application/json`, unless they upload a file, which goes as a multipart form with the file in a field named `file`.

## Books

| Call | What it does |
| --- | --- |
| `GET /books` | The library. Filter with `q`, `status`, `tag`, `author`, `series`, `place`, `recommendedBy`, `loanedTo`, `loved`, `loaned`, and `format`, or one of your own fields with `field` (its id) and `value`; sort with `sort` (`title`, `author`, `series`, `year`, `rating`, `added`). `skip` and `take` (up to 500) page through it, with the total in the `X-Total-Count` header. |
| `GET /books/{id}` | One book, with its tags, quotes, and reading sessions. |
| `POST /books` | Add a book: `{"title": "…", "author": "…", "status": "Want"}` and any other fields. |
| `PUT /books/{id}` | Change a book. |
| `GET /books/fields` | Your own fields. `POST {"name": "Bought from", "kind": "Text"}` adds one (`Text`, `Number`, `Date`, or `YesNo`); `PUT /books/fields/{id}` with `{"name": "…"}` renames it; `DELETE` removes it and its values. |
| `GET /books/{id}/fields` | A book's values in your fields. `PUT` with `{"values": {"3": "Oxfam", "4": "2026-10-29"}}` sets them by field id; a blank value clears one. A number is kept as `3.5`, a date as `2026-10-29`, yes or no as `yes` and `no`. |
| `DELETE /books/{id}` | Delete a book with its files. |
| `POST /books/bulk` | Change up to 1,000 books: `{"ids": [1, 2], "status": "Finished"}`, or `addTag`, `removeTag`, `loved`, `delete`. |
| `GET /books/lookup?isbn=` | Details and a cover from Open Library. `?title=&author=` looks a book up without an ISBN. |
| `POST /books/{id}/enrich` | Fill a book's empty fields from Open Library. |
| `GET /books/pick` | One book from the want list. `?status=` picks from another. |
| `GET /books/jump?q=` | Quick jump: the pages of Shelf whose name or purpose fits `q`, then your books, authors, series, places, and collections whose names hold it, each with its `kind`, `title`, `detail`, and `href`. With no `q`, the main pages. |
| `GET /books/authors`, `/series`, `/places`, `/copies`, `/recommenders`, `/years` | The library gathered by author, series in reading order, place, condition, recommender, and year finished. |
| `GET /books/collections` | Your collections, with how many books each holds. `POST {"name": "…", "description": "…"}` makes one; `GET`, `PUT`, and `DELETE /books/collections/{id}` read, rename, and delete one. |
| `POST /books/collections/{id}/books` | `{"bookId": 12}` adds a book at the end; `DELETE /books/collections/{id}/books/{bookId}` takes it out; `PUT /books/collections/{id}/order` with `{"bookIds": [3, 1, 2]}` puts them in that order. `PUT /books/collections/{id}/books/{bookId}` with `{"note": "…"}` keeps a note on a book in the list. |
| `PUT /books/collections/{id}/plan` | `{"readBy": "2027-01-01"}` makes the collection a plan, or `null` a list again. `GET` says whether it fits your pace: pages left, pages a day, days needed and left, a day that would fit, and how many of the longest books to leave out. No content when there's no date or nothing left. |
| `POST /books/{id}/again` | Reads a finished book again: its read is put by and it starts over. `GET /books/{id}/reads` lists every read with its sessions, highlights, and quotes counted; `PUT /books/{id}/reads/{readId}` with `{"rating": 4, "thoughts": "…"}` changes an earlier one, and `DELETE` forgets it. |
| `GET /books/{id}/links` | The book's links to your other books, both ways. `POST {"toBookId": 7, "kind": "Edition", "note": "…"}` adds one, with `kind` one of `Edition`, `Inspired` (this book was inspired by the other), `Answers` (this book answers the other), `Contrasts`, and `Related`; `DELETE /books/{id}/links/{linkId}` takes one away. |
| `GET /books/map` | Your linked books and the links between them; `?book=` the books within two links of one, with how far each is. |
| `GET /books/{id}/photos` | Pictures of your copy. `POST` a form with `file` and `caption` adds one; `GET` and `DELETE /books/{id}/photos/{photoId}` fetch and remove one. |
| `GET /books/journal` | A year of your journal, latest first: entries, books started and finished, sessions, highlights, quotes, and thoughts on earlier reads. `?year=` picks the year; `?q=` searches every year instead. `GET /books/journal/years` lists the years with anything in them; `GET /books/journal/{year}.md` is the year as Markdown. |
| `GET /books/goals` | The year's goals of your own and how each stands: the books that count, and what would see it done. `?year=` picks another year. `POST {"kind": "Tag", "name": "history", "target": 3}` adds one, with `kind` one of `Tag`, `Author`, `Series`, and `NewAuthors` (no name); `DELETE /books/goals/{id}` takes one away. |
| `POST /books/journal` | `{"date": "2026-10-13", "text": "…", "bookId": 12}` writes an entry (date and book optional); `PUT` and `DELETE /books/journal/{id}` change and remove one. |
| `GET /books/series/alerts` | New books in your series that are not on your shelf. `POST /books/series/alerts/{id}/want` adds one to the want list; `DELETE /books/series/alerts/{id}` dismisses it; `POST /books/series/alerts/check` looks now. |
| `GET /books/years/{year}/review` | A year in books: finished books and pages, time reading and listening, authors new that year, books by month, formats, tags, the longest and shortest, favourites, and quotes and highlights kept. |
| `GET /books/storage` | Your places, each with its full path and the copies at it and within it, and the places copies name that aren't places yet. `POST` with `{"name": "Study", "kind": "Room", "parentId": null, "temporary": false, "destinationId": null}` adds one (`Room`, `Bookcase`, `Shelf`, `Box`, `Other`); `PUT` and `DELETE /books/storage/{id}` change and remove one; `POST /books/storage/move` with `{"bookIds": [4], "placeId": 2}` moves copies; `POST /books/storage/put-back` takes copies home; `POST /books/storage/adopt` makes places of those copies name. |
| `GET /books/duplicates` | Books that look like the same book, by ISBN or by title and author. `POST /books/{id}/merge` with `{"otherId": 12}` merges book 12 into book `id`. |
| `GET /books/stats` | The year's goal, books finished each month, counts, and the streak. |
| `GET /books/calendar` | The days with a reading session. `?year=&month=` picks another month. |
| `GET`, `PUT /settings` | The year's goals: `{"yearlyGoal": 24, "pagesGoal": 8000, "hoursGoal": 200}`. A goal left out of a `PUT` stays as it was. |

Statuses are `Want`, `Reading`, `Finished`, and `Abandoned`; formats are `Hardcover`, `Paperback`, `Ebook`, and `Audiobook`.

## Files and covers

| Call | What it does |
| --- | --- |
| `POST /books/{id}/ebook` | Upload an EPUB, PDF, comic, or Kindle file, up to 80 MB by default ([upload limits](configuration.md#upload-limits)). `DELETE` removes it. Answers with a redirect to the book's page; `?ebook=duplicate&held=…` means the same file is on another book. Send `keepBoth=true` to add it anyway. |
| `GET /books/{id}/ebook/file` | The e-book file. `GET /books/{id}/ebook/download` is the same file named for the book, an EPUB with Shelf's details written in. |
| `GET /books/{id}/ebook/formats` | The book's other formats. `POST` one as `file` (EPUB, PDF, CBZ, AZW3, AZW, or MOBI, kept as it is); it answers with a redirect to the book's page with `?format=saved`, or the reason it wasn't kept. `GET /books/{id}/ebook/formats/{formatId}` downloads one; `DELETE` removes it; `POST …/{formatId}/main` makes an EPUB, PDF, or comic the main e-book, keeping the main one before as another format. `POST /books/{id}/ebook/formats/convert` with `{"to": "azw3"}` has Calibre make an EPUB, AZW3, MOBI, or PDF and keeps it; a refusal says why, with `outcome` (`have`, `unavailable`, `failed`, …). |
| `GET /books/{id}/ebook/chapters/{index}` | One EPUB chapter, ready to show. |
| `GET /books/{id}/ebook/pages/{index}` | One page of a comic, as an image. |
| `GET /books/{id}/ebook/ocr/{page}` | The words read from a scanned PDF page, with where each sits. |
| `POST /books/{id}/audio` | Upload an audio file or a zip of tracks, up to 1 GB (`Uploads:MaxAudioMegabytes`). `DELETE` removes it. |
| `GET /books/{id}/audio/tracks/{index}` | One track, with range requests for seeking. |
| `GET /books/listening/sessions` | Your listening sessions, newest first: book, device, when, seconds heard, and where in the recording. `?book=` for one book; `?take=` up to 500. |
| `GET /books/{id}/audio/cast` | Addresses for each track that a cast device can fetch without signing in, good for twelve hours and only while the book is yours or lent to you. |
| `GET /books/{id}/audio/feed` | Whether the book's private address is on, and until when. `POST` (with `{"days": 7}` to have it stop by itself) makes a new one and answers `{"url": "…", "pageUrl": "…"}`, the podcast feed and a page that plays it in a browser, shown only then (the old address stops); `DELETE` stops it. Everything is under `/listen-feed/{secret}`, open to anyone with the address. |
| `GET /books/narrators` | Each narrator with how many of your books they read. `GET /books?narrator=` finds a narrator's books. |
| `GET /books/playlists` | Your playlists. `POST {"name": "…"}` makes one; `GET`, `PUT` (rename), and `DELETE /books/playlists/{id}`; `POST /books/playlists/{id}/items` with `{"bookId": 3}` or `{"episodeId": 9}` adds to the end; `DELETE /books/playlists/{id}/items/{itemId}` takes one out; `POST /books/playlists/{id}/move` with `{"itemId": 5, "step": -1}` moves one up or down. Each item gives its player `planUrl`. |
| `GET /books/{id}/audio/download` | The recording with the book's details written into its files (one file as itself, tracks as a zip), by ffmpeg without re-encoding; as stored without ffmpeg. |
| `GET /books/{id}/audio/plan` | What the player needs: the tracks with their lengths in seconds, the chapters (each in a track, from a time), where you stopped, and your speed. |
| `PUT /books/{id}/audio/place` | `{"track": 2, "seconds": 341.5}` keeps your place in the recording exactly, back or forward. Add `"listened": 15, "day": "2026-10-10"` to count seconds of listening on that day. Add `"from": {"track": 2, "seconds": 300}`, the place you last had from Shelf, and the place moves only if it is still there: moved since by another device, the answer is `409` with the place now kept, and the time listened still counts. |
| `POST /books/{id}/audio/listened` | `{"day": "2026-10-09", "seconds": 5400}` counts time listened with no connection, sent later: up to a day's worth, from the last month. |
| `POST /books/{id}/audio/finished` | The recording played to its end: marks your own book finished. Answers `{"marked": true}` when it did. |
| `POST /books/{id}/reading-time` | `{"day": "2026-10-11", "seconds": 60}` counts time spent reading the book; up to five minutes at once. `GET /books/reading-time` sums it up as `/books/listening` does. |
| `GET /books/listening` | Time spent listening: today, this week, this year, each of the last 14 days, and the five books most listened to this year, in seconds. |
| `PUT /books/audio/speed` | `{"speed": 125}` keeps your playback speed, in percent from 50 to 300. |
| `GET /books/{id}/cover` | The cover picture kept on the shelf, or the e-book's own. `POST` a picture as `file` to keep one; `DELETE` removes it. |
| `POST /books/import/files` | Make books from any number of files, as on the library's **Add from files**. Ask for `application/json` to get what happened to each file. `keepBoth=true` adds files already on another book. |

## Reading and listening

| Call | What it does |
| --- | --- |
| `GET /books/{id}/place` | Where you stopped: `ebookChapter` (an EPUB chapter, or a PDF or comic page, from 0), `audioTrack`, and `audioSeconds`. |
| `PUT /books/{id}/place` | `{"ebookChapter": 3}` moves your place forward; a place behind the one kept is ignored. |
| `POST /books/highlights/kindle` | A Kindle's `My Clippings.txt` as `file`: highlights land in EPUB chapters or as quotes. Answers with how many of each, how many were already there, and the titles not found. |
| `GET /books/{id}/highlights` | Your highlights in a book. `POST` adds one: `{"text": "…", "chapterIndex": 2, "note": "…", "prefix": "…", "suffix": "…"}`. `PUT` and `DELETE /books/{id}/highlights/{highlightId}` change or remove one. |
| `GET /books/{id}/quotes` | A book's quotes. `POST {"text": "…", "page": 12}` adds one; `DELETE /books/{id}/quotes/{quoteId}` removes it. |
| `GET /books/quotes` | Every quote and highlight. |
| `POST /books/{id}/sessions` | Log a reading session: `{"date": "2026-10-10", "fromPage": 10, "toPage": 42}`. |
| `GET /books/{id}/look-up?words=` | A word or short phrase from the book, looked up in Wiktionary and Wikipedia in the book's language. |
| `GET /books/search?q=` | Places inside your e-books where a phrase appears, best first. |
| `GET /books/{id}/notes.md` | A book's quotes, highlights, notes, and review as Markdown. `GET /books/notes.zip` has every book. |

## Lending and open shelves

| Call | What it does |
| --- | --- |
| `GET /readers` | The other readers on this shelf. |
| `POST /books/{id}/lend` | Lend a book: `{"readerId": 2, "dueOn": "2026-11-01"}`. |
| `POST /books/{id}/return` | End a loan. `{"condition": "Good", "note": "…"}`, both optional, keeps the state it came back in and a note with the loan's record. |
| `POST /books/{id}/renew` | Make a loan longer: `{"days": 14}`, or as long again as it first ran when left out. Answers with the new due date. |
| `GET /books/{id}/loans` | Every loan of a book, the latest first: to whom, when it went and came back, renewals, and the state it went out and came back in. `GET /books/loans/history` lists the latest 100 that have ended, any book. |
| `GET /books/loans/waiting` | Your books with readers waiting for them, each line in the order they asked. |
| `GET /books/loans` | Who has which of your books, and how many are overdue. |
| `GET /books/borrowed` | Books lent to you. `POST /books/borrowed/{id}/return` gives one back. |
| `GET /books/shelves` | Open shelves. `GET /books/shelves/{id}` lists one. `PUT /books/shelves/open {"open": true}` opens yours. |
| `POST /books/{id}/ask` | Ask to borrow a book from an open shelf. |
| `GET /books/asks` | Asks you made and asks waiting on you. `POST /books/asks/{id}/lend {"dueOn": null}` lends the book; `DELETE /books/asks/{id}` declines or takes it back. |
| `GET /books/reminders` | Counts of overdue loans, borrowed books due soon, and asks waiting. |

## Importing and exporting

| Call | What it does |
| --- | --- |
| `GET /books/export` | Your shelf as JSON, with quotes, sessions, highlights, earlier reads, journal entries, and the links between books. `POST /books/import` restores one, skipping books already there and journal entries already kept. |
| `GET /books/export/full` | A zip of the JSON with every e-book, audiobook, and cover picture. `POST /books/import/full` with `file` restores one. |
| `GET /books/export/folders` | Your books' files as `Author/Title (id)/` folders, as Calibre lays them out: each format, the cover, the audiobook under `audiobook/`, and a `metadata.opf`. For other programs; it isn't restored. |
| `POST /books/import/csv` | A Goodreads or StoryGraph CSV export as `file`. |

| `GET /books/import/recipes` | Your import recipes. `POST /books/import/recipes/preview` with `file` (and a `spec` or `recipeId`, or neither to guess one) answers with the file's columns, the recipe, the words in its status column, and its first rows as they would come in; `POST /books/import/recipes/import` with `file`, the same, and `saveAs` to keep the recipe brings it in. A `spec` is `{"columns": {"Title": "Book Name", "Author": "By"}, "statuses": {"wishlist": "Want"}, "tagSeparator": ";", "dateFormat": "dd.MM.yyyy", "ratingOutOfTen": true}`. `DELETE /books/import/recipes/{id}` forgets one. |

`GET /books/export/full` also carries `README.md`, `books.csv`, `collections.json`, `loans.json`, and `borrowed-highlights.json`; the README says what each holds.

## Trash, history, and undo

| Call | What it does |
| --- | --- |
| `GET /books/trash` | What waits in the trash: kind (`Book`, `Quote`, `Session`, `Highlight`), title, when it was deleted, and the days it has left. `POST /books/trash/{id}/restore` brings one back; `DELETE /books/trash/{id}` lets one go for good; `DELETE /books/trash` empties it. |
| `GET /books/{id}/history` | Every change to a book's details, newest first, with the value before and after. `POST /books/{id}/history/{changeId}/undo` puts one back. |
| `GET /books/changes` | Changes to many books at once, newest first: kind (`Edit`, `Delete`, `Import`, `Scan`, `Details`, `Undo`), how many books, and whether it can still be undone. `POST /books/changes/{id}/undo` undoes one. |
| `GET /books/backups` | The nightly backups and whether each was proved to restore. `GET /books/backups/{name}/books?q=` lists your books in one that aren't on your shelf now; `POST /books/backups/{name}/books/{id}/restore` brings one back. |

## Your reading

| Call | What it does |
| --- | --- |
| `GET /books/dna` | Your reading DNA: subjects and authors read most, with their share; the main subjects of recent years; subjects rising and fading; subjects and authors owned but hardly read; and books long waiting that fit, each with its reasons. |
| `GET /books/agenda` | Your calendar from two weeks back for a year, or `?from=&until=`: each item's kind (`Lent`, `Borrowed`, `Meeting`, `Plan`, `Goal`), day, moment for a meeting, and words. |
| `GET /books/research` | Your research questions. `POST {"question": "…"}` starts one; `GET`, `PUT`, and `DELETE /books/research/{id}` read, change, and remove one, `?style=Chicago`, `Apa`, or `Mla` for its bibliography. `POST /books/research/{id}/notes` with `{"bookId": 4, "place": "p. 12", "quote": "…", "text": "…", "link": "…"}` adds a note; `POST /books/research/{id}/quotes/{quoteId}` brings a kept quote; `GET /books/research/{id}/packet.md` is the packet. |
| `GET /books/words` | Words kept, `?language=`, `?bookId=`, or `?due=true` for those to practise. `POST {"word": "…", "meaning": "…", "context": "…", "bookId": 4}` keeps one; `POST /books/words/{id}/review` with `{"knew": true}` records a practice; `GET /books/words/languages` counts them; `GET /books/words/cards.txt` is them as flashcards. |
| `GET /books/passages` | Passages beside their translations. `POST {"original": "…", "translation": "…", "bookId": 4, "originalLanguage": "French", "translationLanguage": "English"}` keeps one. |
| `GET /books/queue` | Up next: the books ready, in order, and those held. `PUT` with `{"bookIds": [4, 9]}` orders them; `POST` and `DELETE /books/queue/{id}` put a book in or take it out; `PUT /books/queue/{id}/hold` with `{"until": "2026-12-01", "note": "for the winter"}` holds one, `DELETE` lets it go. |
| `GET /books/lifecycle` | Your collection over time: each year's copies come, finished, let go, kept, spent, and got back; what waits to be read by age, format, subject, and series; never opened; read again. `POST /books/{id}/let-go` with `{"how": "Sold", "on": "2026-10-01", "to": "…", "soldFor": 4.5}` lets a copy go (`Donated`, `Sold`, `Given`, `Discarded`, `Lost`); `DELETE` takes it back. |
| `GET /books/{id}/positions` | Where each source last said you were in a book, and the place kept. `POST /books/{id}/positions/{reportId}/use` takes one, back or forward. |
| `GET /books/notes/search?q=` | Your own words that say it: notes, reviews, quotes, highlights, the reading log, the journal, and the reference desk; `kind`, `status`, `author`, and `format` narrow it. |
| `GET /books/ask?q=` | A question in everyday words, answered from your shelf: how it was understood, and the books or quotes that fit. |
| `GET /books/rules` | Your rules. `POST` with `{"name": "…", "trigger": "Finished", "conditions": {"tag": "mystery"}, "actions": [{"kind": "AddToCollection", "value": "Solved"}]}` adds one; `PUT` and `DELETE /books/rules/{id}` change and remove one; `POST /books/rules/{id}/try` is a dry run; `GET /books/rules/runs` is what they did. |
| `GET /books/legacy` | Your plan for the library. `PUT` with `{"instructions": "…", "keeper": "…"}` keeps it; `POST /books/legacy/recipients` with `{"name": "…", "tag": "signed"}`, `"collectionId"`, or `"rest": true` names someone; `GET /books/legacy/inventory.csv` is who has what; `DELETE /books/legacy` forgets it all. |

## Scanning

| Call | What it does |
| --- | --- |
| `POST /books/scans` | Starts a run: `{"kind": "Add", "location": "Attic"}`, `"kind": "Check"` to check a place, or `"Pack"` and `"Unpack"` with a box's place to pack and unpack it. `GET /books/scans` lists the runs not finished; `DELETE /books/scans/{id}` drops one. |
| `POST /books/scans/{id}/codes` | `{"isbn": "9780441478125"}` reads a code. A check answers with `Here`, `Again`, `Elsewhere` (with the place it's noted at), or `Unknown`. `DELETE /books/scans/{id}/codes/{isbn}` takes a code off the list. |
| `POST /books/scans/{id}/add` | `{"isbns": […], "status": "Want"}` adds the books read in an adding run, together. Answers with how many were added and skipped, and the change to undo them by. |
| `GET /books/scans/{id}/books` | The books a check expects at its place, and whether each has been seen. `POST /books/scans/{id}/seen/{bookId}` ticks one off by hand, `?moveHere=true` noting it at this place. |
| `POST /books/scans/photo` | Photos of a shelf as `files` (each turned as the spines read), with `scanId` to tick off what is found in a check. Answers with the books matched and how sure, and the lines read that match none. |
| `POST /books/scans/{id}/place` | `{"bookIds": […]}` notes books a photo found at the run's place, together, and in a check ticks them off. Answers with the change to undo it by. |
| `POST /books/scans/{id}/finish` | `{"tagMissing": true}` ends a check, tagging the books not seen `missing`. `GET /books/scans/places` lists the places your books are kept. |

## Tidying

| Call | What it does |
| --- | --- |
| `GET /books/duplicates` | Books that look like the same book, each group with its reason (`isbn`, `title`, or `similar`) and how sure, out of 100. `POST /books/duplicates/dismiss` with `{"ids": [3, 9]}` says they differ. `POST /books/{id}/merge` with `{"otherId": 9}` merges, the other entry going to the trash. |
| `POST /books/details/review` | Asks Open Library and Google Books about the books missing details, or `{"everything": true}` every book, in the background. `GET /books/details/proposals` lists what they said where it differs; `POST /books/details/proposals/apply` with `{"choices": [{"id": 4, "source": "Google"}], "allAgreeing": true}` takes answers (`OpenLibrary`, `Google`, or `Keep`); `DELETE` clears the list. |
| `GET /books/files/health` | Your books with a missing, changed, or unreadable file, when the files were last checked, and the newest backup proved to restore. `POST /books/files/check` starts a check, for an admin. |

## Book clubs

| Call | What it does |
| --- | --- |
| `GET /books/clubs` | Your clubs. `POST {"name": "…"}` starts one; `GET` and `DELETE /books/clubs/{id}` read and end one. |
| `POST /books/clubs/{id}/members` | `{"readerId": 3}` invites a reader, for the reader who started the club. `DELETE /books/clubs/{id}/members/{readerId}` removes one, or leaves. |
| `POST /books/clubs/{id}/books` | `{"bookId": 12}` from your shelf, or `{"title": "…", "author": "…"}`. `PUT /books/clubs/{id}/current/{bookId}` picks the one being read. |
| `GET /books/clubs/{id}/books/{bookId}/posts` | The conversation about a book; `POST {"text": "…", "spoiler": true}` says something, folded away as a spoiler if you like. `POST …/shelve` puts the book on your shelf. |
| `GET /books/clubs/{id}/meetings` | The club's meetings, the next first, each with the questions you may see so far and how many wait. `POST {"clubBookId": 4, "at": "2026-11-02T19:00:00Z", "through": "…", "agenda": "…"}` arranges one, for the reader who started the club; `PUT …/meetings/{meetingId}/summary` with `{"summary": "…"}` keeps what was said, and `DELETE …/meetings/{meetingId}` calls it off. |
| `POST /books/clubs/{id}/meetings/{meetingId}/questions` | `{"text": "…"}` sends a question for the meeting. |

A meeting's `at` is the moment it begins, with an offset or in UTC; `timeZone`, an IANA name such as `Europe/Paris`, is the zone it is told in, in notifications and chat messages.

## Events

Everything a [webhook](notifications.md#webhooks) is sent is also kept for you, the latest 1,000, for a script that would rather ask than be told. Each event is the same JSON a signed webhook carries, so one script can take either.

| Call | What it does |
| --- | --- |
| `GET /books/events` | Your events, oldest first, with `next` to send as `?after=` next time and `more` when another page waits. `take` is up to 200 (100 by default); `kind=book.finished,book.reread` keeps to those. |

```bash
after=0
while true; do
  page=$(curl -s -H "Authorization: Bearer $SHELF_TOKEN" "https://shelf.example.org/books/events?after=$after")
  echo "$page" | jq -c '.events[].event'
  after=$(echo "$page" | jq '.next')
  sleep 60
done
```

## Your account

| Call | What it does |
| --- | --- |
| `GET /account/sessions` | Your signed-in devices. `DELETE /account/sessions/{id}` signs one out. |
| `GET /account/passkeys` | Your passkeys. `DELETE /account/passkeys/{id}` removes one. |
| `GET /account/tokens` | Your API tokens. `POST {"name": "…", "canChange": false}` makes one and answers with it, once; `DELETE /account/tokens/{id}` removes one. Only with the browser's sign-in, not a token. |
| `GET /account/sandbox` | Your sandbox: how many books, and its tokens; nothing when there is none. `POST /account/sandbox/tokens` with `{"name": "…", "canChange": true}` makes a token for it, making the sandbox the first time; `POST /account/sandbox/reset` puts the samples back; `DELETE /account/sandbox` removes it. Only with the browser's sign-in. |
| `GET /admin/accounts` | Every reader, for an admin: address and whether it is confirmed, admin, child, suspended, two-step, books, joined, last seen. |
| `PUT /admin/readers/{id}/suspended`, `/name`, `/email`; `POST /admin/readers/{id}/signout` | `{"suspended": true}`, `{"name": "…"}`, `{"email": "…"}`: suspend or restore, rename, or change a reader's address; sign them out everywhere. |
| `GET/POST /admin/invitations`, `POST /admin/invitations/{id}/resend`, `DELETE /admin/invitations/{id}` | `{"emails": "a@example.org, b@example.org"}` invites each, answering with each one's link and whether it went. |
| `GET/PUT /admin/signup` | `{"mode": "Open"}`, `"Invites"`, or `"Closed"`. |
| `PUT /admin/announcement` | `{"text": "…", "email": false}`; no text takes it down. |
| `GET /admin/jobs`, `POST /admin/jobs/{key}` | The background jobs and when each last ran; run one now. |
| `PUT /account/email` | `{"email": "you@example.org", "reminders": true}`: the address you sign in with, which can change but not be removed, and whether notifications go there too. `204` when the address is yours at once; `202` when a link went to it to confirm it first. `POST /account/email/test` sends a test. |
| `POST /account/signout` | Sign out of this browser. |

## For admins

| Call | What it does |
| --- | --- |
| `GET /admin/readers` | Every reader. `POST /admin/readers/{id}/password` gives a new password; `PUT /admin/readers/{id}/admin {"isAdmin": true}` changes admin; `DELETE /admin/readers/{id}` deletes a reader. |
| `GET /admin/audit` | The newest 500 lines of the activity log. |
| `GET /admin/backups` | The nightly copies, each with what its restore drill found. `POST` takes one now; `GET /admin/backups/{name}` downloads one; `POST /admin/backups/{name}/drill` checks one restores. |
| `GET /admin/snapshot` | The whole server as a zip. |

## Devices

These sign in differently, as their apps expect.

| Call | Signs in with | What it does |
| --- | --- | --- |
| `GET /opds` | Basic, any user name and the device key | The OPDS catalog for e-reader apps. |
| `GET /books/sync` and `/books/sync/{key}/…` | `Authorization: Bearer <device key>` | What another shelf calls to trade files and places. |
| `/kosync/users/auth`, `/kosync/syncs/progress` | `x-auth-user` and `x-auth-key` headers | KOReader's progress sync. |

## Open to anyone

| Call | What it does |
| --- | --- |
| `GET /health` | `Healthy` when the app and database are up. |
| `GET /alive` | `Healthy` when the app is up. |
| `GET /version` | The running version and the commit it was built from. |
| `GET /shared/{link}/list.json` | A reading list shared by link: its name, its reader, and each book's title, author, subtitle, year, series, stars, and cover. |
