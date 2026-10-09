// Shows a PDF one page at a time with selectable text, at the reader's chosen size,
// tells the page which page is showing so it can keep the place, and paints and takes highlights.
import * as pdfjs from "./lib/pdfjs/pdf.min.mjs";

pdfjs.GlobalWorkerOptions.workerSrc = new URL("./lib/pdfjs/pdf.worker.min.mjs", import.meta.url).href;

let state = null;
let resizeTimer = 0;

export async function open(host, url, start, size, dotnet, marks) {
  close();
  // No eval and no XFA forms: a PDF from anywhere should only ever be drawn, never run.
  const task = pdfjs.getDocument({ url, isEvalSupported: false, enableXfa: false });
  const doc = await task.promise;
  state = { host, doc, dotnet, size, marks: marks ?? [], index: clamp(start, doc.numPages), token: 0, rendering: null };
  host.addEventListener("keydown", onKey);
  host.addEventListener("mouseup", onSelect);
  host.addEventListener("keyup", onSelect);
  host.addEventListener("click", onMarkClick);
  window.addEventListener("resize", onResize);
  await render();
  return doc.numPages;
}

export async function show(index) {
  if (!state) {
    return;
  }

  state.index = clamp(index, state.doc.numPages);
  await render();
}

export async function zoom(size) {
  if (!state) {
    return;
  }

  state.size = size;
  await render();
}

// The page's highlights changed: repaint the page showing now, and let go of the old selection.
export function marks(list) {
  if (!state) {
    return;
  }

  state.marks = list ?? [];
  window.getSelection()?.removeAllRanges();
  const layer = state.host.querySelector(".textLayer");
  if (layer) {
    paint(layer, state.index);
  }
}

export function close() {
  if (!state) {
    return;
  }

  state.host.removeEventListener("keydown", onKey);
  state.host.removeEventListener("mouseup", onSelect);
  state.host.removeEventListener("keyup", onSelect);
  state.host.removeEventListener("click", onMarkClick);
  window.removeEventListener("resize", onResize);
  state.rendering?.cancel();
  state.doc.destroy();
  state = null;
}

async function render() {
  const current = state;
  if (!current) {
    return;
  }

  const token = ++current.token;
  const page = await current.doc.getPage(current.index + 1);
  const natural = page.getViewport({ scale: 1 });
  // At 100% the page fills the width it has; the text size setting zooms from there.
  const available = Math.max(240, current.host.clientWidth - 32);
  const scale = (available / natural.width) * (current.size / 100);
  const viewport = page.getViewport({ scale });
  const ratio = window.devicePixelRatio || 1;

  const sheet = document.createElement("div");
  sheet.className = "pdf-page";
  sheet.style.width = `${Math.floor(viewport.width)}px`;
  sheet.style.height = `${Math.floor(viewport.height)}px`;
  sheet.style.setProperty("--scale-factor", String(scale));

  const canvas = document.createElement("canvas");
  canvas.width = Math.floor(viewport.width * ratio);
  canvas.height = Math.floor(viewport.height * ratio);
  canvas.style.width = sheet.style.width;
  canvas.style.height = sheet.style.height;
  canvas.setAttribute("aria-hidden", "true");

  const text = document.createElement("div");
  text.className = "textLayer";
  sheet.append(canvas, text);

  current.rendering?.cancel();
  const drawing = page.render({
    canvasContext: canvas.getContext("2d"),
    viewport,
    transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0],
  });
  current.rendering = drawing;
  try {
    await drawing.promise;
    await new pdfjs.TextLayer({ textContentSource: page.streamTextContent(), container: text, viewport }).render();
  } catch (error) {
    if (error?.name === "RenderingCancelledException") {
      return;
    }

    throw error;
  }

  // A newer page or size may have been asked for while this one was drawing.
  if (state !== current || token !== current.token) {
    return;
  }

  paint(text, current.index);
  current.host.replaceChildren(sheet);
  current.host.scrollTop = 0;
  await current.dotnet.invokeMethodAsync("PdfShown", current.index, current.doc.numPages);
}

