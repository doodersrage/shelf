import { test, expect } from "@playwright/test";
import { createServer } from "node:http";
import { createBook, fixture, noProblems, ready, signUp, unique } from "../helpers.mjs";

test.skip(({ browserName }) => browserName !== "chromium", "The camera is Chromium's fake one, playing a picture of a shelf.");

// Chromium plays this picture of three spines on a shelf as its camera.
test.use({
  launchOptions: {
    ...(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}),
    args: [
      "--use-fake-ui-for-media-stream",
      "--use-fake-device-for-media-stream",
      `--use-file-for-fake-video-capture=${fixture("spines-camera.mjpeg")}`,
    ],
  },
});

// Open Library's search, played from here: the one book it is asked for.
const port = Number(process.env.SHELF_E2E_SEARCH_PORT ?? 5195);
let server;

test.beforeAll(async () => {
  server = createServer((request, response) => {
    const asked = new URL(request.url, "http://localhost").searchParams.get("q") ?? "";
    const docs = /wizard/i.test(asked)
      ? [{ key: "/works/OL59W", title: "A Wizard of Earthsea", author_name: ["Ursula K. Le Guin"], first_publish_year: 1968, isbn: ["9780547773742"] }]
      : [];
    response.setHeader("Content-Type", "application/json");
    response.end(JSON.stringify({ docs }));
  });
  await new Promise((resolve) => server.listen(port, "127.0.0.1", resolve));
});

test.afterAll(() => server?.close());

test("the spines the live camera has in view are read, and those noted elsewhere put here in a shelf check", async ({ browser }) => {
  const page = await signUp(browser, unique("Shelf walker"), { permissions: ["camera"] });
  const book = await createBook(page, { title: "Piranesi", author: "Susanna Clarke", format: "Hardcover", location: "Hall" });
  await page.goto("/scan");
  await ready(page);
  await page.getByLabel(/Checking a shelf/).check();
  await page.getByLabel("The place you are checking").fill("Study");
  await page.getByRole("button", { name: "Start", exact: true }).click();
  await page.getByRole("button", { name: "Start the camera" }).click();
  const read = page.getByRole("button", { name: "Read the spines in view" });
  await expect(read).toBeEnabled();
  // The camera needs a moment before it has a picture to give.
  await page.waitForTimeout(1_000);
  await read.click();
  const found = page.locator("li", { has: page.locator(`a[href="/library/${book.id}"]`), hasText: "% sure" });
  await expect(found).toContainText("noted at Hall", { timeout: 60_000 });
  await expect(found.getByRole("checkbox", { name: "Note Piranesi at Study" })).toBeChecked();
  // Any other book found can be left where it is noted.
  for (const other of await page.getByRole("checkbox", { name: /^Note (?!Piranesi)/ }).all()) await other.uncheck();
  await page.getByRole("button", { name: "Note 1 book at Study" }).click();
  await expect(found).toContainText("ticked off");
  await expect(page.getByRole("status").filter({ hasText: "1 book noted at Study." })).toBeVisible();
  expect((await (await page.request.get(`/books/${book.id}`)).json()).location).toBe("Study");
  noProblems(page);
});

test("a spine not in the catalog is found in Open Library and added to the run", async ({ browser }) => {
  const page = await signUp(browser, unique("Spine adder"), { permissions: ["camera"] });
  await page.goto("/scan");
  await ready(page);
  await page.getByLabel("Where they are going (optional)").fill("Den");
  await page.getByRole("button", { name: "Start", exact: true }).click();
  await page.getByRole("button", { name: "Start the camera" }).click();
  const read = page.getByRole("button", { name: "Read the spines in view" });
  await expect(read).toBeEnabled();
  await page.waitForTimeout(1_000);
  await read.click();

  // Only what was read clearly and names no book found is offered: not the turn read upside down.
  const strangers = page.locator(".spine-strangers > li");
  await expect(strangers.first()).toBeVisible({ timeout: 60_000 });
  await expect(strangers.filter({ hasText: /WIZARD OF EARTHSEA/ })).toHaveCount(1);
  await expect(strangers.filter({ hasText: /VASHLYV3|ANVH|VNNVSNS/ })).toHaveCount(0);
  const wizard = strangers.filter({ hasText: /WIZARD OF EARTHSEA/ });
  await wizard.getByRole("button", { name: "Find it" }).click();
  await expect(wizard.getByText("A Wizard of Earthsea", { exact: true })).toBeVisible();
  await wizard.getByRole("button", { name: "Add to this run" }).click();
  await expect(page.getByRole("checkbox", { name: "9780547773742" })).toBeChecked();
  await expect(strangers.filter({ hasText: /WIZARD OF EARTHSEA/ })).toHaveCount(0);
  noProblems(page);
});
