// Read aloud with the browser's own voices. Text goes out a sentence or two at a time, since some browsers stop
// a long utterance part way. Each start, pause, and stop begins a new run, so a late "end" from an old one is ignored.
const PREFS = "shelf-read-aloud";
let queue = [];
let index = 0;
let run = 0;
let dotnet = null;
let rate = 1;
let voiceName = "";

export function supported() {
  return "speechSynthesis" in window && "SpeechSynthesisUtterance" in window;
}

export function prefs() {
  try {
    const saved = JSON.parse(localStorage.getItem(PREFS) || "{}");
    return { rate: Number(saved.rate) || 1, voice: typeof saved.voice === "string" ? saved.voice : "" };
  } catch {
    return { rate: 1, voice: "" };
  }
}

export function savePrefs(newRate, newVoice) {
  rate = newRate;
  voiceName = newVoice || "";
  try {
    localStorage.setItem(PREFS, JSON.stringify({ rate, voice: voiceName }));
  } catch {
    // Private windows may refuse; the choice still holds for this page.
  }
}

// The voices this browser offers. Some fill the list a moment after the page loads.
export function voices() {
  const describe = (list) => list.map((voice) => ({ name: voice.name, lang: voice.lang }));
  const now = speechSynthesis.getVoices();
  if (now.length) {
    return Promise.resolve(describe(now));
  }

  return new Promise((resolve) => {
    const done = () => resolve(describe(speechSynthesis.getVoices()));
    speechSynthesis.addEventListener("voiceschanged", done, { once: true });
    setTimeout(done, 1500);
  });
}

// source: { chapter: "/books/1/ebook/chapters/3" } for an EPUB, or { page: true } for the PDF page showing.
export async function start(reference, source, options) {
  stop();
  dotnet = reference;
  savePrefs(options.rate, options.voice);
  const mine = run;
  const text = source.chapter ? await chapterText(source.chapter) : await pageText();
  if (mine !== run) {
    return;
  }

  queue = pieces(text);
  index = Math.max(0, Math.min(options.from || 0, queue.length));
  speakNext(mine);
}

export function pause() {
  run++;
  speechSynthesis.cancel();
}

export function resume() {
  if (queue.length) {
    speakNext(run);
  }
}

export function stop() {
  run++;
  queue = [];
  index = 0;
  speechSynthesis.cancel();
}

function speakNext(mine) {
  if (mine !== run) {
    return;
  }

  if (index >= queue.length) {
    queue = [];
    dotnet?.invokeMethodAsync("SpokenToEnd");
    return;
  }

  const utterance = new SpeechSynthesisUtterance(queue[index]);
  utterance.rate = rate;
  const voice = speechSynthesis.getVoices().find((item) => item.name === voiceName);
  if (voice) {
    utterance.voice = voice;
    utterance.lang = voice.lang;
  }

  const next = () => {
    if (mine === run) {
      index++;
      speakNext(mine);
    }
  };
  utterance.onend = next;
  utterance.onerror = (event) => {
    if (event.error !== "interrupted" && event.error !== "canceled") {
      next();
    }
  };
  dotnet?.invokeMethodAsync("Speaking", queue[index]);
  speechSynthesis.speak(utterance);
}

async function chapterText(url) {
  const response = await fetch(url, { credentials: "same-origin" });
  if (!response.ok) {
    return "";
  }

  const page = new DOMParser().parseFromString(await response.text(), "text/html");
  page.querySelectorAll("script, style, noscript, rt").forEach((node) => node.remove());
  const blocks = [...page.body.querySelectorAll("h1, h2, h3, h4, h5, h6, p, li, blockquote, pre, dt, dd, figcaption")]
    .filter((node) => !node.querySelector("p, li, blockquote"))
    .map((node) => node.textContent.trim())
    .filter(Boolean);
  return blocks.length ? blocks.join("\n") : page.body.textContent;
}

// The PDF page's text layer, waiting a little for a page that is still being drawn.
async function pageText() {
  for (let attempt = 0; attempt < 25; attempt++) {
    const layer = document.querySelector(".pdf-viewer .textLayer");
    const text = layer?.textContent.trim();
    if (text) {
      return [...layer.querySelectorAll("span")].map((span) => span.textContent).join(" ");
    }

    await new Promise((resolve) => setTimeout(resolve, 200));
  }

  return "";
}

// Paragraphs cut into sentences, joined back up to about 220 characters a piece.
function pieces(text) {
  const result = [];
  for (const paragraph of text.split(/\n+/)) {
    const sentences = paragraph.replace(/\s+/g, " ").trim().match(/[^.!?…]+[.!?…]+["'”’)\]]*\s*|[^.!?…]+$/g) || [];
    let current = "";
    for (const sentence of sentences) {
      if (current && (current + sentence).length > 220) {
        result.push(current.trim());
        current = "";
      }

      current += sentence;
    }

    if (current.trim()) {
      result.push(current.trim());
    }
  }

  return result;
}
