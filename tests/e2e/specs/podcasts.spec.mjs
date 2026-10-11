import { test, expect } from "@playwright/test";
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { fixture, noProblems, ready, signUp, unique } from "../helpers.mjs";

// A podcast's feed and its episodes, served from here.
const port = Number(process.env.SHELF_E2E_FEED_PORT ?? 5196);
const base = `http://127.0.0.1:${port}`;
const audio = readFileSync(fixture("chapters.m4b"));
let server;

test.beforeAll(async () => {
  server = createServer((request, response) => {
    if (request.url === "/feed.xml") {
      response.setHeader("Content-Type", "application/rss+xml");
      response.end(`<?xml version="1.0"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"><channel>
          <title>Shelf Test Radio</title><itunes:author>The Testers</itunes:author>
          ${[1, 2].map((i) => `<item><title>Episode ${i}</title><guid>e${i}</guid><pubDate>${i} Sep 2025 10:00:00 GMT</pubDate>
            <itunes:duration>12</itunes:duration><enclosure url="${base}/e${i}.m4a" type="audio/mp4" length="${audio.length}" /></item>`).join("")}
        </channel></rss>`);
      return;
    }

    response.setHeader("Content-Type", "audio/mp4");
    response.end(audio);
  });
  await new Promise((resolve) => server.listen(port, "127.0.0.1", resolve));
});

test.afterAll(() => server?.close());

const player = (page) => page.evaluate(() => window.shelfPlayer.now());

test("a podcast is followed by its feed, and an episode plays in the player with its place kept", async ({ browser }) => {
  const page = await signUp(browser, unique("Podcaster"));
  await page.goto("/podcasts");
  await ready(page);
  await page.getByLabel("Name, or the address of its feed").fill(`${base}/feed.xml`);
  await page.getByRole("button", { name: "Find" }).click();
  await expect(page.getByRole("status")).toContainText("Following it.");
  await page.getByRole("link", { name: /Shelf Test Radio/ }).click();
  await ready(page);
  await expect(page.locator(".episodes li")).toHaveCount(2);
  await expect(page.locator(".episodes li").first()).toContainText("downloaded");

  await page.getByRole("button", { name: "Play Episode 2" }).click();
  await expect.poll(async () => (await player(page)).playing).toBe(true);
  expect((await player(page)).key).toMatch(/^episode-\d+$/);
  await expect(page.locator("#mini-player")).toContainText("Episode 2");
  await expect.poll(async () => (await player(page)).seconds).toBeGreaterThan(1);
  await page.evaluate(() => window.shelfPlayer.pause());

  // The place is kept on the shelf.
  await page.reload();
  await ready(page);
  await expect(page.locator(".episodes li").first()).toContainText(" in");
  noProblems(page);
});
