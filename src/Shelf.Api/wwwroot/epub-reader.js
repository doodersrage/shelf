// The EPUB reader, on epub.js (as Audiobookshelf's is): the whole book turned a page at a time or scrolled, the
// place kept as an EPUB CFI, highlights found again by their words, and the page in the reader's own colors.
// The book's own scripts never run: see clean, below.

let book = null;
let rendition = null;
let net = null;
let marks = [];
let style = {};
let reported = 0;
let pending = null;
let ready = false;
let hrefs = [];
let titles = [];
let view = null;
let stage = null;
let gone = null;
const fullKey = "shelf-reader-full";
const painted = new Map();

// Shelf's chapter for a part of the book, by its file: the same as epub.js's spine but for an odd book.
function chapterOf(section) {
  if (!section) return 0;
  const found = hrefs.findIndex((href) => href === section.href || href.endsWith(`/${section.href}`));
  return found >= 0 ? found : section.index;
}

function sectionFor(chapter) {
  const href = hrefs[chapter];
  let found = null;
  book?.spine.each((section) => {
    if (!found && href && (href === section.href || href.endsWith(`/${section.href}`))) found = section;
  });
  return found ?? book?.spine.get(chapter);
}

function loadScript(src) {
  if ([...document.scripts].some((script) => script.src.endsWith(src))) return Promise.resolve();
  return new Promise((done, failed) => {
    const script = document.createElement("script");
    script.src = src;
    script.onload = done;
    script.onerror = failed;
    document.head.append(script);
  });
}

const themes = {
  light: { background: "#fffdf9", color: "#252a28", link: "#263e38" },
  sepia: { background: "#f4ecd8", color: "#3d3225", link: "#6b4f2a" },
  dark: { background: "#1d2420", color: "#e8e3d8", link: "#9fc0ad" },
};

