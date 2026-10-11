// The offline reader: the books kept on this device, read from the cache, with the place sent back later.
import * as pdfjs from "/lib/pdfjs/pdf.min.mjs";
import * as marks from "/offline-marks.js";

const t = (text, ...values) => (window.t ? window.t(text, ...values) : text);
window.translatePage?.();

pdfjs.GlobalWorkerOptions.workerSrc = "/lib/pdfjs/pdf.worker.min.mjs";

const BOOKS = "shelf-books";
const $ = (id) => document.getElementById(id);
let book = null;
let index = 0;
let count = 0;
let show = null;
let picked = null;

function kept() {
  try {
    return JSON.parse(localStorage.getItem("shelf-kept") || "[]");
  } catch {
    return [];
  }
}

function keptAudio() {
  try {
    return JSON.parse(localStorage.getItem("shelf-kept-audio") || "[]");
  } catch {
    return [];
  }
}

// The player's words, in the page's language; listen.js reads them from these attributes.
function labelPlayer() {
  const label = (id, text) => $(id).setAttribute("aria-label", text);
  label("prev", t("Previous chapter"));
  label("jump-back", t("Back"));
  label("jump-forward", t("Forward"));
  label("next-chapter", t("Next chapter"));
  label("slower", t("Slower"));
  label("faster", t("Faster"));
  label("scrub", t("Place in this chapter"));
  $("toggle").dataset.labelPlay = t("Play");
  $("toggle").dataset.labelPause = t("Pause");
  $("toggle").setAttribute("aria-label", t("Play"));
  $("book-left").dataset.template = t("{0} left");
  $("chapter-number").dataset.template = t("Chapter {0} of {1}");
}

// A kept audiobook plays from this device: what the player needs was kept with its tracks.
async function listen(item) {
  const kept = await (await caches.open(BOOKS)).match(`/books/${item.id}/audio/plan`);
  if (!kept) {
    location.hash = "";
    return;
  }

  $("shelf").hidden = true;
  $("reader").hidden = true;
  $("listen").hidden = false;
  $("listen").dataset.book = String(item.id);
  await window.shelfPlayer?.open(await kept.json());
}

function place(id) {
  try {
    return JSON.parse(localStorage.getItem(`shelf-place-${id}`) || "null");
  } catch {
    return null;
  }
}

function remember(id, at) {
  try {
    localStorage.setItem(`shelf-place-${id}`, JSON.stringify({ at, waiting: true }));
  } catch {
  }
}

// Highlights made or removed here go to the shelf, and its own come back down; then the open page is redrawn.
async function sendMarks() {
  for (const item of kept()) {
    try {
      await marks.send(item.id);
    } catch {
      return;
    }
  }

  if (book) {
    showMarks();
  }
}

// Places read offline go back to the shelf once it answers again; the shelf keeps whichever place is further.
async function sendPlaces() {
  for (const item of kept()) {
    const saved = place(item.id);
    if (!saved?.waiting) {
      continue;
    }

    try {
      const response = await fetch(`/books/${item.id}/place`, {
        method: "PUT",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ebookChapter: saved.at }),
      });
      if (response.ok || response.status === 404) {
        localStorage.setItem(`shelf-place-${item.id}`, JSON.stringify({ at: saved.at, waiting: false }));
      }
    } catch {
      return;
    }
  }
}

function listBooks() {
  const books = kept();
  const recordings = keptAudio();
  const list = $("kept");
  list.replaceChildren();
  $("nothing").hidden = books.length + recordings.length > 0;
  $("listening").hidden = recordings.length === 0;
  const audioList = $("kept-audio");
  audioList.replaceChildren();
  for (const item of recordings) {
    const card = document.createElement("li");
    card.className = "strip-card";
    const body = document.createElement("div");
    const title = document.createElement("p");
    title.className = "title";
    const link = document.createElement("a");
    link.href = `#listen-${item.id}`;
    link.textContent = item.title;
    title.append(link);
    const by = document.createElement("p");
    by.className = "by";
    by.textContent = item.author;
    body.append(title, by);
    const cover = document.createElement("span");
    cover.className = "cover-frame";
    const spine = document.createElement("span");
    spine.className = "spine";
    spine.setAttribute("aria-hidden", "true");
    spine.textContent = item.title;
    cover.append(spine);
    card.append(cover, body);
    audioList.append(card);
  }

  for (const item of books) {
    const card = document.createElement("li");
    card.className = "strip-card";
    const body = document.createElement("div");
    const title = document.createElement("p");
    title.className = "title";
    const link = document.createElement("a");
    link.href = `#book-${item.id}`;
    link.textContent = item.title;
    title.append(link);
    const by = document.createElement("p");
    by.className = "by";
    by.textContent = item.author;
    body.append(title, by);
    const cover = document.createElement("span");
    cover.className = "cover-frame";
    const spine = document.createElement("span");
    spine.className = "spine";
    spine.setAttribute("aria-hidden", "true");
    spine.textContent = item.title;
    cover.append(spine);
    card.append(cover, body);
    list.append(card);
  }
}

