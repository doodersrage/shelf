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
  // The EPUB reader's engine, as Audiobookshelf uses: pages turned across the whole book, places as EPUB CFIs.
  ["epubjs/dist/epub.min.js", "lib/epubjs/epub.min.js"],
  ["epubjs/license", "lib/epubjs/LICENSE.txt"],
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

// epub.js tidies its views away on the window's unload. Chrome no longer lets a page listen for unload, and says so in
// the console each time a book opens; the page is going anyway, so the two listeners are left out. If a new epub.js
// has a different number of them, the copy stops here, to be looked at.
const epubjs = join(wwwroot, "lib/epubjs/epub.min.js");
const unloads = /window\.addEventListener\("unload",function\(t\)\{[^{}]*\}\.bind\(this\)\),/g;
const engine = readFileSync(epubjs, "utf8");
const found = engine.match(unloads)?.length ?? 0;
if (found !== 2) {
  throw new Error(`epub.js has ${found} unload listeners where 2 were expected; see copy.mjs.`);
}
writeFileSync(epubjs, engine.replace(unloads, ""));
console.log("wwwroot/lib/epubjs/epub.min.js: unload listeners left out");

const pdfjs = JSON.parse(readFileSync(join(modules, "pdfjs-dist/package.json"), "utf8")).version;
writeFileSync(join(wwwroot, "lib/pdfjs/README.txt"), `PDF.js ${pdfjs} (pdfjs-dist), Apache License 2.0, from https://github.com/mozilla/pdf.js\n`);
