import { defineConfig } from "@playwright/test";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Each run gets an empty shelf of its own, removed by the system's temp cleanup.
const data = process.env.SHELF_E2E_DATA ?? (process.env.SHELF_E2E_DATA = mkdtempSync(join(tmpdir(), "shelf-e2e-")));
const port = process.env.SHELF_E2E_PORT ?? "5199";
// localhost rather than 127.0.0.1: browsers keep passkeys for a host name, never for an address.
const base = `http://localhost:${port}`;

export default defineConfig({
  testDir: "./specs",
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: base,
    trace: "retain-on-failure",
    // CHROMIUM_PATH points at a browser already on the machine; otherwise Playwright's own is used.
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {},
  },
  webServer: {
    command: "dotnet run --project ../../src/Shelf.Api --no-launch-profile",
    url: `http://127.0.0.1:${port}/alive`,
    timeout: 240_000,
    reuseExistingServer: false,
    env: {
      ASPNETCORE_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: base,
      ConnectionStrings__Shelf: `Data Source=${join(data, "shelf.db")}`,
      EbookStore__Root: join(data, "ebooks"),
      AudioStore__Root: join(data, "audio"),
      DataProtection__KeysPath: join(data, "keys"),
      Backup__Enabled: "false",
      Accounts__SignInsPerMinute: "1000",
    },
  },
});