function deviceTheme() {
  return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

// The reader's type and colors over the book's own styles, where they would fight.
function rules() {
  const colors = themes[style.theme] ?? themes[deviceTheme()];
  const face = style.font === "serif" ? '"Iowan Old Style", "Palatino Linotype", Georgia, serif'
    : style.font === "sans" ? 'system-ui, -apple-system, "Segoe UI", sans-serif' : null;
  const body = {
    background: `${colors.background} !important`,
    color: `${colors.color} !important`,
    "line-height": `${(style.leading ?? 160) / 100} !important`,
  };
  if (face) body["font-family"] = `${face} !important`;
  const text = { "line-height": "inherit !important" };
  if (face) text["font-family"] = "inherit !important";
  return {
    "html, body": body,
    "p, li, dd, blockquote, div, span": text,
    a: { color: `${colors.link} !important` },
    "img, svg": { "max-width": "100%", height: "auto" },
    ".shelf-mark": { fill: "rgb(213 164 106)", "fill-opacity": "0.35", "mix-blend-mode": "multiply" },
  };
}

// The reading width: the book's column is kept to it, centered, in either layout.
function widthOf() {
  return `calc(${style.width ?? 38}em * ${(style.size ?? 100) / 100} + 4rem)`;
}

function applyStyle() {
  if (!rendition) return;
  rendition.themes.default(rules());
  rendition.themes.fontSize(`${style.size ?? 100}%`);
  const colors = themes[style.theme] ?? themes[deviceTheme()];
  if (view) view.style.background = colors.background;
  // Full screen, the bars and the space around the page take the page's colors.
  stage?.style.setProperty("--epub-page", colors.background);
  stage?.style.setProperty("--epub-ink", colors.color);
}

// ---- Full screen, as Audiobookshelf's reader: the book fills the screen, and a tap in the middle of the page shows
// or hides the bars with the way out, the contents, the type, and where in the book. The browser's own full screen is
// asked for too where it allows (not on an iPhone); either way the reader covers the page it sits on.

function isFull() {
  return stage?.hasAttribute("data-full") ?? false;
}

function showChrome(shown) {
  if (!isFull()) return;
  if (shown) stage.setAttribute("data-chrome", "");
  else stage.removeAttribute("data-chrome");
}

async function setFull(on, browserToo) {
  if (!stage || on === isFull()) return;
  // Before the first page is drawn there is no place yet, and nothing to lay out again.
  const drawn = !!rendition?.manager;
  const at = drawn ? rendition.currentLocation()?.start?.cfi : null;
  if (on) {
    stage.setAttribute("data-full", "");
    stage.setAttribute("role", "dialog");
    stage.setAttribute("aria-modal", "true");
    stage.setAttribute("aria-label", view?.getAttribute("aria-label") ?? "");
    document.documentElement.classList.add("reading-full");
    showChrome(true);
    // Asked for in the tap itself, as browsers require; a refusal leaves the reader covering the page all the same.
    if (browserToo && stage.requestFullscreen && !document.fullscreenElement) stage.requestFullscreen({ navigationUI: "hide" }).catch(() => {});
  } else {
    stage.removeAttribute("data-full");
    stage.removeAttribute("data-chrome");
    stage.removeAttribute("role");
    stage.removeAttribute("aria-modal");
    stage.removeAttribute("aria-label");
    document.documentElement.classList.remove("reading-full");
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  }

  try { localStorage.setItem(fullKey, on ? "1" : "0"); } catch { /* private */ }
  // The page draws the bars' controls only while they can be seen.
  net?.invokeMethodAsync("FullChanged", on);
  // Laid out afresh for the new size, at the same place, once the browser has drawn it.
  if (!drawn) return;
  await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  if (!rendition?.manager) return;
  rendition.resize();
  if (at) await rendition.display(at);
  if (on) view?.querySelector("iframe")?.contentWindow?.focus();
}

// The buttons marked data-full-toggle go in and out; handled here, in the tap, so the browser allows full screen.
function fullClick(event) {
  const button = event.target.closest?.("[data-full-toggle]");
  if (!button || !stage) return;
  event.preventDefault();
  setFull(!isFull(), true);
}

// Leaving the browser's full screen (by Esc, which the page never sees) leaves the reader's as well; but not as the
// page itself goes (a reload ends the browser's full screen too), so the reader opens full screen again after it.
let unloading = false;
addEventListener("beforeunload", () => { unloading = true; });
addEventListener("pagehide", () => { unloading = true; });

function fullChanged() {
  if (!document.fullscreenElement && isFull() && !unloading) setFull(false, false);
}

export function full(on) {
  return setFull(on, false);
}

// The text around a range, for finding a highlight again however the page is laid out.
function surrounding(range, doc, before) {
  const whole = doc.createRange();
  whole.selectNodeContents(doc.body);
  if (before) whole.setEnd(range.startContainer, range.startOffset);
  else whole.setStart(range.endContainer, range.endOffset);
  const text = whole.toString().replace(/\s+/g, " ");
  return before ? text.slice(-48) : text.slice(0, 48);
}

// A range for a passage in a part of the book, by its words and the words around them.
function findQuote(doc, text, prefix, suffix) {
  const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT);
  const nodes = [];
  let whole = "";
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    nodes.push({ node, start: whole.length });
    whole += node.nodeValue;
  }

  const squash = (value) => value.replace(/\s+/g, " ");
  // Positions in the squashed text map back to the original one.
  const map = [];
  let flat = "";
  for (let i = 0; i < whole.length; i++) {
    const space = /\s/.test(whole[i]);
    if (space && flat.endsWith(" ")) continue;
    flat += space ? " " : whole[i];
    map.push(i);
  }

  const wanted = squash(text).trim();
  if (!wanted) return null;
  let at = -1;
  const withContext = squash(prefix ?? "") + wanted + squash(suffix ?? "");
  const around = flat.indexOf(withContext);
  if (around >= 0) at = around + squash(prefix ?? "").length;
  if (at < 0) at = flat.indexOf(wanted);
  if (at < 0) return null;

  const startIndex = map[at];
  const endIndex = map[Math.min(at + wanted.length - 1, map.length - 1)] + 1;
  const locate = (index) => {
    for (let i = nodes.length - 1; i >= 0; i--) {
      if (nodes[i].start <= index) return { node: nodes[i].node, offset: Math.min(index - nodes[i].start, nodes[i].node.nodeValue.length) };
    }
    return null;
  };
  const start = locate(startIndex);
  const end = locate(endIndex);
  if (!start || !end) return null;
  const range = doc.createRange();
  range.setStart(start.node, start.offset);
  range.setEnd(end.node, end.offset);
  return range;
}

