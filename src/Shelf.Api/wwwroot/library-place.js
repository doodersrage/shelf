// The library's place: its address kept in step with its filters, so a book opened from it comes back to the same
// view, and how many books were drawn and how far down it was, for this tab.
const KEY = "shelf-library-place";
let watching = false;

function read() {
  try {
    return JSON.parse(sessionStorage.getItem(KEY) || "null");
  } catch {
    return null;
  }
}

function write(place) {
  try {
    sessionStorage.setItem(KEY, JSON.stringify(place));
  } catch {
    // Private windows may refuse storage; the address alone still keeps the filters.
  }
}

const here = () => location.pathname + location.search;

export function keepAddress(url, shown) {
  if (here() !== url) {
    // Blazor keeps its own note in the history entry, so it stays.
    history.replaceState(history.state, "", url);
  }

  const place = read();
  write({ url, shown, y: place && place.url === url ? place.y : 0 });
  if (watching) {
    return;
  }

  watching = true;
  let pending = false;
  addEventListener("scroll", () => {
    if (pending) {
      return;
    }

    pending = true;
    requestAnimationFrame(() => {
      pending = false;
      const kept = read();
      if (location.pathname === "/library" && kept && kept.url === here()) {
        write({ ...kept, y: scrollY });
      }
    });
  }, { passive: true });
}

export function place(url) {
  const kept = read();
  return kept && kept.url === url ? kept : null;
}

export function scrollBack(y) {
  scrollTo(0, y);
}
