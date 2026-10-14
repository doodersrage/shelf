// Quick jump: Ctrl K (or ⌘ K, or "/" when not typing) opens a box that goes to any page of Shelf, or any of the
// reader's books, authors, series, places, and collections, by a few letters of its name. The box and its words
// are drawn by the page (MainLayout), already in the reader's language; this only wires it up.
(() => {
  let asked = 0;
  let timer;
  let active = -1;
  let hits = [];

  const box = () => document.getElementById("jump");
  const input = () => document.getElementById("jump-input");
  const list = () => document.getElementById("jump-list");

  const typing = (target) =>
    target instanceof HTMLElement && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName));

  function choose(at) {
    const options = list().querySelectorAll("[role=option]");
    active = options.length === 0 ? -1 : (at + options.length) % options.length;
    options.forEach((option, index) => option.setAttribute("aria-selected", index === active ? "true" : "false"));
    if (active >= 0) {
      input().setAttribute("aria-activedescendant", options[active].id);
      options[active].scrollIntoView({ block: "nearest" });
    } else {
      input().removeAttribute("aria-activedescendant");
    }
  }

  function show(found) {
    hits = found;
    const shown = list();
    shown.replaceChildren();
    found.forEach((hit, at) => {
      const option = document.createElement("li");
      option.id = `jump-option-${at}`;
      option.setAttribute("role", "option");
      option.dataset.href = hit.href;
      const kind = document.createElement("span");
      kind.className = "jump-kind";
      kind.textContent = hit.kind;
      const title = document.createElement("span");
      title.className = "jump-title";
      title.textContent = hit.title;
      option.append(kind, title);
      if (hit.detail) {
        const detail = document.createElement("span");
        detail.className = "jump-detail";
        detail.textContent = hit.detail;
        option.append(detail);
      }
      shown.append(option);
    });
    const none = box().querySelector("[data-jump-none]");
    none.hidden = found.length > 0 || input().value.trim() === "";
    input().setAttribute("aria-expanded", found.length > 0 ? "true" : "false");
    choose(found.length > 0 ? 0 : -1);
  }

  // The pages and books opened lately on this device, newest first, offered before anything is typed. Kept in this
  // browser only, as a convenience: losing them loses nothing.
  const recentKey = "shelf-recent";
  const keep = 6;

  function recent() {
    try {
      return JSON.parse(localStorage.getItem(recentKey) ?? "[]").filter((item) => item && item.href && item.title);
    } catch {
      return [];
    }
  }

  function remember() {
    const href = location.pathname + location.search;
    const title = document.title.replace(/\s*·\s*Shelf\s*$/, "").trim();
    if (!box() || !title || href === "/" || /\/(read|listen)$/.test(location.pathname)) {
      return;
    }
    try {
      const list = [{ href, title }, ...recent().filter((item) => item.href !== href)].slice(0, keep);
      localStorage.setItem(recentKey, JSON.stringify(list));
    } catch {
      // A private window may refuse; quick jump just has no recent places.
    }
  }

  remember();
  // Blazor's enhanced navigation changes the page without loading it again.
  const whenReady = () => window.Blazor?.addEventListener?.("enhancedload", () => setTimeout(remember, 0));
  if (window.Blazor) {
    whenReady();
  } else {
    window.addEventListener("load", whenReady);
  }

  async function search() {
    const mine = ++asked;
    const typed = input().value.trim();
    try {
      const response = await fetch(`/books/jump?q=${encodeURIComponent(typed)}`, { credentials: "same-origin", headers: { Accept: "application/json" } });
      if (response.ok && mine === asked) {
        const found = await response.json();
        if (typed === "") {
          const label = box().dataset.recent;
          const lately = recent().map((item) => ({ kind: label, title: item.title, href: item.href }));
          const seen = new Set(lately.map((item) => item.href));
          show([...lately, ...found.filter((hit) => !seen.has(hit.href))]);
        } else {
          // Enter searches the library, unless what was typed is exactly the name of a page or book.
          const search = { kind: box().dataset.searchKind, title: box().dataset.search.replace("{0}", typed), href: `/library?q=${encodeURIComponent(typed)}` };
          const exact = found.findIndex((hit) => hit.title.toLowerCase() === typed.toLowerCase());
          show(exact >= 0 ? [found[exact], search, ...found.filter((_, at) => at !== exact)] : [search, ...found]);
        }
      }
    } catch {
      // Offline, or the server away for a moment: the box stays as it was.
    }
  }

  function go(href) {
    box().close();
    if (window.Blazor?.navigateTo) {
      window.Blazor.navigateTo(href);
    } else {
      location.assign(href);
    }
  }

  function open(typed = "") {
    const dialog = box();
    if (!dialog || dialog.open) {
      return;
    }
    input().value = typed;
    dialog.showModal();
    input().focus();
    search();
  }

  document.addEventListener("keydown", (event) => {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === "k") {
      if (box()) {
        event.preventDefault();
        open();
      }
    } else if (event.key === "/" && !event.ctrlKey && !event.metaKey && !event.altKey && !typing(event.target) && box() && !document.querySelector("dialog[open]")) {
      event.preventDefault();
      open();
    }
  });

  // The menu's search box opens the box instead, taking with it whatever was typed there.
  document.addEventListener("focusin", (event) => {
    const field = event.target;
    if (field instanceof HTMLInputElement && field.matches("[data-jump-input]") && box()) {
      const typed = field.value;
      field.value = "";
      field.blur();
      field.closest("details.drawer")?.removeAttribute("open");
      open(typed);
    }
  });

  document.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest("[data-jump-open]")) {
      event.preventDefault();
      // The phone menu folds away behind the box.
      event.target.closest("details.drawer")?.removeAttribute("open");
      open();
      return;
    }
    const option = event.target instanceof Element ? event.target.closest("#jump-list [role=option]") : null;
    if (option) {
      go(option.dataset.href);
    } else if (event.target === box()) {
      // A click on the dimmed page around the box closes it.
      box().close();
    }
  });

  document.addEventListener("input", (event) => {
    if (event.target === input()) {
      clearTimeout(timer);
      timer = setTimeout(search, 120);
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.target !== input()) {
      return;
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      choose(active + (event.key === "ArrowDown" ? 1 : -1));
    } else if (event.key === "Enter") {
      event.preventDefault();
      const hit = hits[Math.max(0, active)];
      if (hit) {
        go(hit.href);
      }
    }
  });
})();

