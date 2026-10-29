import { test, expect } from "@playwright/test";
import { authenticatorCode, createBook, emailOf, noProblems, ready, signUp, unique, upload, PASSWORD } from "../helpers.mjs";

test("a reader lends a book to another, who gives it back", async ({ browser }) => {
  const ownerName = unique("Tenar");
  const borrowerName = unique("Ged");
  const owner = await signUp(browser, ownerName);
  const borrower = await signUp(browser, borrowerName);
  const book = await createBook(owner, { title: unique("The Farthest Shore"), author: "Ursula K. Le Guin" });

  await owner.goto(`/library/${book.id}#lending`);
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
  await page.goto("/account#two-step");
  await ready(page);
  await page.click('button:text-is("Set up two-step sign-in")');
  const secret = (await page.locator("code.key").innerText()).trim();
  await expect(page.locator(".qr svg")).toBeVisible();
  await page.fill('input[autocomplete="one-time-code"]', authenticatorCode(secret));
  await page.click('button:text-is("Turn on")');
  await expect(page.locator(".recovery-codes li")).toHaveCount(10);

  const fresh = await (await browser.newContext()).newPage();
  await fresh.goto("/signin");
  await fresh.fill("input[name=email]", emailOf(name));
  await fresh.fill("input[name=password]", PASSWORD);
  await Promise.all([fresh.waitForURL(/\/signin\/code/), fresh.click("button[type=submit]")]);
  // The code that turned it on is taken already; the authenticator's next one signs in.
  await fresh.fill("input[name=code]", authenticatorCode(secret, 1));
  await Promise.all([fresh.waitForURL("**/", { waitUntil: "domcontentloaded" }), fresh.click("button[type=submit]")]);
  await expect(fresh.locator(".reader-name").first()).toHaveText(name);

  await page.reload();
  await ready(page);
  await expect(page.locator("section:has(h2:text('Signed-in devices')) li")).toHaveCount(2);
  noProblems(page);
});

test("adding a book another reader has on an open shelf offers to borrow it", async ({ browser }) => {
  const lenderName = unique("Arha");
  const lender = await signUp(browser, lenderName);
  const title = unique("The Tombs of Atuan");
  await createBook(lender, { title, author: "Ursula K. Le Guin" });
  await lender.request.put("/books/shelves/open", { data: { open: true } });

  const reader = await signUp(browser, unique("Tenar"));
  await reader.goto("/library?add=1");
  await ready(reader);
  await reader.getByLabel("Title", { exact: true }).fill(title);
  await reader.getByLabel("Author", { exact: true }).fill("Ursula K. Le Guin");
  await reader.keyboard.press("Tab");
  await expect(reader.locator(".open-copies")).toContainText(`${lenderName}'s open shelf`);
  await reader.click(`button[aria-label="Ask ${lenderName} to borrow ${title}"]`);
  await expect(reader.locator(".open-copies .saved")).toHaveText("Asked");

  await lender.goto("/loans");
  await ready(lender);
  await expect(lender.locator("main")).toContainText(title);
  noProblems(reader);
});

