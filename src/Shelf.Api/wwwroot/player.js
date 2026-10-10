let audio = null;
let dotNet = null;
let last = 0;
let rate = 1;
let sleepTimer = 0;

function onPause() {
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
  audio.addEventListener("pause", onPause);
  audio.addEventListener("ended", onEnded);
  audio.addEventListener("timeupdate", onTime);
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

  audio.removeEventListener("pause", onPause);
  audio.removeEventListener("ended", onEnded);
  audio.removeEventListener("timeupdate", onTime);
  audio = null;
  dotNet = null;
}
