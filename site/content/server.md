---
title: Running on a server
nav: Running on a server
description: HTTPS behind a reverse proxy, OCR, Calibre, email, and keeping the shelf healthy.
---

# Running on a server

## HTTPS and a reverse proxy

Passwords, device keys, and passkeys need HTTPS on any shelf reached from outside your own network. The usual way is a reverse proxy that holds the certificate and passes requests to Shelf. Tell Shelf it is there with `Hosting:BehindProxy` set to `true`, so it trusts the proxy's forwarded headers, and only when Shelf can't be reached except through it.

### Caddy

Caddy gets and renews a certificate by itself:

```text
shelf.example.org {
    reverse_proxy localhost:8080
}
```

### nginx

```nginx
server {
    listen 443 ssl;
    server_name shelf.example.org;
    # ssl_certificate and ssl_certificate_key, from Let's Encrypt, say

    client_max_body_size 1100m;   # audiobooks can be up to 1 GB

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 1h;
    }
}
```

The `Upgrade` lines matter: Shelf's pages keep a WebSocket open to the server.

> [!TIP]
> Set `Email:PublicAddress` and `Passkeys:Origin` to the address readers use, such as `https://shelf.example.org`, so links in emails and passkeys are right.

Without a proxy, Shelf sends HSTS and redirects HTTP to HTTPS whenever it has an HTTPS port of its own.

## OCR for scanned PDFs

Reading scanned pages needs Tesseract and Poppler. The Docker image has both, with English. From source, install them yourself; Shelf looks for `tesseract`, `pdftoppm`, `pdftotext`, and `pdfinfo` on the path when it starts.

```bash
# Debian and Ubuntu
sudo apt install tesseract-ocr tesseract-ocr-eng poppler-utils

# Arch Linux
sudo pacman -S tesseract tesseract-data-eng poppler

# macOS
brew install tesseract poppler
```

For other languages, install their Tesseract data and list them in `Ocr:Languages`, such as `eng+fra`. In Docker, extend the image:

```dockerfile
FROM ghcr.io/doodersrage/shelf:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends tesseract-ocr-fra && rm -rf /var/lib/apt/lists/*
USER app
```

Each PDF is read once, a page at a time, and the words are kept in the database, so a restart carries on where it stopped.

## Calibre for Kindle files

Kindle files are turned into EPUBs by Calibre's `ebook-convert`. Install Calibre on the server, or build the Docker image with it:

```bash
docker build --build-arg CALIBRE=true -t shelf https://github.com/doodersrage/shelf.git
```

## Email

Email lets readers reset a forgotten password themselves and get a daily reminder about loans. Any SMTP server works: your email provider's, or a service such as Fastmail, Mailgun, or Amazon SES. [Configuration](configuration.md#email) lists the settings. Readers can send themselves a test from **Account** once their address is saved.

## Disk space

Plan for your files more than Shelf itself: e-books are a few megabytes each, audiobooks a few hundred, and the database stays small. Held uploads that nobody decided on are cleared after a day.
