import { test, expect } from "@playwright/test";
import { createBook, ready, signUp, unique, upload } from "../helpers.mjs";

test.skip(({ browserName }) => browserName !== "chromium", "Playwright runs service workers offline only in Chromium; offline reading is not checked in Firefox.");

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

test("a kept audiobook plays with no connection, and its place and time go back when online", async ({ browser }) => {
  const page = await signUp(browser, unique("Commuter"));
  const book = await createBook(page, { title: unique("Three Tracks"), author: "Someone", status: "Reading" });
  await upload(page, book.id, "audio", "three-tracks.zip", "application/zip");

  await page.goto(`/library/${book.id}`);
  await ready(page);
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.click('button:text-is("Keep for listening offline")');
  await expect(page.getByText("Kept on this device.")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByRole("button", { name: "Kept for listening offline · forget" })).toBeVisible();

  await page.context().setOffline(true);
  await page.goto(`/library/${book.id}`);
  await expect(page.locator("#listening h2")).toHaveText("Kept for listening");
  await page.click(`#kept-audio a:text-is("${book.title}")`);
  await expect(page.locator("#listen h1")).toHaveText(book.title);
  await expect(page.locator("#listen [data-show=book-elapsed]")).toHaveText("0:00");

  // It plays from the device, across tracks, and seeks within them.
  await page.evaluate(() => window.shelfPlayer.seek(8));
  await page.click("#toggle");
  await expect.poll(async () => (await page.evaluate(() => window.shelfPlayer.now())).bookSeconds, { timeout: 10_000 }).toBeGreaterThan(9);
  await page.click("#toggle");
  const heard = await page.evaluate(() => window.shelfPlayer.now());
  expect(heard.track).toBe(2);

  await page.context().setOffline(false);
  await page.evaluate(() => window.dispatchEvent(new Event("online")));
  await expect.poll(async () => (await (await page.request.get(`/books/${book.id}/place`)).json()).audioTrack, { timeout: 20_000 }).toBe(2);
  await expect.poll(async () => (await (await page.request.get("/books/listening")).json()).totalSeconds, { timeout: 20_000 }).toBeGreaterThan(0);
});

// Selects a passage inside the offline page, as a reader's drag would, and lets the page notice.
async function selectWords(page, scope, words) {
  await page.locator(scope).evaluate((root, wanted) => {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode()) && !node.data.includes(wanted)) {}
    const start = node.data.indexOf(wanted);
    const range = document.createRange();
    range.setStart(node, start);
    range.setEnd(node, start + wanted.length);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    document.getElementById("page").dispatchEvent(new MouseEvent("mouseup", { bubbles: true }));
  }, words);
}

test("highlights made offline, and one removed, reach the shelf when it answers again", async ({ browser }) => {
  const page = await signUp(browser, unique("Marker"));
  const epub = await createBook(page, { title: unique("Moby-Dick"), author: "Herman Melville", status: "Reading" });
  const pdf = await createBook(page, { title: unique("The Dispossessed"), author: "Ursula K. Le Guin", status: "Reading" });
  await upload(page, epub.id, "ebook", "moby.epub", "application/epub+zip");
  await upload(page, pdf.id, "ebook", "three.pdf", "application/pdf");
  const before = await (await page.request.post(`/books/${epub.id}/highlights`, { data: { text: "Call me Ishmael", chapterIndex: 0, note: "Made online." } })).json();

  for (const id of [epub.id, pdf.id]) {
    await page.goto(`/library/${id}`);
    await ready(page);
    await page.evaluate(() => navigator.serviceWorker.ready);
    await page.click('button:text-is("Keep for reading offline")');
    await expect(page.getByText("Kept on this device.")).toBeVisible();
  }

  await page.context().setOffline(true);
  await page.goto(`/library/${epub.id}`);
  await page.click(`a:text-is("${epub.title}")`);
  // The highlight made online came down with the book, and shows offline.
  await expect(page.locator("#page mark.shelf-mark")).toHaveText("Call me Ishmael");
  await selectWords(page, "#page", "a damp, drizzly November in my soul");
  await page.fill("#marking-note", "Written on a train.");
  await page.click("#marking-save");
  await expect(page.locator("#page mark.shelf-mark")).toHaveCount(2);
  await expect(page.locator("#marks")).toContainText("On this device; sent to the shelf when you are online.");
  await page.click('#marks li:has-text("Call me Ishmael") button:text-is("Remove")');
  await expect(page.locator("#page mark.shelf-mark")).toHaveCount(1);

  await page.click("#back");
  await page.click(`a:text-is("${pdf.title}")`);
  await expect(page.locator("#page .textLayer")).toContainText("the ship leaves");
  await selectWords(page, "#page .textLayer", "the ship leaves");
  await page.click("#marking-save");
  await expect(page.locator("#page .textLayer mark.shelf-mark")).toHaveText("the ship leaves");

  await page.context().setOffline(false);
  await page.evaluate(() => window.dispatchEvent(new Event("online")));
  await expect.poll(async () => (await (await page.request.get(`/books/${epub.id}/highlights`)).json()).map((mark) => mark.text), { timeout: 20_000 })
    .toEqual(["a damp, drizzly November in my soul"]);
  const saved = (await (await page.request.get(`/books/${epub.id}/highlights`)).json())[0];
  expect(saved.note).toBe("Written on a train.");
  expect(saved.chapterIndex).toBe(0);
  expect(saved.id).not.toBe(before.id);
  await expect.poll(async () => (await (await page.request.get(`/books/${pdf.id}/highlights`)).json()).map((mark) => mark.text)).toEqual(["the ship leaves"]);
  await expect(page.locator("#marks-waiting")).toBeHidden();
});
