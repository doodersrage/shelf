// Makes the screenshots in docs/images: starts Shelf on an empty data folder, fills a demo shelf with public-domain
// books (their covers drawn in ./covers), and photographs the pages. Run it with `npm run screenshots` from tests/e2e.
// CHROMIUM_PATH points at a Chromium already installed; otherwise Playwright's own is used.
import { chromium } from "@playwright/test";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync } from "node:fs";
import { createServer } from "node:http";
import { createServer as createSocketServer } from "node:net";
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
// The address the demo shelf says it has, in its emails and on its pages; links to it are followed here instead.
const publicAddress = "https://shelf.example.org";
const local = (address) => address.replaceAll(publicAddress, base);

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

// The mail Shelf sends goes to a little SMTP server here, so the emails can be photographed too.
const smtpPort = 5196;
const mails = [];
const smtp = createSocketServer((socket) => {
  let buffer = "";
  let reading = false;
  let message = "";
  socket.write("220 shelf-shots\r\n");
  socket.on("data", (chunk) => {
    buffer += chunk.toString("latin1");
    for (;;) {
      if (reading) {
        const end = buffer.indexOf("\r\n.\r\n");
        if (end < 0) return;
        message = buffer.slice(0, end);
        buffer = buffer.slice(end + 5);
        reading = false;
        mails.push(parseMail(message));
        socket.write("250 OK\r\n");
        continue;
      }
      const end = buffer.indexOf("\r\n");
      if (end < 0) return;
      const line = buffer.slice(0, end);
      buffer = buffer.slice(end + 2);
      const verb = line.slice(0, 4).toUpperCase();
      if (verb === "EHLO" || verb === "HELO") socket.write("250 shelf-shots\r\n");
      else if (verb === "DATA") {
        reading = true;
        socket.write("354 Go on\r\n");
      } else if (verb === "QUIT") socket.end("221 Bye\r\n");
      else socket.write("250 OK\r\n");
    }
  });
}).listen(smtpPort, "127.0.0.1");

// Just enough MIME for Shelf's own emails: the recipient, the subject, and the HTML part.
function parseMail(raw) {
  const header = (name, text) => new RegExp(`^${name}:\\s*(.*(?:\\r\\n[ \\t].*)*)`, "im").exec(text)?.[1].replace(/\r\n[ \t]+/g, " ").trim() ?? "";
  const words = (text) => text.replace(/=\?utf-8\?(b|q)\?([^?]*)\?=/gi, (_, kind, value) =>
    kind.toLowerCase() === "b" ? Buffer.from(value, "base64").toString("utf8") : decodeQp(value.replace(/_/g, " ")));
  const at = raw.search(/^Content-Type:\s*text\/html/im);
  let html = "";
  if (at >= 0) {
    const part = raw.slice(at);
    const split = part.indexOf("\r\n\r\n");
    const headers = part.slice(0, split);
    let body = part.slice(split + 4);
    const boundary = body.search(/\r\n--[^\r\n]+/);
    if (boundary >= 0) body = body.slice(0, boundary);
    html = /base64/i.test(header("Content-Transfer-Encoding", headers))
      ? Buffer.from(body.replace(/\s+/g, ""), "base64").toString("utf8")
      : /quoted-printable/i.test(header("Content-Transfer-Encoding", headers)) ? decodeQp(body) : Buffer.from(body, "latin1").toString("utf8");
  }
  return { to: header("To", raw).replace(/.*</, "").replace(/>.*/, ""), subject: words(header("Subject", raw)), html, raw };
}

function decodeQp(text) {
  const bytes = [];
  const flat = text.replace(/=\r\n/g, "");
  for (let i = 0; i < flat.length; i++) {
    if (flat[i] === "=" && /^[0-9A-F]{2}$/i.test(flat.slice(i + 1, i + 3))) {
      bytes.push(parseInt(flat.slice(i + 1, i + 3), 16));
      i += 2;
    } else {
      bytes.push(flat.charCodeAt(i) & 0xff);
    }
  }
  return Buffer.from(bytes).toString("utf8");
}

