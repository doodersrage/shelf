---
title: Configuration
description: Every setting Shelf reads, what it does, and its default.
---

# Configuration

Shelf works with no settings at all. Everything here is optional, for when you want email, a reverse proxy, different folders, or to turn something off.

## How to set things

Settings can come from environment variables, from `appsettings.json` beside the app, or from the command line. Names are written here as `Section:Name`; as an environment variable, use two underscores instead of the colon.

| Written here | As an environment variable | On the command line |
| --- | --- | --- |
| `Accounts:AllowSignUp` | `Accounts__AllowSignUp=false` | `--Accounts:AllowSignUp false` |
| `Email:Host` | `Email__Host=smtp.example.org` | `--Email:Host smtp.example.org` |

## Accounts

| Setting | Default | What it does |
| --- | --- | --- |
| `Accounts:AllowSignUp` | `true` | Whether new accounts can be made. Set it to `false` once your readers have signed up. The first account can always be made. |
| `Accounts:SignInsPerMinute` | `10` | Sign-in and sign-up attempts allowed a minute from one address. |

## Single sign-on

Off unless `Oidc:Authority` and `Oidc:ClientId` are set. See [Single sign-on](security.md#single-sign-on).

| Setting | Default | What it does |
| --- | --- | --- |
| `Oidc:Authority` | none | The provider's issuer address, such as `https://auth.example.org/application/o/shelf/` for Authentik. |
| `Oidc:ClientId` | none | The client id the provider gave Shelf. |
| `Oidc:ClientSecret` | none | Its secret. Leave it out for a public client; PKCE is always used. |
| `Oidc:Name` | `SSO` | What the button says: **Sign in with** this. |
| `Oidc:CreateAccounts` | `false` | Make accounts for new people who sign in through the provider even when `Accounts:AllowSignUp` is `false`. |
| `Oidc:Scopes` | `openid profile email` | The scopes asked for. |
| `Oidc:RequireHttpsMetadata` | `true` | Set to `false` only for a provider on plain `http://` inside your own network. |

## Where things are kept

| Setting | Default | What it does |
| --- | --- | --- |
| `ConnectionStrings:Shelf` | `Data Source=shelf.db` | The SQLite database. In Docker, `/data/shelf.db`. |
| `EbookStore:Root` | `ebooks` beside the app | E-book files. In Docker, `/data/ebooks`. |
| `AudioStore:Root` | `audio` beside the app | Audiobooks. In Docker, `/data/audio`. |
| `Uploads:MaxAudioMegabytes` | `1024` | The largest audiobook, all its tracks together, in megabytes; also the most an upload of many files at once may total. A reverse proxy has a limit of its own to raise to match (see [HTTPS and a reverse proxy](server.md#https-and-a-reverse-proxy)). |
| `Uploads:MaxEbookMegabytes` | `80` | The largest e-book, PDF, or comic file, in megabytes. |
| `CoverStore:Root` | `covers` beside the app | Cover pictures kept on the shelf. In Docker, `/data/covers`. |
| `DataProtection:KeysPath` | `keys` beside the database | The keys that seal sign-in cookies and two-step secrets. Keep it private and keep it with backups, or everyone signs in again after a restore. |

## PostgreSQL

Shelf keeps its database in SQLite, one file beside everything else, which suits a shelf of any size a household has. To keep it in PostgreSQL instead (13 or newer), say for a server that runs one for everything:

| Setting | Default | What it does |
| --- | --- | --- |
| `Database:Provider` | `Sqlite` | `Postgres` to use PostgreSQL. |
| `ConnectionStrings:Postgres` | none | The database, as `Host=db;Database=shelf;Username=shelf;Password=…`. Shelf makes its tables on the first start. |

The keys, backups, and files stay in the folders above, so `/data` is still wanted in Docker. A few things differ:

- **Search** finds a phrase anywhere, ignoring case, but doesn't rank the places it finds or overlook accents, as SQLite's full-text index does.
- **Snapshots** hold `pg_dump`'s copy of the database when `pg_dump` is on the server (or at `Database__PgDump`), and otherwise the library as JSON, which **Restore** takes. Back the database up with PostgreSQL's own tools as well.

**Moving an existing shelf.** With PostgreSQL set up and empty, run Shelf once with the move command; it copies every reader, book, note, and setting from the SQLite file, ids and all, then stops:

```bash
docker run --rm -v shelf-data:/data -e Database__Provider=Postgres -e ConnectionStrings__Postgres="Host=db;Database=shelf;Username=shelf;Password=…" ghcr.io/doodersrage/shelf:latest --move-to-postgres true
```

Then start Shelf with the same two settings. The SQLite file stays as it was, to go back to.

## Running behind a proxy

| Setting | Default | What it does |
| --- | --- | --- |
| `Hosting:BehindProxy` | `false` | Trust a reverse proxy's `X-Forwarded-Proto`, `X-Forwarded-Host`, and `X-Forwarded-For`. Only set it when Shelf cannot be reached except through that proxy. |
| `Passkeys:Origin` | the address of each request | The address readers open Shelf at, such as `https://shelf.example.org`, when a proxy hides it. Passkeys are tied to it. |
| `Passkeys:RelyingParty` | the host name of `Passkeys:Origin` | The name passkeys are made for. Rarely needed. |

## Email

Without a mail server, an admin resets forgotten passwords and reminders stay in the app.

| Setting | Default | What it does |
| --- | --- | --- |
| `Email:Host` | none | The SMTP server. Email is on once this is set. |
| `Email:Port` | `587` | Its port. |
| `Email:Security` | `auto` | `auto`, `starttls`, `ssl` (port 465), or `none` for a server on the same machine. |
| `Email:User`, `Email:Password` | none | The account to send with, if the server wants one. |
| `Email:From` | none | The address emails come from. |
| `Email:PublicAddress` | none | Shelf's own address, such as `https://shelf.example.org`, for links in emails. |

## Nightly backups

| Setting | Default | What it does |
| --- | --- | --- |
| `Backup:Enabled` | `true` | Take a copy of the database each night. |
| `Backup:Hour` | `3` | The hour, in UTC, after which it is taken. |
| `Backup:Keep` | `7` | How many to keep. |
| `Backup:Folder` | `backups` beside the database | Where they go. |
| `Backup:IncludeFiles` | `false` | Put every e-book and audiobook in each copy too. They never change once saved, so most people back up their folders separately instead. |
| `Backup:CopyTo` | none | A second folder to copy each backup to, such as another disk or a mounted network share. It keeps as many as `Backup:Keep`. |
| `Backup:S3:Bucket` | none | A bucket in S3-compatible storage to copy each backup to. Needs the two keys below. |
| `Backup:S3:AccessKey`, `Backup:S3:SecretKey` | none | The keys for it. Give them only the rights to list, put, and delete in that bucket. |
| `Backup:S3:Endpoint` | Amazon S3 | The service's address for anything but Amazon, such as `https://s3.us-west-004.backblazeb2.com`, `https://<account>.r2.cloudflarestorage.com`, or your MinIO. |
| `Backup:S3:Region` | `us-east-1` | The region. |
| `Backup:S3:Prefix` | `shelf/` | Where in the bucket the copies go. The newest `Backup:Keep` are kept there. |
| `Backup:S3:PathStyle` | `true` with an endpoint | Address the bucket as part of the path, as most S3-compatible services want. |
| `Backup:S3:PartSizeMegabytes` | `64` | Larger backups go up in parts of this size, at least 5. |

## Calibre

| Setting | Default | What it does |
| --- | --- | --- |
| `Calibre:Library` | none | The Calibre library's folder, filled in on **Open a Calibre library**. See [From Calibre](adding.md#from-calibre). |

## Import folder

| Setting | Default | What it does |
| --- | --- | --- |
| `Import:Folder` | none (off) | A folder to watch. Each reader's books go in a folder inside it named after them; see [From a folder](adding.md#from-a-folder). In Docker, mount one at `/import` and set this to `/import`. |
| `Import:AfterImport` | `move` | `move` puts added files into `.imported`; `delete` removes them, since Shelf keeps its own copy. Files it could not add are always moved to `.not-added`. |
| `Import:EverySeconds` | `60` | How often it looks. |
| `Import:SettleSeconds` | `30` | How long a file must have stayed unchanged before it is taken. |

## Scanned PDFs (OCR)

| Setting | Default | What it does |
| --- | --- | --- |
| `Ocr:Enabled` | `true` | Read the words of scanned pages, when Tesseract and Poppler are installed. |
| `Ocr:Languages` | `eng` | Tesseract's languages, such as `eng+fra` once `tesseract-data-fra` is installed. |
| `Ocr:Resolution` | `200` | The DPI pages are read at. |
| `Ocr:Tesseract`, `Ocr:PdfToPpm`, `Ocr:PdfToText`, `Ocr:PdfInfo` | found on the path | Where those tools are, if not on the path. `Ocr:PdfInfo` also reads a PDF's title and author when one is added from a file. |

## Books from elsewhere

| Setting | Default | What it does |
| --- | --- | --- |
| `Comics:ComicVineKey` | none | An API key from [ComicVine](https://comicvine.gamespot.com/api/), for **Match on ComicVine** on comics' pages. Without one, the button isn't shown. |
| `Ebooks:Convert` | `ebook-convert` | Calibre's converter, which turns Kindle files into EPUBs. Without it, Kindle files are refused with a note saying why. |
| `FreeBooks:Enabled` | `true` | The Free books page, which reaches gutenberg.org, librivox.org, and archive.org. Turn it off for a shelf with no internet. |
| `AbsApi:Enabled` | `true` | Let Audiobookshelf's phone apps sign in to Shelf. See [Audiobookshelf's phone apps](devices.md#audiobookshelfs-phone-apps). |
| `AbsSync:Enabled` | `true` | Look once a day at the Audiobookshelf libraries readers keep in step with. See [Keeping in step](adding.md#keeping-in-step). |
| `AbsSync:LookSeconds` | `30` | How often Shelf checks whether a library is due its daily look, or was asked to look now. |
| `Details:PauseMilliseconds` | `1200` | The pause between books when details or covers are filled in for many, so Open Library and Google Books are asked politely. |
| `Fetch:PollSeconds` | `20` | How often Shelf asks the download client after the books it is fetching. The rest is set by an admin under Downloads; see [Finding and fetching books](adding.md#finding-and-fetching-books). |
| `Fetch:PauseMilliseconds` | `3000` | The pause between books when looking for the whole want list, so the indexers are asked politely. |
| `Kobo:Kepub` | `true` | Send books to Kobo e-readers as KEPUBs, made from the EPUBs and kept beside them, for the Kobo's page numbers and statistics. `false` sends the EPUBs as they are. |
| `Lookup:Dictionary` | `true` | **Look up** in the reader, which sends the chosen words to Wiktionary and Wikipedia. Set `false` to keep them on the shelf. |
| `Lookup:GoogleBooks` | `true` | Ask Google Books when Open Library has no match or leaves fields blank. It sends the ISBN, or the title and author, to Google; set `false` to keep look-ups to Open Library alone. |
| `Lookup:GoogleBooksKey` | none | A Google Books API key (free, from the Google Cloud console). Without one, look-ups share Google's allowance for anonymous callers everywhere, which often runs out for the day; Shelf then carries on with Open Library alone. |
| `Hardcover:LookupToken` | none | A Hardcover API token that every reader's look-ups use, to fill a book's series and number and other blanks. Without it, only readers who connected Hardcover under Account ask it, with their own token. |
| `AuthorWatch:Enabled` | `true` | The daily look at Open Library for new books by the authors readers follow. Set `false` to stop it for everyone. |
| `Finding:SearchUrl` | `https://openlibrary.org/` | Where **Find new books** and followed authors search: Open Library, or a mirror of its search API. |
| `SeriesAlerts:Enabled` | `true` | The daily look at Open Library for new books in a series, for readers who turn it on under Series. Set `false` to stop it for everyone. |

## Recaps

| Setting | Default | What it does |
| --- | --- | --- |
| `Recap:ApiKey` | none | An Anthropic API key, for **Previously in this book**. Without one, recaps are off. |
| `Recap:Model` | `claude-haiku-5-5` | The model that writes recaps. A larger one writes better, for more. |
| `Recap:Enabled` | `true` | Turn recaps off without removing the key. |

The text of the chapters a reader has read is sent to Anthropic to be summed up; see [Previously in this book](reading.md#previously-in-this-book).

## Read-along

| Setting | Default | What it does |
| --- | --- | --- |
| `ReadAlong:Url` | none | The transcriptions address of a speech-to-text service with OpenAI's API, such as `http://speaches:8000/v1/audio/transcriptions`. Without it, read-along is off. See [Read-along](audiobooks.md#read-along). |
| `ReadAlong:Model` | `whisper-1` | The model the service should use. |
| `ReadAlong:ApiKey` | none | Sent as a bearer token, for a service that asks for one. |
| `ReadAlong:FFmpeg` | `ffmpeg` on the path | Where ffmpeg is, to cut recordings into ten-minute pieces. `Audio:FFmpeg` sets it for making M4Bs too. |
| `Audio:M4bBitrate` | `64k` | The AAC bitrate of an M4B made from tracks. |
| `ReadAlong:MaxUploadMegabytes` | `24` for OpenAI, else none | The largest file to send whole. A larger track needs ffmpeg. |

## Linked shelves

| Setting | Default | What it does |
| --- | --- | --- |
| `Federation:Name` | `Shelf at` and the host of `Email:PublicAddress` | The name linked shelves know this one by. |

Linking needs `Email:PublicAddress`, as the other shelf calls this one at that address. See [Linked shelves](lending.md#linked-shelves).

## Notifications and webhooks

| Setting | Default | What it does |
| --- | --- | --- |
| `Notices:Contact` | `Email:From`, or Shelf's project page | Who the push services can contact about this shelf's notifications, as `mailto:you@example.org` or a web address. Apple asks for one. |
| `Webhooks:Enabled` | `true` | Readers' own webhooks. Webhooks post to addresses readers give, which can be on your own network (as Home Assistant usually is); set `false` if you don't want the shelf posting anywhere. |
| `Notices:PushHosts` | none | More push services to allow, by host name and separated by commas, for a browser whose service Shelf doesn't know. Shelf sends browser notifications only to Google's, Mozilla's, Apple's, and Microsoft's. |

Links in ntfy and Gotify notifications need the shelf's own address, `Email:PublicAddress`; without it, they come without a link.

## Logging

Shelf logs with ASP.NET Core's usual settings, such as `Logging:LogLevel:Default`. In Docker, `docker logs shelf` shows them.

## Source code link

| Setting | Default | What it does |
| --- | --- | --- |
| `SourceUrl` | this project on GitHub | Where the **Source code** link at the foot of each page goes. Shelf is under the AGPL: if you run a changed copy for other people, point this at your changed source. |