// A section a tabbed page has just drawn (SectionTabs), brought into view once it is there.
window.shelfReveal = (id) => {
  requestAnimationFrame(() => document.getElementById(id)?.scrollIntoView({ block: "start" }));
};

// The tabbed page shown (SectionTabs) is told when only the address's #… changes, as on Back, or a link to another
// part of the same page, which Blazor doesn't always pass on.
window.shelfWatchHash = (page) => {
  window.shelfHashPage = page;
  if (!window.shelfHashWatched) {
    window.shelfHashWatched = true;
    window.addEventListener("hashchange", () => {
      window.shelfHashPage?.invokeMethodAsync("Follow", location.href).catch(() => {});
    });
  }
};

// "?" (or the menu's Keyboard shortcuts) shows every key Shelf answers to.
(() => {
  const typing = (target) =>
    target instanceof HTMLElement && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName));
  const show = () => {
    const dialog = document.getElementById("shortcuts");
    if (dialog && !dialog.open) {
      dialog.showModal();
    }
  };
  document.addEventListener("keydown", (event) => {
    if (event.key === "?" && !event.ctrlKey && !event.metaKey && !event.altKey && !typing(event.target) && !document.querySelector("dialog[open]")) {
      event.preventDefault();
      show();
    }
  });
  document.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest("[data-shortcuts-open]")) {
      event.target.closest("details.drawer")?.removeAttribute("open");
      show();
    } else if (event.target instanceof HTMLDialogElement && event.target.id === "shortcuts") {
      event.target.close();
    }
  });
})();
