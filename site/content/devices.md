---
title: Devices and apps
description: Read on KOReader and other e-readers through OPDS, keep your place in step with KOReader, trade with another shelf, and use Shelf on a phone.
---

# Devices and apps

Shelf talks to e-reader apps and to other shelves, and works on a phone like an app of its own. All of this is set up on **Devices**.

## The device key

E-reader catalogs and other shelves sign in with a **device key**, not your password. Make one on **Devices**; it is shown once, so copy it then. Making a new key stops the old one working. A key reaches only your e-books and audiobooks, never your account.

## E-reader apps (OPDS)

Shelf has an OPDS catalog, the format many reading apps browse, such as KOReader, Moon+ Reader, and Thorium. In the app, add a catalog with:

| Setting | What to enter |
| --- | --- |
| Address | `https://<your shelf>/opds` |
| User name | anything |
| Password | your device key |

The catalog lists your e-books by status, and the books lent to you, ready to download.

## Send to Kindle

When the shelf has a mail server (see [Configuration](configuration.md#email)), each reader can send an e-book to their Kindle by email, the way Amazon's own Send to Kindle works:

1. On **Account**, under **Send to Kindle**, enter your Kindle's address, such as `name_12@kindle.com`. It is in your Amazon account under **Devices**, or in the Kindle's settings.
2. At Amazon, add the shelf's sending address (`Email:From`, shown on **Account**) to the **Approved Personal Document E-mail List**. Amazon quietly drops mail from anyone else.
3. On a book's page, **Send to Kindle** emails its file. It arrives on the Kindle within a few minutes.

Amazon takes EPUBs and PDFs by email, up to 50 MB; comics and Kindle-format files are not sent. Only your own books can be sent: a lent book's copy on a Kindle would outlast the loan.

## Keeping your place in step with KOReader

KOReader's **progress sync** sends the page you reach to Shelf, and fetches the one you reached in Shelf's reader:

1. On **Devices**, choose **Make a KOReader password**. It is shown once.
2. In KOReader, open a book, then **Tools, Progress sync, Custom sync server**, and enter `https://<your shelf>/kosync`.
3. Sign in with your reader name and that password.

A book matches when KOReader has the same file Shelf has, which it does when it came from Shelf's catalog. Reading further on the device moves Shelf's place forward; reading further in Shelf sends the device to the start of that chapter, or that page of a PDF. Going back on the device never pulls Shelf's place back. **Stop syncing with KOReader** turns the password off.

> [!NOTE]
> KOReader sends its password as a hash. Shelf keeps only a hash of that, separate from your sign-in password, so the KOReader password can't sign in to Shelf itself.

### Highlights from KOReader

Progress sync carries your place, not your highlights. To bring highlights made in KOReader:

1. In KOReader, open the menu, then **Tools**, **Export highlights**. Choose **JSON** as the format, then export the current book, or all books.
2. Copy the `.json` file it writes to your computer or phone (KOReader can also share it).
3. On **Devices**, under **Bring highlights from KOReader**, choose the file.

Each book is matched by its title, or else by its file name, among the books on your shelf (or lent to you) that have an e-book. Each highlight goes to its chapter, or for a PDF its page, with its note and the time you made it. KOReader's position picks the chapter, and Shelf checks the words are there, so a highlight still lands right in a different edition. Highlights already on Shelf are not added twice, so exporting everything again later is fine.

## Highlights from a Kindle

A Kindle keeps every highlight and note you make in one file, **My Clippings.txt**, in its `documents` folder. Connect the Kindle to a computer by its cable, then on **Devices**, under **Bring highlights from a Kindle**, choose that file.

Each clipping's book is found on your shelf by title and author (a Kindle writes "Le Guin, Ursula K."; Shelf knows that is Ursula K. Le Guin, and leaves off a subtitle or series the Kindle adds to the title). On a book with an EPUB, a highlight goes to the chapter that has its words, with the note you made at the same place, and shows in the reader. On any other book, a paper copy say, it becomes a quote, with its page when the Kindle gave one. Bookmarks are left out, and a clipping already on your shelf is not added twice, so you can bring the same file again later. The file can be in any of the Kindle's languages.

## Trading with another shelf

Two shelves, yours at home and one at a friend's, or yours on two servers, can trade e-books and audiobooks:

1. Each side makes a device key on **Devices**.
2. Each enters the other shelf's address and the key it made for them, under **Another shelf**.
3. **Sync now** brings across the files the other shelf has and you don't, and sends yours the other way.

Books are matched by title and author or ISBN. The furthest place in each book is kept on both sides, and notes on passages come along with the e-book.

> [!WARNING]
> The key travels with every request, so use an `https://` address for a shelf outside your own network.

## On a phone

Every page works on a small screen, with a bottom bar for the library, reading, adding, loans, and stats. Add Shelf to the home screen from the browser's menu and it opens like an app. Books you keep for reading offline open even with no connection; see [Reading offline](reading.md#reading-offline).

### Sharing a book into Shelf

Once Shelf is on an Android phone's home screen, it appears in the phone's **Share** menu. Share a book from a bookshop, Goodreads, Open Library, or anywhere else, and Shelf opens the **Add a book** form: with the ISBN when the page or its link has one, looked up straight away, or else with the title (and the author, when the page names one), ready to look up. Opening `/?add=1&isbn=…` or `/?add=1&title=…&author=…` does the same from a bookmark or a script. (iPhones don't let web apps into the Share menu yet.)
