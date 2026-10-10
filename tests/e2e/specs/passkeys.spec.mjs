import { test, expect } from "@playwright/test";
import { authenticatorCode, noProblems, ready, signUp, unique } from "../helpers.mjs";

// A virtual authenticator, as a phone or laptop would be: it keeps passkeys and confirms the reader.
async function addAuthenticator(page) {
  const client = await page.context().newCDPSession(page);
  await client.send("WebAuthn.enable");
  await client.send("WebAuthn.addVirtualAuthenticator", {
    options: { protocol: "ctap2", transport: "internal", hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  });
}

async function signOut(page) {
  await page.goto("/");
  await ready(page);
  await Promise.all([page.waitForURL(/\/signin/), page.click('.sidebar-foot button:text-is("Sign out")')]);
}

test("a passkey signs in without the password or the authenticator code", async ({ browser }) => {
  const name = unique("Keyholder");
  const page = await signUp(browser, name);
  await addAuthenticator(page);

  // Two-step sign-in is on, and the passkey still goes straight in.
  await page.goto("/account");
  await ready(page);
  await page.click('button:text-is("Set up two-step sign-in")');
  const secret = (await page.locator("code.key").innerText()).trim();
  await page.fill('input[autocomplete="one-time-code"]', authenticatorCode(secret));
  await page.click('button:text-is("Turn on")');
  await expect(page.locator(".recovery-codes li")).toHaveCount(10);

  await page.fill('input[placeholder="My phone"]', "Test laptop");
  await page.click('button:text-is("Add a passkey")');
  await expect(page.locator("section:has(h2:text('Passkeys')) li")).toContainText("Test laptop");
  await expect(page.locator("section:has(h2:text('Recent activity'))")).toContainText("added a passkey");

  await signOut(page);
  await page.click('button:text-is("Sign in with a passkey")');
  await page.waitForURL((url) => url.pathname === "/");
  await expect(page.locator(".reader-name").first()).toHaveText(name);

  // A passkey removed from the shelf no longer opens it.
  await page.goto("/account");
  await ready(page);
  await page.click('button[aria-label="Remove the passkey Test laptop"]');
  await expect(page.locator("section:has(h2:text('Passkeys')) li")).toHaveCount(0);
  await signOut(page);
  await page.click('button:text-is("Sign in with a passkey")');
  await expect(page.locator("[data-passkey-status]")).toContainText("does not know that passkey");
  // That refusal is the one error the browser should have logged.
  page.problems.splice(0, page.problems.length, ...page.problems.filter((problem) => !problem.includes("/account/passkey/signin")));
  noProblems(page);
});
