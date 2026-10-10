import { test, expect } from "@playwright/test";
import { authenticatorCode, createBook, noProblems, ready, signUp, unique, PASSWORD } from "../helpers.mjs";

test("a reader lends a book to another, who gives it back", async ({ browser }) => {
  const ownerName = unique("Tenar");
  const borrowerName = unique("Ged");
  const owner = await signUp(browser, ownerName);
  const borrower = await signUp(browser, borrowerName);
  const book = await createBook(owner, { title: unique("The Farthest Shore"), author: "Ursula K. Le Guin" });

  await owner.goto(`/library/${book.id}`);
  await ready(owner);
  await owner.selectOption('select:has(option:text("Choose a reader"))', { label: borrowerName });
  await owner.click('button:text-is("Lend")');
  await expect(owner.locator("#lending")).toContainText(`Loaned to ${borrowerName}, a reader here`);

  await borrower.goto("/loans");
  await ready(borrower);
  await expect(borrower.locator("ul.quotes")).toContainText(book.title);
  await borrower.click('button:text-is("Give it back")');
  // With nothing left on loan either way, the page says so as a whole.
  await expect(borrower.getByRole("heading", { name: "Nothing is on loan" })).toBeVisible();

  await owner.reload();
  await expect(owner.locator("#lending")).not.toContainText("Loaned to");
  await expect(owner.locator('select:has(option:text("Choose a reader"))')).toBeVisible();
  noProblems(owner);
  noProblems(borrower);
});

test("two-step sign-in is set up from the account page and asked for at the next sign-in", async ({ browser }) => {
  const name = unique("Careful");
  const page = await signUp(browser, name);
  await page.goto("/account");
  await ready(page);
  await page.click('button:text-is("Set up two-step sign-in")');
  const secret = (await page.locator("code.key").innerText()).trim();
  await expect(page.locator(".qr svg")).toBeVisible();
  await page.fill('input[autocomplete="one-time-code"]', authenticatorCode(secret));
  await page.click('button:text-is("Turn on")');
  await expect(page.locator(".recovery-codes li")).toHaveCount(10);

  const fresh = await (await browser.newContext()).newPage();
  await fresh.goto("/signin");
  await fresh.fill("input[name=name]", name);
  await fresh.fill("input[name=password]", PASSWORD);
  await Promise.all([fresh.waitForURL(/\/signin\/code/), fresh.click("button[type=submit]")]);
  await fresh.fill("input[name=code]", authenticatorCode(secret));
  await Promise.all([fresh.waitForURL("**/", { waitUntil: "domcontentloaded" }), fresh.click("button[type=submit]")]);
  await expect(fresh.locator(".reader-name").first()).toHaveText(name);

  await page.reload();
  await ready(page);
  await expect(page.locator("section:has(h2:text('Signed-in devices')) li")).toHaveCount(2);
  noProblems(page);
});
