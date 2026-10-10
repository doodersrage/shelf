---
title: Shelf
nav: Introduction
layout: home
description: Shelf is a personal library you run yourself: your books, e-books, and audiobooks, a reading log, highlights, and lending between the readers on your server.
---

<div class="hero">
<p class="eyebrow">A personal library you run yourself</p>
<h1>Every book you own, read, and lend, in one quiet place.</h1>
<p class="lead">Shelf keeps your catalog, your e-books and audiobooks, where you stopped, what you highlighted, and who has which book. It runs on your own computer or server, for you and the people you share it with.</p>
<div class="actions">
<a class="button primary" href="install.html">Install Shelf</a>
<a class="button" href="library.html">See what it does</a>
<a class="button" href="https://github.com/doodersrage/shelf">Source on GitHub</a>
</div>
</div>

<figure class="shot"><img src="assets/images/library.png" alt="The library: the books being read, the one to read next, and the ones just finished" /></figure>

## What it does

<ul class="features">
<li><strong>A real catalog</strong><span>Series, translators, editions, where each copy sits, its condition, who recommended it, ratings, reviews, and tags. Fill it from an ISBN.</span></li>
<li><strong>Read in the browser</strong><span>EPUBs, PDFs, and comics, at your text size, back where you stopped, with highlights, notes, and read aloud. Scanned PDFs are read with OCR.</span></li>
<li><strong>Listen to audiobooks</strong><span>One file or a folder of tracks, with chapters, bookmarks, a speed of your own, and a sleep timer.</span></li>
<li><strong>Lend to each other</strong><span>Every reader has a shelf of their own. Lend a book and the borrower reads or listens with their own place and notes.</span></li>
<li><strong>Bring a library in</strong><span>Drop in a folder of files, import from Audiobookshelf, Goodreads, or StoryGraph, or add free public-domain books.</span></li>
<li><strong>Keep in step with devices</strong><span>KOReader downloads from Shelf's catalog and syncs its place back. Read offline on a phone.</span></li>
<li><strong>Know your reading</strong><span>A yearly goal, a monthly chart, a calendar of reading days, a streak, and every quote in one place.</span></li>
<li><strong>Yours, and private</strong><span>Your data stays on your server. Passkeys, two-step sign-in, an activity log, and nightly backups.</span></li>
<li><strong>In four languages</strong><span>English, Spanish, French, and German, chosen by each reader, with dates written the way their region writes them.</span></li>
</ul>

<div class="gallery">
<figure><img src="assets/images/covers.png" alt="Every book on the shelf as a grid of covers, with status labels" /><figcaption><strong>The whole shelf</strong> as covers or a compact list, with search, status, and sort.</figcaption></figure>
<figure><img src="assets/images/book.png" alt="A book's page, led by its cover, status, and next step" /><figcaption><strong>A book's page</strong> leads with its cover and one next step.</figcaption></figure>
<figure><img src="assets/images/reader.png" alt="The e-book reader with a highlighted passage and text settings" /><figcaption><strong>The reader</strong> keeps your place, your highlights, and your text settings.</figcaption></figure>
<figure><img src="assets/images/stats.png" alt="Stats with the year's goal and books finished each month" /><figcaption><strong>Stats</strong> lead with the year's goal and a month-by-month chart.</figcaption></figure>
<figure><img src="assets/images/loans.png" alt="Loans, with a book lent out and another reader asking to borrow" /><figcaption><strong>Loans</strong> between readers, and asks to borrow from an open shelf.</figcaption></figure>
<figure><img src="assets/images/dark.png" alt="The library in dark mode" /><figcaption><strong>Dark mode</strong> follows the system, and every page works on a phone.</figcaption></figure>
</div>

## Start in a minute

With Docker installed, this runs the latest release and keeps everything in one volume:

```bash
docker run -d --name shelf -p 8080:8080 -v shelf-data:/data \
  --restart unless-stopped ghcr.io/doodersrage/shelf:latest
```

Open [http://localhost:8080](http://localhost:8080) and make the first account; it becomes the admin. [Install](install.md) covers Docker Compose, running from source, and putting Shelf on the internet safely.

## Find your way

<ul class="cards">
<li><a href="install.html"><strong>Install</strong><span>Docker, Compose, or from source, the first account, and updates.</span></a></li>
<li><a href="adding.html"><strong>Bring your books in</strong><span>Files, free books, Audiobookshelf, Goodreads, and StoryGraph.</span></a></li>
<li><a href="reading.html"><strong>Reading</strong><span>The reader, highlights, read aloud, search, OCR, and offline.</span></a></li>
<li><a href="lending.html"><strong>Readers and lending</strong><span>Accounts, loans, open shelves, and reminders.</span></a></li>
<li><a href="devices.html"><strong>Devices and apps</strong><span>KOReader, OPDS, and trading with another shelf.</span></a></li>
<li><a href="configuration.html"><strong>Configuration</strong><span>Every setting, with its default.</span></a></li>
</ul>

> [!NOTE]
> The screenshots show public-domain books, with covers drawn for the demo.
