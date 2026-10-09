// Shows a PDF one page at a time with selectable text, at the reader's chosen size,
// and tells the page which page is showing so it can keep the place.
import * as pdfjs from "./lib/pdfjs/pdf.min.mjs";

pdfjs.GlobalWorkerOptions.workerSrc = new URL("./lib/pdfjs/pdf.worker.min.mjs", import.meta.url).href;

let state = null;
let resizeTimer = 0;

export async function open(host, url, start, size, dotnet) {
  close();
  // No eval and no XFA forms: a PDF from anywhere should only ever be drawn, never run.
  const task = pdfjs.getDocument({ url, isEvalSupported: false, enableXfa: false });
  const doc = await task.promise;
  state = { host, doc, dotnet, size, index: clamp(start, doc.numPages), token: 0, rendering: null };
  host.addEventListener("keydown", onKey);
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

export function close() {
  if (!state) {
    return;
  }

  state.host.removeEventListener("keydown", onKey);
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

  current.host.replaceChildren(sheet);
  current.host.scrollTop = 0;
  await current.dotnet.invokeMethodAsync("PdfShown", current.index, current.doc.numPages);
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
