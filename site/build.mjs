// Builds Shelf's documentation into site/_site: Markdown pages in content/, one template, and the assets beside it.
// Every link is relative, so the site works at https://<owner>.github.io/shelf/ and from a folder on disk alike.
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Marked } from "marked";
import { markedHighlight } from "marked-highlight";
import hljs from "highlight.js";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const out = join(here, "_site");
const repository = "https://github.com/doodersrage/shelf";

// The sidebar, in order. Each page is content/<slug>.md, apart from the changelog, made from CHANGELOG.md.
const groups = [
  { title: "Get started", pages: ["index", "install", "configuration"] },
  { title: "Using Shelf", pages: ["library", "adding", "reading", "audiobooks", "lending", "devices"] },
  { title: "Running Shelf", pages: ["security", "backups", "server"] },
  { title: "Reference", pages: ["api", "development", "changelog"] },
];

const slugify = (text) =>
  text.toLowerCase().replace(/<[^>]+>/g, "").replace(/&[a-z]+;/g, "").replace(/[^\p{L}\p{N}]+/gu, "-").replace(/^-|-$/g, "");

const escape = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

// Front matter: a few `key: value` lines between --- fences.
function read(slug) {
  const source = slug === "changelog"
    ? "---\ntitle: Changelog\ndescription: What changed in each release of Shelf.\n---\n" + readFileSync(join(root, "CHANGELOG.md"), "utf8")
    : readFileSync(join(here, "content", `${slug}.md`), "utf8");
  const match = source.match(/^---\n([\s\S]*?)\n---\n/);
  const meta = Object.fromEntries((match?.[1] ?? "").split("\n").filter(Boolean).map((line) => {
    const at = line.indexOf(":");
    return [line.slice(0, at).trim(), line.slice(at + 1).trim()];
  }));
  return { slug, meta, body: match ? source.slice(match[0].length) : source };
}

