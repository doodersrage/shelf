---
title: Install
description: Run Shelf with Docker, Docker Compose, or from source, make the first account, and keep it up to date.
---

# Install

Shelf is one small web app with its data in one folder: a SQLite database, and the e-books, audiobooks, and cover pictures beside it. The Docker image is the easiest way to run it, and has OCR for scanned PDFs built in.

## With Docker

```bash
docker run -d --name shelf -p 8080:8080 -v shelf-data:/data \
  --restart unless-stopped ghcr.io/doodersrage/shelf:latest
```

Then open [http://localhost:8080](http://localhost:8080). Images are published for both amd64 and arm64 (a Raspberry Pi 4 or 5, most NAS boxes, Apple silicon). Every release is also published under its own version, such as `ghcr.io/doodersrage/shelf:1.4.0`, which is the better choice when you want to decide when to update.

Everything Shelf keeps is under `/data` in the container:

| Path | What it holds |
| --- | --- |
| `/data/shelf.db` | The database: books, readers, notes, highlights, places, and settings. |
| `/data/ebooks` | E-book files, named for good when saved and never changed. |
| `/data/audio` | Audiobooks, one folder each. |
| `/data/covers` | Cover pictures kept on the shelf. |
| `/data/keys` | The keys that seal sign-in cookies and two-step secrets. Keep this private. |
| `/data/backups` | Nightly copies of the database. |

## With Docker Compose

```yaml
services:
  shelf:
    image: ghcr.io/doodersrage/shelf:latest
    ports:
      - "8080:8080"
    volumes:
      - shelf-data:/data
      # A folder to drop books into; each reader gets a folder inside it (see Import:Folder):
      # - /srv/books-inbox:/import
    environment:
      # Behind a proxy that ends HTTPS (Caddy, nginx, Traefik):
      # Hosting__BehindProxy: "true"
      # Email for password resets and reminders:
      # Email__Host: smtp.example.org
      # Email__From: shelf@example.org
      # Email__PublicAddress: https://shelf.example.org
      # Stop new accounts once yours exists:
      # Accounts__AllowSignUp: "false"
      # Add books dropped into the folder mounted at /import:
      # Import__Folder: /import
      Ocr__Languages: eng
    restart: unless-stopped

volumes:
  shelf-data:
```

```bash
docker compose up -d
```

Settings are environment variables, with `__` where the documentation writes `:`. [Configuration](configuration.md) lists them all.

> [!TIP]
> Kindle files need Calibre to be turned into EPUBs, which adds several hundred megabytes, so the published image leaves it out. Build your own with it: `docker build --build-arg CALIBRE=true -t shelf .`

## On Unraid, TrueNAS, or CasaOS

Ready-made templates are in the [`deploy`](https://github.com/doodersrage/shelf/tree/main/deploy) folder:

- **Unraid**: in **Docker**, choose **Add Container**, and paste `https://raw.githubusercontent.com/doodersrage/shelf/main/deploy/unraid/shelf.xml` into **Template**. Data goes to `/mnt/user/appdata/shelf`, and Shelf runs as Unraid's `nobody:users` (99:100).
- **TrueNAS SCALE** 24.10 or later: make a dataset for Shelf, then **Apps**, **Discover Apps**, **Custom App**, **Install via YAML**, and paste [`deploy/truenas/docker-compose.yml`](https://github.com/doodersrage/shelf/blob/main/deploy/truenas/docker-compose.yml) with your dataset's path. It runs as TrueNAS's `apps` user (568).
- **CasaOS**: **App Store**, **Custom Install**, import, and paste [`deploy/casaos/docker-compose.yml`](https://github.com/doodersrage/shelf/blob/main/deploy/casaos/docker-compose.yml).

### Which user Shelf runs as

The image starts as root only long enough to make `/data` belong to the user Shelf runs as, then switches to that user: `PUID` and `PGID`, 1654 (the image's own `app` user) unless you set them. So a folder from the host, which is often owned by someone else, works as `/data` without any `chown`. Give an import folder's owner as `PUID` and `PGID`, since Shelf never changes who owns your own files there. Started with `--user`, the image runs as that user and changes nothing.

## The first account

The first visit goes to the sign-in page; choose **Make an account**. The first account:

- becomes the admin, who can manage the other readers, see the activity log, and take backups;
- takes any books that were on the shelf before there were accounts.

Every later account starts with an empty shelf of its own. Once your household is signed up, you can close sign-up with `Accounts:AllowSignUp` set to `false`.

## From source

Shelf needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). For OCR, install Tesseract and Poppler too (see [Running on a server](server.md#ocr-for-scanned-pdfs)).

```bash
git clone https://github.com/doodersrage/shelf.git
cd shelf
dotnet run --project src/Shelf.Api
```

Open [http://localhost:5041](http://localhost:5041). The database, `ebooks`, `audio`, `covers`, and `keys` are made beside the project on first run.

For development there is also an Aspire app host, which starts Shelf with a dashboard of its logs and traces:

```bash
dotnet run --project Shelf.AppHost --launch-profile http
```

> [!NOTE]
> On Arch Linux, the `dotnet-sdk` package does not include ASP.NET Core. Install `aspnet-runtime` and `aspnet-targeting-pack` before building.

## Updating

Shelf updates its own database when it starts, so updating is replacing the app:

```bash
docker pull ghcr.io/doodersrage/shelf:latest
docker stop shelf && docker rm shelf
docker run -d --name shelf -p 8080:8080 -v shelf-data:/data \
  --restart unless-stopped ghcr.io/doodersrage/shelf:latest
```

With Compose, `docker compose pull && docker compose up -d`. The [changelog](changelog.md) says what each release changes, including anything to know before updating.

> [!TIP]
> Take a snapshot from **Readers** before a big update. It holds the whole server, and restoring it is unpacking it over a stopped shelf. See [Backups](backups.md).

## Putting it on the internet

Passwords and device keys travel with every request, so a shelf reached from outside your home needs HTTPS. Put it behind a reverse proxy such as Caddy, nginx, or Traefik, and tell Shelf it is there. [Running on a server](server.md) shows how.
