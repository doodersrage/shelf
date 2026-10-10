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