function render(page) {
  const headings = [];
  const used = new Map();
  const marked = new Marked(
    markedHighlight({
      emptyLangClass: "hljs",
      langPrefix: "hljs language-",
      highlight(code, lang) {
        const language = hljs.getLanguage(lang) ? lang : "plaintext";
        return hljs.highlight(code, { language }).value;
      },
    }),
    {
      gfm: true,
      renderer: {
        heading({ tokens, depth }) {
          const html = this.parser.parseInline(tokens);
          let id = slugify(html) || "section";
          const seen = used.get(id) ?? 0;
          used.set(id, seen + 1);
          if (seen) id = `${id}-${seen}`;
          if (depth === 2 || depth === 3) headings.push({ depth, id, text: html.replace(/<[^>]+>/g, "") });
          return `<h${depth} id="${id}"><a class="anchor" href="#${id}" aria-hidden="true" tabindex="-1">#</a>${html}</h${depth}>\n`;
        },
        // GitHub-style callouts: a quote starting [!NOTE], [!TIP], or [!WARNING].
        blockquote({ tokens }) {
          const html = this.parser.parse(tokens);
          const callout = html.match(/^<p>\[!(NOTE|TIP|WARNING)\]\s*/);
          if (!callout) return `<blockquote>${html}</blockquote>\n`;
          const kind = callout[1].toLowerCase();
          const label = { note: "Note", tip: "Tip", warning: "Take care" }[kind];
          return `<div class="callout ${kind}" role="note"><p class="callout-title">${label}</p><p>${html.slice(callout[0].length)}</div>\n`;
        },
        link({ href, title, tokens }) {
          const text = this.parser.parseInline(tokens);
          // Other pages are written as page.md in the sources, so they read on GitHub too.
          const target = href.replace(/^([a-z-]+)\.md(#.*)?$/, "$1.html$2");
          const external = /^https?:/.test(target) && !target.startsWith(repository + "/blob");
          return `<a href="${escape(target)}"${title ? ` title="${escape(title)}"` : ""}${external ? ' rel="noopener"' : ""}>${text}</a>`;
        },
        image({ href, title, text }) {
          return `<img src="${escape(href)}" alt="${escape(text)}"${title ? ` title="${escape(title)}"` : ""} loading="lazy" />`;
        },
      },
    },
  );
  // Wide tables scroll on their own on a phone, rather than the page.
  const html = marked.parse(page.body).replace(/<table>/g, '<div class="table-wrap"><table>').replace(/<\/table>/g, "</table></div>");
  return { html, headings };
}

const pages = groups.flatMap((group) => group.pages.map((slug) => ({ ...read(slug), group: group.title })));
const template = readFileSync(join(here, "template.html"), "utf8");
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const search = [];
pages.forEach((page, index) => {
  const { html, headings } = render(page);
  const file = page.slug === "index" ? "index.html" : `${page.slug}.html`;
  page.file = file;

  const nav = groups.map((group) => `
      <div class="nav-group">
        <p class="nav-title">${group.title}</p>
        <ul>${group.pages.map((slug) => {
          const other = pages.find((item) => item.slug === slug);
          const target = slug === "index" ? "index.html" : `${slug}.html`;
          return `<li><a href="${target}"${slug === page.slug ? ' aria-current="page"' : ""}>${escape(other.meta.nav ?? other.meta.title)}</a></li>`;
        }).join("")}</ul>
      </div>`).join("");

  const toc = headings.filter((heading) => heading.depth === 2).length >= 2
    ? `<nav class="toc" aria-label="On this page"><p class="nav-title">On this page</p><ul>${headings.map((heading) =>
        `<li class="depth-${heading.depth}"><a href="#${heading.id}">${escape(heading.text)}</a></li>`).join("")}</ul></nav>`
    : "";

  const previous = pages[index - 1];
  const next = pages[index + 1];
  const pager = `<nav class="pager" aria-label="Previous and next">${
    previous ? `<a class="previous" href="${previous.slug === "index" ? "index.html" : previous.slug + ".html"}"><span>Previous</span>${escape(previous.meta.nav ?? previous.meta.title)}</a>` : "<span></span>"}${
    next ? `<a class="next" href="${next.slug}.html"><span>Next</span>${escape(next.meta.nav ?? next.meta.title)}</a>` : ""}</nav>`;

  const source = page.slug === "changelog" ? `${repository}/blob/main/CHANGELOG.md` : `${repository}/blob/main/site/content/${page.slug}.md`;
  const filled = template
    .replaceAll("{{title}}", escape(page.slug === "index" ? "Shelf: a personal library you run yourself" : `${page.meta.title} · Shelf docs`))
    .replaceAll("{{description}}", escape(page.meta.description ?? ""))
    .replaceAll("{{nav}}", nav)
    .replaceAll("{{toc}}", toc)
    .replaceAll("{{layout}}", page.meta.layout ?? "page")
    .replaceAll("{{content}}", (page.meta.layout === "home" ? "" : `<p class="eyebrow">${escape(page.group)}</p>`) + html)
    .replaceAll("{{pager}}", page.meta.layout === "home" ? "" : pager)
    .replaceAll("{{source}}", source)
    .replaceAll("{{repository}}", repository)
    .replaceAll("{{base}}", "");
  writeFileSync(join(out, file), filled);

  // One search entry per section: its page, heading, and words.
  // The changelog is searched a release at a time, so each result names its version.
  const sections = html.split(page.slug === "changelog" ? /(?=<h2 id=")/ : /(?=<h[23] id=")/);
  for (const section of sections) {
    const heading = section.match(/^<h[23] id="([^"]+)">(?:<a[^>]*>#<\/a>)?([\s\S]*?)<\/h[23]>/);
    const words = section.replace(/<a class="anchor"[^>]*>#<\/a>/g, "").replace(/<pre[\s\S]*?<\/pre>/g, " ").replace(/<[^>]+>/g, " ").replace(/&[a-z#0-9]+;/g, " ").replace(/\s+/g, " ").trim();
    if (!words) continue;
    search.push({
      page: page.meta.nav ?? page.meta.title,
      title: heading ? heading[2].replace(/<[^>]+>/g, "").trim() : page.meta.title,
      url: file + (heading ? `#${heading[1]}` : ""),
      text: words.slice(0, 1200),
    });
  }
});

writeFileSync(join(out, "search.json"), JSON.stringify(search));
cpSync(join(here, "assets"), join(out, "assets"), { recursive: true });
cpSync(join(root, "docs", "images"), join(out, "assets", "images"), { recursive: true });
mkdirSync(join(out, "assets", "fonts"), { recursive: true });
for (const font of readdirSync(join(root, "src", "Shelf.Api", "wwwroot", "fonts")).filter((name) => name.endsWith(".woff2"))) {
  cpSync(join(root, "src", "Shelf.Api", "wwwroot", "fonts", font), join(out, "assets", "fonts", font));
}

cpSync(join(root, "src", "Shelf.Api", "wwwroot", "favicon.svg"), join(out, "favicon.svg"));
if (existsSync(join(root, "src", "Shelf.Api", "wwwroot", "icon-192.png"))) {
  cpSync(join(root, "src", "Shelf.Api", "wwwroot", "icon-192.png"), join(out, "assets", "icon-192.png"));
}

// GitHub Pages runs files through Jekyll unless told not to.
writeFileSync(join(out, ".nojekyll"), "");
// Served at whatever address was asked for, so its links and styles name the site's folder outright.
const notFound = template
  .replaceAll("{{title}}", "Not found · Shelf docs").replaceAll("{{description}}", "").replaceAll("{{layout}}", "page")
  .replaceAll("{{nav}}", "").replaceAll("{{toc}}", "").replaceAll("{{pager}}", "")
  .replaceAll("{{content}}", '<h1>That page is not here</h1><p>It may have moved. <a href="./">Start from the beginning</a>, or search above.</p>')
  .replaceAll("{{source}}", repository).replaceAll("{{repository}}", repository)
  .replaceAll("{{base}}", "")
  .replace(/(href|src)="(assets\/|favicon\.svg|\.\/)/g, (_, attribute, path) => `${attribute}="/shelf/${path === "./" ? "" : path}`);
writeFileSync(join(out, "404.html"), notFound);
console.log(`Built ${pages.length} pages and ${search.length} search entries into ${out}`);
