import { test, expect } from "@playwright/test";
import { createBook, ready, signUp, unique, upload } from "../helpers.mjs";

test("a kept book opens offline, and the place goes back when online", async ({ browser }) => {
  const page = await signUp(browser, unique("Traveller"));
  const book = await createBook(page, { title: unique("Moby-Dick"), author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");

  await page.goto(`/library/${book.id}`);
  await ready(page);
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.click('button:text-is("Keep for reading offline")');
  await expect(page.getByText("Kept on this device.")).toBeVisible();

  await page.context().setOffline(true);
  await page.goto(`/library/${book.id}`);
  await expect(page.locator("#shelf h1")).toHaveText("Kept for reading offline");
  await page.click(`a:text-is("${book.title}")`);
  await expect(page.locator("#page")).toContainText("Call me Ishmael");
  await page.click("#next");
  await expect(page.locator("#where")).toHaveText("Chapter 2 of 2");
  await expect(page.locator("#page")).toContainText("carpet-bag");

  await page.context().setOffline(false);
  await page.evaluate(() => window.dispatchEvent(new Event("online")));
  await expect.poll(async () => (await (await page.request.get(`/books/${book.id}/place`)).json()).ebookChapter, { timeout: 20_000 }).toBe(1);
});
