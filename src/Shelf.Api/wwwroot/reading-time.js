// Counts time spent reading: while the page is in view, and the reader has scrolled, turned a page, or touched the
// page (or the chapter inside it) in the last two minutes. It goes to the shelf every minute, and when the page goes.
const IDLE = 2 * 60 * 1000;
const STEP = 5 * 1000;

let bookId = 0;
let lastActive = 0;
let counted = 0;
let ticker = 0;
let sender = 0;

const active = () => {
  lastActive = Date.now();
};

const fromFrame = (event) => {
  if (event.data && typeof event.data.source === "string" && event.data.source.startsWith("shelf-")) active();
};

function send(leaving) {
  const seconds = Math.round(counted);
  if (!bookId || seconds < 1) return;
  counted -= seconds;
  fetch(`/books/${bookId}/reading-time`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ day: new Date().toISOString().slice(0, 10), seconds }),
    credentials: "same-origin",
    keepalive: leaving,
  }).catch(() => {});
}

const hidden = () => document.visibilityState === "hidden" && send(true);
const leaving = () => send(true);

export function start(id) {
  stop();
  bookId = id;
  active();
  for (const name of ["keydown", "pointerdown", "wheel", "scroll", "touchstart"]) {
    window.addEventListener(name, active, { passive: true, capture: true });
  }
  window.addEventListener("message", fromFrame);
  document.addEventListener("visibilitychange", hidden);
  window.addEventListener("pagehide", leaving);
  ticker = setInterval(() => {
    if (document.visibilityState === "visible" && Date.now() - lastActive < IDLE) counted += STEP / 1000;
  }, STEP);
  sender = setInterval(() => send(false), 60 * 1000);
}

export function stop() {
  if (!bookId) return;
  send(true);
  clearInterval(ticker);
  clearInterval(sender);
  for (const name of ["keydown", "pointerdown", "wheel", "scroll", "touchstart"]) {
    window.removeEventListener(name, active, { capture: true });
  }
  window.removeEventListener("message", fromFrame);
  document.removeEventListener("visibilitychange", hidden);
  window.removeEventListener("pagehide", leaving);
  bookId = 0;
}
