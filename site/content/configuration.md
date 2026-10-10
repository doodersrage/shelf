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
| `CoverStore:Root` | `covers` beside the app | Cover pictures kept on the shelf. In Docker, `/data/covers`. |
| `DataProtection:KeysPath` | `keys` beside the database | The keys that seal sign-in cookies and two-step secrets. Keep it private and keep it with backups, or everyone signs in again after a restore. |

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
| `Ebooks:Convert` | `ebook-convert` | Calibre's converter, which turns Kindle files into EPUBs. Without it, Kindle files are refused with a note saying why. |
| `FreeBooks:Enabled` | `true` | The Free books page, which reaches gutenberg.org, librivox.org, and archive.org. Turn it off for a shelf with no internet. |
| `Lookup:GoogleBooks` | `true` | Ask Google Books when Open Library has no match or leaves fields blank. It sends the ISBN, or the title and author, to Google; set `false` to keep look-ups to Open Library alone. |
| `Lookup:GoogleBooksKey` | none | A Google Books API key (free, from the Google Cloud console). Without one, look-ups share Google's allowance for anonymous callers everywhere, which often runs out for the day; Shelf then carries on with Open Library alone. |

## Logging

Shelf logs with ASP.NET Core's usual settings, such as `Logging:LogLevel:Default`. In Docker, `docker logs shelf` shows them.

## Source code link

| Setting | Default | What it does |
| --- | --- | --- |
| `SourceUrl` | this project on GitHub | Where the **Source code** link at the foot of each page goes. Shelf is under the AGPL: if you run a changed copy for other people, point this at your changed source. |
