import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { createBook, signUp, unique, upload } from "../helpers.mjs";

const axe = readFileSync(createRequire(import.meta.url).resolve("axe-core/axe.min.js"), "utf8");

test("every page passes an accessibility audit in both themes", async ({ browser }) => {
  // Every page twice, in both themes: longer than one test is given, and longer in Firefox.
  test.slow();
  const page = await signUp(browser, unique("Auditor"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading", pages: 600, currentPage: 120, tags: ["sea"] });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");
  await page.request.post(`/books/${book.id}/quotes`, { data: { text: "Call me Ishmael.", page: 1 } });
  const year = new Date().getUTCFullYear();
  const walden = await createBook(page, { title: "Walden", author: "Henry David Thoreau", status: "Finished", pages: 352, rating: 5, loved: true, finishedOn: `${year}-01-15`, tags: ["nature"] });
  const club = await (await page.request.post("/books/clubs", { data: { name: "Sea readers" } })).json();
  await page.request.post(`/books/clubs/${club.id}/books`, { data: { bookId: book.id } });
  const collection = await (await page.request.post("/books/collections", { data: { name: "Sea stories", description: "Books about the sea." } })).json();
  await page.request.post(`/books/collections/${collection.id}/books`, { data: { bookId: book.id } });
  await page.request.put(`/books/collections/${collection.id}/books/${book.id}`, { data: { note: "Start with the whale." } });
  await page.request.put(`/books/collections/${collection.id}/plan`, { data: { readBy: `${year + 1}-01-01` } });
  await page.request.post(`/books/${book.id}/links`, { data: { toBookId: walden.id, kind: "Contrasts", note: "Sea and pond." } });
  await page.request.post("/books/goals", { data: { kind: "Tag", name: "nature", target: 3 } });
  await page.request.post("/books/journal", { data: { text: "Read the first chapters on the ferry.", bookId: book.id } });
  const clubBook = (await (await page.request.get(`/books/clubs/${club.id}`)).json()).books[0];
  await page.request.post(`/books/clubs/${club.id}/meetings`, { data: { clubBookId: clubBook.id, at: new Date(Date.now() + 7 * 86400000).toISOString(), through: "Chapters 1–20", agenda: "Ishmael and Queequeg." } });
  await page.request.post(`/books/clubs/${club.id}/books/${clubBook.id}/posts`, { data: { text: "The whale wins.", spoiler: true } });
  const paper = await createBook(page, { title: "Two Years Before the Mast", author: "Richard Henry Dana", status: "Want", location: "Study", loanedTo: "Ishmael" });
  const kept = await (await page.request.post(`/books/${paper.id}/quotes`, { data: { text: "The sea, the sea." } })).json();
  await page.request.delete(`/books/${paper.id}/quotes/${kept.id}`);
  await page.request.post("/books/scans", { data: { kind: "Check", location: "Study" } });
  const topic = await (await page.request.post("/books/research", { data: { question: "Why do sailors tell stories?" } })).json();
  await page.request.post(`/books/research/${topic.id}/notes`, { data: { bookId: book.id, place: "ch. 1", text: "Ishmael frames it all." } });
  await page.request.post("/books/words", { data: { word: "baleine", language: "French", meaning: "whale" } });
  const room = await (await page.request.post("/books/storage", { data: { name: "Study room", kind: "Room" } })).json();
  await page.request.post("/books/storage", { data: { name: "", kind: "Box", destinationId: room.id } });
  await page.request.post("/books/rules", { data: { name: "Finished sea books", trigger: "Finished", conditions: { tag: "sea" }, actions: [{ kind: "AddToCollection", value: "Sea stories" }] } });
  await page.request.post(`/books/queue/${book.id}`);
  await page.request.post("/books/legacy/recipients", { data: { name: "A niece", rest: true } });

  const pages = ["/", "/library", "/reading", "/insights", "/library?view=list", "/library?add=1", `/library/${book.id}`, `/library/${book.id}/read`, `/library/${book.id}/listen`, "/quotes", "/search?q=ishmael",
    "/free", "/import/audiobookshelf", "/import/calibre", "/authors", "/series", "/places", "/copies", "/recommenders", "/years", `/years/${year}`, "/loans", "/shelves", "/collections", `/collections/${collection.id}`, "/duplicates", "/clubs", `/clubs/${club.id}`, "/stats", "/backup", "/sync", "/account", "/admin", "/next", "/podcasts", "/downloads", "/wanted", "/find", "/find?q=Harbour", "/tidy", "/activity", "/journal", "/map", `/map?book=${book.id}`, "/scan", "/add", "/trash", "/labels?place=Study", `/c/${paper.id}`, "/agenda", "/dna", "/research", `/research/${topic.id}`, "/words", "/import/spreadsheet",
    "/places/labels", `/places/${room.id}`, "/moving", "/queue", "/rules", "/lifecycle", "/ask", "/ask?q=sea%20stories", "/account/legacy", "/admin/health", "/admin/access", "/admin?tab=tab-backups", "/admin?tab=tab-system", "/admin?tab=tab-activity", "/help", "/search?q=ferry&in=mine"];
  const failures = [];
  for (const scheme of ["light", "dark"]) {
    await page.emulateMedia({ colorScheme: scheme });
    for (const url of pages) {
      await page.goto(url, { waitUntil: "networkidle" });
      await page.addScriptTag({ content: axe });
      const violations = await page.evaluate(async () =>
        (await window.axe.run(document, { runOnly: ["wcag2a", "wcag2aa", "wcag21aa", "best-practice"] })).violations
          .map((violation) => `${violation.id}: ${violation.nodes.map((node) => node.target.join(" ")).join(", ")}`));
      failures.push(...violations.map((violation) => `${scheme} ${url} ${violation}`));
    }
  }

  expect(failures, failures.join("\n")).toEqual([]);
});
