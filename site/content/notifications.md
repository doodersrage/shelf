---
title: Notifications
description: Hear about loans, asks to borrow, new books in your series, and your book clubs, on your phone or computer, or by ntfy or Gotify.
---

# Notifications

Shelf can tell you when something needs you, without waiting for you to open it. Set it up under **Account**, in **Notifications**.

## What you hear about

Tick the kinds you want:

- **Loans:** someone lends you a book, a borrower returns one of yours, and once a day, any loan due within three days or overdue.
- **Asks to borrow** one of your books, as soon as they're made.
- **New books in your series,** when the daily look finds some (with series alerts on, under **Series**).
- **Your book clubs:** new posts in the discussion, and being added to a club.

A notification opens the page it's about: **Loans**, the book, **Series**, or the club.

## On this device

**Notify me on this device** asks the browser's permission, then shows Shelf's notifications like any app's, even with Shelf closed. Do it on each device you want them on; the list below shows them all, and **Remove** stops one.

- **Android, Windows, macOS, and Linux:** works in Chrome, Edge, Firefox, and Safari on a Mac.
- **iPhone and iPad:** add Shelf to the home screen first (Share, then **Add to Home Screen**), open it from there, and turn notifications on in that app. Safari tabs can't show them.

Messages go through your browser's own push service (Google's, Mozilla's, Apple's, or Microsoft's), sealed so only your browser can read them.

## ntfy

[ntfy](https://ntfy.sh) sends notifications to its phone app and desktop. Install the app, subscribe to a topic, and give Shelf the topic's whole address, such as `https://ntfy.sh/a-name-nobody-would-guess`. On ntfy.sh anyone who knows a topic's name can read it, so make it hard to guess, or use your own ntfy server with an access token.

## Gotify

For a [Gotify](https://gotify.net) server of your own, make an application on it, and give Shelf the server's address and the application's token.

**Send a test notification** sends one by every way you set up, to check.

## For admins

Notifications to browsers are signed with a key Shelf makes the first time it starts, kept beside the keys that seal sign-ins. A restore that brings those keys back keeps every device subscribed. Settings are under [Configuration](configuration.md#notifications-and-webhooks).
