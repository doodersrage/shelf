// Listening along: the audiobook plays through the page-wide player while the chapter marks the word being read,
// turns its pages, and moves on to the next chapter. A double tap on a word moves the recording there.
let state = null;

const frame = () => document.querySelector("iframe.reader-frame");
const post = (message) => frame()?.contentWindow?.postMessage(message, "*");

// The words that have a time, in order of their times, to find the one being read.
function timeline(times) {
  const list = [];
  let latest = -1;
  times.forEach((time, word) => {
    if (time >= 0 && time >= latest) {
      list.push([time, word]);
      latest = time;
    }
  });
  return list;
}

function wordAt(list, hundredths) {
  let lo = 0, hi = list.length - 1, found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (list[mid][0] <= hundredths) {
      found = list[mid][1];
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return found;
}

// The time of a word, or of the next word that has one.
function timeOf(times, word) {
  for (let i = Math.max(0, word); i < times.length; i++) if (times[i] >= 0) return times[i] / 100;
  for (let i = Math.min(times.length - 1, word); i >= 0; i--) if (times[i] >= 0) return times[i] / 100;
  return null;
}

function ask() {
  return new Promise((resolve) => {
    const answer = (event) => {
      if (event.data?.source !== "shelf-here") return;
      window.removeEventListener("message", answer);
      clearTimeout(timer);
      resolve(Number(event.data.word) || 0);
    };
    const timer = setTimeout(() => {
      window.removeEventListener("message", answer);
      resolve(0);
    }, 1500);
    window.addEventListener("message", answer);
    post({ source: "shelf-where" });
  });
}

function heard(event) {
  if (!state || event.data?.source !== "shelf-word") return;
  const time = timeOf(state.times, Number(event.data.word));
  if (time !== null) {
    window.shelfPlayer.seek(time);
    window.shelfPlayer.play();
  }
}

function tick() {
  const player = window.shelfPlayer;
  if (!state || !player) return;
  const now = player.now();
  if (now.bookId !== state.bookId) {
    stop(false);
    state?.dotNet.invokeMethodAsync("AlongStopped")?.catch(() => {});
    return;
  }

  // The page may have opened a new chapter since; it is told again now and then.
  if (Date.now() - state.told > 1500) {
    state.told = Date.now();
    state.word = -1;
    post({ source: "shelf-along", on: true });
  }

  const time = now.bookSeconds;
  const here = state.chapters.find((item) => item.index === state.chapter);
  if (now.playing && here && (time > here.end + 0.6 || time < here.start - 0.6)) {
    const next = state.chapters.find((item) => time >= item.start - 0.05 && time <= item.end + 0.6) ?? state.chapters.find((item) => item.start > time);
    if (next && next.index !== state.chapter && !state.moving) {
      state.moving = true;
      state.dotNet.invokeMethodAsync("AlongTo", next.index)?.catch(() => {});
    }
    return;
  }

  const word = wordAt(state.list, Math.round(time * 100));
  if (word >= 0 && word !== state.word) {
    state.word = word;
    post({ source: "shelf-say", word });
  }
}

// Starts from the first word on the screen. Returns false when the player could not take the book.
export async function start(dotNet, bookId, chapter, times, chapters) {
  stop(false);
  const player = window.shelfPlayer;
  if (!player) return false;
  state = { dotNet, bookId, chapter, times, list: timeline(times), chapters, word: -1, told: 0, moving: false, timer: 0 };
  window.addEventListener("message", heard);
  post({ source: "shelf-along", on: true });
  const first = await ask();
  const range = chapters.find((item) => item.index === chapter);
  const time = timeOf(times, first) ?? range?.start ?? 0;
  if (player.now().bookId !== bookId) {
    const response = await fetch(`/books/${bookId}/audio/plan`, { credentials: "same-origin" });
    if (!response.ok || !state) return false;
    await player.open(await response.json());
  }

  player.seek(time);
  player.play();
  state.timer = setInterval(tick, 200);
  return true;
}

// The page opened another chapter, by itself or because the recording moved on.
export function chapter(index, times) {
  if (!state) return;
  Object.assign(state, { chapter: index, times, list: timeline(times), word: -1, told: 0, moving: false });
}

export function stop(pause = true) {
  if (!state) return;
  clearInterval(state.timer);
  window.removeEventListener("message", heard);
  post({ source: "shelf-along", on: false });
  if (pause) window.shelfPlayer?.pause();
  state = null;
}

// A chapter chosen by hand: the recording goes to its start.
export function move(index, times, start) {
  if (!state) return;
  chapter(index, times);
  state.moving = true;
  window.shelfPlayer?.seek(start);
  setTimeout(() => {
    if (state) state.moving = false;
  }, 800);
}