function paint(contents, section) {
  if (!rendition || !contents?.document?.body) return;
  const chapter = chapterOf(section);
  for (const mark of marks.filter((item) => item.chapter === chapter)) {
    if (painted.has(mark.id)) continue;
    const range = findQuote(contents.document, mark.text, mark.prefix, mark.suffix);
    if (!range) continue;
    const cfi = section.cfiFromRange(range);
    rendition.annotations.highlight(cfi, { id: mark.id }, () => net?.invokeMethodAsync("OpenMark", mark.id), "shelf-mark", { fill: "rgb(213, 164, 106)", "fill-opacity": "0.4", "mix-blend-mode": "multiply" });
    painted.set(mark.id, cfi);
  }
}

const blocked = "script, iframe, frame, frameset, object, embed, applet, form, base, meta[http-equiv]";

export function clean(markup) {
  try {
    const kind = /^\s*<\?xml|xmlns="http:\/\/www\.w3\.org\/1999\/xhtml"/.test(markup) ? "application/xhtml+xml" : "text/html";
    const doc = new DOMParser().parseFromString(markup, kind);
    if (doc.getElementsByTagName("parsererror").length) throw new Error("not well formed");
    for (const node of [...doc.querySelectorAll(blocked)]) node.remove();
    for (const element of doc.querySelectorAll("*")) {
      for (const attribute of [...element.attributes]) {
        const name = attribute.name.toLowerCase();
        const value = attribute.value.replace(/[\s\u0000-\u001f]/g, "").toLowerCase();
        if (name.startsWith("on") || ((name === "href" || name === "src" || name.endsWith(":href") || name === "action" || name === "formaction") && value.startsWith("javascript:"))) {
          element.removeAttribute(attribute.name);
        }
      }
    }
    const head = doc.querySelector("head") ?? doc.documentElement;
    const policy = doc.createElementNS(head.namespaceURI ?? "http://www.w3.org/1999/xhtml", "meta");
    policy.setAttribute("http-equiv", "Content-Security-Policy");
    policy.setAttribute("content", "script-src 'none'; object-src 'none'; frame-src 'none'; form-action 'none'");
    head.prepend(policy);
    return kind === "text/html" ? "<!DOCTYPE html>" + doc.documentElement.outerHTML : new XMLSerializer().serializeToString(doc);
  } catch {
    // A part that will not parse is shown as text alone, with nothing in it that could run.
    const text = markup.replace(/<[^>]*>/g, " ");
    return `<!DOCTYPE html><html><head><meta http-equiv="Content-Security-Policy" content="script-src 'none'"></head><body><p>${text.replace(/[&<]/g, (c) => (c === "&" ? "&amp;" : "&lt;"))}</p></body></html>`;
  }
}

// The parts of the book drawn now.
function views() {
  const all = rendition?.manager?.views;
  return typeof all?.all === "function" ? all.all() : [];
}

function repaint() {
  for (const cfi of painted.values()) rendition?.annotations.remove(cfi, "highlight");
  painted.clear();
  for (const shown of views()) paint(shown.contents, shown.section);
}

