import { test, expect } from "@playwright/test";
import { createBook, fixture, noProblems, ready, signUp, unique, upload } from "../helpers.mjs";

test("an e-book uploads through the form with progress, and lands on the saved page", async ({ browser }) => {
  const page = await signUp(browser, unique("Uploader"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await page.goto(`/library/${book.id}`);
  await ready(page);
  await page.locator('form[action$="/ebook"] input[name=file]').setInputFiles(fixture("moby.epub"));
  await Promise.all([page.waitForURL(/ebook=saved/), page.locator('form[action$="/ebook"] button[type=submit]').click()]);
  await expect(page.getByText("The e-book is on the shelf.")).toBeVisible();
  noProblems(page);
});

test("many books change at once, the list view is remembered, and search finds words inside books", async ({ browser }) => {
  const page = await signUp(browser, unique("Librarian"));
  const tag = unique("batch").replace(" ", "-");
  const first = await createBook(page, { title: unique("Alpha"), author: "Someone" });
  const second = await createBook(page, { title: unique("Beta"), author: "Someone" });
  await upload(page, first.id, "ebook", "moby.epub", "application/epub+zip");

  await page.goto("/?view=list");
  await ready(page);
  await page.check(`input[aria-label="Choose ${first.title}"]`);
  await page.check(`input[aria-label="Choose ${second.title}"]`);
  await page.fill('.bulk-bar input[placeholder="Tag"]', tag);
  await page.click('.bulk-bar button:text-is("Add tag")');
  await expect(page.locator(".bulk-bar [role=status]")).toHaveText("Changed 2 books.");
  const books = await (await page.request.get(`/books?tag=${encodeURIComponent(tag)}`)).json();
  expect(books.map((book) => book.id).sort()).toEqual([first.id, second.id].sort());

  await page.goto("/");
  await ready(page);
  await page.click('button:text-is("List")');
  await page.goto("/");
  await expect(page.locator(".book-list")).toBeVisible();
  await ready(page);
  await page.click('button:text-is("Covers")');

  await expect.poll(async () => (await (await page.request.get("/books/search?q=drizzly%20november")).json()).length).toBeGreaterThan(0);
  await page.goto("/search?q=drizzly%20november");
  await expect(page.locator(".search-book mark")).toHaveText(/drizzly November/i);
  await page.click('a:text-is("Open here")');
  await expect(page).toHaveURL(new RegExp(`/library/${first.id}/read\\?chapter=0`));
  noProblems(page);
});

test("the same file on a second book asks first, then keeps both or lets it go", async ({ browser }) => {
  const page = await signUp(browser, unique("Collector"));
  const first = await createBook(page, { title: unique("Moby-Dick"), author: "Herman Melville" });
  const second = await createBook(page, { title: unique("Moby-Dick, another edition"), author: "Herman Melville" });
  const third = await createBook(page, { title: unique("Moby-Dick, a third"), author: "Herman Melville" });
  await upload(page, first.id, "ebook", "moby.epub", "application/epub+zip");

  const send = async (id) => {
    await page.goto(`/library/${id}`);
    await ready(page);
    await page.locator('form[action$="/ebook"] input[name=file]').setInputFiles(fixture("moby.epub"));
    await Promise.all([page.waitForURL(/ebook=duplicate/), page.locator('form[action$="/ebook"] button[type=submit]').click()]);
    await ready(page);
    await expect(page.locator(".notice.ask")).toContainText(`is already on ${first.title}`);
  };

  await send(second.id);
  await page.click('button:text-is("Keep both")');
  await expect(page.getByText("The e-book is on the shelf.")).toBeVisible();
  expect((await (await page.request.get(`/books/${second.id}`)).json()).ebookFileName).toBe("moby.epub");

  await send(third.id);
  await page.click(`button:text-is("Don't add it")`);
  await expect(page.locator(".notice.ask")).toHaveCount(0);
  expect((await (await page.request.get(`/books/${third.id}`)).json()).ebookFileName).toBeNull();
  noProblems(page);
});

test("files added together become books, each named from what it says", async ({ browser }) => {
  const page = await signUp(browser, unique("Importer"));
  await page.goto("/?add=1");
  await ready(page);
  await page.locator('form[action="/books/import/files"] input[name=file]').setInputFiles([fixture("moby.epub"), fixture("chapters.m4b"), fixture("comic.cbz")]);
  await Promise.all([page.waitForURL(/imported=/), page.locator('form[action="/books/import/files"] button[type=submit]').click()]);
  await expect(page.locator('[role=status]:has-text("Added 3 books.")')).toBeVisible();
  const books = await (await page.request.get("/books")).json();
  expect(books.map((book) => book.format).sort()).toEqual(["Audiobook", "Ebook", "Ebook"]);
  expect(books.some((book) => book.title === "A Comic" && book.author === "Someone")).toBe(true);

  // The same files again are already on the shelf.
  await page.goto("/?add=1");
  await ready(page);
  await page.locator('form[action="/books/import/files"] input[name=file]').setInputFiles([fixture("moby.epub")]);
  await Promise.all([page.waitForURL(/imported=/), page.locator('form[action="/books/import/files"] button[type=submit]').click()]);
  await expect(page.locator('[role=status]:has-text("already on another book")')).toBeVisible();
  noProblems(page);
});
