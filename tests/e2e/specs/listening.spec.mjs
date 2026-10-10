import { test, expect } from "@playwright/test";
import { createBook, noProblems, ready, signUp, unique, upload } from "../helpers.mjs";

test("an audiobook keeps its speed, shows its chapters, and keeps a bookmark", async ({ browser }) => {
  const page = await signUp(browser, unique("Listener"));
  const book = await createBook(page, { title: "A Chaptered Recording", author: "Someone", status: "Reading" });
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");

  await page.goto(`/library/${book.id}/listen`);
  await ready(page);
  await page.selectOption('select:has(option:text("1.5×"))', "150");
  await expect.poll(() => page.locator("audio").evaluate((audio) => audio.playbackRate)).toBe(1.5);

  await expect(page.getByText("Now in Opening")).toBeVisible();
  await page.click('button:text-is("Next chapter")');
  await expect(page.getByText("Now in The Middle Part")).toBeVisible();
  await expect.poll(() => page.locator("audio").evaluate((audio) => Math.round(audio.currentTime))).toBe(4);

  await page.fill('input[maxlength="500"]', "Where the middle starts");
  await page.click('button:text-is("Bookmark this moment")');
  await expect(page.locator("ul.quotes li")).toContainText("The Middle Part · 0:04");
  await expect(page.locator("ul.quotes li")).toContainText("Where the middle starts");

  await page.selectOption('select:has(option:text("In 15 minutes"))', "15");
  await expect(page.getByText("Stops in 15 minutes.")).toBeVisible();

  await page.reload();
  await ready(page);
  await expect.poll(() => page.locator("audio").evaluate((audio) => audio.playbackRate)).toBe(1.5);
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
  await expect(page.getByText("Now in The Middle Part")).toBeVisible();
  await expect.poll(metadata).toBe("The Middle Part | Someone Reads | A Chaptered Recording");
  await expect.poll(() => page.locator("audio").evaluate((audio) => Math.round(audio.currentTime))).toBe(4);

  await page.evaluate(() => window.mediaHandlers.seekforward({ seekOffset: 2 }));
  await expect.poll(() => page.locator("audio").evaluate((audio) => Math.round(audio.currentTime))).toBe(6);
  await page.evaluate(() => window.mediaHandlers.previoustrack());
  await expect(page.getByText("Now in Opening")).toBeVisible();

  // Leaving the player within the same page load clears the lock screen.
  await page.evaluate(() => { window.stillHere = true; });
  await page.getByRole("link", { name: "Library", exact: true }).first().click();
  await page.waitForURL((url) => url.pathname === "/");
  expect(await page.evaluate(() => window.stillHere)).toBe(true);
  await expect.poll(metadata).toBeNull();
  noProblems(page);
});
