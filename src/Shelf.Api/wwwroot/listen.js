// The audiobook player. One audio element, kept here rather than on a page, so a book plays on while the reader
// moves around the shelf. It plays a book's tracks as one recording, keeps the place on the shelf as it goes, and
// drives whatever controls a page has: the full player, the mini player at the foot of every page, the lock
// screen, headphones, and the keyboard.
//
// Controls are plain markup. A button says what it does with data-player ("toggle", "back", "forward", "prev",
// "next", "slower", "faster", "rate", "sleep", "chapter", "close"); a place for a value says which with data-show
// ("chapter", "elapsed", "remaining", "book-elapsed", "book-left", "percent", "rate", "sleep", "cover", "title",
// "author", "link", "book-bar", "ticks", "chapter-number", "note"). A data-template such as "{0} left" puts the
// value into a sentence in the page's language.
(() => {
  const STORE = "shelf-player";
  const audio = new Audio();
  audio.preload = "auto";
  const session = "mediaSession" in navigator ? navigator.mediaSession : null;

  let plan = null;
  let lengths = [];
  let offsets = [];
  let total = 0;
  let chapters = [];
  let track = -1;
  let rate = 1;
  let wantPlay = false;
  let pending = null;
  // While another track loads, the pause that comes with it is not the reader's.
  let switching = false;
  let sleep = { mode: "off", left: 0, until: 0 };
  let lastTick = 0;
  let lastSaved = 0;
  let saveSoon = 0;
  let speedSoon = 0;
  let shownChapter = -1;
  let note = "";

  const read = (key, fallback) => {
    try {
      const value = JSON.parse(localStorage.getItem(key));
      return value ?? fallback;
    } catch {
      return fallback;
    }
  };
  const write = (key, value) => {
    try {
      if (value === null) localStorage.removeItem(key);
      else localStorage.setItem(key, JSON.stringify(value));
    } catch {
      // A private window: the player still works, it just does not come back after a reload.
    }
  };

  const jumps = () => ({ back: read("shelf-jump-back", 15), forward: read("shelf-jump-forward", 30) });
  audio.volume = Math.min(1, Math.max(0, read("shelf-volume", 1)));

  // 1:02:03, or 4:05 under an hour.
  function clock(seconds) {
    const s = Math.max(0, Math.floor(seconds || 0));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const rest = String(s % 60).padStart(2, "0");
    return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${rest}` : `${m}:${rest}`;
  }

  // 3 h 20 min, or 12 min, for what is left of a book.
  function span(seconds) {
    const m = Math.max(0, Math.round((seconds || 0) / 60));
    return m >= 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m} min`;
  }

  const fill = (element, value) => {
    const text = element.dataset.template ? element.dataset.template.replace("{0}", value[0]).replace("{1}", value[1] ?? "") : value[0];
    if (element.textContent !== text) element.textContent = text;
  };

  const now = () => (plan && track >= 0 ? offsets[track] + (pending ? pending.seconds : audio.currentTime || 0) : 0);

  function chapterAt(time) {
    let found = 0;
    for (let i = 0; i < chapters.length; i++) {
      if (chapters[i].start <= time + 0.25) found = i;
    }
    return found;
  }

  function locate(time) {
    time = Math.max(0, Math.min(total, time));
    for (let i = lengths.length - 1; i >= 0; i--) {
      if (offsets[i] <= time) return { track: i, seconds: Math.min(time - offsets[i], Math.max(0, lengths[i] - 0.05)) };
    }
    return { track: 0, seconds: 0 };
  }

  // Lengths the shelf could not read come from the browser, which loads just enough of the track to know.
  function measure(url) {
    return new Promise((done) => {
      const probe = new Audio();
      probe.preload = "metadata";
      const finish = () => {
        done(Number.isFinite(probe.duration) ? probe.duration : 0);
        probe.removeAttribute("src");
        probe.load();
      };
      probe.addEventListener("loadedmetadata", finish, { once: true });
      probe.addEventListener("error", finish, { once: true });
      probe.src = url;
    });
  }

  async function prepare(next) {
    const known = await Promise.all(next.tracks.map((item) => (item.length ? item.length : measure(item.url))));
    lengths = known.map((value) => value || 0);
    offsets = [];
    total = 0;
    for (const length of lengths) {
      offsets.push(total);
      total += length;
    }

    chapters = next.chapters.map((chapter) => ({ title: chapter.title, start: offsets[chapter.track] + chapter.start }));
    chapters.forEach((chapter, i) => {
      chapter.end = i + 1 < chapters.length ? chapters[i + 1].start : total;
    });
  }

  function setTrack(index, seconds, play) {
    wantPlay = play;
    if (index !== track || !audio.src) {
      track = index;
      pending = { seconds };
      switching = true;
      audio.src = plan.tracks[index].url;
      audio.defaultPlaybackRate = rate;
      audio.playbackRate = rate;
      audio.load();
    } else if (audio.readyState >= 1) {
      audio.currentTime = seconds;
      if (play) audio.play().catch(() => {});
    } else {
      pending = { seconds };
    }
    render();
  }

  audio.addEventListener("loadedmetadata", () => {
    switching = false;
    if (pending) {
      audio.currentTime = Math.min(pending.seconds, Math.max(0, (audio.duration || pending.seconds) - 0.05));
      pending = null;
    }
    audio.playbackRate = rate;
    if (wantPlay) audio.play().catch(() => {});
    render();
  });

  audio.addEventListener("play", () => {
    wantPlay = true;
    lastTick = performance.now();
    render();
  });

  audio.addEventListener("pause", () => {
    if (switching) return;
    if (!audio.ended) wantPlay = false;
    save();
    render();
  });

  audio.addEventListener("ended", () => {
    if (track + 1 < lengths.length) {
      setTrack(track + 1, 0, true);
    } else {
      wantPlay = false;
      save();
      render();
    }
  });

  audio.addEventListener("error", () => {
    switching = false;
    wantPlay = false;
    render();
  });

  audio.addEventListener("seeked", () => {
    clearTimeout(saveSoon);
    saveSoon = setTimeout(save, 800);
    render();
  });

  function save() {
    if (!plan || track < 0) return;
    lastSaved = performance.now();
    const seconds = pending ? pending.seconds : audio.currentTime || 0;
    fetch(`/books/${plan.bookId}/audio/place`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ track, seconds }),
      keepalive: true,
      credentials: "same-origin",
    }).catch(() => {});
  }

  // Seconds into the whole book.
  function seek(time) {
    if (!plan) return;
    const where = locate(time);
    setTrack(where.track, where.seconds, wantPlay || !audio.paused);
  }

  function jump(by) {
    seek(now() + by);
  }

  // Back goes to the start of this chapter first, unless that is where it already is.
  function chapter(direction) {
    if (!chapters.length) return;
    const time = now();
    const index = chapterAt(time);
    if (direction < 0 && time - chapters[index].start > 3) seek(chapters[index].start);
    else seek(chapters[Math.max(0, Math.min(chapters.length - 1, index + direction))].start);
  }

  function setRate(value) {
    rate = Math.round(Math.min(3, Math.max(0.5, value)) * 20) / 20;
    audio.defaultPlaybackRate = rate;
    audio.playbackRate = rate;
    clearTimeout(speedSoon);
    speedSoon = setTimeout(() => {
      fetch("/books/audio/speed", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ speed: Math.round(rate * 100) }),
        credentials: "same-origin",
      }).catch(() => {});
    }, 600);
    render();
  }

  // Minutes of listening, the end of this chapter, or off. The minutes count only while the book plays.
  function setSleep(what) {
    note = "";
    if (what === "chapter") sleep = { mode: "chapter", left: 0, until: chapters[chapterAt(now())]?.end ?? total };
    else if (Number(what) > 0) sleep = { mode: "time", left: Number(what) * 60 * 1000, until: 0 };
    else sleep = { mode: "off", left: 0, until: 0 };
    render();
  }

  function slept() {
    sleep = { mode: "off", left: 0, until: 0 };
    audio.pause();
    note = "slept";
    render();
  }

  function tick() {
    if (!plan) return;
    const moment = performance.now();
    const playing = !audio.paused && !audio.ended;
    if (playing && sleep.mode === "time") {
      sleep.left -= moment - lastTick;
      if (sleep.left <= 0) slept();
    }
    if (playing && sleep.mode === "chapter" && now() >= sleep.until - 0.3) slept();
    if (playing && moment - lastSaved > 15000) save();
    lastTick = moment;
    render();
  }

  // The lock screen: the chapter as the title, the book as the album, and the whole book as the scrubber.
  function describe() {
    if (!session || !plan) return;
    try {
      if (typeof MediaMetadata !== "undefined") {
        session.metadata = new MediaMetadata({
          title: chapters[shownChapter]?.title || plan.title,
          artist: plan.author || "",
          album: plan.title,
          artwork: plan.cover ? [{ src: new URL(plan.cover, location.href).href, sizes: "512x512" }] : [],
        });
      }
    } catch {
      // A cover address this browser will not take: the lock screen goes without.
    }
  }

  function tellPosition() {
    if (!session?.setPositionState || !plan || total <= 0) return;
    try {
      session.setPositionState({ duration: total, playbackRate: rate, position: Math.min(now(), total) });
    } catch {
      // A position past the end while a track changes; the next update corrects it.
    }
  }

  const handler = (action, run) => {
    try {
      session?.setActionHandler(action, run);
    } catch {
      // This browser has no such button.
    }
  };

  function handle() {
    handler("play", () => play());
    handler("pause", () => audio.pause());
    handler("stop", () => audio.pause());
    handler("seekbackward", (details) => jump(-(details?.seekOffset || jumps().back)));
    handler("seekforward", (details) => jump(details?.seekOffset || jumps().forward));
    handler("seekto", (details) => Number.isFinite(details?.seekTime) && seek(details.seekTime));
    handler("previoustrack", () => chapter(-1));
    handler("nexttrack", () => chapter(1));
  }

  function play() {
    if (!plan) return;
    note = "";
    wantPlay = true;
    if (!audio.src) setTrack(Math.max(0, track), 0, true);
    else audio.play().catch(() => {});
    render();
  }

  // The full player for this book is on the page: the mini player steps aside.
  const fullView = () => (plan ? document.querySelector(`[data-player-view="full"][data-book="${plan.bookId}"]`) : null);

  let drawnTicks = null;
  function render() {
    const mini = document.getElementById("mini-player");
    if (mini) mini.hidden = !plan || Boolean(fullView());
    document.documentElement.classList.toggle("has-mini-player", Boolean(mini && !mini.hidden));
    if (!plan) return;

    const time = now();
    const index = chapterAt(time);
    const current = chapters[index] ?? { title: plan.title, start: 0, end: total };
    const length = Math.max(0.001, current.end - current.start);
    const playing = wantPlay && !audio.ended;
    if (index !== shownChapter) {
      shownChapter = index;
      describe();
    }

    const { back, forward } = jumps();
    for (const element of document.querySelectorAll("[data-show]")) {
      switch (element.dataset.show) {
        case "title": fill(element, [plan.title]); break;
        case "author": fill(element, [plan.author]); break;
        case "chapter": fill(element, [current.title]); break;
        case "chapter-number": fill(element, [index + 1, chapters.length]); break;
        case "elapsed": fill(element, [clock(time - current.start)]); break;
        case "remaining": fill(element, [`-${clock((current.end - time) / rate)}`]); break;
        case "book-elapsed": fill(element, [clock(time)]); break;
        case "book-left": fill(element, [span((total - time) / rate)]); break;
        case "percent": fill(element, [`${Math.floor((time / Math.max(1, total)) * 100)}%`]); break;
        case "rate": fill(element, [`${rate.toFixed(2).replace(/\.?0+$/, "")}×`]); break;
        case "back-by": fill(element, [back]); break;
        case "forward-by": fill(element, [forward]); break;
        case "book-bar": element.style.width = `${(time / Math.max(1, total)) * 100}%`; break;
        case "chapter-bar": element.style.width = `${((time - current.start) / length) * 100}%`; break;
        case "sleep":
          element.hidden = sleep.mode === "off";
          if (sleep.mode === "time") fill(element, [clock(sleep.left / 1000)]);
          else if (sleep.mode === "chapter") fill(element, [element.dataset.chapterText || ""]);
          break;
        case "note":
          element.hidden = !note;
          if (note && element.dataset[note]) fill(element, [element.dataset[note]]);
          break;
        case "cover":
          if (plan.cover && element.getAttribute("src") !== plan.cover) element.setAttribute("src", plan.cover);
          element.hidden = !plan.cover;
          break;
        case "link": element.setAttribute("href", `/library/${plan.bookId}/listen`); break;
        case "ticks":
          if (drawnTicks !== element || element.dataset.drawn !== String(plan.bookId)) {
            element.replaceChildren(...chapters.slice(1).map((item) => {
              const mark = document.createElement("span");
              mark.style.left = `${(item.start / Math.max(1, total)) * 100}%`;
              return mark;
            }));
            element.dataset.drawn = String(plan.bookId);
            drawnTicks = element;
          }
          break;
      }
    }

    for (const button of document.querySelectorAll('[data-player="toggle"]')) {
      button.dataset.playing = playing ? "true" : "false";
      const label = playing ? button.dataset.labelPause : button.dataset.labelPlay;
      if (label && button.getAttribute("aria-label") !== label) button.setAttribute("aria-label", label);
    }
    for (const scrub of document.querySelectorAll('input[data-player="scrub"]')) {
      if (document.activeElement !== scrub || scrub.dataset.dragging !== "true") scrub.value = String(Math.round(((time - current.start) / length) * 1000));
    }
    for (const item of document.querySelectorAll('[data-player="chapter"]')) {
      const on = Number(item.dataset.chapter) === index;
      item.classList.toggle("current", on);
      if (on) item.setAttribute("aria-current", "true");
      else item.removeAttribute("aria-current");
    }
    for (const item of document.querySelectorAll('[data-player="rate"]')) {
      item.setAttribute("aria-pressed", Number(item.dataset.rate) === rate ? "true" : "false");
    }
    for (const item of document.querySelectorAll('[data-player="sleep"]')) {
      const value = item.dataset.sleep;
      const on = (value === "chapter" && sleep.mode === "chapter") || (value === "off" && sleep.mode === "off");
      item.setAttribute("aria-pressed", on ? "true" : "false");
    }
    for (const select of document.querySelectorAll("select[data-jump]")) {
      const value = String(select.dataset.jump === "back" ? back : forward);
      if (select.value !== value) select.value = value;
    }
    for (const range of document.querySelectorAll('input[data-player="volume"]')) {
      if (document.activeElement !== range) range.value = String(Math.round(audio.volume * 100));
    }

    if (session) session.playbackState = playing ? "playing" : "paused";
    tellPosition();
  }

  // Opens a book: the one already playing carries on; another stops the first, keeping its place.
  async function open(next, options = {}) {
    if (plan && plan.bookId === next.bookId && track >= 0) {
      plan = { ...plan, ...next, tracks: plan.tracks };
      if (options.arrive) arrive(options.arrive);
      render();
      return;
    }

    if (plan) {
      save();
      audio.pause();
    }

    plan = next;
    track = -1;
    wantPlay = false;
    shownChapter = -1;
    note = "";
    sleep = { mode: "off", left: 0, until: 0 };
    rate = Math.min(3, Math.max(0.5, (next.speed || 100) / 100));
    write(STORE, { bookId: next.bookId });
    await prepare(next);
    handle();
    const start = Math.min(Math.max(0, next.track), lengths.length - 1);
    if (options.arrive) arrive(options.arrive);
    else setTrack(start, Math.min(next.seconds || 0, Math.max(0, lengths[start] - 1)), Boolean(options.play));
  }

  // The way in from the e-book: a fraction of the way through a part of one track.
  function arrive(way) {
    const index = Math.min(Math.max(0, way.track), lengths.length - 1);
    const end = way.to ?? lengths[index];
    setTrack(index, way.from + Math.max(0, Math.min(1, way.fraction)) * Math.max(0, end - way.from), wantPlay);
    save();
  }

  function close() {
    save();
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    plan = null;
    track = -1;
    write(STORE, null);
    if (session) {
      session.metadata = null;
      session.playbackState = "none";
    }
    render();
  }

  // A Play button on a book's page or card: the book starts in the mini player, and the page stays.
  async function playBook(bookId) {
    if (plan?.bookId === bookId && track >= 0) {
      play();
      return;
    }
    try {
      const response = await fetch(`/books/${bookId}/audio/plan`, { credentials: "same-origin" });
      if (response.ok) await open(await response.json(), { play: true });
    } catch {
      // The shelf is out of reach.
    }
  }

  // Clicks anywhere: the controls are wherever a page put them.
  document.addEventListener("click", (event) => {
    const starter = event.target.closest?.("[data-play-book]");
    if (starter) {
      event.preventDefault();
      playBook(Number(starter.dataset.playBook));
      return;
    }

    const control = event.target.closest?.("[data-player]");
    if (!control || control.tagName === "INPUT" || control.tagName === "SELECT" || !plan) return;
    switch (control.dataset.player) {
      case "toggle": if (wantPlay && !audio.ended) audio.pause(); else play(); break;
      case "back": jump(-jumps().back); break;
      case "forward": jump(jumps().forward); break;
      case "prev": chapter(-1); break;
      case "next": chapter(1); break;
      case "slower": setRate(rate - 0.05); break;
      case "faster": setRate(rate + 0.05); break;
      case "rate": setRate(Number(control.dataset.rate)); break;
      case "sleep": setSleep(control.dataset.sleep); break;
      case "sleep-more": if (sleep.mode === "time") sleep.left += 5 * 60 * 1000; render(); break;
      case "chapter": seek(chapters[Number(control.dataset.chapter)]?.start ?? 0); break;
      case "close": close(); break;
      default: return;
    }
    event.preventDefault();
  });

  document.addEventListener("input", (event) => {
    const control = event.target;
    if (!plan || !control.dataset) return;
    if (control.dataset.player === "scrub") {
      control.dataset.dragging = "true";
      const current = chapters[chapterAt(now())] ?? { start: 0, end: total };
      const preview = current.start + (Number(control.value) / 1000) * (current.end - current.start);
      for (const element of document.querySelectorAll('[data-show="elapsed"]')) fill(element, [clock(preview - current.start)]);
    } else if (control.dataset.player === "volume") {
      audio.volume = Math.min(1, Math.max(0, Number(control.value) / 100));
      write("shelf-volume", audio.volume);
    }
  });

  document.addEventListener("change", (event) => {
    const control = event.target;
    if (!plan || !control.dataset) return;
    if (control.dataset.player === "scrub") {
      control.dataset.dragging = "false";
      const current = chapters[chapterAt(now())] ?? { start: 0, end: total };
      seek(current.start + (Number(control.value) / 1000) * (current.end - current.start));
    } else if (control.dataset.jump) {
      write(control.dataset.jump === "back" ? "shelf-jump-back" : "shelf-jump-forward", Number(control.value));
      render();
    }
  });

  // On the full player: space plays and pauses, the arrows skip (with shift, by chapter), and [ ] change the speed.
  document.addEventListener("keydown", (event) => {
    if (!plan || !fullView() || event.altKey || event.ctrlKey || event.metaKey) return;
    const target = event.target;
    if (target.closest?.("input, textarea, select, [contenteditable]") || (target.tagName === "BUTTON" && (event.key === " " || event.key === "Enter"))) return;
    const keys = {
      " ": () => (wantPlay && !audio.ended ? audio.pause() : play()),
      k: () => (wantPlay && !audio.ended ? audio.pause() : play()),
      ArrowLeft: () => (event.shiftKey ? chapter(-1) : jump(-jumps().back)),
      ArrowRight: () => (event.shiftKey ? chapter(1) : jump(jumps().forward)),
      "[": () => setRate(rate - 0.05),
      "]": () => setRate(rate + 0.05),
      m: () => { audio.muted = !audio.muted; },
    };
    if (keys[event.key]) {
      event.preventDefault();
      keys[event.key]();
    }
  });

  // Leaving or hiding the page keeps the place.
  document.addEventListener("visibilitychange", () => document.visibilityState === "hidden" && save());
  window.addEventListener("pagehide", save);
  setInterval(tick, 500);

  // After a reload, the book that was playing waits in the mini player, paused where it stopped.
  async function restore() {
    const kept = read(STORE, null);
    if (!kept?.bookId || plan) return;
    try {
      const response = await fetch(`/books/${kept.bookId}/audio/plan`, { credentials: "same-origin" });
      if (!response.ok) {
        if (response.status === 401 || response.status === 404) write(STORE, null);
        return;
      }
      if (!plan) await open(await response.json());
    } catch {
      // The shelf is out of reach; nothing to bring back.
    }
  }

  window.shelfPlayer = {
    open,
    close,
    play,
    pause: () => audio.pause(),
    seek,
    seekTrack: (index, seconds) => plan && setTrack(Math.min(Math.max(0, index), lengths.length - 1), seconds, wantPlay),
    // Where the player is, for a bookmark or for carrying the place to the e-book.
    now: () => ({
      bookId: plan?.bookId ?? 0,
      track: Math.max(0, track),
      seconds: pending ? pending.seconds : audio.currentTime || 0,
      trackLength: lengths[track] || 0,
      chapter: chapterAt(now()),
      bookSeconds: now(),
      rate,
      playing: wantPlay && !audio.paused && !audio.ended,
    }),
    render,
  };

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", restore, { once: true });
  else restore();
})();
