import { test, expect } from "@playwright/test";
import { createBook, fixture, noProblems, ready, signUp, unique, upload } from "../helpers.mjs";

test("an e-book uploads through the form with progress, and lands on the saved page", async ({ browser }) => {
  const page = await signUp(browser, unique("Uploader"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await page.goto(`/library/${book.id}#files`);
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

  await page.goto("/library?view=list");
  await ready(page);
  await page.check(`input[aria-label="Choose ${first.title}"]`);
  await page.check(`input[aria-label="Choose ${second.title}"]`);
  await page.fill('.bulk-bar input[placeholder="Tag"]', tag);
  await page.click('.bulk-bar button:text-is("Add tag")');
  await expect(page.locator(".bulk-bar [role=status]")).toContainText("Changed: 2 books.");
  const books = await (await page.request.get(`/books?tag=${encodeURIComponent(tag)}`)).json();
  expect(books.map((book) => book.id).sort()).toEqual([first.id, second.id].sort());

  await page.goto("/library");
  await ready(page);
  await page.click('button:text-is("List")');
  await page.goto("/library");
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
    await page.goto(`/library/${id}#files`);
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
  await page.goto("/library?add=1");
  await ready(page);
  await page.locator('form[action="/books/import/files"] input[name=file]').setInputFiles([fixture("moby.epub"), fixture("chapters.m4b"), fixture("comic.cbz")]);
  await Promise.all([page.waitForURL(/imported=/), page.locator('form[action="/books/import/files"] button[type=submit]').click()]);
  await expect(page.locator('[role=status]:has-text("Added: 3 books.")')).toBeVisible();
  const books = await (await page.request.get("/books")).json();
  expect(books.map((book) => book.format).sort()).toEqual(["Audiobook", "Ebook", "Ebook"]);
  expect(books.some((book) => book.title === "A Comic" && book.author === "Someone")).toBe(true);

  // The same files again are already on the shelf.
  await page.goto("/library?add=1");
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

  await page.goto("/library?status=Want");
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
    await page.goto(`/library/${book.id}#collections`);
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
  // Nothing changes until what merging does has been seen and agreed to.
  const preview = page.getByRole("region", { name: "What merging does" });
  await expect(preview).toContainText("From the copy added");
  await expect(preview).toContainText("The other entry then goes to the trash");
  await preview.getByRole("button", { name: "Merge" }).click();
  await expect(page.getByText("Merged into one book.")).toBeVisible();
  await expect(page.getByRole("heading", { name: "No duplicates" })).toBeVisible();
  const kept = await (await page.request.get(`/books/${first.id}`)).json();
  expect([kept.status, kept.rating, kept.year]).toEqual(["Finished", 5, 1971]);
  const trash = await (await page.request.get("/books/trash")).json();
  expect(trash.map((item) => item.kind)).toEqual(["Book"]);
  noProblems(page);
});

test("a field of one's own is made, filled in on a book, and leads to the books holding the same", async ({ browser }) => {
  const page = await signUp(browser, unique("Fielder"));
  const book = await createBook(page, { title: "The Boxed Book", author: "Someone" });
  await createBook(page, { title: "The Loose Book", author: "Someone" });

  await page.goto("/fields");
  await ready(page);
  await page.getByLabel("Name").fill("Packing box");
  await page.getByRole("button", { name: "Add the field" }).click();
  await expect(page.getByRole("status")).toContainText("Packing box is ready");

  await page.goto(`/library/${book.id}#details`);
  await ready(page);
  await page.locator("summary", { hasText: "Edit the catalog" }).click();
  const box = page.locator("fieldset.custom-fields").getByLabel("Packing box");
  await box.fill("Box 7");
  await box.blur();
  await page.getByRole("button", { name: "Save changes" }).click();
  await expect(page.locator(".book-facts")).toContainText("Box 7");

  await page.locator(".book-facts a", { hasText: "Box 7" }).click();
  await expect(page).toHaveURL(/\/library\?field=\d+&value=Box%207$/);
  await ready(page);
  await expect(page.locator(".active-filters")).toContainText("Packing box");
  await expect(page.getByRole("link", { name: "The Boxed Book" }).first()).toBeVisible();
  await expect(page.getByRole("link", { name: "The Loose Book" })).toHaveCount(0);
  noProblems(page);
});

test("another format is added beside the main e-book, and made the main one", async ({ browser }) => {
  const page = await signUp(browser, unique("Formatter"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading" });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");
  await page.goto(`/library/${book.id}#files`);
  await ready(page);
  await page.locator('form[action$="/ebook/formats"] input[name=file]').setInputFiles(fixture("three.pdf"));
  await Promise.all([page.waitForURL(/format=saved/), page.locator('form[action$="/ebook/formats"] button[type=submit]').click()]);
  await expect(page.getByText("The format is kept with the book.")).toBeVisible();
  const formats = page.locator("ul.formats");
  await expect(formats).toContainText("PDF");
  await expect(formats.getByRole("link", { name: "Download" })).toHaveAttribute("href", /\/ebook\/formats\/\d+$/);

  await formats.getByRole("button", { name: "Make it the main e-book" }).click();
  await expect(formats.getByRole("alert")).toContainText("starts again at the beginning");
  await formats.getByRole("button", { name: "Make it the main e-book" }).click();
  await expect(formats).toContainText("EPUB");
  await expect(formats).toContainText("moby.epub");
  await expect(page.getByText("three.pdf").first()).toBeVisible();
  noProblems(page);
});

test("a book's page left open saves only what was changed on it, and asks when the same was changed elsewhere", async ({ browser }) => {
  const page = await signUp(browser, unique("Stale Tab"));
  const book = await createBook(page, { title: "Opened Long Ago", author: "Someone", status: "Reading" });
  await page.goto(`/library/${book.id}#details`);
  await ready(page);
  await page.locator("summary", { hasText: "Edit the catalog" }).click();

  // Finished elsewhere while the page sat open; here, only the title changes.
  const finishing = await page.request.put(`/books/${book.id}`, { data: { title: "Opened Long Ago", author: "Someone", status: "Finished" } });
  expect(finishing.ok()).toBe(true);
  await page.getByLabel("Title", { exact: true }).fill("Opened Long Ago, Renamed");
  await page.getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText("Saved.")).toBeVisible();
  let now = await (await page.request.get(`/books/${book.id}`)).json();
  expect([now.title, now.status]).toEqual(["Opened Long Ago, Renamed", "Finished"]);

  // The status changed both elsewhere and here: named, and kept only on a second save.
  await page.request.put(`/books/${book.id}`, { data: { title: "Opened Long Ago, Renamed", author: "Someone", status: "Abandoned" } });
  await page.locator("select", { has: page.locator('option[value="Want"]') }).first().selectOption("Want");
  await page.getByRole("button", { name: "Save changes" }).click();
  await expect(page.locator(".validation-errors")).toContainText("changed elsewhere: Status");
  expect((await (await page.request.get(`/books/${book.id}`)).json()).status).toBe("Abandoned");
  await page.getByRole("button", { name: "Save changes" }).click();
  await expect(page.getByText("Saved.")).toBeVisible();
  now = await (await page.request.get(`/books/${book.id}`)).json();
  expect(now.status).toBe("Want");
  noProblems(page);
});


test("the library sorts by a field of the reader's own, numbers as numbers and empty ones last", async ({ browser }) => {
  const page = await signUp(browser, unique("Field Sorter"));
  const field = await (await page.request.post("/books/fields", { data: { name: "Shelf number", kind: "Number" } })).json();
  const ten = await createBook(page, { title: "A Tenth", author: "Someone", status: "Want" });
  const nine = await createBook(page, { title: "B Ninth", author: "Someone", status: "Want" });
  await createBook(page, { title: "C Unnumbered", author: "Someone", status: "Want" });
  await page.request.put(`/books/${ten.id}/fields`, { data: { values: { [field.id]: "10" } } });
  await page.request.put(`/books/${nine.id}/fields`, { data: { values: { [field.id]: "9" } } });

  await page.goto("/library?view=list");
  await ready(page);
  await page.getByLabel("Sort").selectOption(`field:${field.id}`);
  await expect.poll(async () => (await page.locator(".book-list > li").allInnerTexts()).map((text) => text.match(/[ABC] \w+/)?.[0])).toEqual(["B Ninth", "A Tenth", "C Unnumbered"]);
  noProblems(page);
});
