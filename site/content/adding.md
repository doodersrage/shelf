---
title: Bringing books in
nav: Bringing books in
description: Add books by hand, from files, from free public-domain catalogs, from Audiobookshelf, or from a Goodreads or StoryGraph export.
---

# Bringing books in

Books come in five ways, and Shelf keeps out what you already have.

## By hand

**Add a book** takes a title and author, or an ISBN that fills the rest from Open Library. If the same book is already on your shelf, the form says so. If another reader has it on an [open shelf](lending.md#open-shelves), the form says that too, with a button to ask to borrow it instead.

**Scan the barcode** reads the ISBN from the back of a book with a phone's camera, then looks the book up. Where Shelf is opened over `https://` (or on the computer it runs on), the camera shows live and reads the code as soon as it is in view. Over plain `http://` on your network, browsers keep the camera to themselves, so the button takes a photo of the barcode instead, which works just as well. The barcode is read in the browser; the picture never leaves the phone.

## From files

Under **Add a book**, **Or add from files** takes as many files as you like at once, up to 1 GB in total, and makes a book of each:

| File | Becomes a book named from |
| --- | --- |
| EPUB | Its title, author, ISBN, publisher, language, and cover. |
| PDF | Its title and author, when Poppler's `pdfinfo` is installed; otherwise its file name. |
| Comic (`.cbz`) | Its `ComicInfo.xml`: title or series and number, writer, and publisher. |
| Kindle (`.mobi`, `.azw3`, `.azw`) | Turned into an EPUB first, when Calibre is installed. |
| Audio (mp3, m4a, m4b, aac, ogg, opus, wav, flac) | Its album and artist tags. Tracks that share an album become one audiobook. |
| A zip of tracks | One audiobook. |

A file with nothing to say is named from its file name, with the author left for you to fill in. A file for a book you already typed in goes onto that book rather than making a second one.

You can also add a file to one book from its page, under **Files**: an e-book up to 80 MB, or an audiobook up to 1 GB.

### Files you already have

When you upload a file that is byte for byte the same as one on another of your books, Shelf asks first: **Keep both**, or **Don't add it**. The question is only about your own shelf, never other readers'. Before you upload, a book's **Files** panel also mentions when another reader has the file on an open shelf, so you can borrow it instead.

## From a folder

Shelf can watch a folder and add whatever lands in it: books saved by a downloader, or copied over a network share from another computer. Set `Import:Folder` (in Docker, mount a folder at `/import` and set `Import__Folder=/import`; Shelf runs as user 1654 there, so give it write access, for instance with `chown 1654 /srv/books-inbox`), then:

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
2. **Choose** a library and the books to bring. Books already on your shelf are marked and left unticked.
3. **Bring** them. The import runs in the background, one book at a time, so you can leave the page.

Each book comes across with its audio tracks in Audiobookshelf's order, its e-book, cover, series and number, narrator, and genres as tags. Tick **Bring where I stopped** to bring your listening place, your place in the e-book, and which books you finished.

> [!TIP]
> The Audiobookshelf user needs permission to download (**Can Download** in its Users settings). Nothing on the Audiobookshelf server changes. If it is served under a path, include it in the address, such as `https://example.org/audiobookshelf`.

## From Goodreads or StoryGraph

Export your library as CSV from either service, then upload it on **Backup & restore**. Shelves become statuses and tags, ratings, reviews, and dates come along, and books already on your shelf are skipped.

## From a Shelf backup

A full backup from another shelf, or from this one, restores on **Backup & restore**, files and covers included. Books already on the shelf are skipped. See [Backups](backups.md).
