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
  await expect(page.locator(".bulk-bar [role=status]")).toHaveText("Changed: 2 books.");
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
    await expect(page.locator(".notice.ask")).toContainText(`already on another book, byte for byte: ${first.title}`);
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
  await expect(page.locator('[role=status]:has-text("Added: 3 books.")')).toBeVisible();
  const books = await (await page.request.get("/books")).json();
  expect(books.map((book) => book.format).sort()).toEqual(["Audiobook", "Ebook", "Ebook"]);
  expect(books.some((book) => book.title === "A Comic" && book.author === "Someone")).toBe(true);

  // The same files again are already on the shelf.
  await page.goto("/?add=1");
  await ready(page);
  await page.locator('form[action="/books/import/files"] input[name=file]').setInputFiles([fixture("moby.epub")]);
  await Promise.all([page.waitForURL(/imported=/), page.locator('form[action="/books/import/files"] button[type=submit]').click()]);
  await expect(page.locator('[role=status]:has-text("Already on another book: ")')).toBeVisible();
  noProblems(page);
});

test("a filtered library is saved as a search, listed in the sidebar, and forgotten", async ({ browser }) => {
  const page = await signUp(browser, unique("Searcher"));
  const loved = await createBook(page, { title: unique("Loved Book"), author: "Someone", status: "Want" });
  await createBook(page, { title: unique("Plain Book"), author: "Someone", status: "Want" });
  await page.request.post("/books/bulk", { data: { ids: [loved.id], loved: true } });

  await page.goto("/?status=Want");
  await ready(page);
  await page.locator("summary", { hasText: "More filters" }).click();
  await page.getByLabel("Loved", { exact: true }).check();
  await page.getByLabel("Sort").selectOption("added");
  await expect(page.locator(".book-grid > li, .book-list > li")).toHaveCount(1);

  await page.getByRole("button", { name: "Save this search" }).click();
  await page.getByLabel("Name", { exact: true }).fill("Loved and waiting");
  await Promise.all([page.waitForURL(/saved=\d+/), page.getByRole("button", { name: "Save", exact: true }).click()]);
  await ready(page);
  await expect(page.locator("h1")).toHaveText("Loved and waiting");
  await expect(page).toHaveURL(/status=Want/);
  await expect(page).toHaveURL(/loved=1/);
  await expect(page).toHaveURL(/sort=added/);
  await expect(page.locator(".book-grid > li, .book-list > li")).toHaveCount(1);

  // From anywhere, the sidebar opens it again with the same filters.
  await page.goto("/stats");
  await ready(page);
  await page.getByRole("link", { name: "Loved and waiting" }).first().click();
  await page.waitForURL(/saved=\d+/);
  await ready(page);
  await expect(page.locator("h1")).toHaveText("Loved and waiting");
  await expect(page.getByRole("link", { name: "Loved and waiting" }).first()).toHaveAttribute("aria-current", "page");
  await expect(page.getByLabel("Loved", { exact: true })).toBeChecked();

  // Shared by link, it opens for someone signed out, with the books and nothing else of the shelf.
  await page.getByRole("button", { name: "Share by link" }).click();
  const link = await page.locator("#share-link input").inputValue();
  expect(link).toMatch(/\/shared\/[A-Za-z0-9_-]+$/);
  const stranger = await (await browser.newContext()).newPage();
  await stranger.goto(link);
  await expect(stranger.locator("h1")).toHaveText("Loved and waiting");
  await expect(stranger.locator(".shared-list li")).toHaveCount(1);
  await expect(stranger.locator(".sidebar")).toHaveCount(0);
  await page.getByRole("button", { name: "Stop sharing" }).click();
  await expect(page.locator("#share-link")).toHaveCount(0);
  await stranger.reload();
  await expect(stranger.locator("h1")).toHaveText("This list is not shared");

  await Promise.all([page.waitForURL((url) => !url.search.includes("saved=")), page.getByRole("button", { name: "Forget this search" }).click()]);
  await ready(page);
  await expect(page.getByRole("link", { name: "Loved and waiting" })).toHaveCount(0);
  noProblems(page);
});

test("a collection is made, filled from books' pages, put in order, and shared", async ({ browser }) => {
  const page = await signUp(browser, unique("Curator"));
  const first = await createBook(page, { title: unique("The Dispossessed"), author: "Ursula K. Le Guin" });
  const second = await createBook(page, { title: unique("The Word for World Is Forest"), author: "Ursula K. Le Guin" });

  await page.goto("/collections");
  await ready(page);
  await page.getByLabel("New collection").fill("Book club");
  await Promise.all([page.waitForURL(/\/collections\/\d+$/), page.getByRole("button", { name: "Make it" }).click()]);
  await ready(page);
  await expect(page.locator("h1")).toHaveText("Book club");

  for (const book of [first, second]) {
    await page.goto(`/library/${book.id}`);
    await ready(page);
    await page.locator("#collections select").selectOption({ label: "Book club" });
    await page.locator("#collections").getByRole("button", { name: "Add", exact: true }).click();
    await expect(page.locator("#collections .chip-row")).toContainText("Book club");
  }

  await page.locator("#collections .chip-row a").click();
  await page.waitForURL(/\/collections\/\d+$/);
  await ready(page);
  const titles = page.locator(".collection-books .title");
  await expect(titles).toHaveText([first.title, second.title]);
  await page.getByRole("button", { name: `Move ${second.title} up` }).click();
  await expect(titles).toHaveText([second.title, first.title]);

  await page.getByRole("button", { name: "Share by link" }).click();
  const link = await page.locator("#share-link input").inputValue();
  const stranger = await (await browser.newContext()).newPage();
  await stranger.goto(link);
  await expect(stranger.locator(".shared-list strong")).toHaveText([second.title, first.title]);
  noProblems(page);
});

test("two entries for one book are found and merged into the one kept", async ({ browser }) => {
  const page = await signUp(browser, unique("Tidy"));
  const title = unique("The Tombs of Atuan");
  const first = await createBook(page, { title, author: "Ursula K. Le Guin", status: "Finished", rating: 5 });
  await createBook(page, { title, author: "Ursula K. Le Guin", status: "Want", year: 1971 });

  await page.goto("/duplicates");
  await ready(page);
  await expect(page.locator(".duplicate-group")).toHaveCount(1);
  await page.locator(".duplicate-books li").first().getByRole("button", { name: "Keep this one" }).click();
  await expect(page.getByText("Merged into one book.")).toBeVisible();
  await expect(page.getByRole("heading", { name: "No duplicates" })).toBeVisible();
  const kept = await (await page.request.get(`/books/${first.id}`)).json();
  expect([kept.status, kept.rating, kept.year]).toEqual(["Finished", 5, 1971]);
  noProblems(page);
});
