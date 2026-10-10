let audio = null;
let dotNet = null;
let last = 0;
let rate = 1;
let sleepTimer = 0;
// Set when a lock-screen or headphone skip moves to another track while playing, so the next one carries on.
let keepPlaying = false;
let lastPosition = 0;

// The lock screen, notification, headphones, and car: what is playing, and buttons that drive this player.
const session = "mediaSession" in navigator ? navigator.mediaSession : null;

function tellPosition() {
  if (!session?.setPositionState || !audio || !Number.isFinite(audio.duration) || audio.duration <= 0) {
    return;
  }

  try {
    session.setPositionState({
      duration: audio.duration,
      playbackRate: audio.playbackRate || 1,
      position: Math.min(audio.currentTime || 0, audio.duration),
    });
  } catch {
    // A position past the end while a track changes; the next update corrects it.
  }
}

function onPlay() {
  if (session) {
    session.playbackState = "playing";
  }
  tellPosition();
}

function onPause() {
  if (session) {
    session.playbackState = "paused";
  }
  if (dotNet && audio) {
    dotNet.invokeMethodAsync("Progress", Math.floor(audio.currentTime || 0));
  }
}

function onEnded() {
  if (dotNet) {
    dotNet.invokeMethodAsync("Ended");
  }
}

function onTime() {
  if (!audio || !dotNet) {
    return;
  }

  if (Math.abs(audio.currentTime - lastPosition) >= 5) {
    lastPosition = audio.currentTime;
    tellPosition();
  }

  if (audio.currentTime - last >= 10) {
    last = audio.currentTime;
    dotNet.invokeMethodAsync("Progress", Math.floor(audio.currentTime));
  }
}

export function attach(element, callback, playbackRate) {
  detach();
  audio = element;
  dotNet = callback;
  last = 0;
  rate = playbackRate || rate;
  // A new track is a new element, so the reader's speed is set on each one.
  audio.defaultPlaybackRate = rate;
  audio.playbackRate = rate;
  audio.addEventListener("play", onPlay);
  audio.addEventListener("pause", onPause);
  audio.addEventListener("ended", onEnded);
  audio.addEventListener("timeupdate", onTime);
  audio.addEventListener("loadedmetadata", tellPosition);
  audio.addEventListener("ratechange", tellPosition);
  handle();
  if (keepPlaying) {
    keepPlaying = false;
    audio.play().catch(() => {});
  }
}

// What the lock screen shows: the chapter or track as the title, the book as the album, and its cover.
export function describe(title, author, book, cover) {
  if (!session || typeof MediaMetadata === "undefined") {
    return;
  }

  try {
    session.metadata = new MediaMetadata({
      title: title || book,
      artist: author || "",
      album: book || "",
      artwork: cover ? [{ src: new URL(cover, location.href).href, sizes: "512x512" }] : [],
    });
  } catch {
    // An older browser, or a cover address it will not take: the lock screen goes without.
  }
}

function on(action, handler) {
  try {
    session.setActionHandler(action, handler);
  } catch {
    // This browser has no such button.
  }
}

function skip(direction) {
  if (!dotNet) {
    return;
  }
  keepPlaying = Boolean(audio && !audio.paused);
  dotNet.invokeMethodAsync("Skip", direction).finally(() => {
    // A skip within the same track (a chapter) keeps the element, and so its playing.
    if (keepPlaying && audio && audio.paused) {
      audio.play().catch(() => {});
    }
  });
}

function handle() {
  if (!session) {
    return;
  }

  on("play", () => audio?.play());
  on("pause", () => audio?.pause());
  on("stop", () => audio?.pause());
  on("seekbackward", (details) => {
    if (audio) audio.currentTime = Math.max(0, audio.currentTime - (details?.seekOffset || 15));
  });
  on("seekforward", (details) => {
    if (audio) audio.currentTime = Math.min(audio.duration || Infinity, audio.currentTime + (details?.seekOffset || 30));
  });
  on("seekto", (details) => {
    if (audio && Number.isFinite(details?.seekTime)) {
      if (details.fastSeek && "fastSeek" in audio) audio.fastSeek(details.seekTime);
      else audio.currentTime = details.seekTime;
      tellPosition();
    }
  });
  on("previoustrack", () => skip(-1));
  on("nexttrack", () => skip(1));
}

// Where playback is now, for a bookmark.
export function position() {
  return audio ? Math.floor(audio.currentTime || 0) : 0;
}

export function seek(element, seconds) {
  const go = () => {
    element.currentTime = seconds;
  };

  if (element.readyState >= 1) {
    go();
    return;
  }

  element.addEventListener("loadedmetadata", go, { once: true });
}

export function speed(playbackRate) {
  rate = playbackRate || 1;
  if (audio) {
    audio.defaultPlaybackRate = rate;
    audio.playbackRate = rate;
  }
}

// Pause after this many minutes of the timer running; 0 turns it off. The pause saves the place as usual.
export function sleep(minutes) {
  clearTimeout(sleepTimer);
  sleepTimer = 0;
  if (!minutes) {
    return;
  }

  sleepTimer = setTimeout(() => {
    sleepTimer = 0;
    if (audio && !audio.paused) {
      audio.pause();
    }

    if (dotNet) {
      dotNet.invokeMethodAsync("Slept");
    }
  }, minutes * 60 * 1000);
}

export function detach() {
  if (!audio) {
    return;
  }

  audio.removeEventListener("play", onPlay);
  audio.removeEventListener("pause", onPause);
  audio.removeEventListener("ended", onEnded);
  audio.removeEventListener("timeupdate", onTime);
  audio.removeEventListener("loadedmetadata", tellPosition);
  audio.removeEventListener("ratechange", tellPosition);
  audio = null;
  dotNet = null;
  if (session) {
    session.metadata = null;
    session.playbackState = "none";
    for (const action of ["play", "pause", "stop", "seekbackward", "seekforward", "seekto", "previoustrack", "nexttrack"]) {
      on(action, null);
    }
  }
}