// How far into its part a place is, by the words before it: the same on any screen, for the audiobook and the recap.
function shareOf(location) {
  const shown = views().find((item) => item.section?.index === location?.start?.index);
  const doc = shown?.contents?.document;
  if (!doc?.body) return null;
  try {
    const range = shown.contents.range(location.start.cfi);
    const before = doc.createRange();
    before.selectNodeContents(doc.body);
    before.setEnd(range.startContainer, range.startOffset);
    const total = doc.body.textContent.length;
    return total > 0 ? Math.min(1, before.toString().length / total) : 0;
  } catch {
    return null;
  }
}

// The place a share of the way into a part, by its words, as an EPUB CFI.
function placeAt(section, contents, share) {
  const doc = contents?.document;
  if (!doc?.body) return null;
  const target = Math.floor(doc.body.textContent.length * share);
  const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT);
  let seen = 0;
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (seen + node.nodeValue.length >= target) {
      const range = doc.createRange();
      range.setStart(node, Math.max(0, target - seen));
      range.collapse(true);
      return section.cfiFromRange(range);
    }
    seen += node.nodeValue.length;
  }
  return null;
}

function turn(step) {
  if (!rendition) return;
  // A page turned is a page read: the bars step out of the way.
  showChrome(false);
  if (step > 0) rendition.next();
  else rendition.prev();
}

// A tap at a place across the box showing the page: its sides turn it, and full screen, its middle shows the bars.
function tapAt(x) {
  const box = view?.getBoundingClientRect();
  if (!box) return;
  if (style.flow !== "scrolled" && x - box.left < box.width * 0.25) turn(-1);
  else if (style.flow !== "scrolled" && x - box.left > box.width * 0.75) turn(1);
  else if (isFull()) showChrome(!stage.hasAttribute("data-chrome"));
}

// Beside the book's column (a wide screen leaves room either side), taps count as on the page.
function marginClick(event) {
  if (event.target.closest?.("iframe")) return;
  tapAt(event.clientX);
}

// Taps on the page's sides, swipes, and the arrow keys turn it; a tap in the middle, or a selection, does not.
function controls(contents) {
  const doc = contents.document;
  let startX = null;
  let startY = null;
  doc.addEventListener("keydown", (event) => keys(event));
  doc.addEventListener("touchstart", (event) => {
    startX = event.changedTouches[0].screenX;
    startY = event.changedTouches[0].screenY;
  }, { passive: true });
  doc.addEventListener("touchend", (event) => {
    if (startX === null || style.flow === "scrolled") return;
    const dx = event.changedTouches[0].screenX - startX;
    const dy = event.changedTouches[0].screenY - startY;
    startX = null;
    if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) turn(dx < 0 ? 1 : -1);
  }, { passive: true });
  doc.addEventListener("click", (event) => {
    if (event.target.closest?.("a") || doc.getSelection()?.toString()) return;
    // Where on the box showing the page the tap fell, since a chapter's frame runs wider than the box.
    const frame = doc.defaultView.frameElement;
    if (frame) tapAt(frame.getBoundingClientRect().left + event.clientX);
  });
}

function keys(event) {
  if (event.target?.closest?.("input, textarea, select, [contenteditable]") || event.altKey || event.ctrlKey || event.metaKey) return;
  if (event.key === "Escape" && isFull()) {
    event.preventDefault();
    setFull(false, false);
    return;
  }

  if ((event.key === "f" || event.key === "F") && stage) {
    event.preventDefault();
    setFull(!isFull(), true);
    return;
  }

  const step = { ArrowRight: 1, PageDown: 1, ArrowLeft: -1, PageUp: -1 }[event.key] ?? (event.key === " " && style.flow !== "scrolled" ? (event.shiftKey ? -1 : 1) : 0);
  if (step) {
    event.preventDefault();
    turn(step);
  }
}

