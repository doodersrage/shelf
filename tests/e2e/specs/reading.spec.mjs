import { test, expect } from "@playwright/test";
import { createBook, noProblems, ready, signUp, unique, upload } from "../helpers.mjs";

test("an EPUB takes text settings and keeps a highlight with a note", async ({ browser }) => {
  const page = await signUp(browser, unique("Ishmael"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");

  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  await expect(page.locator(".focus-bar")).toBeVisible();
  await page.click('summary:text("Text settings")');
  await page.selectOption('select:has(option:text("Largest"))', "130");
  const chapter = page.frameLocator("iframe.reader-frame");
  await expect(chapter.locator("p").first()).toContainText("Call me Ishmael");
  // Changing the size reloads the chapter, so ask whichever frame is current, and ask again if it is closing.
  const chapterFrame = () => page.frames().find((frame) => frame.url().includes("/ebook/chapters/0") && !frame.isDetached());
  await expect.poll(async () => {
    try {
      // A number, since Firefox rounds to 1/60 of a pixel (20.8125px) where Chromium says 20.8px.
      return parseFloat(await chapterFrame()?.evaluate(() => getComputedStyle(document.documentElement).fontSize));
    } catch {
      return null;
    }
  }).toBeCloseTo(20.8, 1);

  const frame = chapterFrame();
  await frame.evaluate(() => {
    const paragraph = [...document.querySelectorAll("p")].find((item) => item.textContent.includes("drizzly November"));
    const text = paragraph.firstChild;
    const start = text.data.indexOf("a damp, drizzly November");
    const range = document.createRange();
    range.setStart(text, start);
    range.setEnd(text, start + "a damp, drizzly November in my soul".length);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    document.dispatchEvent(new MouseEvent("mouseup", { bubbles: true }));
  });
  await expect(page.locator("blockquote.passage")).toHaveText("a damp, drizzly November in my soul");
  await page.fill(".reader textarea", "The best reason to go to sea.");
  await page.click('button:text-is("Highlight")');
  await expect(page.frameLocator("iframe.reader-frame").locator("mark.shelf-mark")).toHaveText("a damp, drizzly November in my soul");
  await expect(page.locator("h2:text('In this chapter') + ul")).toContainText("The best reason to go to sea.");
  noProblems(page);
});

test("a PDF turns pages, keeps a highlight, and remembers the page", async ({ browser }) => {
  const page = await signUp(browser, unique("Shevek"));
  const book = await createBook(page, { title: "The Dispossessed", author: "Ursula K. Le Guin", status: "Reading" });
  await upload(page, book.id, "ebook", "three.pdf", "application/pdf");

  await page.goto(`/library/${book.id}/read`);
  await expect(page.locator(".textLayer")).toContainText("the ship leaves");
  await page.locator(".textLayer").evaluate((layer) => {
    const walker = document.createTreeWalker(layer, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode()) && !node.data.includes("the ship leaves")) {}
    const start = node.data.indexOf("the ship leaves");
    const range = document.createRange();
    range.setStart(node, start);
    range.setEnd(node, start + "the ship leaves".length);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    layer.dispatchEvent(new MouseEvent("mouseup", { bubbles: true }));
  });
  await page.click('button:text-is("Highlight")');
  await expect(page.locator(".textLayer mark.shelf-mark")).toHaveText("the ship leaves");

  await page.locator(".pdf-viewer").focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.locator(".reader-nav .hint")).toHaveText("Page 2 of 3");
  await page.reload();
  await expect(page.locator(".reader-nav .hint")).toHaveText("Page 2 of 3");
  noProblems(page);
});

test("a scanned PDF is read with OCR, so its words can be highlighted", async ({ browser }) => {
  const page = await signUp(browser, unique("Scanner"));
  const book = await createBook(page, { title: "A Scanned Book", author: "Someone", status: "Reading" });
  await upload(page, book.id, "ebook", "scanned.pdf", "application/pdf");
  const state = await (await page.request.get(`/books/${book.id}/ebook/ocr/0`)).json();
  test.skip(state.state === "unavailable", "Tesseract and Poppler are not installed here.");

  await page.goto(`/library/${book.id}/read`);
  await expect(page.locator(".textLayer")).toContainText("ship", { timeout: 60_000 });
  await expect(page.getByText("Shelf read its words")).toBeVisible();
  noProblems(page);
});

test("an EPUB is read aloud, going on into the next chapter", async ({ browser }) => {
  const page = await signUp(browser, unique("Listener"));
  // A stand-in for the browser's speech: it keeps what it was asked to say and finishes each piece at once.
  await page.addInitScript(() => {
    window.__spoken = [];
    class Utterance {
      constructor(text) { this.text = text; }
    }
    const synth = {
      speak(utterance) {
        window.__spoken.push(utterance.text);
        setTimeout(() => utterance.onend?.(), 5);
      },
      cancel() {},
      getVoices: () => [{ name: "Test Voice", lang: "en-US" }],
      addEventListener() {},
    };
    Object.defineProperty(window, "speechSynthesis", { value: synth });
    window.SpeechSynthesisUtterance = Utterance;
  });
  const book = await createBook(page, { title: unique("Moby-Dick"), author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");

  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  await page.selectOption('.read-aloud select:has(option:text("Faster"))', "1.2");
  await page.click('.read-aloud button:text-is("Read aloud")');
  await expect.poll(() => page.evaluate(() => window.__spoken.some((text) => text.includes("Call me Ishmael")))).toBe(true);

  // At the end of the book it stops by itself, having turned to the second chapter on the way.
  await expect(page.locator('.read-aloud button:text-is("Read aloud")')).toBeVisible({ timeout: 30_000 });
  expect(await page.evaluate(() => window.__spoken.some((text) => text.toLowerCase().includes("carpet-bag")))).toBe(true);
  await expect.poll(async () => (await (await page.request.get(`/books/${book.id}/place`)).json()).ebookChapter).toBe(1);
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem("shelf-read-aloud")).rate)).toBe(1.2);
  noProblems(page);
});

test("a comic turns its pages with the arrow keys", async ({ browser }) => {
  const page = await signUp(browser, unique("Comics"));
  const book = await createBook(page, { title: unique("A Comic"), author: "Someone", status: "Reading" });
  await upload(page, book.id, "ebook", "comic.cbz", "application/vnd.comicbook+zip");
  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  await expect(page.locator(".reader-nav .hint")).toHaveText("Page 1 of 3");
  await expect.poll(() => page.locator(".comic-viewer img:not([hidden])").evaluate((image) => image.complete && image.naturalWidth > 0)).toBe(true);
  await page.locator(".comic-viewer").focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.locator(".reader-nav .hint")).toHaveText("Page 2 of 3");
  await expect.poll(async () => (await (await page.request.get(`/books/${book.id}/place`)).json()).ebookChapter).toBe(1);
  noProblems(page);
});
