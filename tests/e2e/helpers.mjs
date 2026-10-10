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
  page.on("console", (message) => {
    if (message.type() === "error" && !/favicon/.test(message.location().url)) {
      page.problems.push(`${message.text()} @ ${message.location().url}`);
    }
  });
  page.on("pageerror", (error) => page.problems.push(error.message));
  await page.goto("/signup");
  await page.fill("input[name=name]", name);
  await page.fill("input[name=password]", PASSWORD);
  await page.fill("input[name=confirm]", PASSWORD);
  await Promise.all([page.waitForURL("**/", { waitUntil: "domcontentloaded" }), page.click("button[type=submit]")]);
  return page;
}

// Interactive pages answer clicks once their live connection is up.
export async function ready(page) {
  await page.waitForFunction(() => window.Blazor !== undefined);
  await page.waitForTimeout(1200);
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

export function noProblems(page) {
  expect(page.problems, page.problems.join("\n")).toEqual([]);
}
