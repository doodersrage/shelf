// Copies the pinned front-end files from node_modules into wwwroot, so the app serves them itself.
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const modules = join(here, "node_modules");
const wwwroot = join(here, "..", "wwwroot");

const files = [
  ["@fontsource-variable/inter/files/inter-latin-wght-normal.woff2", "fonts/inter-latin-wght-normal.woff2"],
  ["@fontsource-variable/inter/files/inter-latin-ext-wght-normal.woff2", "fonts/inter-latin-ext-wght-normal.woff2"],
  ["@fontsource-variable/inter/LICENSE", "fonts/Inter-LICENSE.txt"],
  ["@fontsource-variable/lora/files/lora-latin-wght-normal.woff2", "fonts/lora-latin-wght-normal.woff2"],
  ["@fontsource-variable/lora/files/lora-latin-wght-italic.woff2", "fonts/lora-latin-wght-italic.woff2"],
  ["@fontsource-variable/lora/files/lora-latin-ext-wght-normal.woff2", "fonts/lora-latin-ext-wght-normal.woff2"],
  ["@fontsource-variable/lora/LICENSE", "fonts/Lora-LICENSE.txt"],
  ["pdfjs-dist/build/pdf.min.mjs", "lib/pdfjs/pdf.min.mjs"],
  ["pdfjs-dist/build/pdf.worker.min.mjs", "lib/pdfjs/pdf.worker.min.mjs"],
  ["pdfjs-dist/LICENSE", "lib/pdfjs/LICENSE.txt"],
  ["jszip/dist/jszip.min.js", "lib/jszip/jszip.min.js"],
  ["jszip/LICENSE.markdown", "lib/jszip/LICENSE.md"],
  // Barcode reading where the browser has none of its own: ZXing compiled to WebAssembly, served from here.
  ["barcode-detector/dist/iife/ponyfill.js", "lib/barcode/barcode-detector.js"],
  ["barcode-detector/LICENSE", "lib/barcode/LICENSE-barcode-detector.txt"],
  ["zxing-wasm/dist/reader/zxing_reader.wasm", "lib/barcode/zxing_reader.wasm"],
  ["zxing-wasm/LICENSE", "lib/barcode/LICENSE-zxing-wasm.txt"],
];

for (const [from, to] of files) {
  const target = join(wwwroot, to);
  mkdirSync(dirname(target), { recursive: true });
  copyFileSync(join(modules, from), target);
  console.log(`${from} -> wwwroot/${to}`);
}

const pdfjs = JSON.parse(readFileSync(join(modules, "pdfjs-dist/package.json"), "utf8")).version;
writeFileSync(join(wwwroot, "lib/pdfjs/README.txt"), `PDF.js ${pdfjs} (pdfjs-dist), Apache License 2.0, from https://github.com/mozilla/pdf.js\n`);
