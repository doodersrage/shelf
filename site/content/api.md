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

A token opens the calls on this page under `/books`, `/settings`, and `/readers`. It never opens the account (passwords, keys, other tokens) or the admin pages, so a token that leaks cannot take the account over; remove it on **Account** and it stops working at once. A read-only token is refused anything but `GET`, with 403. Account shows when each token was last used.

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

A script can also sign in with the cookie the browser uses, posting the sign-in form with the anti-forgery token the page carries:

```bash
site=https://shelf.example.org
jar=$(mktemp)
token=$(curl -s -c "$jar" "$site/signin" \
  | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | sed 's/.*value="//; s/"$//')
curl -s -b "$jar" -c "$jar" -o /dev/null "$site/account/signin" \
  --data-urlencode "name=Tenar" --data-urlencode "password=$SHELF_PASSWORD" \
  --data-urlencode "__RequestVerificationToken=$token"

curl -s -b "$jar" "$site/books?status=Reading"
```

A reader with two-step sign-in is asked for a code next, at `/account/signin/code`. The [device key](devices.md#the-device-key) is for e-reader catalogs and shelf-to-shelf sync only, not for this API.

> [!TIP]
> Requests that change something take JSON, with `Content-Type: application/json`, unless they upload a file, which goes as a multipart form with the file in a field named `file`.

## Books

| Call | What it does |
| --- | --- |
| `GET /books` | The library. Filter with `q`, `status`, `tag`, `author`, `series`, `place`, `recommendedBy`, `loanedTo`, `loved`, `loaned`, and `format`; sort with `sort` (`title`, `author`, `series`, `year`, `rating`, `added`). `skip` and `take` (up to 500) page through it, with the total in the `X-Total-Count` header. |
| `GET /books/{id}` | One book, with its tags, quotes, and reading sessions. |
| `POST /books` | Add a book: `{"title": "…", "author": "…", "status": "Want"}` and any other fields. |
| `PUT /books/{id}` | Change a book. |
| `DELETE /books/{id}` | Delete a book with its files. |
| `POST /books/bulk` | Change up to 1,000 books: `{"ids": [1, 2], "status": "Finished"}`, or `addTag`, `removeTag`, `loved`, `delete`. |
| `GET /books/lookup?isbn=` | Details and a cover from Open Library. `?title=&author=` looks a book up without an ISBN. |
| `POST /books/{id}/enrich` | Fill a book's empty fields from Open Library. |
| `GET /books/pick` | One book from the want list. `?status=` picks from another. |
| `GET /books/authors`, `/series`, `/places`, `/copies`, `/recommenders`, `/years` | The library gathered by author, series in reading order, place, condition, recommender, and year finished. |
| `GET /books/collections` | Your collections, with how many books each holds. `POST {"name": "…", "description": "…"}` makes one; `GET`, `PUT`, and `DELETE /books/collections/{id}` read, rename, and delete one. |
| `POST /books/collections/{id}/books` | `{"bookId": 12}` adds a book at the end; `DELETE /books/collections/{id}/books/{bookId}` takes it out; `PUT /books/collections/{id}/order` with `{"bookIds": [3, 1, 2]}` puts them in that order. |
| `GET /books/series/alerts` | New books in your series that are not on your shelf. `POST /books/series/alerts/{id}/want` adds one to the want list; `DELETE /books/series/alerts/{id}` dismisses it; `POST /books/series/alerts/check` looks now. |
| `GET /books/years/{year}/review` | A year in books: finished books and pages, time reading and listening, authors new that year, books by month, formats, tags, the longest and shortest, favourites, and quotes and highlights kept. |
| `GET /books/stats` | The year's goal, books finished each month, counts, and the streak. |
| `GET /books/calendar` | The days with a reading session. `?year=&month=` picks another month. |
| `GET`, `PUT /settings` | The yearly goal. |

Statuses are `Want`, `Reading`, `Finished`, and `Abandoned`; formats are `Hardcover`, `Paperback`, `Ebook`, and `Audiobook`.

## Files and covers

| Call | What it does |
| --- | --- |
| `POST /books/{id}/ebook` | Upload an EPUB, PDF, comic, or Kindle file, up to 80 MB. `DELETE` removes it. Answers with a redirect to the book's page; `?ebook=duplicate&held=…` means the same file is on another book. Send `keepBoth=true` to add it anyway. |
| `GET /books/{id}/ebook/file` | The e-book file. |
| `GET /books/{id}/ebook/chapters/{index}` | One EPUB chapter, ready to show. |
| `GET /books/{id}/ebook/pages/{index}` | One page of a comic, as an image. |
| `GET /books/{id}/ebook/ocr/{page}` | The words read from a scanned PDF page, with where each sits. |
| `POST /books/{id}/audio` | Upload an audio file or a zip of tracks, up to 1 GB. `DELETE` removes it. |
| `GET /books/{id}/audio/tracks/{index}` | One track, with range requests for seeking. |
| `GET /books/{id}/audio/plan` | What the player needs: the tracks with their lengths in seconds, the chapters (each in a track, from a time), where you stopped, and your speed. |
| `PUT /books/{id}/audio/place` | `{"track": 2, "seconds": 341.5}` keeps your place in the recording exactly, back or forward. Add `"listened": 15, "day": "2026-10-10"` to count seconds of listening on that day. |
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
| `GET /books/search?q=` | Places inside your e-books where a phrase appears, best first. |
| `GET /books/{id}/notes.md` | A book's quotes, highlights, notes, and review as Markdown. `GET /books/notes.zip` has every book. |

## Lending and open shelves

| Call | What it does |
| --- | --- |
| `GET /readers` | The other readers on this shelf. |
| `POST /books/{id}/lend` | Lend a book: `{"readerId": 2, "dueOn": "2026-11-01"}`. |
| `POST /books/{id}/return` | End a loan. |
| `GET /books/loans` | Who has which of your books, and how many are overdue. |
| `GET /books/borrowed` | Books lent to you. `POST /books/borrowed/{id}/return` gives one back. |
| `GET /books/shelves` | Open shelves. `GET /books/shelves/{id}` lists one. `PUT /books/shelves/open {"open": true}` opens yours. |
| `POST /books/{id}/ask` | Ask to borrow a book from an open shelf. |
| `GET /books/asks` | Asks you made and asks waiting on you. `POST /books/asks/{id}/lend {"dueOn": null}` lends the book; `DELETE /books/asks/{id}` declines or takes it back. |
| `GET /books/reminders` | Counts of overdue loans, borrowed books due soon, and asks waiting. |

## Importing and exporting

| Call | What it does |
| --- | --- |
| `GET /books/export` | Your shelf as JSON, with quotes, sessions, and highlights. `POST /books/import` restores one, skipping books already there. |
| `GET /books/export/full` | A zip of the JSON with every e-book, audiobook, and cover picture. `POST /books/import/full` with `file` restores one. |
| `POST /books/import/csv` | A Goodreads or StoryGraph CSV export as `file`. |

## Your account

| Call | What it does |
| --- | --- |
| `GET /account/sessions` | Your signed-in devices. `DELETE /account/sessions/{id}` signs one out. |
| `GET /account/passkeys` | Your passkeys. `DELETE /account/passkeys/{id}` removes one. |
| `GET /account/tokens` | Your API tokens. `POST {"name": "…", "canChange": false}` makes one and answers with it, once; `DELETE /account/tokens/{id}` removes one. Only with the browser's sign-in, not a token. |
| `PUT /account/email` | `{"email": "you@example.org", "reminders": true}`. `POST /account/email/test` sends a test. |
| `POST /account/signout` | Sign out of this browser. |

## For admins

| Call | What it does |
| --- | --- |
| `GET /admin/readers` | Every reader. `POST /admin/readers/{id}/password` gives a new password; `PUT /admin/readers/{id}/admin {"isAdmin": true}` changes admin; `DELETE /admin/readers/{id}` deletes a reader. |
| `GET /admin/audit` | The newest 500 lines of the activity log. |
| `GET /admin/backups` | The nightly copies. `POST` takes one now; `GET /admin/backups/{name}` downloads one. |
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
