---
title: Readers and lending
description: Accounts for everyone in your household, lending between readers, open shelves and asks to borrow, and reminders.
---

# Readers and lending

One Shelf serves everyone you share it with. Each reader signs in to a shelf of their own, and no reader sees another's books, notes, highlights, or reading log unless they choose to share.

## Readers

Each person makes an account with a name and a password of at least eight characters; the first account is the admin. Each reader's settings are their own: language, region for dates, text size, read-aloud voice, audiobook speed, and the yearly goal. See [Accounts and security](security.md) for passkeys, two-step sign-in, and what an admin can do.

## Languages

Shelf's pages are in English, Spanish, French, and German. Each reader sees the language their browser asks for, unless they pick one under **Language and region** on **Account**. Emails, the reset link and the loan reminders, go out in the language the reader picked. Dates and numbers follow the region separately, so a reader can have the pages in English and the dates written the German way.

The translations were made by machine and checked for consistency, but have not yet been read by native speakers. Corrections are welcome: [Translating Shelf](https://github.com/doodersrage/shelf/blob/main/docs/translating.md) shows where the words are and the terms each language uses, or [open a translation issue](https://github.com/doodersrage/shelf/issues/new?template=translation.yml) with the sentence and what it should say. A test fails when a sentence on a page is missing from a language, so a new sentence can't be left untranslated by accident.

## Lending to another reader

On a book's page, under **Lending**, choose a reader and, if you like, a date it's due back, then **Lend**. The book stays on your shelf, marked as lent to them, and appears under **Borrowed** on their **Loans** page.

While it's out, the borrower can:

- **read its e-book and play its audiobook** from **Loans**, with a place of their own;
- **highlight passages and keep bookmarks** of their own, which stay theirs;
- **search inside it** along with their own books;
- **give it back** when they're done.

Your own place, highlights, and notes stay private and unmoved. Either of you can end the loan: you with **Mark returned**, they with **Give it back**.

## Lending to someone else

For a friend who isn't a reader on your shelf, type their name in the book's **Loaned to** field, with a due date if you like. It shows on **Loans** like any other loan.

## Open shelves

From **Account**, a reader can **open their shelf** to the others. The other readers then see its books under **Shelves**: titles, authors, covers, and whether each has an e-book or audiobook, but never notes, reviews, quotes, highlights, or where the owner stopped.

On an open shelf, **Ask to borrow** sends an ask. Asks wait on the owner's **Loans** page until they lend the book or decline, and the reader who asked can take an ask back.

Shelf also points out open shelves where they help:

- adding a book that someone has on an open shelf offers to ask to borrow it instead;
- a book's **Files** panel mentions when an open copy has an e-book or audiobook yours lacks, before you upload your own.

Only shelves their owners opened are ever mentioned.

## Reminders

A notice under the header points to **Loans** when:

- a loan of yours is overdue;
- a book lent to you is due within three days;
- someone has asked to borrow from you.

With [email set up](configuration.md#email), a reader can add their address on **Account** to get these as one email a day, and to reset a forgotten password from the sign-in page.
