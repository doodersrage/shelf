import { test, expect } from "@playwright/test";
import { createServer } from "node:http";
import { createBook, noProblems, ready, signUp, unique, upload } from "../helpers.mjs";

// A stand-in speech-to-text service (ReadAlong:Url in the config): each track it is sent, it hears the next third
// of the book's words, spread over the track.
const lengths = [3, 4, 5];
let words = [];
let calls = 0;
let server;

test.beforeAll(async () => {
  server = createServer((request, response) => {
    request.resume();
    request.on("end", () => {
      const part = calls++ % 3;
      const share = words.slice(Math.floor((part * words.length) / 3), Math.floor(((part + 1) * words.length) / 3));
      const step = lengths[part] / Math.max(1, share.length);
      response.setHeader("Content-Type", "application/json");
      response.end(JSON.stringify({ words: share.map((word, i) => ({ word: ` ${word}`, start: i * step, end: (i + 0.9) * step })) }));
    });
  });
  await new Promise((resolve) => server.listen(Number(process.env.SHELF_E2E_SPEECH_PORT ?? 5197), "127.0.0.1", resolve));
});

test.afterAll(() => server?.close());

// The words of a chapter as the shelf and the page count them.
function count(html) {
  const body = html.slice(Math.max(0, html.toLowerCase().indexOf("<body")));
  const text = body
    .replace(/<script[\s>][\s\S]*?<\/script>|<style[\s>][\s\S]*?<\/style>|<!--[\s\S]*?-->/gi, "")
    .replace(/<[^>]*>/g, " ")
    .replace(/&nbsp;/g, " ").replace(/&amp;/g, "&").replace(/&#8217;|&rsquo;/g, "’");
  return text.match(/[\p{L}\p{M}\p{N}]+(?:['’][\p{L}\p{M}\p{N}]+)*/gu) ?? [];
}

test("a read-along marks the word being read as the audiobook plays, and turns to the next chapter", async ({ browser }) => {
  const page = await signUp(browser, unique("Read Along"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");
  await upload(page, book.id, "audio", "three-tracks.zip", "application/zip");
  words = [];
  for (let chapter = 0; chapter < 20; chapter++) {
    const response = await page.request.get(`/books/${book.id}/ebook/chapters/${chapter}`);
    if (!response.ok()) break;
    words.push(...count(await response.text()));
  }

  calls = 0;
  await page.goto(`/library/${book.id}`);
  await ready(page);
  await page.getByRole("button", { name: "Make the read-along" }).click();
  await expect(page.locator("#read-along")).toContainText("Ready.", { timeout: 30_000 });
  expect(calls).toBe(3);

  await page.goto(`/library/${book.id}/read?chapter=0`);
  await ready(page);
  await page.getByRole("button", { name: "Listen along" }).click();
  await expect.poll(() => page.evaluate(() => window.shelfPlayer.now().playing)).toBe(true);

  // The word being read is marked in the chapter, where the browser can mark it.
  const chapter = page.frames().find((frame) => frame.url().includes("/ebook/chapters/"));
  const marks = await chapter.evaluate(() => Boolean(window.CSS?.highlights && window.Highlight));
  if (marks) {
    await expect.poll(() => page.frames().find((frame) => frame.url().includes("/ebook/chapters/0")).evaluate(() => CSS.highlights.has("shelf-say"))).toBe(true);
  }

  // The recording reaches the second chapter, and the page turns to it.
  await expect(page.locator(".reader-nav select")).toHaveValue("1", { timeout: 20_000 });
  await page.getByRole("button", { name: "Stop listening along" }).click();
  await expect.poll(() => page.evaluate(() => window.shelfPlayer.now().playing)).toBe(false);
  noProblems(page);
});
