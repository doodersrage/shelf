// Large uploads (e-books, audiobooks, backups) show how far they have got. A form opts in with
// data-upload; without this script it still posts the ordinary way.
document.addEventListener("submit", (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-upload") || !window.FormData || !window.XMLHttpRequest) {
    return;
  }

  event.preventDefault();
  const button = form.querySelector("button[type=submit]");
  const status = form.querySelector("[data-upload-status]");
  const bar = form.querySelector("progress[data-upload-progress]");
  const request = new XMLHttpRequest();

  const say = (text) => {
    if (status) {
      status.hidden = false;
      status.textContent = text;
    }
  };

  if (button) {
    button.disabled = true;
  }

  if (bar) {
    bar.hidden = false;
    bar.removeAttribute("value");
  }

  say(t("Sending…"));
  request.upload.addEventListener("progress", (progress) => {
    if (!progress.lengthComputable) {
      return;
    }

    const percent = Math.round((progress.loaded / progress.total) * 100);
    if (bar) {
      bar.max = 100;
      bar.value = percent;
    }

    say(percent < 100 ? t("Sent {0}%", percent) : t("Sent. Shelf is putting it away…"));
  });

  request.addEventListener("load", () => {
    // The server answers with a redirect to the page that reports the outcome; go where it led.
    window.location.assign(request.responseURL || window.location.href);
  });

  request.addEventListener("error", () => {
    say(t("The upload stopped. Check the connection and try again."));
    if (button) {
      button.disabled = false;
    }
  });

  request.open(form.method || "post", form.action);
  request.send(new FormData(form));
});
