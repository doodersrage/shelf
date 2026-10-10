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
