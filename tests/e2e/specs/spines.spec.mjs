import { test, expect } from "@playwright/test";
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

test("the spines the live camera has in view are read and ticked off in a shelf check", async ({ browser }) => {
  const page = await signUp(browser, unique("Shelf walker"), { permissions: ["camera"] });
  const book = await createBook(page, { title: "Piranesi", author: "Susanna Clarke", format: "Hardcover", location: "Study" });
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
  await expect(found).toContainText("ticked off", { timeout: 60_000 });
  noProblems(page);
});
