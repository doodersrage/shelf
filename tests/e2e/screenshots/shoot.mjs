// Makes the screenshots in docs/images: starts Shelf on an empty data folder, fills a demo shelf with public-domain
// books (their covers drawn in ./covers), and photographs the pages. Run it with `npm run screenshots` from tests/e2e.
// CHROMIUM_PATH points at a Chromium already installed; otherwise Playwright's own is used.
import { chromium } from "@playwright/test";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..", "..");
const out = join(root, "docs", "images");
const fixture = (name) => readFileSync(join(here, "..", "fixtures", name));
const base = "http://127.0.0.1:5198";
const coverPort = 5197;
const cover = (key) => `http://127.0.0.1:${coverPort}/${key}.svg`;

// The covers come from a little server of their own, as they would from Open Library.
const covers = createServer((request, response) => {
  try {
    const name = (request.url ?? "").replace(/[^a-z]/g, "").replace(/svg$/, "");
    response.writeHead(200, { "Content-Type": "image/svg+xml" });
    response.end(readFileSync(join(here, "covers", `${name}.svg`)));
  } catch {
    response.writeHead(404).end();
  }
}).listen(coverPort, "127.0.0.1");

const data = mkdtempSync(join(tmpdir(), "shelf-shots-"));
const app = spawn("dotnet", ["run", "--project", join(root, "src", "Shelf.Api"), "--no-launch-profile"], {
  env: {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: "Development",
    ASPNETCORE_URLS: base,
    ConnectionStrings__Shelf: `Data Source=${join(data, "shelf.db")}`,
    EbookStore__Root: join(data, "ebooks"),
    AudioStore__Root: join(data, "audio"),
    CoverStore__Root: join(data, "covers"),
    DataProtection__KeysPath: join(data, "keys"),
    Backup__Enabled: "false",
    FreeBooks__Enabled: "false",
    SeriesAlerts__Enabled: "false",
  },
  stdio: "ignore",
  // Its own process group, so stopping it stops the app dotnet run started too.
  detached: true,
});

