import { createHash } from "node:crypto";
import { test, expect } from "@playwright/test";
import { createBook, noProblems, ready, signUp, unique, upload } from "../helpers.mjs";

// The page-turning reader draws each part of the book in a frame of its own, inside .shelf-epub.
const bookFrame = (page) => page.frameLocator(".shelf-epub iframe").first();

// The page-turning reader's bars, brought back with a tap in the middle of the page if a page turned has hidden them.
async function showBars(page) {
  const stage = page.locator(".epub-stage");
  if ((await stage.getAttribute("data-chrome")) !== null) return;
  const box = await page.locator(".shelf-epub").boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await expect(stage).toHaveAttribute("data-chrome", "");
}

test("an EPUB takes text settings and keeps a highlight with a note", async ({ browser }) => {
  const page = await signUp(browser, unique("Ishmael"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");

  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  await expect(page.locator(".focus-bar")).toBeVisible();
  await expect(bookFrame(page).locator("p").first()).toContainText("Call me Ishmael", { timeout: 20_000 });
  // Two steps up from the usual size: 130%.
  await page.getByRole("button", { name: "Text settings" }).click();
  await page.getByRole("button", { name: "Larger text" }).click();
  await expect(page.locator(".stepper output")).toHaveText("115%");
  await page.getByRole("button", { name: "Larger text" }).click();
  await expect(page.locator(".stepper output")).toHaveText("130%");
  await page.keyboard.press("Escape");
  await expect(page.locator(".reader-sheet")).toHaveCount(0);
  await expect.poll(async () => {
    try {
      // A number, since Firefox rounds to 1/60 of a pixel where Chromium does not.
      return parseFloat(await bookFrame(page).locator("p").first().evaluate((paragraph) => getComputedStyle(paragraph).fontSize));
    } catch {
      return null;
    }
  }).toBeCloseTo(20.8, 1);

  await bookFrame(page).locator("p", { hasText: "drizzly November" }).evaluate((paragraph) => {
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
  await expect(page.locator(".shelf-epub .shelf-mark")).toHaveCount(1);
  await expect(page.locator("h2:text('In this chapter') + ul")).toContainText("The best reason to go to sea.");
  const marks = await (await page.request.get(`/books/${book.id}/highlights`)).json();
  expect(marks.map((mark) => [mark.text, mark.chapterIndex, mark.prefix])).toEqual([["a damp, drizzly November in my soul", 0, expect.stringContaining("whenever it is")]]);
  noProblems(page);
});

test("nothing in an EPUB can run, whatever it tries", async ({ browser }) => {
  const page = await signUp(browser, unique("Careful"));
  const book = await createBook(page, { title: "A Book With Tricks", author: "Nobody", status: "Reading" });
  await upload(page, book.id, "ebook", "scripted.epub", "application/epub+zip");

  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  const frame = bookFrame(page);
  await expect(frame.locator("h1")).toHaveText("A Book With Tricks", { timeout: 20_000 });
  await frame.locator("#trap").click();
  await page.waitForTimeout(1000);
  expect(await page.evaluate(() => [document.title, window.__ran ?? null])).toEqual([expect.not.stringMatching(/^ran /), null]);
  expect(await frame.locator("script").count()).toBe(0);
  expect(await frame.locator("[onerror]").count()).toBe(0);
  expect(await frame.locator("meta[http-equiv='Content-Security-Policy']").getAttribute("content")).toContain("script-src 'none'");
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

test("a page turned while the connection was down is kept on the device, and the shelf gets it next time", async ({ browser }) => {
  const page = await signUp(browser, unique("Offline Turner"));
  const book = await createBook(page, { title: "The Lathe of Heaven", author: "Ursula K. Le Guin", status: "Reading" });
  await upload(page, book.id, "ebook", "three.pdf", "application/pdf");

  await page.goto(`/library/${book.id}/read`);
  await expect(page.locator(".reader-nav .hint")).toHaveText("Page 1 of 3");
  // The device kept page 3 the shelf never heard, as when the connection drops between turns.
  await page.evaluate((id) => localStorage.setItem(`shelf-pdf-page-${id}`, JSON.stringify({ index: 2, waiting: true })), book.id);
  await page.reload();
  await expect(page.locator(".reader-nav .hint")).toHaveText("Page 3 of 3");
  await expect.poll(async () => (await (await page.request.get(`/books/${book.id}/place`)).json()).ebookChapter).toBe(2);
  await expect.poll(() => page.evaluate((id) => JSON.parse(localStorage.getItem(`shelf-pdf-page-${id}`)).waiting, book.id)).toBe(false);

  // Kept further back than the shelf's own place, it moves nothing.
  await page.evaluate((id) => localStorage.setItem(`shelf-pdf-page-${id}`, JSON.stringify({ index: 0, waiting: true })), book.id);
  await page.reload();
  await expect(page.locator(".reader-nav .hint")).toHaveText("Page 3 of 3");
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
  await page.getByRole("button", { name: "More" }).click();
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

test("time with a book open in the reader is counted while someone is reading", async ({ browser }) => {
  const page = await signUp(browser, unique("Timed"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");

  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  // Two counts of five seconds, kept busy, then leaving the reader sends them.
  for (let i = 0; i < 3; i++) {
    await page.mouse.wheel(0, 200);
    await page.waitForTimeout(4000);
  }
  await showBars(page);
  await page.getByRole("link", { name: "Back to Moby-Dick" }).click();
  await page.waitForURL((url) => url.pathname === `/library/${book.id}`);
  await expect.poll(async () => (await (await page.request.get("/books/reading-time")).json()).totalSeconds, { timeout: 10_000 }).toBeGreaterThanOrEqual(10);
  noProblems(page);
});

test("the page-turning reader turns across chapters, and opens again at the very page", async ({ browser }) => {
  const page = await signUp(browser, unique("Turner"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");
  await page.setViewportSize({ width: 360, height: 640 });

  // A reader who has never set the type turns pages.
  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  const where = page.locator(".reader-where");
  await expect(where).toContainText(/page 1 of [2-9]/, { timeout: 20_000 });
  const pages = Number((await where.textContent()).match(/page 1 of (\d+)/)[1]);

  await page.keyboard.press("ArrowRight");
  await expect(where).toContainText(`page 2 of ${pages}`);
  for (let i = 2; i <= pages; i++) await page.keyboard.press("ArrowRight");
  await expect(where).toContainText("Chapter 2 of 2");
  await expect(page.locator(".epub-line-chapter")).toContainText("Carpet-Bag");

  // Back from the first page of a chapter is the end of the one before.
  await page.keyboard.press("ArrowLeft");
  await expect(where).toContainText("Chapter 1 of 2");
  await expect(where).not.toContainText("page 1 of");

  // The place is kept to the page: opened again, here or on another device, it is the same page.
  await expect.poll(async () => (await (await page.request.get(`/books/${book.id}/place`)).json()).ebookCfi).toMatch(/^epubcfi\(\/6\/2/);
  const here = (await where.textContent()).split(" · ").slice(0, 2).join(" · ");
  const other = await (await browser.newContext({ storageState: await page.context().storageState(), viewport: { width: 360, height: 640 } })).newPage();
  await other.goto(`/library/${book.id}/read`);
  await ready(other);
  await expect(other.locator(".reader-where")).toContainText(here, { timeout: 20_000 });

  // The classic reader is a link away, in the panel.
  await showBars(page);
  await page.getByRole("button", { name: "More" }).click();
  await page.click('a:text-is("Classic reader")');
  await expect(page.locator("iframe.reader-frame")).toBeVisible();
  noProblems(page);
});

test("a reader left open doesn't send back a place moved on elsewhere; it asks, and goes there or keeps its own", async ({ browser }) => {
  const page = await signUp(browser, unique("Two Tabs"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");
  await page.setViewportSize({ width: 360, height: 640 });
  const place = async () => (await (await page.request.get(`/books/${book.id}/place`)).json());

  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  const where = page.locator(".reader-where");
  await expect(where).toContainText(/Chapter 1 of 2 · page 1 of/, { timeout: 20_000 });
  await expect.poll(async () => (await place()).ebookCfi).toMatch(/^epubcfi\(\/6\/2/);

  // Read on to the second chapter elsewhere; this tab, still in the first, turns a page.
  expect((await page.request.put(`/books/${book.id}/place`, { data: { ebookChapter: 1 } })).ok()).toBe(true);
  await page.keyboard.press("ArrowRight");
  const moved = page.locator(".reader-moved");
  await expect(moved).toContainText("part 2");
  await expect(where).toContainText("page 2 of");
  expect((await place()).ebookChapter).toBe(1);

  // Kept here, the place is this tab's again, and its pages are saved as before.
  await moved.getByRole("button", { name: "Keep my place here" }).click();
  await expect(moved).toHaveCount(0);
  await expect.poll(async () => (await place()).ebookChapter).toBe(0);
  await page.keyboard.press("ArrowRight");
  await expect(where).toContainText("Chapter 2 of 2");
  await expect.poll(async () => (await place()).ebookChapter).toBe(1);
  await page.keyboard.press("ArrowLeft");
  await expect(where).toContainText("Chapter 1 of 2 · page 2 of");
  await expect.poll(async () => (await place()).ebookChapter).toBe(0);
  await expect(moved).toHaveCount(0);

  // Moved on again elsewhere, "Go there" opens the book at that place.
  expect((await page.request.put(`/books/${book.id}/place`, { data: { ebookChapter: 1 } })).ok()).toBe(true);
  await page.keyboard.press("ArrowLeft");
  await expect(where).toContainText("Chapter 1 of 2 · page 1 of");
  await moved.getByRole("button", { name: "Go there" }).click();
  await ready(page);
  await expect(page.locator(".reader-where")).toContainText("Chapter 2 of 2", { timeout: 20_000 });
  await expect(page.locator(".reader-moved")).toHaveCount(0);
  noProblems(page);
});

// The paragraphs of the book showing in the reader's box: its frames are wider than the box, a page of columns each.
const showing = (page) =>
  page.evaluate(() => {
    const box = document.querySelector(".shelf-epub").getBoundingClientRect();
    return [...document.querySelectorAll(".shelf-epub iframe")].flatMap((frame) => {
      const at = frame.getBoundingClientRect();
      return [...frame.contentDocument.querySelectorAll("p")]
        .filter((paragraph) => [...paragraph.getClientRects()].some((rect) => at.left + rect.right > box.left + 1 && at.left + rect.left < box.right - 1 && at.top + rect.bottom > box.top && at.top + rect.top < box.bottom))
        .map((paragraph) => Number(paragraph.textContent.match(/^Paragraph (\d+)/)?.[1] ?? 0));
    });
  });

test("a place reached in KOReader opens at that very paragraph, and one reached here goes back to it", async ({ browser }) => {
  const name = unique("Koreader");
  const page = await signUp(browser, name);
  const book = await createBook(page, { title: "The Harbour", author: "Someone", status: "Reading" });
  await upload(page, book.id, "ebook", "koreader.epub", "application/epub+zip");
  await page.goto("/sync");
  await ready(page);
  await page.click('button:text-is("Make a KOReader password")');
  const shown = page.locator("dd", { hasText: "Copy it now" }).locator("code.key");
  await expect(shown).toBeVisible();
  const password = (await shown.textContent()).trim();
  const md5 = (text) => createHash("md5").update(text).digest("hex");
  const device = { headers: { "x-auth-user": name, "x-auth-key": md5(password), accept: "application/vnd.koreader.v1+json" } };
  // KOReader names a book it got with this file name by the name, as well as by samples of the file.
  const document = md5("koreader.epub");

  // KOReader, at the start of the thirtieth paragraph of the second chapter.
  const put = await page.request.put("/kosync/syncs/progress", { ...device, data: { document, progress: "/body/DocFragment[2]/body/div/p[30]/text().0", percentage: 0.6, device: "Kobo", device_id: "kobo-1" } });
  expect(put.ok()).toBe(true);
  expect((await (await page.request.get(`/books/${book.id}/place`)).json()).ebookCfi).toMatch(/^epubcfi\(\/6\/4!/);

  await page.setViewportSize({ width: 360, height: 640 });
  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  await expect(page.locator(".reader-where")).toContainText(/Chapter 2 of 2 · page ([2-9]|\d\d)/, { timeout: 20_000 });
  await expect.poll(() => showing(page)).toContain(30);

  // Two pages on, here: KOReader is sent the paragraph at the top of the page, which may have begun on the one before.
  const pageNumber = async () => Number((await page.locator(".reader-where").textContent()).match(/page (\d+)/)[1]);
  const opened = await pageNumber();
  await page.click('button[aria-label="Next page"]');
  await page.click('button[aria-label="Next page"]');
  await expect.poll(pageNumber).toBe(opened + 2);
  const top = Math.min(...(await showing(page)).filter((number) => number > 0));
  expect(top).toBeGreaterThan(30);
  await expect.poll(async () => (await (await page.request.get(`/kosync/syncs/progress/${document}`, device)).json()).progress, { timeout: 10_000 })
    .toMatch(new RegExp(`^/body/DocFragment\\[2\\]/body/div/p\\[${top}\\]/text\\(\\)(\\[2\\])?\\.\\d+$`));
  noProblems(page);
});

test("the page-turning reader fills the window, with its bars over the book a tap in the middle away", async ({ browser }) => {
  const page = await signUp(browser, unique("Fullscreen"));
  const book = await createBook(page, { title: "The Harbour", author: "Someone", status: "Reading" });
  await upload(page, book.id, "ebook", "koreader.epub", "application/epub+zip");
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  const where = page.locator(".reader-where");
  // The first chapter is a page of its own.
  await expect(where).toContainText("Chapter 1 of 2", { timeout: 20_000 });

  // The book fills the window from the start, the bars over it, with no other bars of the page's around it.
  const stage = page.locator(".epub-stage");
  await expect(stage).toHaveAttribute("data-full", "");
  await expect(stage).toHaveAttribute("data-chrome", "");
  await expect(page.getByRole("dialog", { name: "The Harbour" })).toBeVisible();
  await expect(page.locator(".reader-nav")).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Back to The Harbour" })).toBeVisible();
  await expect.poll(async () => {
    const [box, size] = [await page.locator(".shelf-epub").boundingBox(), await page.evaluate(() => [innerWidth, innerHeight])];
    return box.width === size[0] && box.height === size[1];
  }).toBe(true);
  const box = await page.locator(".shelf-epub").boundingBox();
  const middle = [box.x + box.width / 2, box.y + box.height / 2];

  // A tap in the middle hides the bars, another brings them back; a page turned on the page hides them, one turned
  // with the bar's buttons leaves them.
  await page.mouse.click(...middle);
  await expect(stage).not.toHaveAttribute("data-chrome", "");
  await page.mouse.click(...middle);
  await expect(stage).toHaveAttribute("data-chrome", "");
  await page.mouse.click(box.x + box.width * 0.95, middle[1]);
  await expect(stage).not.toHaveAttribute("data-chrome", "");
  await expect(where).toContainText("Chapter 2 of 2 · page 1 of");
  await page.mouse.click(...middle);
  await page.click('button[aria-label="Previous page"]');
  await expect(where).toContainText("Chapter 1 of 2");
  await expect(stage).toHaveAttribute("data-chrome", "");

  // The slider goes anywhere in the book, once its places are counted.
  const slider = page.getByLabel("Where in the book");
  await expect(slider).toBeEnabled({ timeout: 20_000 });
  await slider.fill("900");
  await slider.dispatchEvent("change");
  await expect(where).toContainText(/Chapter 2 of 2/);
  await expect.poll(async () => Number((await where.textContent()).match(/(\d+)% of the book/)?.[1] ?? 0)).toBeGreaterThan(80);

  // The rest is in the panel from the bar's ⋯, over the book.
  await page.getByRole("button", { name: "More" }).click();
  await expect(page.locator("#epub-panel")).toBeVisible();
  await expect(page.locator("#epub-panel")).toContainText("Classic reader");
  await page.getByRole("button", { name: "Close" }).click();
  await expect(page.locator("#epub-panel")).toBeHidden();

  // The way out is the book's page.
  await page.getByRole("link", { name: "Back to The Harbour" }).click();
  await expect(page).toHaveURL(new RegExp(`/library/${book.id}$`));
  await expect(page.locator("html")).not.toHaveClass(/reading-full/);
  noProblems(page);
});

test("the reader's contents name the chapters, and its settings are a tap each; Esc or a tap outside closes them", async ({ browser }) => {
  const page = await signUp(browser, unique("Contents"));
  const book = await createBook(page, { title: "The Harbour", author: "Someone", status: "Reading" });
  await upload(page, book.id, "ebook", "koreader.epub", "application/epub+zip");
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  await expect(page.locator(".reader-where")).toContainText("Chapter 1 of 2", { timeout: 20_000 });

  // Each chapter by its heading (this book has no table of contents), the one being read marked.
  await page.getByRole("button", { name: "Contents" }).click();
  const contents = page.getByRole("navigation", { name: "Contents" });
  await expect(contents.locator(".epub-toc button")).toHaveText(["Arrival", "The Harbour"]);
  await expect(contents.locator("[aria-current]")).toHaveText("Arrival");
  await contents.getByRole("button", { name: "The Harbour" }).click();
  await expect(contents).toHaveCount(0);
  await expect(page.locator(".reader-where")).toContainText("Chapter 2 of 2");
  await expect(page.locator(".epub-line-chapter")).toHaveText("The Harbour");

  // Esc closes the contents.
  await page.getByRole("button", { name: "Contents" }).click();
  await expect(contents.locator("[aria-current]")).toHaveText("The Harbour");
  await page.keyboard.press("Escape");
  await expect(contents).toHaveCount(0);

  // The settings: a swatch for the page, a step for the size; a tap outside closes them, and they are kept.
  await page.getByRole("button", { name: "Text settings" }).click();
  const sheet = page.getByRole("dialog", { name: "Text settings" });
  await sheet.getByRole("button", { name: "Dark" }).click();
  await expect(sheet.getByRole("button", { name: "Dark" })).toHaveAttribute("aria-pressed", "true");
  await sheet.getByRole("button", { name: "Smaller text" }).click();
  await expect(sheet.locator("output")).toHaveText("85%");
  await expect(sheet.getByRole("button", { name: "Smaller text" })).toBeDisabled();
  await sheet.getByRole("button", { name: "Airy" }).click();
  await page.mouse.click(10, 10);
  await expect(sheet).toHaveCount(0);
  await expect(page.locator(".epub-stage")).toHaveAttribute("data-theme", "dark");
  await page.reload();
  await ready(page);
  await page.getByRole("button", { name: "Text settings" }).click();
  await expect(sheet.getByRole("button", { name: "Airy" })).toHaveAttribute("aria-pressed", "true");
  await expect(sheet.locator("output")).toHaveText("85%");
  noProblems(page);
});

test("a manga in a 7-Zip archive reads right to left, two pages at a time", async ({ browser }) => {
  const page = await signUp(browser, unique("Manga Reader"));
  const book = await createBook(page, { title: "A Manga", author: "Someone", status: "Reading" });
  await upload(page, book.id, "ebook", "manga.cb7", "application/x-cb7-compressed");

  await page.goto(`/library/${book.id}/read`);
  await ready(page);
  await expect(page.getByLabel("Right to left, as manga")).toBeChecked();
  await expect(page.locator(".reader-nav")).toContainText("Page 1 of 3");
  // Right to left, the left arrow goes forward.
  await page.locator(".comic-viewer").focus();
  await page.keyboard.press("ArrowLeft");
  await expect(page.locator(".reader-nav")).toContainText("Page 2 of 3");

  await page.getByLabel("Two pages at a time").check();
  await expect(page.locator(".reader-nav")).toContainText("Pages 2 and 3 of 3");
  await expect(page.locator(".comic-viewer img:not([hidden])")).toHaveCount(2);
  await page.reload();
  await ready(page);
  await expect(page.getByLabel("Two pages at a time")).toBeChecked();
  noProblems(page);
});