// A page's text, as one string in the text layer's own order, with each text node's place in it.
function textMap(layer) {
  const nodes = [];
  let length = 0;
  const walker = document.createTreeWalker(layer, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (node.parentElement?.closest(".endOfContent")) {
      continue;
    }

    nodes.push({ node, start: length });
    length += node.data.length;
  }

  return { nodes, text: nodes.map((item) => item.node.data).join("") };
}

function paint(layer, index) {
  for (const old of layer.querySelectorAll("mark.shelf-mark")) {
    old.replaceWith(...old.childNodes);
  }

  layer.normalize();
  for (const mark of state.marks.filter((item) => item.page === index)) {
    const { nodes, text } = textMap(layer);
    // The words around a passage tell two copies of the same words apart; the passage alone is the fallback.
    let at = text.indexOf(mark.prefix + mark.text + mark.suffix);
    at = at >= 0 ? at + mark.prefix.length : text.indexOf(mark.text);
    if (at < 0) {
      continue;
    }

    wrap(nodes, at, at + mark.text.length, mark);
  }
}

function wrap(nodes, from, to, mark) {
  const pieces = nodes
    .map(({ node, start }) => ({ node, from: Math.max(from, start) - start, to: Math.min(to, start + node.data.length) - start }))
    .filter((piece) => piece.from < piece.to);
  for (const piece of pieces.reverse()) {
    let target = piece.node;
    if (piece.to < target.data.length) {
      target.splitText(piece.to);
    }

    if (piece.from > 0) {
      target = target.splitText(piece.from);
    }

    const element = document.createElement("mark");
    element.className = "shelf-mark";
    element.dataset.id = String(mark.id);
    if (mark.note) {
      element.title = mark.note;
    }

    target.replaceWith(element);
    element.append(target);
  }
}

function onSelect() {
  if (!state) {
    return;
  }

  const selection = window.getSelection();
  const layer = state.host.querySelector(".textLayer");
  if (!selection || selection.isCollapsed || selection.rangeCount === 0 || !layer) {
    return;
  }

  const range = selection.getRangeAt(0);
  if (!layer.contains(range.commonAncestorContainer) && range.commonAncestorContainer !== layer) {
    return;
  }

  const { nodes, text } = textMap(layer);
  let from = -1;
  let to = -1;
  for (const { node, start } of nodes) {
    if (!range.intersectsNode(node)) {
      continue;
    }

    if (from < 0) {
      from = start + (node === range.startContainer ? range.startOffset : 0);
    }

    to = start + (node === range.endContainer ? range.endOffset : node.data.length);
  }

  const passage = from >= 0 ? text.slice(from, to).trim() : "";
  if (!passage) {
    return;
  }

  const lead = text.slice(from).indexOf(passage);
  const begin = from + Math.max(0, lead);
  state.dotnet.invokeMethodAsync(
    "Selected",
    passage,
    text.slice(Math.max(0, begin - 32), begin),
    text.slice(begin + passage.length, begin + passage.length + 32));
}

function onMarkClick(event) {
  const mark = event.target instanceof Element ? event.target.closest("mark.shelf-mark") : null;
  if (!state || !mark || !window.getSelection()?.isCollapsed) {
    return;
  }

  state.dotnet.invokeMethodAsync("OpenMark", Number(mark.dataset.id));
}

function onKey(event) {
  if (!state) {
    return;
  }

  if (event.key === "ArrowRight" || event.key === "PageDown") {
    event.preventDefault();
    show(state.index + 1);
  } else if (event.key === "ArrowLeft" || event.key === "PageUp") {
    event.preventDefault();
    show(state.index - 1);
  }
}

function onResize() {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(render, 200);
}

function clamp(index, count) {
  return Math.min(Math.max(Number.isFinite(index) ? index : 0, 0), count - 1);
}