// Where the reader is, told at most once a second: the CFI, the part of the book and how far into it, how far into
// the whole book (once the book's places are counted), and the page of the part.
function report(location) {
  if (!location?.start || !net) return;
  const start = location.start;
  const page = start.displayed?.page ?? 1;
  const pages = start.displayed?.total ?? 1;
  const percent = ready ? book.locations.percentageFromCfi(start.cfi) : null;
  const send = () => {
    reported = Date.now();
    pending = null;
    const share = shareOf(location) ?? (pages > 1 ? (page - 1) / pages : 0);
    net.invokeMethodAsync("Relocated", start.cfi, chapterOf(book.spine.get(start.href) ?? book.spine.get(start.index)), share, percent ?? -1, page, pages);
  };
  clearTimeout(pending);
  if (Date.now() - reported > 1000) send();
  else pending = setTimeout(send, 1000);
}

export async function open(host, options, dotNet) {
  close();
  net = dotNet;
  marks = options.marks ?? [];
  hrefs = options.hrefs ?? [];
  titles = options.titles ?? [];
  style = { theme: options.theme || null, font: options.font || null, size: options.size, leading: options.leading, width: options.width, flow: options.flow };
  await loadScript("/lib/jszip/jszip.min.js");
  await loadScript("/lib/epubjs/epub.min.js");
  book = window.ePub(options.url, { openAs: "epub" });
  host.replaceChildren();
  view = host;
  host.addEventListener("click", marginClick);
  stage = host.closest(".epub-stage");
  document.addEventListener("click", fullClick);
  document.addEventListener("fullscreenchange", fullChanged);
  // Full screen last time on this device: so again, before the book is laid out, at the size it will have (the
  // browser's own full screen waits for a tap).
  try {
    if (localStorage.getItem(fullKey) === "1") await setFull(true, false);
  } catch {
    // Without storage, the reader opens as it sits on the page.
  }

  const column = document.createElement("div");
  column.className = "epub-column";
  column.style.maxWidth = widthOf();
  column.style.margin = "0 auto";
  host.append(column);
  rendition = book.renderTo(column, {
    width: "100%",
    height: "100%",
    flow: options.flow === "scrolled" ? "scrolled-doc" : "paginated",
    spread: "none",
    allowScriptedContent: true,
  });
  rendition.hooks.content.register((contents) => controls(contents));
  // Nothing in the book may run. Each part is cleaned before it is drawn: scripts, handlers, javascript: links,
  // forms, and embedded frames and objects taken out; and it carries a policy that refuses any script at all, so
  // anything the cleaning missed is stopped by the browser. The frame itself allows scripts only so that Shelf's
  // own taps, swipes, and keys reach the page (browsers give no events to a frame that allows none).
  rendition.on("rendered", (section, shown) => {
    // A frame a screen reader can name: its chapter's title.
    shown.iframe?.setAttribute("title", titles[chapterOf(section)] ?? options.title ?? "");
    paint(shown.contents, section);
  });
  rendition.on("relocated", report);
  rendition.on("selected", (cfiRange, contents) => {
    const range = contents.range(cfiRange);
    const text = range?.toString().trim();
    const location = rendition.currentLocation()?.start;
    const section = chapterOf(book.spine.get(location?.href) ?? book.spine.get(location?.index ?? 0));
    if (text) net?.invokeMethodAsync("SelectedIn", text, surrounding(range, contents.document, true), surrounding(range, contents.document, false), section);
  });
  document.addEventListener("keydown", keys);
  // Taken off the page (by going elsewhere), the reader stops at once, before the page itself is done with it.
  gone?.disconnect();
  gone = new MutationObserver(() => {
    if (!host.isConnected) close();
  });
  gone.observe(document.body, { childList: true, subtree: true });
  applyStyle();

  await book.ready;
  // epub.js runs the spine's hook (not the renderer's) on each part, and its own hook, which puts in the addresses
  // of the book's pictures, rewrites the part from the original markup: so this one comes after it, and cleans the
  // part as it will be drawn.
  book.spine.hooks.serialize.register((output, section) => {
    section.output = clean(section.output ?? output);
  });
  if (options.cfi) {
    await rendition.display(options.cfi).catch(() => rendition.display());
    // The first word of a page can fall at the end of the page before; if the place is past what shows, go on.
    const shown = rendition.currentLocation();
    try {
      if (shown?.end?.cfi && new window.ePub.CFI().compare(options.cfi, shown.end.cfi) > 0) await rendition.next();
    } catch {
      // A place that will not compare is shown as it is.
    }
  } else {
    const section = sectionFor(options.chapter ?? 0);
    await rendition.display(section?.href);
    // A place known only as a share of its part (from the audiobook, say): found by its words.
    if (options.at > 0) {
      const shown = views().find((item) => item.section?.index === section?.index);
      const cfi = shown && placeAt(section, shown.contents, options.at);
      if (cfi) await rendition.display(cfi);
    }
  }

  // The book's places, counted once and kept on this device, give how far through the whole book a place is.
  const key = `shelf-epub-places-${options.key}`;
  try {
    const kept = localStorage.getItem(key);
    if (kept) {
      book.locations.load(kept);
      ready = true;
      report(rendition.currentLocation());
    } else {
      book.locations.generate(1600).then(() => {
        ready = true;
        try { localStorage.setItem(key, book.locations.save()); } catch { /* full or private */ }
        report(rendition.currentLocation());
      });
    }
  } catch {
    // Without storage, the share of the book is simply not shown.
  }
}

