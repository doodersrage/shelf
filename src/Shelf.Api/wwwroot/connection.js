// Says plainly when this device has lost the shelf, instead of pages that just fail to load, and where the books kept
// on it are. The reader and the player say it their own way, so they are left alone.
(() => {
  let note = null;

  function own() {
    return /^\/library\/\d+\/(read|listen)$/.test(location.pathname);
  }

  function show() {
    if (note || own()) {
      return;
    }

    note = document.createElement("p");
    note.className = "connection-lost";
    note.setAttribute("role", "status");
    note.append(t("You're offline. Pages that need the shelf open again once you're back. Meanwhile, the books kept on this device are in") + " ");
    const link = document.createElement("a");
    link.href = "/offline.html";
    link.textContent = t("Offline reading");
    note.append(link, ".");
    document.body.append(note);
  }

  function hide() {
    note?.remove();
    note = null;
  }

  addEventListener("offline", show);
  addEventListener("online", hide);
  if (!navigator.onLine) {
    show();
  }
})();
