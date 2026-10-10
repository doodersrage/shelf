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

## Logging

Shelf logs with ASP.NET Core's usual settings, such as `Logging:LogLevel:Default`. In Docker, `docker logs shelf` shows them.
