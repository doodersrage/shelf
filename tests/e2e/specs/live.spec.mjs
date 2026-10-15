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

test("a connection that drops and is slow to come back, as after a laptop sleeps, does not say the buttons are blocked", async ({ browser }) => {
  const page = await signUp(browser, unique("Sleepy"));
  const book = await createBook(page, { title: unique("Bedside Book"), author: "Someone" });
  await page.goto(`/library/${book.id}`);
  await ready(page);
  await expect(page.locator("html")).toHaveAttribute("data-live", "connected");

  // The connection goes, and the first tries to bring it back fail.
  await page.context().route("**/_blazor/negotiate**", (route) => route.abort());
  await page.evaluate(() => window.Blazor.pauseCircuit());
  await page.evaluate(() => window.Blazor.resumeCircuit().catch(() => {}));
  await page.waitForTimeout(17_000);
  await expect(page.locator("#live-unavailable")).toBeHidden();
  await expect(page.locator("html")).not.toHaveAttribute("data-live", "failed");

  // Once the shelf can be reached again, it is back.
  await page.context().unroute("**/_blazor/negotiate**");
  await page.evaluate(() => window.Blazor.resumeCircuit());
  await expect(page.locator("html")).toHaveAttribute("data-live", "connected");
  await expect(page.locator("#live-unavailable")).toBeHidden();
});