async function mail(match) {
  for (let tries = 0; tries < 100; tries++) {
    const found = mails.findLast(match);
    if (found) return found;
    await new Promise((done) => setTimeout(done, 100));
  }
  throw new Error("An email did not come.");
}

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
    Email__Host: "127.0.0.1",
    Email__Port: String(smtpPort),
    Email__Security: "none",
    Email__From: "Shelf <shelf@example.org>",
    Email__PublicAddress: publicAddress,
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
    await page.fill("input[name=email]", `${name.toLowerCase().split(/\s+/).join(".")}@example.test`);
    await page.fill("input[name=password]", "a long enough password");
    await page.fill("input[name=confirm]", "a long enough password");
    await page.click("button[type=submit]");
    await page.waitForURL(`${base}/`, { waitUntil: "domcontentloaded" });
    // The address is confirmed from the link emailed to it, as a reader would.
    const address = `${name.toLowerCase().split(/\s+/).join(".")}@example.test`;
    const confirm = await mail((item) => item.to === address && /confirm/i.test(item.subject));
    const link = local(/href="([^"]*\/account\/email\/confirm\?token=[^"]+)"/.exec(confirm.html)[1].replace(/&amp;/g, "&"));
    await page.goto(link, { waitUntil: "domcontentloaded" });
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
  await shot(tenar, "covers", "/library");
  await shot(tenar, "book", `/library/${moby}`);
  await shot(tenar, "reader", `/library/${moby}/read`, async (page) => {
    // The book fills the window, the bars over it showing, and the panel of contents open from the top bar.
    await page.click('.epub-top button[aria-label="Contents"]');
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
  await tenar.goto("/library?loved=1&sort=title", { waitUntil: "networkidle" });
  await tenar.waitForTimeout(600);
  await tenar.getByRole("button", { name: "Save this search" }).click();
  await tenar.getByLabel("Name", { exact: true }).fill("Books I would lend you");
  await Promise.all([tenar.waitForURL(/saved=\d+/), tenar.getByRole("button", { name: "Save", exact: true }).click()]);
  await tenar.waitForTimeout(900);
  await tenar.getByRole("button", { name: "Share by link" }).click();
  const link = await tenar.locator("#share-link input").inputValue();
  const stranger = await (await browser.newContext({ viewport: { width: 1280, height: 860 } })).newPage();
  await shot(stranger, "shared", link);

  // The player just after a skip to the next chapter, offering the way back.
  await shot(tenar, "listen-undo", `/library/${moby}/listen`, async (page) => {
    await page.getByRole("button", { name: "Next chapter" }).click();
    await page.locator(".listen-undo").waitFor({ state: "visible" });
  });
  // The book put away, so the pages after are seen without the mini player.
  await tenar.evaluate(() => window.shelfPlayer.close());
  await shot(tenar, "compact", "/library?view=compact");
  await shot(tenar, "jump", "/", async (page) => {
    await page.keyboard.press("Control+k");
    await page.keyboard.type("mo");
    await page.waitForTimeout(600);
  });
  await shot(tenar, "add", "/add");
  await shot(tenar, "devices", "/sync");
  await shot(tenar, "notifications", "/account?tab=tab-notices");
  await shot(tenar, "security", "/account?tab=tab-security");

  // A book club, with a meeting coming up and a word from each member.
  const club = await (await tenar.request.post("/books/clubs", { data: { name: "The Pequod Readers", description: "Classics, a chapter a week." } })).json();
  await tenar.request.post(`/books/clubs/${club.id}/members`, { data: { readerId: gedId } });
  await tenar.request.post(`/books/clubs/${club.id}/books`, { data: { bookId: moby } });
  const clubBook = (await (await tenar.request.get(`/books/clubs/${club.id}`)).json()).books[0].id;
  await tenar.request.post(`/books/clubs/${club.id}/meetings`, { data: { clubBookId: clubBook, at: new Date(Date.now() + 5 * 864e5).toISOString(), through: "Chapter 36", timeZone: "Europe/London" } });
  await tenar.request.post(`/books/clubs/${club.id}/books/${clubBook}/posts`, { data: { text: "Ishmael's opening still gets me: the sea as the cure for a drizzly November." } });
  await ged.request.post(`/books/clubs/${club.id}/books/${clubBook}/posts`, { data: { text: "I'm a few chapters behind; no spoilers past the Spouter-Inn, please." } });
  await shot(tenar, "club", `/clubs/${club.id}`);

  // Shelf admin: invitations sent, and one reader opened.
  await tenar.request.post("/admin/invitations", { data: { emails: "ogion@example.test, lebannen@example.test" } });
  await shot(tenar, "admin", "/admin");
  await tenar.locator(".reader-rows summary", { hasText: "Ged" }).click();
  await tenar.waitForTimeout(300);
  await tenar.locator("#readers").screenshot({ path: join(out, "admin-reader.png") });
  await shot(tenar, "admin-system", "/admin?tab=tab-system");
  await shot(tenar, "health", "/admin/health");

  // An announcement, as every reader sees it.
  await tenar.request.put("/admin/announcement", { data: { text: "Shelf is being updated on Sunday morning; it will be back within the hour.", email: false } });
  await ged.waitForTimeout(1200);
  await shot(ged, "announcement", "/");
  await tenar.request.put("/admin/announcement", { data: { text: null } });

  // The emails: an invitation, a notification, and a password reset.
  await ged.context().clearCookies();
  await ged.goto("/forgot");
  await ged.fill("input[name=who]", "ged@example.test");
  await ged.click("button[type=submit]");
  const emails = [
    ["email-invite", (item) => item.to === "ogion@example.test"],
    ["email-notice", (item) => item.to === "ged@example.test" && item.subject.includes("lent you")],
    ["email-reset", (item) => item.to === "ged@example.test" && item.subject.includes("Reset")],
  ];
  const inbox = await (await browser.newContext({ viewport: { width: 680, height: 600 } })).newPage();
  for (const [name, match] of emails) {
    const sent = await mail(match);
    await inbox.setContent(sent.html.replaceAll(`src="${publicAddress}/`, `src="${base}/`), { waitUntil: "load" });
    await inbox.waitForTimeout(300);
    await inbox.screenshot({ path: join(out, `${name}.png`), fullPage: true });
  }

  await tenar.emulateMedia({ colorScheme: "dark" });
  await shot(tenar, "dark", "/");
  await tenar.emulateMedia({ colorScheme: "light" });
  await tenar.setViewportSize({ width: 390, height: 844 });
  await shot(tenar, "phone", "/");
  await shot(tenar, "phone-library", "/library");
  await shot(tenar, "phone-book", `/library/${moby}`);
  await shot(tenar, "phone-listen", `/library/${moby}/listen`);

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
  smtp.close();
}