export function next() { turn(1); }

export function prev() { turn(-1); }

export async function goTo(chapter) {
  const section = sectionFor(chapter);
  if (section) await rendition.display(section.href);
}

// A share of the whole book, from the bar's slider: once the book's places are counted.
export async function goToShare(share) {
  if (!rendition || !ready) return;
  const cfi = book.locations.cfiFromPercentage(Math.min(1, Math.max(0, share)));
  if (cfi) await rendition.display(cfi);
}

export async function goToCfi(cfi) {
  if (cfi) await rendition?.display(cfi);
}

export async function setStyle(options) {
  const flowChanged = options.flow && options.flow !== style.flow;
  const widthChanged = (options.width && options.width !== style.width) || (options.size && options.size !== style.size);
  style = { ...style, ...options };
  applyStyle();
  if (!rendition || (!flowChanged && !widthChanged)) return;
  // Laid out afresh, at the same place.
  const at = rendition.currentLocation()?.start?.cfi;
  const column = rendition.manager?.container?.closest(".epub-column");
  if (column) column.style.maxWidth = widthOf();
  if (flowChanged) rendition.flow(style.flow === "scrolled" ? "scrolled-doc" : "paginated");
  rendition.resize();
  if (at) await rendition.display(at);
}

export function setMarks(list) {
  marks = list ?? [];
  repaint();
}

export function clearSelection() {
  for (const shown of views()) shown.contents?.window?.getSelection()?.removeAllRanges();
}

// The words from the top of the page to the end of its part, for reading aloud from where the reader is.
export function textFromHere() {
  const location = rendition?.currentLocation();
  const shown = views().find((item) => item.section?.index === location?.start?.index);
  if (!location?.start || !shown?.contents) return null;
  const chapter = chapterOf(shown.section);
  try {
    const range = shown.contents.range(location.start.cfi);
    const rest = shown.contents.document.createRange();
    rest.setStart(range.startContainer, range.startOffset);
    rest.setEndAfter(shown.contents.document.body.lastChild ?? shown.contents.document.body);
    return { chapter, text: rest.toString() };
  } catch {
    return { chapter, text: shown.contents.document.body.innerText };
  }
}

export function close() {
  gone?.disconnect();
  gone = null;
  document.removeEventListener("keydown", keys);
  document.removeEventListener("click", fullClick);
  document.removeEventListener("fullscreenchange", fullChanged);
  document.documentElement.classList.remove("reading-full");
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  stage = null;
  clearTimeout(pending);
  painted.clear();
  ready = false;
  try { book?.destroy(); } catch { /* already gone */ }
  book = null;
  rendition = null;
}
