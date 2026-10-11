---
title: Bringing books in
nav: Bringing books in
description: Add books by hand, from files, from free public-domain catalogs, from Audiobookshelf, or from a Goodreads or StoryGraph export.
---

# Bringing books in

Books come in five ways, and Shelf keeps out what you already have.

A new shelf opens on a welcome with a card for each way below, so the first step is bringing what you already have.

## By hand

**Add a book** takes a title and author, or an ISBN that fills the rest from Open Library. If the same book is already on your shelf, the form says so. If another reader has it on an [open shelf](lending.md#open-shelves), the form says that too, with a button to ask to borrow it instead.

**Scan the barcode** reads the ISBN from the back of a book with a phone's camera, then looks the book up. Where Shelf is opened over `https://` (or on the computer it runs on), the camera shows live and reads the code as soon as it is in view. Over plain `http://` on your network, browsers keep the camera to themselves, so the button takes a photo of the barcode instead, which works just as well. The barcode is read in the browser; the picture never leaves the phone.

## From files

Under **Add a book**, **Or add from files** takes as many files as you like at once, up to 1 GB in total (`Uploads:MaxAudioMegabytes` changes it), and makes a book of each:

| File | Becomes a book named from |
| --- | --- |
| EPUB | Its title, author, ISBN, publisher, language, and cover. |
| PDF | Its title and author, when Poppler's `pdfinfo` is installed; otherwise its file name. |
| Comic (`.cbz`) | Its `ComicInfo.xml`: title or series and number, writer, and publisher. |
| Kindle (`.mobi`, `.azw3`, `.azw`) | Turned into an EPUB first, when Calibre is installed. |
| Audio (mp3, m4a, m4b, aac, ogg, opus, wav, flac) | Its album and artist tags. Tracks that share an album become one audiobook. |
| A zip of tracks | One audiobook. |

A file with nothing to say is named from its file name, with the author left for you to fill in. A file for a book you already typed in goes onto that book rather than making a second one.

You can also add a file to one book from its page, under **Files**: an e-book up to 80 MB, or an audiobook up to 1 GB, unless an admin has [set other limits](configuration.md#upload-limits).

### Files you already have

When you upload a file that is byte for byte the same as one on another of your books, Shelf asks first: **Keep both**, or **Don't add it**. The question is only about your own shelf, never other readers'. Before you upload, a book's **Files** panel also mentions when another reader has the file on an open shelf, so you can borrow it instead.

## From a folder

Shelf can watch a folder and add whatever lands in it: books saved by a downloader, or copied over a network share from another computer. Set `Import:Folder` (in Docker, mount a folder at `/import` and set `Import__Folder=/import`; set `PUID` and `PGID` to the folder's owner, or give user 1654 write access, since Shelf moves files out of it once they are added), then:

- Each reader has a folder inside it named after them. **Make a folder for each reader** on **Readers** creates them. Files at the top of the import folder go to the first admin.
- Each e-book, PDF, comic, Kindle file, or zip of tracks there becomes a book, named from what the file says. A folder of audio files becomes one audiobook, its tracks in the order of their names.
- Shelf looks every minute, and takes something only once it has stopped changing, so a file still being copied is left until it is whole. Hidden files and `.part` downloads are left alone.
- Once added, a file moves into `.imported` in the same folder; one Shelf cannot take moves into `.not-added`. Nothing is deleted unless `Import:AfterImport` is `delete`. A file already on another of your books is not added twice.
- Each addition appears in the reader's activity on **Account**.

## Free public-domain books

**Free books** finds public-domain books on [Project Gutenberg](https://www.gutenberg.org), for e-books, and [LibriVox](https://librivox.org), for audiobooks read by volunteers.

- **Browse** without searching: it opens on Gutenberg's most read books, or LibriVox's newest recordings, and **Browse** picks one of Gutenberg's categories (classics, mystery, science fiction, history, and sixty more) or a LibriVox genre. **Show more** brings the next page.
- **Search** by title or author.
- **About this book** opens the details under a book: Gutenberg's summary, subjects, reading level, download count, and when it went online; or LibriVox's description, genres, length, chapters, and translators, with a link to the book's own page.

**Add to shelf** brings one in with its file, tagged `public domain`, with a note of where it came from. Downloads run in the background with their progress on the page, since a long recording can be several hundred megabytes.

> [!NOTE]
> Public domain differs by country. Project Gutenberg follows United States law; check what applies where you live. The shelf has to reach gutenberg.org, librivox.org, and archive.org, and `FreeBooks:Enabled` turns the page off.

## From Audiobookshelf

From **Backup & restore**, **Connect to Audiobookshelf** copies books from an [Audiobookshelf](https://www.audiobookshelf.org) server (version 2):

1. **Connect** with the address you open Audiobookshelf at, and either an API key or a user name and password. Make a key in Audiobookshelf under **Settings, API Keys**, and turn it on. The key or password is used for that import only and never saved.
2. **Choose** a library and the books to bring. A large library is listed with a count as it comes in. Books already on your shelf are marked and left unticked. **Find in the list** and **Show** (new to your shelf, audiobooks, or e-books) narrow a long list, and **Choose these** or **Leave these out** apply to what is shown.
3. **Bring** them. The import runs in the background, one book at a time, so you can leave the page or close the browser. The page shows how many are done, the book in hand, about how long the rest will take, the last few brought, and any that couldn't be. Every other page shows a line with how far it has got, until it finishes. **Stop after this book** ends it early; books already brought stay.

A Calibre import, from **Bring a Calibre library**, shows its progress the same way.

Each book comes across with its audio tracks in Audiobookshelf's order, its e-book, cover, series and number, narrator, and genres as tags. Tick **Bring where I stopped** to bring your listening place, your place in the e-book, and which books you finished.

### Keeping in step

Connected with an API key, tick **Keep in step** before bringing books, and Shelf looks at that library once a day from then on, bringing across books added to it since. It knows each Audiobookshelf book it has brought, so none comes twice, and a book already on your shelf by title and author is left alone. The Audiobookshelf page shows when it last looked and what it found, with **Check now** and **Stop keeping in step**. Shelf keeps the API key for this, sealed with its own keys, until you stop; a password can't be used, since Audiobookshelf's sign-in by password doesn't last. One library is kept in step for each reader. `AbsSync:Enabled` set to `false` turns it off for the whole shelf.

> [!TIP]
> The Audiobookshelf user needs permission to download (**Can Download** in its Users settings). Nothing on the Audiobookshelf server changes. If it is served under a path, include it in the address, such as `https://example.org/audiobookshelf`.

## From Calibre

An admin can bring in a [Calibre](https://calibre-ebook.com) library from the server's own disk: **Backup & restore**, then **Open a Calibre library**. Give the folder that holds `metadata.db` (in Docker, mount the library into the container first, read-only is fine, and give the path inside it), choose the books, and **Bring** them to your shelf.

- Each book brings one file: an EPUB if it has one, otherwise a PDF, a comic, or a Kindle file (converted when Calibre's converter is on the server), or an audiobook.
- Its cover, series and number, tags, publisher, rating, ISBN, language, year, and Calibre's description (as notes) come with it. Several authors are joined as Calibre shows them, with `&`.
- A book with no file Shelf takes still comes in as a catalog entry.
- The library is only read; nothing in it changes. A book already on your shelf is left alone, so bringing the same library again adds only what is new.

`Calibre:Library` fills in the folder ahead of time.

## From Goodreads or StoryGraph

Export your library as CSV from either service, then upload it on **Backup & restore**. Shelves become statuses and tags, ratings, reviews, and dates come along, and books already on your shelf are skipped.

## Finding new books

**Find new books**, under **Browse** (and linked from **Authors**), searches [Open Library](https://openlibrary.org) for a book you don't have yet, by its title, its author, or its ISBN. Each book found shows its cover, author, and year, and those on your shelf already say so. **Want to read** puts it on your want list with what Open Library knows of it (ISBN, pages, cover, year); with finding and fetching set up (below), **Fetch it** then takes you to the book's page to search your indexers. The page's address keeps the search, to come back to or share.

### Following an author

Follow an author from **Authors**, from a book found on **Find new books**, or by name at the foot of that page. Once a day Shelf asks Open Library for each author's newest books, sending only the name. A book published since you followed them, and not on your shelf, is offered under **New from authors you follow**, and the sidebar's **Find new books** shows how many are waiting: **Want to read** adds it, **Not for me** lets it go, and either way the same book is never offered twice. **Look now** asks straight away.

With finding and fetching set up, ticking **Want and fetch new books straight away** beside an author skips the offer: a new book goes onto the want list and its best match is fetched at once, as **Wanted** would. **Stop following** ends it.

## Finding and fetching books

Like Sonarr does for shows, Shelf can search your indexers for a book, send the release to your download client, and bring the finished file onto the book that wanted it. It works with:

- **Indexers**: [Jackett](https://github.com/Jackett/Jackett) or [Prowlarr](https://prowlarr.com), searched in the book (7000) and audiobook (3030) categories;
- **Torrents**: [Deluge](https://deluge-torrent.org) (its web interface) or [qBittorrent](https://www.qbittorrent.org);
- **Usenet**, if your indexers give it: [SABnzbd](https://sabnzbd.org).

What you download, and whether you may, is between you and your indexers.

### Setting it up

An admin sets it up once for the whole shelf, under **Settings**, **Downloads**:

1. **Indexers**: choose Jackett or Prowlarr, give its address (such as `http://jackett:9117`) and API key (at the top of Jackett's dashboard; under Settings, General in Prowlarr), and press **Test**.
2. **Torrents**: choose Deluge or qBittorrent, give the address of its web interface (such as `http://deluge:8112`) and its password (and qBittorrent's user name), and **Test**. Deluge's web interface needs to be connected to its daemon; Shelf connects it to the first one it knows if it isn't. With Deluge's Label plugin on, Shelf's downloads are labelled with the **Category** (`shelf` by default).
3. **Usenet** (optional): SABnzbd's address and API key.
4. **Where finished downloads are**: the folder the download client saves to, named as the client names it and as Shelf sees it. In Docker these usually differ: if Deluge saves to `/downloads` inside its container, and the same folder is mounted into Shelf at `/data/downloads`, give `/downloads` and `/data/downloads`. Shelf needs to be able to read that folder; it copies the file it wants, so torrents go on seeding.
5. **What to fetch**: the e-book formats in the order you like them (`epub,azw3,mobi,pdf`), the audiobook formats (`m4b,mp3`), and the fewest seeders a torrent may have.
6. Tick **Find and fetch books**, and **Save**. Passwords and keys are kept sealed with Shelf's own keys; leave a field empty to keep the one saved.

```yaml
# Shelf beside Deluge, sharing the downloads folder
services:
  shelf:
    image: ghcr.io/doodersrage/shelf:latest
    volumes:
      - ./shelf:/data
      - ./downloads:/data/downloads:ro
  deluge:
    image: lscr.io/linuxserver/deluge
    volumes:
      - ./downloads:/downloads
```

### Fetching a book

On a book's page, **Search your indexers** looks for an e-book or an audiobook of it. The releases come best first: those that are this book (its title words and author's last name are in the release's name, and it is the kind of file asked for), in the format you like most, with the most seeders. Each that isn't a match says why. **Fetch** sends it to the download client; the page follows it there and, once it finishes, the file is on the book.

**Wanted**, under **Browse**, lists your want-to-read books with no file yet, what is being fetched, and what was fetched lately. **Look for all of them now** searches for each and fetches the best match, a few seconds apart, up to the number set under Downloads; **Activity** shows it going. Ticking **Every day, look for want-to-read books** under Downloads does that once a day for every reader. A book already being fetched isn't searched again.

Children's accounts can't search or fetch.

## From a Shelf backup

A full backup from another shelf, or from this one, restores on **Backup & restore**, files and covers included. Books already on the shelf are skipped. See [Backups](backups.md).
