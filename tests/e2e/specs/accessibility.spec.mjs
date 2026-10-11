import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { createBook, signUp, unique, upload } from "../helpers.mjs";

const axe = readFileSync(createRequire(import.meta.url).resolve("axe-core/axe.min.js"), "utf8");

test("every page passes an accessibility audit in both themes", async ({ browser }) => {
  const page = await signUp(browser, unique("Auditor"));
  const book = await createBook(page, { title: "Moby-Dick", author: "Herman Melville", status: "Reading", pages: 600, currentPage: 120, tags: ["sea"] });
  await upload(page, book.id, "ebook", "moby.epub", "application/epub+zip");
  await upload(page, book.id, "audio", "chapters.m4b", "audio/mp4");
  await page.request.post(`/books/${book.id}/quotes`, { data: { text: "Call me Ishmael.", page: 1 } });

  const pages = ["/", "/?view=list", "/?add=1", `/library/${book.id}`, `/library/${book.id}/read`, `/library/${book.id}/listen`, "/quotes", "/search?q=ishmael",
    "/free", "/import/audiobookshelf", "/import/calibre", "/authors", "/series", "/places", "/copies", "/recommenders", "/years", "/loans", "/shelves", "/stats", "/backup", "/sync", "/account", "/admin"];
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
