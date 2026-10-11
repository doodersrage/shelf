---
title: Reading
description: The reader for EPUBs, PDFs, and comics; highlights and notes; read aloud; search inside books; OCR for scanned pages; and reading offline.
---

# Reading

Any book with an e-book opens in the reader from its page or its card in the library. The reader keeps your place for you, whatever device you open it on next.

## EPUBs

An EPUB reads chapter by chapter, with **Previous**, **Next**, and a list of its chapters. **Text settings** sets:

- **Text size**, from smaller to largest;
- **Line spacing**: snug, comfortable, or airy;
- **Reading width**: narrow, medium, or wide.

Your settings are kept for you, so every book opens the same way. A book's own styles are kept where they don't fight these, and anything active in a book (scripts, forms) is stripped, so an EPUB can never act on your shelf.

## PDFs

A PDF keeps its own layout, page by page. **Text size** zooms from fitting the width; the arrow keys and Page Up and Page Down turn pages. **Open the file itself** hands it to your browser's own viewer.

### Scanned PDFs

A scanned PDF's pages are pictures. When Tesseract and Poppler are installed on the server (the Docker image has them), Shelf reads each page's words in the background after the PDF is uploaded, so they can be selected, highlighted, and searched like any other text. A page says whether its words are its own, read by Shelf, still being read, or not readable. See [Running on a server](server.md#ocr-for-scanned-pdfs) for languages.

## Comics

A comic book archive (`.cbz`) reads a page at a time, in natural name order (page 2 before page 10), as large as the window allows. The arrow keys turn pages, and the next page loads ahead.

## Kindle files

A `.mobi`, `.azw3`, or `.azw` is turned into an EPUB as it comes in, when Calibre's `ebook-convert` is installed on the server. After that it is an ordinary EPUB. Files locked with DRM cannot be converted.

## Highlights and notes

Select a passage in an EPUB or a PDF, scanned pages included, and the passage appears beside the page with a box for a note. **Highlight** keeps it. Highlights are marked in the text; select one to change its note or remove it. Each chapter's or page's highlights are listed beneath it, and every highlight also appears on the book's page and under **Quotes & highlights**.

A highlight remembers the words around it, so it finds its place again even when the same words appear twice on a page.

### Exporting highlights and notes

Every book's quotes, highlights (under their chapter titles or pages), notes, and review download as Markdown, ready for Obsidian, Notion, or any folder of notes:

- one book, from the link under its highlights;
- every book with something written about it, as a zip, from **Quotes & highlights** or **Backup & restore**.

Each file starts with YAML front matter: title, author, ISBN, year, rating, status, and finish date.

## Read aloud

In an EPUB or a PDF, **Read aloud** speaks the chapter or page with your browser's own voices, a sentence or two at a time, and goes on into the next chapter or page by itself. Pause, resume, or stop at any point; turning a page by hand stops it too. Choose a speed and a voice; both are kept in that browser.

> [!NOTE]
> Nothing is sent to the server or anywhere else to read aloud. How it sounds depends on the voices your device has; phones and Macs usually have good ones.

## Search inside books

**Search inside books** finds a phrase in the words of your e-books and of books lent to you, scanned PDFs included once they have been read. Results come best match first, shown in context, and **Open here** takes you to that chapter or page. Case and accents don't matter (`cafe` finds `café`), and the last word can be partial.

## Reading offline

On a book with an e-book, **Keep for reading offline** saves the file in that browser. When the shelf can't be reached, any page opens the offline reader instead, which lists your kept books and reads them: EPUBs chapter by chapter, PDFs page by page, at your text size.

Offline, you can still:

- **keep your place**, which is sent back the next time the shelf answers; the shelf keeps whichever place is further;
- **see your highlights**, which come along when you keep a book;
- **highlight passages and remove highlights**, kept on the device and sent to the shelf when it answers.

A scanned PDF's OCR words are not kept offline, and comics aren't offered for offline reading. Audiobooks can be kept too; see [Listening offline](audiobooks.md#listening-offline).

> [!TIP]
> On a phone, add Shelf to the home screen from the browser's menu. It opens like an app, and the offline reader works from there too.

## Where you stopped

Your place is your own. A borrower has a place of their own in a book lent to them, separate from the owner's. KOReader and another shelf can move your place forward too; see [Devices and apps](devices.md).