let browser;
try {
  for (let tries = 0; ; tries++) {
    try {
      if ((await fetch(`${base}/alive`)).ok) break;
    } catch {
      // Not up yet.
    }
    if (tries > 240) throw new Error("Shelf did not start.");
    await new Promise((done) => setTimeout(done, 1000));
  }

  browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
  const errors = [];
  const reader = async (name) => {
    const context = await browser.newContext({ baseURL: base, viewport: { width: 1280, height: 860 } });
    const page = await context.newPage();
    page.on("console", (message) => {
      if (message.type() === "error") errors.push(`${name}: ${message.text()}`);
    });
    await page.goto("/signup");
    await page.fill("input[name=name]", name);
    await page.fill("input[name=password]", "a long enough password");
    await page.fill("input[name=confirm]", "a long enough password");
    await page.click("button[type=submit]");
    await page.waitForURL(`${base}/`, { waitUntil: "domcontentloaded" });
    return page;
  };

  const day = (offset) => new Date(Date.now() + offset * 864e5).toISOString().slice(0, 10);
  const year = new Date().getFullYear();
  const tenar = await reader("Tenar");
  const ged = await reader("Ged");

  // The first account takes Development's sample book; the demo starts without it.
  for (const book of await (await tenar.request.get("/books")).json()) {
    await tenar.request.delete(`/books/${book.id}`);
  }

  const books = [
    { title: "Moby-Dick", author: "Herman Melville", status: "Reading", pages: 635, currentPage: 212, coverUrl: cover("moby"), year: 1851, tags: ["sea", "whaling"], startedOn: day(-20) },
    { title: "Middlemarch", author: "George Eliot", status: "Reading", pages: 880, currentPage: 340, coverUrl: cover("middlemarch"), year: 1871, tags: ["victorian"] },
    { title: "Pride and Prejudice", author: "Jane Austen", status: "Finished", pages: 432, rating: 5, loved: true, coverUrl: cover("pride"), year: 1813, finishedOn: `${year}-03-14`, startedOn: `${year}-02-20`, tags: ["romance"] },
    { title: "Frankenstein", author: "Mary Shelley", status: "Finished", pages: 280, rating: 4, loved: true, coverUrl: cover("frankenstein"), year: 1818, finishedOn: `${year}-05-02`, startedOn: `${year}-04-21`, tags: ["gothic"] },
    { title: "The Time Machine", author: "H. G. Wells", status: "Want", pages: 118, coverUrl: cover("timemachine"), year: 1895, queued: true, tags: ["science fiction"] },
    { title: "Jane Eyre", author: "Charlotte Brontë", status: "Want", pages: 532, coverUrl: cover("eyre"), year: 1847, tags: ["gothic"] },
    { title: "Dracula", author: "Bram Stoker", status: "Finished", pages: 418, rating: 4, loved: true, coverUrl: cover("dracula"), year: 1897, finishedOn: `${year}-09-28`, startedOn: `${year}-09-10`, tags: ["gothic"] },
    { title: "Little Women", author: "Louisa May Alcott", status: "Want", pages: 759, coverUrl: cover("women"), year: 1868, tags: [] },
    { title: "The Odyssey", author: "Homer", status: "Abandoned", pages: 541, coverUrl: cover("odyssey"), tags: ["epic"] },
    { title: "Walden", author: "Henry David Thoreau", status: "Finished", pages: 352, rating: 5, loved: true, coverUrl: cover("walden"), year: 1854, finishedOn: `${year}-07-19`, startedOn: `${year}-07-01`, tags: ["nature"] },
  ];
  for (const book of books) {
    const response = await tenar.request.post("/books", { data: book });
    if (response.status() !== 201) throw new Error(`${book.title}: ${await response.text()}`);
  }

  await tenar.request.put("/settings", { data: { yearlyGoal: 12 } });
  const all = await (await tenar.request.get("/books")).json();
  const id = (title) => all.find((book) => book.title === title).id;
  const moby = id("Moby-Dick");
  for (const [offset, from, to] of [[-6, 120, 141], [-5, 141, 158], [-3, 158, 170], [-2, 170, 189], [-1, 189, 200], [0, 200, 212]]) {
    await tenar.request.post(`/books/${moby}/sessions`, { data: { date: day(offset), fromPage: from, toPage: to } });
  }

  await tenar.request.post(`/books/${moby}/quotes`, { data: { text: "It is not down on any map; true places never are.", page: 77 } });
  await tenar.request.post(`/books/${id("Walden")}/quotes`, { data: { text: "I went to the woods because I wished to live deliberately.", page: 90 } });
  await tenar.request.post(`/books/${moby}/ebook`, { multipart: { file: { name: "moby-dick.epub", mimeType: "application/epub+zip", buffer: fixture("moby.epub") } } });
  await tenar.request.post(`/books/${moby}/audio`, { multipart: { file: { name: "moby-dick.m4b", mimeType: "audio/mp4", buffer: fixture("chapters.m4b") } } });
  await tenar.request.post(`/books/${moby}/highlights`, { data: { text: "a damp, drizzly November in my soul", chapterIndex: 0, note: "The best reason ever given for going to sea.", prefix: "whenever it is ", suffix: "; whenever I find" } });
  const gedId = (await (await tenar.request.get("/readers")).json())[0].id;
  await tenar.request.post(`/books/${id("Dracula")}/lend`, { data: { readerId: gedId, dueOn: day(10) } });
  await tenar.request.put("/books/shelves/open", { data: { open: true } });
  await ged.request.post(`/books/${id("Jane Eyre")}/ask`);

  const shot = async (page, name, url, prepare) => {
    await page.goto(url, { waitUntil: "networkidle" });
    await page.waitForTimeout(900);
    if (prepare) await prepare(page);
    await page.screenshot({ path: join(out, `${name}.png`) });
  };

  await shot(tenar, "library", "/");
  await shot(tenar, "covers", "/", async (page) => {
    await page.getByRole("heading", { name: "All books" }).evaluate((heading) => window.scrollTo(0, heading.getBoundingClientRect().top + window.scrollY - 32));
    await page.waitForTimeout(300);
  });
  await shot(tenar, "book", `/library/${moby}`);
  await shot(tenar, "reader", `/library/${moby}/read`, async (page) => {
    await page.click('summary:text("Text settings")');
    await page.waitForTimeout(400);
  });
  await shot(tenar, "listen", `/library/${moby}/listen`, async (page) => {
    await page.evaluate(() => window.shelfPlayer.seek(6));
    await page.fill('input[maxlength="500"]', "Ishmael meets Queequeg");
    await page.click('button:text-is("Bookmark this moment")');
    await page.waitForTimeout(600);
  });
  // Moving on from the player leaves the book in the mini player at the foot of the page.
  await shot(tenar, "mini-player", "/stats");
  await shot(tenar, "stats", "/stats");
  await shot(tenar, "year", `/years/${year}`);
  await shot(tenar, "loans", "/loans");

  // A reading list shared by link, as someone who is not signed in sees it.
  await tenar.goto("/?loved=1&sort=title", { waitUntil: "networkidle" });
  await tenar.waitForTimeout(600);
  await tenar.getByRole("button", { name: "Save this search" }).click();
  await tenar.getByLabel("Name", { exact: true }).fill("Books I would lend you");
  await Promise.all([tenar.waitForURL(/saved=\d+/), tenar.getByRole("button", { name: "Save", exact: true }).click()]);
  await tenar.waitForTimeout(900);
  await tenar.getByRole("button", { name: "Share by link" }).click();
  const link = await tenar.locator("#share-link input").inputValue();
  const stranger = await (await browser.newContext({ viewport: { width: 1280, height: 860 } })).newPage();
  await shot(stranger, "shared", link);

  await tenar.emulateMedia({ colorScheme: "dark" });
  await shot(tenar, "dark", "/");
  await tenar.emulateMedia({ colorScheme: "light" });
  await tenar.setViewportSize({ width: 390, height: 844 });
  await shot(tenar, "phone", "/");

  if (errors.length > 0) {
    console.log("Errors on the pages:\n" + errors.join("\n"));
  }

  console.log(`Screenshots are in ${out}`);
} finally {
  await browser?.close();
  try {
    process.kill(-app.pid);
  } catch {
    // Already gone.
  }
  covers.close();
}
