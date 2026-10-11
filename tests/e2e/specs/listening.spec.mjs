import { test, expect } from "@playwright/test";
import { createBook, noProblems, ready, signUp, unique, upload } from "../helpers.mjs";

// What the player says about itself: where it is, how fast, and whether it plays.
const player = (page) => page.evaluate(() => window.shelfPlayer.now());

test("an audiobook keeps its speed, shows its chapters, and keeps a bookmark", async ({ browser }) => {
  const page = await signUp(browser, unique("Listener"));
  const book = await createBook(page, { title: "A Chaptered Recording", author: "Someone", status: "Reading" });
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");

  await page.goto(`/library/${book.id}/listen`);
  await ready(page);
  await expect(page.locator(".listen-chapter")).toHaveText("Opening");
  await expect(page.locator(".listen [data-show=chapter-number]")).toHaveText("Chapter 1 of 3");
  await page.locator("summary", { hasText: "Speed" }).click();
  await page.click('[data-player=rate][data-rate="1.5"]');
  await expect.poll(async () => (await player(page)).rate).toBe(1.5);
  await expect(page.locator(".speed output")).toHaveText("1.5×");
  await page.click('[data-player=faster]');
  await expect(page.locator(".speed output")).toHaveText("1.55×");
  await page.click('[data-player=slower]');

  await page.getByRole("button", { name: "Next chapter" }).click();
  await expect(page.locator(".listen-chapter")).toHaveText("The Middle Part");
  await expect(page.locator(".chapter-list .current")).toContainText("The Middle Part");
  await expect.poll(async () => Math.round((await player(page)).seconds)).toBe(4);

  await page.fill('input[maxlength="500"]', "Where the middle starts");
  await page.click('button:text-is("Bookmark this moment")');
  await expect(page.locator("ul.quotes li")).toContainText("The Middle Part · 0:04");
  await expect(page.locator("ul.quotes li")).toContainText("Where the middle starts");

  // A chapter from the list, and back to the bookmark.
  await page.locator(".chapter-list button", { hasText: "Ending" }).click();
  await expect.poll(async () => Math.round((await player(page)).seconds)).toBe(9);
  await page.click('button:text-is("Go there")');
  await expect.poll(async () => Math.round((await player(page)).seconds)).toBe(4);

  await page.locator("summary", { hasText: "Sleep timer" }).click();
  await page.click('[data-player=sleep][data-sleep="15"]');
  await expect(page.locator(".sleep-left")).toHaveText("15:00");

  // The place and the speed are kept on the shelf.
  await expect.poll(async () => (await (await page.request.get(`/books/${book.id}/place`)).json()).audioSeconds).toBe(4);
  await page.reload();
  await ready(page);
  await expect(page.locator(".speed output")).toHaveText("1.5×");
  await expect(page.locator(".listen-chapter")).toHaveText("The Middle Part");
  noProblems(page);
});

test("the book plays on in the mini player while the reader moves around the shelf", async ({ browser }) => {
  const page = await signUp(browser, unique("Wanderer"));
  const book = await createBook(page, { title: "A Walking Recording", author: "Someone", status: "Reading" });
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");

  await page.goto(`/library/${book.id}/listen`);
  await ready(page);
  await expect(page.locator("#mini-player")).toBeHidden();
  await page.locator(".listen [data-player=toggle]").click();
  await expect.poll(async () => (await player(page)).playing).toBe(true);

  await page.evaluate(() => { window.stillHere = true; });
  await page.getByRole("link", { name: "Library", exact: true }).first().click();
  await page.waitForURL((url) => url.pathname === "/");
  await ready(page);
  const mini = page.locator("#mini-player");
  await expect(mini).toBeVisible();
  await expect(mini).toContainText("A Walking Recording");
  await page.getByRole("link", { name: "Stats" }).first().click();
  await page.waitForURL("**/stats");
  expect(await page.evaluate(() => window.stillHere)).toBe(true);
  await expect.poll(async () => (await player(page)).playing).toBe(true);

  await mini.getByRole("button", { name: "Pause", exact: true }).click();
  await expect.poll(async () => (await player(page)).playing).toBe(false);
  await expect(mini.getByRole("button", { name: "Play", exact: true })).toBeVisible();

  // After a reload the book waits there, paused, where it stopped.
  await page.reload();
  await ready(page);
  await expect(mini).toBeVisible();
  await expect(mini).toContainText("A Walking Recording");
  await mini.getByRole("button", { name: "Close the player" }).click();
  await expect(mini).toBeHidden();
  await page.reload();
  await ready(page);
  await expect(mini).toBeHidden();
  noProblems(page);
});

