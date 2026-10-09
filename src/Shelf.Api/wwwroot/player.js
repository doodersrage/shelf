let audio = null;
let dotNet = null;
let last = 0;

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

export function attach(element, callback) {
  detach();
  audio = element;
  dotNet = callback;
  last = 0;
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
