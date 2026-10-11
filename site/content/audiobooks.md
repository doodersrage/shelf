---
title: Audiobooks
description: The audiobook player, chapters, bookmarks, speed, the sleep timer, and the files Shelf takes.
---

# Audiobooks

A book with an audiobook gets **Listen to this audiobook** on its page, and plays in the browser on any device.

## Adding an audiobook

On a book's page, under **Files**, upload one audio file or a zip of tracks, up to 1 GB: mp3, m4a, m4b, aac, ogg, opus, wav, or flac. A zip's tracks play in name order. You can also make audiobooks from files in bulk, or bring them from Audiobookshelf or LibriVox; see [Bringing books in](adding.md).

If the files carry cover art (an `.m4b`'s cover, or an MP3's front-cover picture) and the book has no cover yet, the art becomes its cover.

## The player

![The audiobook player: the cover, the chapter, the controls, and the book's progress with a mark at each chapter](assets/images/listen.png)

- **One recording.** A book in many tracks plays as one, straight from one track into the next, with one timeline for the whole book. Shelf reads each track's length from the file (MP3, M4B and M4A, Ogg and Opus, FLAC, WAV), and the browser fills in any it cannot.
- **Back where you stopped.** Playback resumes from your place, on any device; the place is kept every few seconds as you listen, and when you pause or leave.
- **Chapters.** The chapter you're in, its time gone and left, and a slider to move within it. Below, a bar for the whole book with a mark at each chapter, its time left at your speed, and how far through you are. The **Chapters** list (or **Tracks**, for a recording without chapter marks) shows each one's start and goes to it. **Previous chapter** goes back to the start of the chapter first, then to the one before.
- **Skips.** Back 15 and ahead 30 seconds, or any of 5 to 60 seconds, under **Player settings**.
- **Speed.** From 0.5× to 3× in steps of 0.05 with **−** and **+**, or a common speed from **Speed**. Kept for you.
- **Sleep timer.** Pause after 5 to 90 minutes of listening, or at the end of the chapter, with **5 more minutes** when you are not sleepy yet. The minutes count only while the book plays.
- **Volume,** kept on that device.
- **Keys.** Space plays and pauses, the arrow keys skip, Shift and an arrow move by chapter, `[` and `]` change the speed, and M mutes.
- **Bookmarks.** **Bookmark this moment** keeps where you are, with a note if you like. Each bookmark shows its chapter or track and time, and **Go there** goes back to it.
- **Lock screen and headphones.** While a book plays, a phone's lock screen and notifications, headphones, a car, or a smartwatch show the chapter, the book, its author, and cover, with the whole book as the scrubber. Their buttons play and pause, skip, scrub, and go to the previous or next chapter.

### Listening while you browse

![Stats, with the book playing in the mini player at the foot of the page](assets/images/mini-player.png)

Leave the player and the book plays on in a bar at the foot of every page, with its cover, chapter, play and pause, skips, and the time left in the chapter; its title opens the full player again, and **×** closes it. After a reload it waits there, paused where you stopped. **Play while you browse** on a book's page, and the play button on a book being read in the library, start a book there without leaving the page.

A borrower listening to a book lent to them has their own place and bookmarks.

## Audiobooks and e-books together

A book can have both an e-book and an audiobook. Each keeps its own place. Bringing a book from Audiobookshelf that has both brings both onto one book.

### Switching between them

With an EPUB and an audiobook on the same book, the player has **Continue in the e-book**, and the reader has **Continue in the audiobook**. Each opens the other at the same point: the chapter, and how far through it.

Shelf finds the point by the chapters' names first. When the recording's chapters (or its tracks) are named like the book's (*Chapter 7*, *Seven*, *03 - Chapter VII*), those are lined up with each other. Between them, and in a recording whose parts are named nothing useful, it goes by how far through the text or the recording you are, leaving out the e-book's cover, title page, and contents. It does not listen to the recording, so the point is usually within a page or two; a recording with an introduction the book lacks, or an abridged one, can land further off. PDFs and comics do not switch.