async function open(item) {
  const response = await (await caches.open(BOOKS)).match(`/books/${item.id}/ebook/file`);
  if (!response) {
    $("page").textContent = t("This book's file is no longer on this device.");
    return;
  }

  book = item;
  index = place(item.id)?.at ?? 0;
  $("shelf").hidden = true;
  $("reader").hidden = false;
  $("title").textContent = item.title;
  const data = await response.arrayBuffer();
  show = item.type === "pdf" ? await pdfReader(data) : await epubReader(data);
  index = Math.min(Math.max(index, 0), count - 1);
  await turn(0);
}

async function turn(step) {
  index = Math.min(Math.max(index + step, 0), count - 1);
  $("previous").disabled = index === 0;
  $("next").disabled = index >= count - 1;
  $("where").textContent = book.type === "pdf" ? t("Page {0} of {1}", index + 1, count) : t("Chapter {0} of {1}", index + 1, count);
  await show(index);
  hideMarking();
  showMarks();
  remember(book.id, index);
  $("page").scrollTop = 0;
  window.scrollTo(0, 0);
}

async function pdfReader(data) {
  const doc = await pdfjs.getDocument({ data, isEvalSupported: false, enableXfa: false }).promise;
  count = doc.numPages;
  return async (at) => {
    const page = await doc.getPage(at + 1);
    const natural = page.getViewport({ scale: 1 });
    // The width inside the page's padding, so the text layer lies exactly over the drawing.
    const style = getComputedStyle($("page"));
    const width = Math.max(240, $("page").clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight));
    const viewport = page.getViewport({ scale: (width / natural.width) * (Number($("size").value) / 100) });
    const sheet = document.createElement("div");
    sheet.className = "pdf-page";
    sheet.style.width = `${Math.floor(viewport.width)}px`;
    sheet.style.height = `${Math.floor(viewport.height)}px`;
    sheet.style.setProperty("--scale-factor", String(viewport.scale));
    const canvas = document.createElement("canvas");
    const ratio = window.devicePixelRatio || 1;
    canvas.width = Math.floor(viewport.width * ratio);
    canvas.height = Math.floor(viewport.height * ratio);
    canvas.style.width = sheet.style.width;
    canvas.style.height = sheet.style.height;
    canvas.setAttribute("aria-hidden", "true");
    const text = document.createElement("div");
    text.className = "textLayer";
    sheet.append(canvas, text);
    $("page").replaceChildren(sheet);
    await page.render({ canvasContext: canvas.getContext("2d"), viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0] }).promise;
    await new pdfjs.TextLayer({ textContentSource: page.streamTextContent(), container: text, viewport }).render();
  };
}

// An EPUB is a zip: its container names the package, whose spine lists the chapters in order.
async function epubReader(data) {
  const zip = await JSZip.loadAsync(data);
  const parse = (text, type) => new DOMParser().parseFromString(text, type);
  const container = parse(await zip.file("META-INF/container.xml").async("string"), "application/xml");
  const packagePath = container.querySelector("rootfile").getAttribute("full-path");
  const folder = packagePath.includes("/") ? packagePath.slice(0, packagePath.lastIndexOf("/") + 1) : "";
  const opf = parse(await zip.file(packagePath).async("string"), "application/xml");
  const items = new Map([...opf.querySelectorAll("manifest > item")].map((item) => [item.getAttribute("id"), item.getAttribute("href")]));
  const chapters = [...opf.querySelectorAll("spine > itemref")].map((ref) => resolve(folder, items.get(ref.getAttribute("idref")) || "")).filter((path) => zip.file(path));
  count = chapters.length;
  const urls = [];

  return async (at) => {
    urls.splice(0).forEach((url) => URL.revokeObjectURL(url));
    const path = chapters[at];
    const base = path.includes("/") ? path.slice(0, path.lastIndexOf("/") + 1) : "";
    const html = await zip.file(path).async("string");
    let doc = parse(html, "application/xhtml+xml");
    if (doc.querySelector("parsererror")) {
      doc = parse(html, "text/html");
    }

    // Only the words and pictures come across: no scripts, styles, frames, or forms from the book.
    doc.querySelectorAll("script, style, link, iframe, object, embed, form, meta, base").forEach((element) => element.remove());
    for (const element of doc.querySelectorAll("*")) {
      for (const attribute of [...element.attributes]) {
        const name = attribute.name.toLowerCase();
        if (name.startsWith("on") || (/href|src/.test(name) && /^\s*javascript:/i.test(attribute.value))) {
          element.removeAttribute(attribute.name);
        }
      }
    }

    for (const image of doc.querySelectorAll("img[src], image")) {
      const source = image.getAttribute("src") || image.getAttribute("href") || image.getAttribute("xlink:href");
      const file = source ? zip.file(resolve(base, source)) : null;
      if (file) {
        const url = URL.createObjectURL(await file.async("blob"));
        urls.push(url);
        image.setAttribute(image.tagName.toLowerCase() === "img" ? "src" : "href", url);
      }
    }

    const page = $("page");
    page.style.fontSize = `${$("size").value}%`;
    page.replaceChildren(...[...(doc.body || doc.documentElement).childNodes].map((node) => document.importNode(node, true)));
  };
}

