// A long list's "Show more" (marked data-more) is pressed for you as you scroll near it, so a big library reads as
// one list; the button stays, for the keyboard and for anyone who'd rather press it.
(() => {
  if (!("IntersectionObserver" in window)) {
    return;
  }

  const watched = new WeakSet();
  const observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        const button = entry.target;
        if (!entry.isIntersecting || button.disabled || !button.isConnected) {
          continue;
        }
        button.click();
        // Looked at again once the next books are drawn: if it is still near, more come.
        observer.unobserve(button);
        setTimeout(() => button.isConnected && observer.observe(button), 400);
      }
    },
    { rootMargin: "0px 0px 800px 0px" },
  );

  const watch = () => {
    for (const button of document.querySelectorAll("[data-more]")) {
      if (!watched.has(button)) {
        watched.add(button);
        observer.observe(button);
      }
    }
  };

  new MutationObserver(watch).observe(document.documentElement, { childList: true, subtree: true });
  watch();
})();

// A menu that folds open (the phone's Add, a book's More) folds away again on a tap elsewhere or Escape.
(() => {
  const menus = "details.add-sheet[open], details.menu[open]";
  document.addEventListener("click", (event) => {
    for (const open of document.querySelectorAll(menus)) {
      if (!open.contains(event.target)) {
        open.removeAttribute("open");
      }
    }
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      for (const open of document.querySelectorAll(menus)) {
        open.removeAttribute("open");
        open.querySelector("summary")?.focus();
      }
    }
  });
})();
