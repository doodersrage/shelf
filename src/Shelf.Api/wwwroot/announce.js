// An admin's announcement, at the top of every page: Hide puts it away in this browser until a newer one goes up.
(() => {
  const KEY = "shelf-announcement-hidden";
  const hidden = () => {
    try {
      return localStorage.getItem(KEY);
    } catch {
      return null;
    }
  };

  const apply = () => {
    for (const note of document.querySelectorAll("[data-announcement]")) {
      note.hidden = note.dataset.announcement === hidden();
    }
  };

  document.addEventListener("click", (event) => {
    const button = event.target.closest?.("[data-hide-announcement]");
    const note = button?.closest("[data-announcement]");
    if (!note) return;
    try {
      localStorage.setItem(KEY, note.dataset.announcement);
    } catch {
      // A private window: it hides for this page only.
    }
    note.hidden = true;
  });

  // Pages change without a reload; the note comes back with each, so it is put away again.
  const start = () => {
    apply();
    const whenReady = () => window.Blazor?.addEventListener?.("enhancedload", apply);
    if (window.Blazor) whenReady();
    else window.addEventListener("load", whenReady);
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
