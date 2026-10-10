import { test, expect } from "@playwright/test";
import { createBook, ready, signUp, unique } from "../helpers.mjs";

test("a page whose live connection cannot start says so, instead of ignoring its buttons", async ({ browser }) => {
  const page = await signUp(browser, unique("Blocked"));
  const book = await createBook(page, { title: unique("Unreachable Book"), author: "Someone" });

  // Connected as usual: no banner, and the buttons work.
  await page.goto(`/library/${book.id}`);
  await ready(page);
  await expect(page.locator("html")).toHaveAttribute("data-live", "connected");
  await expect(page.locator("#live-unavailable")).toBeHidden();

  // As an extension that blocks the connection would.
  await page.context().route("**/_blazor/negotiate**", (route) => route.abort());
  await page.goto(`/library/${book.id}`);
  await expect(page.locator("#live-unavailable")).toBeVisible({ timeout: 20_000 });
  await expect(page.locator("#live-unavailable")).toContainText("This page's buttons cannot reach Shelf.");
  await expect(page.locator("html")).toHaveAttribute("data-live", "failed");
});
