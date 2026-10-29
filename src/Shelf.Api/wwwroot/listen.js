// The audiobook player. One audio element, kept here rather than on a page, so a book plays on while the reader
// moves around the shelf. It plays a book's tracks as one recording, keeps the place on the shelf as it goes, and
// drives whatever controls a page has: the full player, the mini player at the foot of every page, the lock
// screen, headphones, and the keyboard.
//
// Controls are plain markup. A button says what it does with data-player ("toggle", "back", "forward", "prev",
// "next", "slower", "faster", "rate", "sleep", "chapter", "undo", "close"); a place for a value says which with
// data-show ("chapter", "elapsed", "remaining", "book-elapsed", "book-left", "percent", "rate", "sleep", "cover",
// "title", "author", "link", "book-bar", "ticks", "chapter-number", "note", "undo"). A data-template such as "{0} left" puts the
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
  // Seconds played since the last save, by the clock, for the reader's listening time.
  let heard = 0;
  // Where the book was before a long move (a chapter, the scrubber, a bookmark), to go back to for a few seconds.
  let undo = null;
  // The place the shelf kept when this player last heard from it. A save says so, and a place moved since, by
  // another tab or device, is not undone: the shelf answers with it and the player goes there, with a way back.
  // known is the place last sent, so saves close together each name the one before; confirmed, the last the shelf took.
  let known = null;
  let confirmed = null;
  const sameSpot = (a, b) => Boolean(a && b) && a.track === b.track && Math.abs(a.seconds - b.seconds) <= 2;
  const MOVED_UNDO_FOR = 60000;

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
  // The volume the reader set; the sleep timer fades from it, and the slider shows it.
  let volume = Math.min(1, Math.max(0, read("shelf-volume", 1)));
  audio.volume = volume;

  // Coming back after a pause, the book starts a little before where it stopped, more the longer it was away, so
  // the thread is not lost: none under half a minute, then 5, 10, or 20 seconds after an hour or more.
  const UNDO_FOR = 10000;
  const smartRewind = () => read("shelf-smart-rewind", true);
  function rewindAfter(away) {
    if (away < 30 * 1000) return 0;
    if (away < 5 * 60 * 1000) return 5;
    if (away < 60 * 60 * 1000) return 10;
    return 20;
  }

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

  // Casting to a Chromecast or AirPlay, by the browser's own Remote Playback: the device fetches the tracks itself,
  // without the reader's sign-in, so while it's connected the player plays signed addresses the shelf hands out.
  let castAvailable = false;
  let casting = false;
  let castUrls = null;
  const castable = () => Boolean(plan?.bookId && !plan.placeUrl && (audio.remote || audio.webkitShowPlaybackTargetPicker));
  const trackUrl = (index) => (casting && castUrls?.[index]) || plan.tracks[index].url;

  if (audio.remote?.watchAvailability) {
    audio.remote.watchAvailability((available) => {
      castAvailable = available;
      if (available && !castUrls) prepareCast();
      render();
    }).catch(() => {
      // This browser can't tell, or can't cast audio: the button stays hidden.
    });
    audio.remote.addEventListener("connect", () => {
      casting = true;
      render();
    });
    audio.remote.addEventListener("disconnect", () => {
      casting = false;
      uncast();
      render();
    });
  } else if (audio.webkitShowPlaybackTargetPicker) {
    audio.addEventListener("webkitplaybacktargetavailabilitychanged", (event) => {
      castAvailable = event.availability === "available";
      if (castAvailable && !castUrls) prepareCast();
      render();
    });
  }

  // The signed addresses, asked for as a book opens where casting can happen, so the button needn't wait on them.
  async function prepareCast() {
    castUrls = null;
    if (!castAvailable || !castable()) return;
    const bookId = plan.bookId;
    try {
      const response = await fetch(`/books/${bookId}/audio/cast`, { credentials: "same-origin" });
      if (response.ok && plan?.bookId === bookId) castUrls = (await response.json()).tracks;
    } catch {
      // Out of reach: casting waits.
    }
  }

  // Within the tap: the track goes over to its signed address, where it was, and the browser asks which device.
  function cast() {
    if (!plan || track < 0) return;
    if (castUrls) {
      casting = true;
      const index = track;
      const at = pending ? pending.seconds : audio.currentTime || 0;
      // Forgotten first, so the same track loads again from its signed address.
      track = -1;
      setTrack(index, at, wantPlay);
    }
    const asked = audio.remote ? audio.remote.prompt() : Promise.resolve(audio.webkitShowPlaybackTargetPicker());
    Promise.resolve(asked).catch(() => {
      // Chosen nowhere: back to the shelf's own addresses.
      casting = audio.remote?.state === "connected";
      if (!casting) uncast();
    });
  }

  // Back to the shelf's own addresses, where it was: the track forgotten first, so it loads again.
  function uncast() {
    if (!plan || track < 0) return;
    const index = track;
    const at = pending ? pending.seconds : audio.currentTime || 0;
    track = -1;
    setTrack(index, at, wantPlay);
  }

  function setTrack(index, seconds, play) {
    wantPlay = play;
    if (index !== track || !audio.src) {
      track = index;
      pending = { seconds };
      switching = true;
      audio.src = trackUrl(index);
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
    // A tab left open finds out at once whether the place moved on elsewhere while it waited.
    if (known) {
      clearTimeout(saveSoon);
      saveSoon = setTimeout(save, 300);
    }
    render();
  });

  audio.addEventListener("pause", () => {
    if (switching) return;
    if (!audio.ended) {
      wantPlay = false;
      write("shelf-paused-at", { key: keyOf(plan), at: Date.now() });
    }
    fade();
    // At the end, the ended handler saves; saving here too would send the place twice at once.
    if (!audio.ended) save();
    render();
  });

  audio.addEventListener("ended", () => {
    if (track + 1 < lengths.length) {
      setTrack(track + 1, 0, true);
    } else {
      wantPlay = false;
      // A save waiting since a seek would land after the book is marked finished and started over; the end is
      // saved here instead.
      clearTimeout(saveSoon);
      // The place first, so marking the book finished (which starts the place over) comes after it; then the next
      // thing in the playlist, if this came from one.
      save().finally(() => {
        finished();
        playNext();
      });
      render();
    }
  });

  // What is playing, as a name for this device's keeping: a book by its id, anything else (a podcast episode) by
  // the key its plan gives. A plan that is not a book also gives where its place, its end, and its page go.
  const keyOf = (some) => (some?.key ?? String(some?.bookId ?? ""));
  const placeUrl = () => plan.placeUrl ?? `/books/${plan.bookId}/audio/place`;

  // The end of the book: it is marked finished on the shelf, and the player says so.
  function finished() {
    const key = keyOf(plan);
    fetch(plan.finishedUrl ?? `/books/${plan.bookId}/audio/finished`, { method: "POST", credentials: "same-origin" })
      .then((response) => (response.ok ? response.json() : null))
      .then((answer) => {
        if (keyOf(plan) !== key) return;
        // Marked finished, the book's place starts over on the shelf: the player knows it, so its next save isn't
        // taken for a place moved elsewhere.
        if (answer?.marked) known = confirmed = { track: 0, seconds: 0 };
        note = answer?.marked ? "finished" : "ended";
        render();
      })
      .catch(() => {});
  }

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

  // The day as the shelf counts days (UTC, as reading sessions and the streak do), taken now rather than when sent.
  const shelfDay = () => new Date().toISOString().slice(0, 10);

  // The place is kept on this device as well, marked waiting until the shelf has it: with no connection (a kept
  // book in the offline player, say) it goes back once the shelf answers again, as does the time listened.
  function save() {
    if (!plan || track < 0) return Promise.resolve();
    lastSaved = performance.now();
    const bookId = plan.bookId;
    const key = keyOf(plan);
    const seconds = pending ? pending.seconds : audio.currentTime || 0;
    const sent = track;
    const from = known && !plan.placeUrl ? known : null;
    if (from) known = { track: sent, seconds };
    const listened = Math.round(heard);
    heard -= listened;
    const day = shelfDay();
    write(`shelf-audio-place-${key}`, { track, seconds, waiting: true });
    const missed = () => {
      if (listened < 1 || !bookId) return;
      const owed = read("shelf-audio-heard", {});
      owed[bookId] = owed[bookId] || {};
      owed[bookId][day] = (owed[bookId][day] || 0) + listened;
      write("shelf-audio-heard", owed);
    };
    return fetch(placeUrl(), {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(from ? { track: sent, seconds, listened, day, from } : { track: sent, seconds, listened, day }),
      keepalive: true,
      credentials: "same-origin",
    }).then((response) => {
      if (response.ok) {
        const kept = read(`shelf-audio-place-${key}`, null);
        if (kept && kept.track === sent && kept.seconds === seconds) write(`shelf-audio-place-${key}`, { track: sent, seconds, waiting: false });
        if (keyOf(plan) === key && !plan.placeUrl) {
          confirmed = { track: sent, seconds };
          // A first save sent without the check (a place kept here offline) starts the checking from here.
          if (!known) known = confirmed;
        }
      } else if (response.status === 409) {
        return response.json().then((spot) => {
          if (keyOf(plan) !== key) return;
          // This player's own place, a save before this one having gone astray: sent again from there.
          if (sameSpot(spot, confirmed)) {
            known = { ...confirmed };
            save();
            return;
          }

          // Moved on elsewhere since: the listening time still counted, the place there is the one to go to.
          write(`shelf-audio-place-${key}`, { track: spot.track, seconds: spot.seconds, waiting: false });
          movedTo(spot);
        });
      } else {
        if (from && keyOf(plan) === key) known = confirmed;
        if (response.status !== 404) missed();
      }
    }, () => {
      if (from && keyOf(plan) === key) known = confirmed;
      missed();
    });
  }

  // The place kept by another tab or device: the player goes there, and "Back to" returns for a minute.
  function movedTo(spot) {
    if (!plan || !lengths.length) return;
    known = confirmed = { track: spot.track, seconds: spot.seconds };
    const before = now();
    const index = Math.min(Math.max(0, spot.track), lengths.length - 1);
    setTrack(index, Math.min(Math.max(0, spot.seconds), Math.max(0, (lengths[index] ?? spot.seconds + 1) - 1)), wantPlay || !audio.paused);
    undo = { time: before, until: performance.now() + MOVED_UNDO_FOR };
    note = "moved";
    render();
  }

  // One place kept here sent late: it moves the shelf's place on only. True when the shelf took it.
  async function replay(bookId, kept) {
    try {
      const response = await fetch(`/books/${bookId}/audio/place`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ track: kept.track, seconds: kept.seconds, replayed: true }),
        credentials: "same-origin",
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  // Places and time from while the shelf could not be reached go back to it now.
  async function catchUp() {
    if (!navigator.onLine) return;
    try {
      for (let i = 0; i < localStorage.length; i++) {
        const key = localStorage.key(i);
        const bookId = Number(/^shelf-audio-place-(\d+)$/.exec(key || "")?.[1]);
        const kept = bookId ? read(key, null) : null;
        if (!kept?.waiting || plan?.bookId === bookId) continue;
        const response = await fetch(`/books/${bookId}/audio/place`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          // Sent late, it moves the place on only: listening done elsewhere since is never undone.
          body: JSON.stringify({ track: kept.track, seconds: kept.seconds, replayed: true }),
          credentials: "same-origin",
        });
        if (response.ok || response.status === 404 || response.status === 400) write(key, { ...kept, waiting: false });
      }

      const owed = read("shelf-audio-heard", {});
      for (const [bookId, days] of Object.entries(owed)) {
        for (const [day, seconds] of Object.entries(days)) {
          const response = await fetch(`/books/${bookId}/audio/listened`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ day, seconds }),
            credentials: "same-origin",
          });
          if (!response.ok && response.status !== 404 && response.status !== 400) return;
          delete days[day];
          write("shelf-audio-heard", owed);
        }
        delete owed[bookId];
        write("shelf-audio-heard", owed);
      }
    } catch {
      // Still out of reach; next time.
    }
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

  // A move far enough to lose one's place by mistake, half a minute or into another chapter, can be taken back for
  // a few seconds.
  function move(time) {
    const before = now();
    seek(time);
    if (plan && (Math.abs(time - before) >= 30 || chapterAt(time) !== chapterAt(before))) undo = { time: before, until: performance.now() + UNDO_FOR };
  }

  // Back goes to the start of this chapter first, unless that is where it already is.
  function chapter(direction) {
    if (!chapters.length) return;
    const time = now();
    const index = chapterAt(time);
    if (direction < 0 && time - chapters[index].start > 3) move(chapters[index].start);
    else move(chapters[Math.max(0, Math.min(chapters.length - 1, index + direction))].start);
  }

  // Over its last half minute the sleep timer turns the book down, so it stops gently rather than all at once.
  function fade() {
    let left = Infinity;
    if (!audio.paused && sleep.mode === "time") left = sleep.left / 1000;
    else if (!audio.paused && sleep.mode === "chapter") left = (sleep.until - now()) / rate;
    const wanted = volume * Math.min(1, Math.max(0, left / 30));
    if (Math.abs(audio.volume - wanted) > 0.005) audio.volume = wanted;
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
    fade();
    render();
  }

  function slept() {
    sleep = { mode: "off", left: 0, until: 0 };
    audio.pause();
    note = "slept";
    fade();
    render();
  }

  function tick() {
    if (!plan) return;
    const moment = performance.now();
    const playing = !audio.paused && !audio.ended;
    if (playing) heard += Math.min(5, (moment - lastTick) / 1000);
    if (playing && sleep.mode === "time") {
      sleep.left -= moment - lastTick;
      if (sleep.left <= 0) slept();
    }
    if (playing && sleep.mode === "chapter" && now() >= sleep.until - 0.3) slept();
    if (playing && moment - lastSaved > 15000) save();
    if (playing) fade();
    if (undo && moment > undo.until) undo = null;
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
    handler("seekto", (details) => Number.isFinite(details?.seekTime) && move(details.seekTime));
    handler("previoustrack", () => chapter(-1));
    handler("nexttrack", () => chapter(1));
  }

  function play() {
    if (!plan) return;
    note = "";
    // Only for the book that was paused: another one opened since starts where its place is.
    const paused = read("shelf-paused-at", null);
    write("shelf-paused-at", null);
    const back = smartRewind() && paused?.key === keyOf(plan) && paused.at ? rewindAfter(Date.now() - paused.at) : 0;
    if (back > 0 && track >= 0) {
      // Not back past the start of the chapter, which is where a reader who stopped there means to begin.
      const time = now();
      const start = chapters[chapterAt(time)]?.start ?? 0;
      wantPlay = true;
      seek(Math.max(start, time - back));
      return;
    }

    wantPlay = true;
    if (!audio.src) setTrack(Math.max(0, track), 0, true);
    else audio.play().catch(() => {});
    render();
  }

  // The full player for this book is on the page: the mini player steps aside.
  const fullView = () => (plan ? document.querySelector(`[data-player-view="full"][data-book="${keyOf(plan)}"]`) : null);

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
        case "undo":
          element.hidden = !undo;
          if (undo) fill(element, [clock(undo.time)]);
          break;
        case "cast":
          element.hidden = !(castAvailable && castable());
          element.setAttribute("aria-pressed", casting ? "true" : "false");
          break;
        case "note":
          element.hidden = !note;
          if (note && element.dataset[note]) fill(element, [element.dataset[note]]);
          break;
        case "cover":
          if (plan.cover && element.getAttribute("src") !== plan.cover) element.setAttribute("src", plan.cover);
          element.hidden = !plan.cover;
          break;
        case "link": element.setAttribute("href", plan.link ?? `/library/${plan.bookId}/listen`); break;
        case "ticks":
          if (drawnTicks !== element || element.dataset.drawn !== keyOf(plan)) {
            element.replaceChildren(...chapters.slice(1).map((item) => {
              const mark = document.createElement("span");
              mark.style.left = `${(item.start / Math.max(1, total)) * 100}%`;
              return mark;
            }));
            element.dataset.drawn = keyOf(plan);
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
    for (const box of document.querySelectorAll('input[data-player="smart-rewind"]')) {
      box.checked = smartRewind();
    }
    for (const select of document.querySelectorAll("select[data-jump]")) {
      const value = String(select.dataset.jump === "back" ? back : forward);
      if (select.value !== value) select.value = value;
    }
    for (const range of document.querySelectorAll('input[data-player="volume"]')) {
      if (document.activeElement !== range) range.value = String(Math.round(volume * 100));
    }

    if (session) session.playbackState = playing ? "playing" : "paused";
    tellPosition();
  }

  // Opens a book: the one already playing carries on; another stops the first, keeping its place.
  async function open(next, options = {}) {
    if (plan && keyOf(plan) === keyOf(next) && track >= 0) {
      plan = { ...plan, ...next, tracks: plan.tracks };
      if (options.arrive) arrive(options.arrive);
      render();
      return;
    }

    if (plan) {
      // A book played to its end was saved there already, and may since have been marked finished and started over.
      if (!audio.ended) save();
      audio.pause();
    }

    plan = next;
    track = -1;
    wantPlay = false;
    shownChapter = -1;
    note = "";
    sleep = { mode: "off", left: 0, until: 0 };
    undo = null;
    audio.volume = volume;
    rate = Math.min(3, Math.max(0.5, (next.speed || 100) / 100));
    write(STORE, { bookId: next.bookId, planUrl: next.planUrl });
    await prepare(next);
    handle();
    prepareCast();
    // A place kept here that the shelf has not had yet is newer than the shelf's.
    const local = read(`shelf-audio-place-${keyOf(next)}`, null);
    const shelf = { track: next.track ?? 0, seconds: next.seconds ?? 0 };
    let from = next;
    known = confirmed = next.placeUrl ? null : shelf;
    if (local?.waiting && next.placeUrl) {
      // Anything else with a plan of its own keeps its place here as before.
      from = local;
    } else if (local?.waiting) {
      // A place kept here and not yet sent (a save as the page closed, say) is never sent over a newer one: further
      // on, it goes to the shelf as one from listening offline, which moves the place on only, and plays from
      // there; behind, the shelf's place is the one, and the stale one is let go.
      const key = keyOf(next);
      const further = local.track > shelf.track || (local.track === shelf.track && local.seconds > shelf.seconds + 2);
      if (further) {
        from = local;
        known = confirmed = (await replay(next.bookId, local)) ? { track: local.track, seconds: local.seconds } : null;
      }
      if (keyOf(plan) === key) write(`shelf-audio-place-${key}`, { ...(further ? local : shelf), waiting: false });
    }
    const start = Math.min(Math.max(0, from.track), lengths.length - 1);
    if (options.arrive) arrive(options.arrive);
    else setTrack(start, Math.min(from.seconds || 0, Math.max(0, lengths[start] - 1)), Boolean(options.play));
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

  // A Play button on a book's page or card: the book starts in the mini player, and the page stays. True when it
  // started.
  async function playPlan(url) {
    try {
      const response = await fetch(url, { credentials: "same-origin" });
      if (!response.ok) return false;
      const next = await response.json();
      if (plan && keyOf(plan) === keyOf(next) && track >= 0) play();
      else await open(next, { play: true });
      return true;
    } catch {
      // The shelf is out of reach.
      return false;
    }
  }

  // A playlist being played: its things' plans in order, and which is playing. Kept on this device, so it goes on
  // after a reload; anything else started by hand ends it.
  const QUEUE = "shelf-queue";

  async function playFrom(queue) {
    for (let index = queue.index; index < queue.urls.length; index++) {
      // A thing gone since (a book let go, an episode its feed dropped) is passed over.
      if (await playPlan(queue.urls[index])) {
        write(QUEUE, { ...queue, index, key: keyOf(plan) });
        render();
        return;
      }
    }

    write(QUEUE, null);
  }

  function playNext() {
    const queue = read(QUEUE, null);
    if (!queue || !plan || queue.key !== keyOf(plan)) return;
    if (queue.index + 1 >= queue.urls.length) {
      write(QUEUE, null);
      return;
    }

    playFrom({ ...queue, index: queue.index + 1 });
  }

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

    // A playlist, from its start or from one thing in it.
    const list = event.target.closest?.("[data-play-queue]");
    if (list) {
      event.preventDefault();
      try {
        const urls = JSON.parse(list.dataset.playQueue);
        if (Array.isArray(urls) && urls.length > 0) playFrom({ urls, index: Math.max(0, Number(list.dataset.start) || 0), name: list.dataset.queueName || "" });
      } catch {
        // Not a list after all.
      }
      return;
    }

    // Anything else with a plan of its own, such as a podcast episode.
    const other = event.target.closest?.("[data-play-plan]");
    if (other) {
      event.preventDefault();
      playPlan(other.dataset.playPlan);
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
      case "chapter": move(chapters[Number(control.dataset.chapter)]?.start ?? 0); break;
      case "undo": if (undo) { const back = undo.time; undo = null; seek(back); } break;
      case "close": close(); break;
      case "cast": cast(); break;
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
      volume = Math.min(1, Math.max(0, Number(control.value) / 100));
      write("shelf-volume", volume);
      fade();
    }
  });

  document.addEventListener("change", (event) => {
    const control = event.target;
    if (!plan || !control.dataset) return;
    if (control.dataset.player === "scrub") {
      control.dataset.dragging = "false";
      const current = chapters[chapterAt(now())] ?? { start: 0, end: total };
      move(current.start + (Number(control.value) / 1000) * (current.end - current.start));
    } else if (control.dataset.player === "smart-rewind") {
      write("shelf-smart-rewind", control.checked);
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
  window.addEventListener("online", catchUp);
  setInterval(tick, 500);

  // After a reload, the book that was playing waits in the mini player, paused where it stopped.
  async function restore() {
    const kept = read(STORE, null);
    if (!(kept?.bookId || kept?.planUrl) || plan) return;
    try {
      const response = await fetch(kept.planUrl ?? `/books/${kept.bookId}/audio/plan`, { credentials: "same-origin" });
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
    // The playlist playing, if any: its name, and which of its things is on.
    queue: () => read(QUEUE, null),
    close,
    play,
    pause: () => audio.pause(),
    seek,
    // A bookmark: a long move, so it can be taken back.
    seekTrack: (index, seconds) => {
      if (!plan) return;
      const at = Math.min(Math.max(0, index), lengths.length - 1);
      move(offsets[at] + seconds);
    },
    // Where the player is, for a bookmark or for carrying the place to the e-book.
    now: () => ({
      bookId: plan?.bookId ?? 0,
      key: plan ? keyOf(plan) : "",
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

  const start = () => {
    catchUp();
    if (!document.body.dataset.noRestore) restore();
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
