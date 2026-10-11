// Keeps the notice of an import running in the background up to date, on whatever page it shows: every few
// seconds it asks how far the import has come, and when it is done, says so and links to what came across.
(() => {
  let asking = false;

  async function tick() {
    const notice = document.querySelector("[data-import-progress]");
    if (!notice || notice.dataset.importDone || asking || document.visibilityState === "hidden") return;
    asking = true;
    try {
      const response = await fetch("/books/imports/running", { credentials: "same-origin", headers: { Accept: "application/json" } });
      if (response.status === 204) {
        notice.dataset.importDone = "true";
        const link = notice.querySelector("a");
        if (link) link.textContent = notice.dataset.done;
        notice.querySelector("[data-import-bar]")?.remove();
        return;
      }

      if (!response.ok) return;
      const summary = await response.json();
      const bar = notice.querySelector("[data-import-bar]");
      if (bar) {
        bar.max = Math.max(1, summary.total);
        bar.value = summary.total - summary.left;
      }

      const text = notice.querySelector("[data-import-text]");
      if (text && summary.text) text.textContent = summary.text;
    } catch {
      // Not answering just now; the next tick asks again.
    } finally {
      asking = false;
    }
  }

  setInterval(tick, 5000);
})();
