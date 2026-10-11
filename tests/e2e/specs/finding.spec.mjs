import { test, expect } from "@playwright/test";
import { createServer } from "node:http";
import { createBook, noProblems, ready, signUp, unique } from "../helpers.mjs";

// Open Library's search, played from here: two books for a title, and an author's newest.
const port = Number(process.env.SHELF_E2E_SEARCH_PORT ?? 5195);
let server;
const asked = [];

test.beforeAll(async () => {
  server = createServer((request, response) => {
    const url = new URL(request.url, "http://localhost");
    asked.push(url.search);
    const docs = url.searchParams.get("q")?.includes("Harbour")
      ? [
          { key: "/works/OL1W", title: "The Grey Harbour", author_name: ["Ada Wren"], first_publish_year: 2019, isbn: ["9780000000019"], number_of_pages_median: 312 },
          { key: "/works/OL2W", title: "Harbour Lights", author_name: ["Tom Quill"], first_publish_year: 2004 },
        ]
      : url.searchParams.get("author") === "Ada Wren"
        ? [
            // Published since she was followed: offered. Before: let go, as is a book of another author's.
            { key: "/works/OL9W", title: "The Far Shore", author_name: ["Ada Wren"], first_publish_year: 2099 },
            { key: "/works/OL1W", title: "The Grey Harbour", author_name: ["Ada Wren"], first_publish_year: 2019 },
            { key: "/works/OL8W", title: "Someone Else's", author_name: ["Bea Other"], first_publish_year: 2099 },
          ]
        : [];
    response.setHeader("Content-Type", "application/json");
    response.end(JSON.stringify({ docs }));
  });
  await new Promise((resolve) => server.listen(port, "127.0.0.1", resolve));
});

test.afterAll(() => server?.close());

test("a book not on the shelf is found and wanted, and a followed author's new book is offered", async ({ browser }) => {
  const page = await signUp(browser, unique("Finder"));
  await createBook(page, { title: "Harbour Lights", author: "Tom Quill", status: "Finished" });

  await page.goto("/find");
  await ready(page);
  await page.getByLabel("Title, author, or ISBN").fill("Harbour");
  await page.getByRole("button", { name: "Search", exact: true }).click();
  const results = page.getByRole("list", { name: "Books found" }).locator("li");
  await expect(results).toHaveCount(2);
  await expect(page).toHaveURL(/\/find\?q=Harbour$/);
  // The address opens the same search.
  await page.reload();
  await ready(page);
  await expect(results).toHaveCount(2);
  // What the shelf has already is marked so.
  await expect(results.nth(1)).toContainText("On your shelf already");

  await results.nth(0).getByRole("button", { name: "Want to read" }).click();
  await expect(results.nth(0)).toContainText("On your want list");
  await results.nth(0).getByRole("link", { name: "Open it" }).click();
  await ready(page);
  await expect(page.locator("h1")).toHaveText("The Grey Harbour");
  await expect(page.locator("body")).toContainText("9780000000019");

  // Following the author, from the Authors page.
  await page.goto("/authors");
  await ready(page);
  await page.getByRole("button", { name: "Follow Ada Wren" }).click();
  await expect(page.locator(".tag-counts li", { hasText: "Ada Wren" })).toContainText("Following");

  await page.goto("/find");
  await ready(page);
  await page.getByRole("button", { name: "Look now" }).click();
  await expect(page.getByRole("status")).toHaveText("1 new book");
  const offered = page.locator("section", { has: page.getByRole("heading", { name: "New from authors you follow" }) }).locator("li");
  await expect(offered).toHaveCount(1);
  await expect(offered).toContainText("The Far Shore");
  // The menu counts what is offered, as it does a series' new books.
  await page.reload();
  await ready(page);
  await expect(page.locator("a[href='/find'] .badge").first()).toHaveText("1");

  await offered.getByRole("button", { name: "Want to read" }).click();
  await expect(page.getByRole("heading", { name: "New from authors you follow" })).toHaveCount(0);
  await page.goto("/library?status=Want");
  await ready(page);
  await expect(page.locator("main")).toContainText("The Far Shore");

  // Looking again offers nothing new; following can stop.
  await page.goto("/find");
  await ready(page);
  await page.getByRole("button", { name: "Look now" }).click();
  await expect(page.getByRole("status")).toHaveText("Nothing new.");
  await page.getByRole("button", { name: "Stop following Ada Wren" }).click();
  await expect(page.getByRole("button", { name: "Look now" })).toHaveCount(0);
  noProblems(page);
});