// Where a book's words are on the page: the PDF's text layer, or the chapter itself.
function words() {
  return book?.type === "pdf" ? $("page").querySelector(".textLayer") : $("page");
}

function showMarks() {
  const container = words();
  const here = marks.load(book.id).filter((mark) => mark.chapter === index && !mark.deleted);
  if (container) {
    marks.paint(container, here);
  }

  $("marks-heading").textContent = book.type === "pdf" ? t("On this page") : t("In this chapter");
  $("marks-here").hidden = here.length === 0;
  $("marks-waiting").hidden = !marks.waiting(book.id);
  const list = $("marks");
  list.replaceChildren();
  for (const mark of here) {
    const item = document.createElement("li");
    const quote = document.createElement("blockquote");
    const words = document.createElement("p");
    words.textContent = mark.text;
    quote.append(words);
    item.append(quote);
    if (mark.note) {
      const note = document.createElement("p");
      note.textContent = mark.note;
      item.append(note);
    }

    if (mark.pending) {
      const hint = document.createElement("p");
      hint.className = "hint";
      hint.textContent = t("On this device; sent to the shelf when you are online.");
      item.append(hint);
    }

    const remove = document.createElement("button");
    remove.type = "button";
    remove.textContent = t("Remove");
    remove.addEventListener("click", () => {
      marks.remove(book.id, mark.id);
      showMarks();
      if (navigator.onLine) {
        sendMarks();
      }
    });
    item.append(remove);
    list.append(item);
  }
}

function pick() {
  const container = words();
  const chosen = container ? marks.selected(container) : null;
  if (!chosen) {
    return;
  }

  picked = chosen;
  $("marking-text").textContent = chosen.text;
  $("marking-note").value = "";
  $("marking").hidden = false;
}

function hideMarking() {
  picked = null;
  $("marking").hidden = true;
}

function saveMarking() {
  if (!picked || !book) {
    return;
  }

  marks.add(book.id, { ...picked, chapter: index, note: $("marking-note").value.trim() });
  window.getSelection()?.removeAllRanges();
  hideMarking();
  showMarks();
  if (navigator.onLine) {
    sendMarks();
  }
}

function resolve(base, relative) {
  const parts = (base + decodeURIComponent(relative.split("#")[0])).split("/");
  const out = [];
  for (const part of parts) {
    if (part === "..") {
      out.pop();
    } else if (part !== "." && part !== "") {
      out.push(part);
    }
  }

  return out.join("/");
}

function route() {
  const heard = location.hash.match(/^#listen-(\d+)$/);
  const recording = heard ? keptAudio().find((entry) => entry.id === Number(heard[1])) : null;
  if (recording) {
    listen(recording);
    return;
  }

  $("listen").hidden = true;
  window.shelfPlayer?.pause();
  const match = location.hash.match(/^#book-(\d+)$/);
  const item = match ? kept().find((entry) => entry.id === Number(match[1])) : null;
  if (item) {
    open(item);
  } else {
    $("reader").hidden = true;
    $("shelf").hidden = false;
    listBooks();
  }
}

function connection() {
  $("connection").textContent = navigator.onLine ? t("Online again") : t("Offline");
  if (navigator.onLine) {
    sendPlaces();
    sendMarks();
  }
}

$("previous").addEventListener("click", () => turn(-1));
$("next").addEventListener("click", () => turn(1));
$("size").addEventListener("change", () => turn(0));
$("listen-back").addEventListener("click", (event) => {
  event.preventDefault();
  location.hash = "";
});
$("back").addEventListener("click", (event) => {
  event.preventDefault();
  location.hash = "";
});
$("page").addEventListener("keydown", (event) => {
  if (event.key === "ArrowRight") turn(1);
  if (event.key === "ArrowLeft") turn(-1);
});
$("page").addEventListener("mouseup", pick);
$("page").addEventListener("keyup", (event) => {
  if (event.shiftKey) pick();
});
$("page").addEventListener("touchend", () => setTimeout(pick, 50));
$("marking-save").addEventListener("click", saveMarking);
$("marking-cancel").addEventListener("click", hideMarking);
window.addEventListener("hashchange", route);
window.addEventListener("online", connection);
window.addEventListener("offline", connection);
labelPlayer();
connection();
route();
