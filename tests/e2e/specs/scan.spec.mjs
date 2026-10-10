import { test, expect } from "@playwright/test";
import { fixture, noProblems, ready, signUp, unique } from "../helpers.mjs";

test.skip(({ browserName }) => browserName !== "chromium", "The camera is Chromium's fake one, playing a picture of a barcode.");

// Chromium plays this picture of a book's barcode as its camera.
test.use({
  launchOptions: {
    ...(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}),
    args: [
      "--use-fake-ui-for-media-stream",
      "--use-fake-device-for-media-stream",
      `--use-file-for-fake-video-capture=${fixture("isbn-camera.mjpeg")}`,
    ],
  },
});

test("a barcode held to the camera fills in the ISBN", async ({ browser }) => {
  const page = await signUp(browser, unique("Scanner"), { permissions: ["camera"] });
  await page.goto("/?add=1");
  await ready(page);
  const scan = page.getByRole("button", { name: "Scan the barcode" });
  await expect(scan).toBeVisible();
  await scan.click();
  await expect(page.getByLabel("ISBN")).toHaveValue("9780441478125", { timeout: 20_000 });
  await expect(page.locator("dialog.scanner")).toHaveCount(0);
  noProblems(page);
});

test("a photo of a barcode fills in the ISBN, and one without a barcode says so", async ({ browser }) => {
  const page = await signUp(browser, unique("Photographer"));
  // ZXing's WebAssembly comes from the shelf itself, never from a CDN.
  const elsewhere = [];
  page.on("request", (request) => {
    if (!/^https?:\/\/(127\.0\.0\.1|localhost)[:/]/.test(request.url()) && !request.url().startsWith("data:")) elsewhere.push(request.url());
  });
  await page.goto("/?add=1");
  await ready(page);
  await expect(page.getByRole("button", { name: "Scan the barcode" })).toBeVisible();
  // Where the camera cannot stream (a plain-http address), the button takes a photo; the picture arrives here.
  await page.locator("[data-scan-photo]").setInputFiles(fixture("isbn-barcode.png"));
  await expect(page.getByLabel("ISBN")).toHaveValue("9780441478125", { timeout: 20_000 });

  // A plain white picture has nothing to read.
  const blank = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=", "base64");
  await page.locator("[data-scan-photo]").setInputFiles({ name: "blank.png", mimeType: "image/png", buffer: blank });
  await expect(page.locator("[data-scan-status]")).toHaveText(/No ISBN barcode was found/);
  await expect(page.getByLabel("ISBN")).toHaveValue("9780441478125");
  expect(elsewhere.filter((url) => !url.includes("openlibrary.org"))).toEqual([]);
  noProblems(page);
});
