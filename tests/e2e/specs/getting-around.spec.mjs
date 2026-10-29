import { test, expect } from "@playwright/test";
import { createBook, noProblems, ready, signUp, unique } from "../helpers.mjs";

test("quick jump offers the places opened lately before anything is typed", async ({ browser }) => {
  const page = await signUp(browser, unique("Returner"));
  const book = await createBook(page, { title: "Lately Opened", author: "Someone" });
  await page.goto(`/library/${book.id}`);
  await ready(page);
  await page.goto("/trash");
  await ready(page);

  await page.keyboard.press("Control+k");
  const options = page.getByRole("option");
  await expect(options.first()).toContainText("Trash & undo");
  await expect(options.nth(1)).toContainText("Lately Opened");
  await expect(options.first()).toContainText("Recent");
  noProblems(page);
});

test("books chosen in the list are put at a place together, and undone there and then", async ({ browser }) => {
  const page = await signUp(browser, unique("Shelver"));
  for (const title of ["Placed One", "Placed Two", "Placed Three"]) {
    await createBook(page, { title, author: "Someone" });
  }
  await page.goto("/library?q=Placed");
  await ready(page);
  const asList = page.getByRole("button", { name: "List" });
  if (await asList.count()) await asList.click();
  await page.getByLabel("Choose all shown").check();
  await page.getByLabel("Place for the chosen books").fill("Hall shelf");
  await page.getByRole("button", { name: "Put there" }).click();
  const note = page.getByRole("status").filter({ hasText: "Changed: 3 books." });
  await expect(note).toBeVisible();
  const books = await (await page.request.get("/books?q=Placed")).json();
  expect(books.map((book) => book.location)).toEqual(["Hall shelf", "Hall shelf", "Hall shelf"]);

  await note.getByRole("button", { name: "Undo" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Undone." })).toBeVisible();
  const after = await (await page.request.get("/books?q=Placed")).json();
  expect(after.map((book) => book.location)).toEqual([null, null, null]);
  noProblems(page);
});

test("on a phone, Add opens a short list with scanning first", async ({ browser }) => {
  const page = await signUp(browser, unique("Phoner"), { viewport: { width: 390, height: 844 } });
  await page.goto("/");
  await ready(page);
  const bar = page.getByRole("navigation", { name: "Most used" });
  await bar.getByText("Add", { exact: true }).click();
  const scan = bar.getByRole("link", { name: "Scan books" });
  await expect(scan).toBeVisible();
  await expect(bar.getByRole("link", { name: "More ways to add" })).toHaveAttribute("href", "/add");
  // A tap elsewhere folds it away.
  await page.locator("h1").click();
  await expect(scan).not.toBeVisible();
  noProblems(page);
});

test("following an author and starting a club work as plain pages", async ({ browser }) => {
  const page = await signUp(browser, unique("Planer"));
  await createBook(page, { title: "Followed Book", author: "Ada Plainpage" });
  await page.goto("/authors");
  await ready(page);
  await page.getByRole("button", { name: "Follow Ada Plainpage" }).click();
  await expect(page.locator("li", { hasText: "Ada Plainpage" })).toContainText("Following");

  await page.goto("/clubs");
  await ready(page);
  await page.getByLabel("New club").fill("Plain club");
  await Promise.all([page.waitForURL(/\/clubs\/\d+$/), page.getByRole("button", { name: "Start it" }).click()]);
  noProblems(page);
});

test("the compact table holds every book, drawing only those in view, with the columns chosen", async ({ browser }) => {
  const page = await signUp(browser, unique("Tabulator"));
  const many = Array.from({ length: 400 }, (_, at) => ({ title: `Row Book ${String(at).padStart(3, "0")}`, author: "Someone", location: at % 2 ? "Attic" : "Hall", tags: [] }));
  for (const book of many) {
    await page.request.post("/books", { data: { status: "Want", ...book } });
  }
  await page.goto("/library?q=Row%20Book");
  await ready(page);
  await page.getByRole("button", { name: "Compact" }).click();
  const rows = page.locator(".book-table tbody tr:not([style])");
  await expect(rows.first()).toContainText("Row Book 000");
  // Far fewer than 400 drawn, and the rest come as it scrolls.
  expect(await page.locator(".book-table tbody tr td a[href^='/library/']").count()).toBeLessThan(150);
  // To the foot of the page: a wheel turn goes only so far in Firefox.
  await page.evaluate(() => scrollTo(0, document.documentElement.scrollHeight));
  await expect(page.locator(".book-table").getByRole("link", { name: "Row Book 399" })).toBeVisible();

  // A column taken away is gone, and the choice is kept.
  await expect(page.locator(".book-table th", { hasText: "Place" })).toHaveCount(1);
  await page.locator(".columns-pick summary").click();
  await page.locator(".columns-pick").getByLabel("Place").uncheck();
  await expect(page.locator(".book-table th", { hasText: "Place" })).toHaveCount(0);
  await page.goto("/library?q=Row%20Book");
  await ready(page);
  await expect(page.getByRole("button", { name: "Compact" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator(".book-table th", { hasText: "Place" })).toHaveCount(0);
  noProblems(page);
});

// A tablet upright and a phone on its side: every main page fits the width, with nothing to scroll sideways.
for (const [label, viewport] of [["a tablet", { width: 820, height: 1180 }], ["a phone on its side", { width: 844, height: 390 }]]) {
  test(`the main pages fit ${label}`, async ({ browser }) => {
    const page = await signUp(browser, unique("Sideways"), { viewport });
    const book = await createBook(page, { title: unique("Wide Margins"), author: "Someone", status: "Reading" });
    for (const path of ["/", "/library", `/library/${book.id}`, "/loans", "/journal", "/account", "/backup", "/fields"]) {
      await page.goto(path);
      await ready(page);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, `${path} scrolls sideways by ${overflow}px`).toBeLessThanOrEqual(1);
    }

    noProblems(page);
  });
}