test("an API token made on the account page opens the books API", async ({ browser, playwright, baseURL }) => {
  const page = await signUp(browser, unique("Scripted"));
  const book = await createBook(page, { title: unique("The Lathe of Heaven"), author: "Ursula K. Le Guin" });
  await page.goto("/account#tokens");
  await ready(page);
  const tokens = page.locator("#tokens");
  await tokens.getByLabel("What will use it").fill("Home Assistant");
  await tokens.locator('button:text-is("Make a token")').click();
  const token = (await tokens.locator("code.key").innerText()).trim();
  expect(token).toMatch(/^shelf_/);
  await expect(tokens.locator("li")).toContainText("reads only");

  const script = await playwright.request.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Bearer ${token}` } });
  const answer = await script.get(`/books/${book.id}`);
  expect(answer.ok()).toBe(true);
  expect((await answer.json()).title).toBe(book.title);
  expect((await script.post("/books", { data: { title: "Refused", author: "Nobody" } })).status()).toBe(403);
  await script.dispose();
  noProblems(page);
});

test("a book club is started, a reader invited, and a book talked about", async ({ browser }) => {
  const hostName = unique("Host");
  const guestName = unique("Guest");
  const host = await signUp(browser, hostName);
  const guest = await signUp(browser, guestName);

  await host.goto("/clubs");
  await ready(host);
  await host.getByLabel("New club").fill("Thursday readers");
  await Promise.all([host.waitForURL(/\/clubs\/\d+$/), host.getByRole("button", { name: "Start it" }).click()]);
  await ready(host);
  await host.getByLabel("Invite a reader").selectOption({ label: guestName });
  await host.getByRole("button", { name: "Invite", exact: true }).click();
  await expect(host.locator(".panel", { hasText: "Members" })).toContainText(guestName);
  await host.locator("summary", { hasText: "Add a book" }).click();
  await host.getByLabel("Title").fill("Kindred");
  await host.getByLabel("Author").fill("Octavia E. Butler");
  await host.locator("details").getByRole("button", { name: "Add", exact: true }).last().click();
  await expect(host.locator(".club-talk h2")).toHaveText("Kindred");
  const clubUrl = host.url();

  await guest.goto("/clubs");
  await ready(guest);
  await guest.getByRole("link", { name: "Thursday readers" }).click();
  await guest.waitForURL(clubUrl);
  await ready(guest);
  await guest.getByLabel("Say something about it").fill("Dana's 1976 is so vivid.");
  await guest.getByRole("button", { name: "Post" }).click();
  await expect(guest.locator(".club-posts")).toContainText("Dana's 1976 is so vivid.");
  await guest.getByRole("button", { name: "Put it on my shelf" }).click();
  await expect(guest.getByRole("link", { name: "Your copy" })).toBeVisible();

  await host.reload();
  await ready(host);
  await expect(host.locator(".club-posts")).toContainText(guestName);
  noProblems(host);
  noProblems(guest);
});

test("a parent makes a child's account and shares a collection, which the child reads", async ({ browser }) => {
  const parent = await signUp(browser, unique("Parent"));
  const childName = unique("Kid");
  const book = await createBook(parent, { title: unique("The Hobbit"), author: "J. R. R. Tolkien", status: "Finished" });
  const collection = await (await parent.request.post("/books/collections", { data: { name: "For the children" } })).json();
  await parent.request.post(`/books/collections/${collection.id}/books`, { data: { bookId: book.id } });

  await parent.goto("/account");
  await ready(parent);
  await parent.locator("#children summary").click();
  await parent.getByLabel("Their name, to sign in with").fill(childName);
  await parent.getByLabel(/A password for them/).fill(PASSWORD);
  await parent.getByRole("button", { name: "Make it" }).click();
  await expect(parent.locator("#children")).toContainText(`${childName} can sign in now.`);

  await parent.goto(`/collections/${collection.id}`);
  await ready(parent);
  await parent.getByLabel(`Let ${childName} read the books in this collection`).check();
  await expect(parent.getByLabel(`Let ${childName} read the books in this collection`)).toBeChecked();

  const context = await browser.newContext();
  const child = await context.newPage();
  child.problems = [];
  child.navigations = 0;
  await child.goto("/signin");
  await child.fill("input[name=email]", childName);
  await child.fill("input[name=password]", PASSWORD);
  await Promise.all([child.waitForURL("**/", { waitUntil: "domcontentloaded" }), child.click("button[type=submit]")]);
  await ready(child);
  await expect(child.locator(".family")).toContainText(book.title);
  await child.goto("/account");
  await ready(child);
  await expect(child.getByRole("heading", { name: "A child's account" })).toBeVisible();
  await expect(child.getByRole("heading", { name: "API tokens" })).toHaveCount(0);
  await expect(child.locator("nav").getByRole("link", { name: "Shelves", exact: true })).toHaveCount(0);
  noProblems(parent);
});

test("a collection shared as a household library opens its books to the reader chosen", async ({ browser }) => {
  const ownerName = unique("Household");
  const memberName = unique("Housemate");
  const owner = await signUp(browser, ownerName);
  const member = await signUp(browser, memberName);
  const book = await createBook(owner, { title: unique("Shared at Home"), author: "Someone", status: "Reading" });
  await upload(owner, book.id, "ebook", "moby.epub", "application/epub+zip");
  const collection = await (await owner.request.post("/books/collections", { data: { name: unique("The Den") } })).json();
  await owner.request.post(`/books/collections/${collection.id}/books`, { data: { bookId: book.id } });

  await owner.goto(`/collections/${collection.id}`);
  await ready(owner);
  await owner.locator("summary", { hasText: "Share as a household library" }).click();
  await owner.locator("details.household select").selectOption({ label: memberName });
  await owner.locator("details.household").getByRole("button", { name: "Share", exact: true }).click();
  await expect(owner.locator(".household-members")).toContainText(memberName);

  await member.goto("/");
  await ready(member);
  const shared = member.locator("section.family", { hasText: collection.name });
  await expect(shared).toContainText(book.title);
  await shared.getByRole("link", { name: `Read ${book.title}` }).click();
  await ready(member);
  await expect(member.locator(".reader-where")).toContainText("Chapter 1 of 2", { timeout: 20_000 });

  // Without downloads, the book opens a chapter at a time, and still reads.
  await owner.locator("details.household").getByLabel("Can download files").uncheck();
  await expect(owner.locator("details.household").getByLabel("Can download files")).not.toBeChecked();
  await member.goto(`/library/${book.id}/read`);
  await ready(member);
  await expect(member.getByText("opens a chapter at a time")).toBeVisible();
  await expect(member.frameLocator("iframe.reader-frame").locator("body")).toContainText("Call me Ishmael", { timeout: 20_000 });
  noProblems(owner);
  noProblems(member);
});

