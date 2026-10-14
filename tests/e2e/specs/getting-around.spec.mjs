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
