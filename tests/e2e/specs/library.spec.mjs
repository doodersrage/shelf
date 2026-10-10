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