test("the lock screen shows the chapter, the book, and its author, and its buttons drive the player", async ({ browser }) => {
  const page = await signUp(browser, unique("Commuter"));
  // Keeps the handlers the page gives the media session, so the test can press them as a lock screen would.
  await page.addInitScript(() => {
    window.mediaHandlers = {};
    const original = navigator.mediaSession.setActionHandler.bind(navigator.mediaSession);
    navigator.mediaSession.setActionHandler = (action, handler) => {
      window.mediaHandlers[action] = handler;
      original(action, handler);
    };
  });
  const book = await createBook(page, { title: "A Chaptered Recording", author: "Someone Reads", status: "Reading" });
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");

  await page.goto(`/library/${book.id}/listen`);
  await ready(page);
  const metadata = () => page.evaluate(() => {
    const now = navigator.mediaSession.metadata;
    return now ? `${now.title} | ${now.artist} | ${now.album}` : null;
  });
  await expect.poll(metadata).toBe("Opening | Someone Reads | A Chaptered Recording");

  await page.evaluate(() => window.mediaHandlers.nexttrack());
  await expect(page.locator(".listen-chapter")).toHaveText("The Middle Part");
  await expect.poll(metadata).toBe("The Middle Part | Someone Reads | A Chaptered Recording");
  await expect.poll(async () => Math.round((await player(page)).seconds)).toBe(4);

  await page.evaluate(() => window.mediaHandlers.seekforward({ seekOffset: 2 }));
  await expect.poll(async () => Math.round((await player(page)).seconds)).toBe(6);
  await page.evaluate(() => window.mediaHandlers.previoustrack());
  await expect(page.locator(".listen-chapter")).toHaveText("Opening");

  // The lock screen stays while the book is in the mini player, and goes with it.
  await page.getByRole("link", { name: "Library", exact: true }).first().click();
  await page.waitForURL((url) => url.pathname === "/");
  await expect.poll(metadata).toBe("Opening | Someone Reads | A Chaptered Recording");
  await page.locator("#mini-player").getByRole("button", { name: "Close the player" }).click();
  await expect.poll(metadata).toBeNull();
  noProblems(page);
});

test("a book in both formats carries the place from the audiobook to the e-book and back", async ({ browser }) => {
  const page = await signUp(browser, unique("Switcher"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");

  await page.goto(`/library/${book.id}/listen`);
  await ready(page);
  await expect.poll(async () => (await player(page)).bookId).toBe(book.id);
  await page.evaluate(() => window.shelfPlayer.seek(6));
  await expect.poll(async () => Math.round((await player(page)).seconds)).toBe(6);

  // Six seconds in, nearly half way through the recording, is about half way through the first chapter's text.
  await Promise.all([page.waitForURL(/\/read\?chapter=0&at=0\.5\d*$/), page.click('button:text-is("Continue in the e-book")')]);
  await ready(page);
  await expect(page.locator("iframe.reader-frame")).toHaveAttribute("src", /chapters\/0\?t=0&at=0\.5\d*$/);
  // On a narrow screen the chapter runs long, and opens part way down.
  await page.setViewportSize({ width: 360, height: 640 });
  await page.reload();
  await ready(page);
  const chapter = () => page.frames().find((frame) => frame.url().includes("/ebook/chapters/0"));
  await expect.poll(() => chapter()?.evaluate(() => window.scrollY > 0 && window.scrollY < document.documentElement.scrollHeight - window.innerHeight)).toBe(true);

  // And back again to the same moment.
  await Promise.all([page.waitForURL(/\/listen\?part=1&at=0\.4/), page.click('button:text-is("Continue in the audiobook")')]);
  await ready(page);
  await expect.poll(async () => Math.round((await player(page)).seconds)).toBe(6);
  await expect(page.locator(".listen-chapter")).toHaveText("The Middle Part");
  noProblems(page);
});

test("a book of several tracks plays as one recording, from one track into the next", async ({ browser }) => {
  const page = await signUp(browser, unique("Trackwise"));
  const book = await createBook(page, { title: "Three Tracks", author: "Someone", status: "Reading" });
  await upload(page, book.id, "audio", "three-tracks.zip", "application/zip");

  await page.goto(`/library/${book.id}/listen`);
  await ready(page);
  // The shelf read each track's length, so the whole book's is known before a note plays.
  await expect(page.locator(".listen-book")).toContainText("/ 0:12");
  await expect(page.locator(".chapter-list li")).toHaveCount(3);
  await expect(page.locator(".chapter-list li").nth(2)).toContainText("0:07");

  await page.getByRole("button", { name: "Next chapter" }).click();
  await expect.poll(async () => (await player(page)).track).toBe(1);
  await expect.poll(async () => Math.round((await player(page)).bookSeconds)).toBe(3);

  // Near the end of the second track, playing carries straight on into the third.
  await page.evaluate(() => window.shelfPlayer.seek(6.5));
  await page.locator(".listen [data-player=toggle]").click();
  await expect.poll(async () => (await player(page)).track, { timeout: 10_000 }).toBe(2);
  await expect.poll(async () => (await player(page)).playing).toBe(true);
  await expect(page.locator(".chapter-list .current")).toHaveCount(1);
  await page.locator(".listen [data-player=toggle]").click();
  noProblems(page);
});

test("Play on a book's page or its card starts it in the mini player, and the page stays", async ({ browser }) => {
  const page = await signUp(browser, unique("Browser"));
  const book = await createBook(page, { title: "Played From Its Card", author: "Someone", status: "Reading" });
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");

  await page.goto("/");
  await ready(page);
  await page.getByRole("button", { name: "Play Played From Its Card" }).first().click();
  const mini = page.locator("#mini-player");
  await expect(mini).toContainText("Played From Its Card");
  await expect.poll(async () => (await player(page)).playing).toBe(true);
  expect(new URL(page.url()).pathname).toBe("/");
  await mini.getByRole("button", { name: "Pause", exact: true }).click();

  await page.goto(`/library/${book.id}`);
  await ready(page);
  await page.getByRole("button", { name: "Play while you browse" }).click();
  await expect.poll(async () => (await player(page)).playing).toBe(true);
  await mini.getByRole("button", { name: "Close the player" }).click();
  noProblems(page);
});
