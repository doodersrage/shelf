import { expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { createHmac } from "node:crypto";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
export const PASSWORD = "a long enough password";

export const fixture = (name) => join(here, "fixtures", name);

// Reader names are unique per test, so the tests can share one shelf without seeing each other's books.
export const unique = (name) => `${name} ${Math.random().toString(36).slice(2, 7)}`;

export async function signUp(browser, name, options = {}) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...options });
  const page = await context.newPage();
  page.problems = [];
  // Each problem notes how many times the page had navigated when it came, so a live connection cut off by leaving
  // the page (which Firefox reports and Chromium does not) can be told from one that failed on a page that stayed.
  page.navigations = 0;
  page.on("framenavigated", (frame) => {
    if (frame === page.mainFrame()) page.navigations++;
  });
  page.on("console", (message) => {
    if (message.type() === "error" && !/favicon/.test(message.location().url)) {
      page.problems.push({ text: `${message.text()} @ ${message.location().url}`, at: page.navigations });
    }
  });
  page.on("pageerror", (error) => page.problems.push({ text: error.message, at: page.navigations }));
  await page.goto("/signup");
  await page.fill("input[name=name]", name);
  await page.fill("input[name=password]", PASSWORD);
  await page.fill("input[name=confirm]", PASSWORD);
  await Promise.all([page.waitForURL("**/", { waitUntil: "domcontentloaded" }), page.click("button[type=submit]")]);
  return page;
}

// Interactive pages answer clicks once their live connection is up.
// Waits until a page's live connection is up (live.js marks it on <html>), or a page with no interactive parts has
// had its moment.
export async function ready(page) {
  await page.waitForFunction(() => window.Blazor !== undefined);
  await page.waitForFunction(() => ["connected", "failed"].includes(document.documentElement.dataset.live), null, { timeout: 5_000 }).catch(() => {});
  await page.waitForTimeout(250);
}

export async function createBook(page, book) {
  const response = await page.request.post("/books", { data: { status: "Want", tags: [], ...book } });
  expect(response.status()).toBe(201);
  return response.json();
}

export async function upload(page, id, kind, file, type) {
  const response = await page.request.post(`/books/${id}/${kind}`, {
    multipart: { file: { name: file, mimeType: type, buffer: readFileSync(fixture(file)) } },
  });
  expect(response.ok()).toBeTruthy();
}

// The code an authenticator app would show now, for a base32 secret.
export function authenticatorCode(secret) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const character of secret.replace(/=+$/, "")) {
    bits += alphabet.indexOf(character).toString(2).padStart(5, "0");
  }

  const key = Buffer.from(bits.match(/.{8}/g).map((byte) => parseInt(byte, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(Math.floor(Date.now() / 1000 / 30)));
  const hash = createHmac("sha1", key).update(counter).digest();
  const offset = hash[19] & 0x0f;
  const value = hash.readUInt32BE(offset) & 0x7fffffff;
  return String(value % 1_000_000).padStart(6, "0");
}

const connecting = /NetworkError when attempting to fetch|Invocation canceled due to the underlying connection being closed|Circuit host not initialized|The operation was aborted|WebSocket is not in the OPEN state|connection was closed before the hub handshake|Failed to start the circuit|connection could not be found on the server|Failed to complete negotiation|Failed to start the (connection|transport)/;

export function noProblems(page) {
  const left = page.problems.filter((problem) => !(connecting.test(problem.text) && problem.at < page.navigations)).map((problem) => problem.text);
  expect(left, left.join("\n")).toEqual([]);
}
