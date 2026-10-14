import { test, expect } from "@playwright/test";
import { createBook, noProblems, ready, signUp, unique } from "../helpers.mjs";

test("quick jump goes to a page or a book from the keyboard", async ({ browser }) => {
  const page = await signUp(browser, unique("Jumper"));
  const book = await createBook(page, { title: "Piranesi", author: "Susanna Clarke" });
  await page.goto("/library");
  await ready(page);

  // Ctrl K opens it; typing finds a page, Enter goes there.
  await page.keyboard.press("Control+k");
  const box = page.getByRole("combobox", { name: "Go to a page or a book" });
  await expect(box).toBeFocused();
  await box.fill("trash");
  await expect(page.getByRole("option", { name: /Trash & undo/ })).toBeVisible();
  // Enter would search the library; the arrow goes down to the page.
  await expect(page.locator("#jump-list").getByRole("option").first()).toHaveAttribute("aria-selected", "true");
  await expect(page.locator("#jump-list").getByRole("option").first()).toContainText("Search the library for “trash”");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/trash$/);
  await expect(page.locator("dialog.jump")).not.toBeVisible();

  // "/" opens it too; the arrows choose among what is found.
  await ready(page);
  await page.keyboard.press("/");
  await box.fill("piran");
  const option = page.getByRole("option", { name: /Piranesi/ });
  await expect(option).toBeVisible();
  await option.click();
  await expect(page).toHaveURL(new RegExp(`/library/${book.id}$`));

  // The menu's search box opens it too, and Enter there searches the library.
  await ready(page);
  await page.getByRole("searchbox", { name: "Search or go to" }).first().click();
  await expect(box).toBeFocused();
  await box.fill("melville whale");
  await expect(page.locator("#jump-list").getByRole("option").first()).toContainText("Search the library");
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/library\?q=melville%20whale$/);

  // Escape closes it again.
  await ready(page);
  await page.keyboard.press("Control+k");
  await page.keyboard.press("Escape");
  await expect(page.locator("dialog.jump")).not.toBeVisible();

  // "?" lists the keys.
  await page.keyboard.press("?");
  await expect(page.getByRole("dialog", { name: "Keyboard shortcuts" })).toBeVisible();
  await page.keyboard.press("Escape");
  noProblems(page);
});

test("a book's page is in tabs, and a link to a section opens its tab", async ({ browser }) => {
  const page = await signUp(browser, unique("Tabber"));
  const book = await createBook(page, { title: "Tabbed Book", author: "Someone", status: "Reading" });
  await page.goto(`/library/${book.id}`);
  await ready(page);
  const tabs = page.getByRole("navigation", { name: "This book" });
  await expect(tabs.getByRole("link", { name: "Reading" })).toHaveAttribute("aria-current", "page");
  await expect(page.locator("#reading")).toBeVisible();
  await expect(page.locator("#lending")).toHaveCount(0);

  await tabs.getByRole("link", { name: "Your copy" }).click();
  await expect(page.locator("#lending")).toBeVisible();
  await expect(page.locator("#reading")).toHaveCount(0);
  await expect(page).toHaveURL(/#tab-copy$/);

  // A link into a section from elsewhere opens the tab it's in.
  await page.goto(`/library/${book.id}#details`);
  await ready(page);
  await expect(tabs.getByRole("link", { name: "Details" })).toHaveAttribute("aria-current", "page");
  await expect(page.locator("#details")).toBeVisible();
  noProblems(page);
});

test("pages left out of the menu are gone from it, and the account's tabs follow links", async ({ browser }) => {
  const page = await signUp(browser, unique("Trimmer"));
  await page.goto("/account#menu");
  await ready(page);
  await expect(page.getByRole("navigation", { name: "Account" }).getByRole("link", { name: "You" })).toHaveAttribute("aria-current", "page");
  const podcasts = page.locator("#menu").getByLabel("Podcasts");
  await expect(podcasts).toBeChecked();
  await podcasts.uncheck();
  await expect(page.locator("#menu").getByRole("status")).toBeVisible();
  await page.goto("/library");
  await expect(page.locator(".sidebar").first().locator('a[href="/podcasts"]')).toHaveCount(0);

  await page.goto("/account#readwise");
  await ready(page);
  await expect(page.getByRole("navigation", { name: "Account" }).getByRole("link", { name: "Connections" })).toHaveAttribute("aria-current", "page");
  await expect(page.locator("#readwise")).toBeVisible();
  noProblems(page);
});
