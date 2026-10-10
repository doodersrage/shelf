// The docs' few behaviours: the theme switch, the phone menu, copying code, the section list, and search.
(() => {
  const root = document.documentElement;
  // Beside this script's folder, wherever the page itself is (the not-found page can be anywhere).
  const searchIndex = new URL("../search.json", document.currentScript?.src ?? location.href).href;
  const siteRoot = new URL("../", document.currentScript?.src ?? location.href).href;

  // Theme: light, dark, or the system's, remembered in this browser.
  const dark = () => root.dataset.theme === "dark" || (!root.dataset.theme && matchMedia("(prefers-color-scheme: dark)").matches);
  document.querySelector(".theme")?.addEventListener("click", () => {
    const next = dark() ? "light" : "dark";
    root.dataset.theme = next;
    try {
      localStorage.setItem("shelf-docs-theme", next);
    } catch {
      // Storage refused; the choice still holds on this page.
    }
  });

  // On a phone the sidebar is a drawer.
  const menu = document.querySelector(".menu");
  const setMenu = (open) => {
    document.body.classList.toggle("nav-open", open);
    menu?.setAttribute("aria-expanded", String(open));
  };
  menu?.addEventListener("click", () => setMenu(!document.body.classList.contains("nav-open")));
  document.querySelector(".sidebar")?.addEventListener("click", (event) => {
    if (event.target.closest("a")) setMenu(false);
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") setMenu(false);
  });
  document.addEventListener("click", (event) => {
    if (document.body.classList.contains("nav-open") && !event.target.closest(".sidebar, .menu")) setMenu(false);
  });

  // A copy button on every block of code.
  for (const block of document.querySelectorAll(".prose pre")) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "copy";
    button.textContent = "Copy";
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(block.querySelector("code")?.innerText ?? block.innerText);
        button.textContent = "Copied";
        button.classList.add("done");
      } catch {
        button.textContent = "Select and copy";
      }

      setTimeout(() => {
        button.textContent = "Copy";
        button.classList.remove("done");
      }, 1600);
    });
    block.append(button);
  }

  // "On this page" marks the section being read.
  const links = [...document.querySelectorAll(".toc a")];
  if (links.length && "IntersectionObserver" in window) {
    const byId = new Map(links.map((link) => [decodeURIComponent(link.hash.slice(1)), link]));
    const observer = new IntersectionObserver((entries) => {
      const visible = entries.filter((entry) => entry.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
      if (!visible) return;
      links.forEach((link) => link.classList.remove("here"));
      byId.get(visible.target.id)?.classList.add("here");
    }, { rootMargin: "-15% 0px -70% 0px" });
    document.querySelectorAll(".prose h2[id], .prose h3[id]").forEach((heading) => observer.observe(heading));
  }

  // Search: the sections of every page, matched on all the words typed, titles first.
  const input = document.getElementById("search");
  const results = document.getElementById("results");
  if (!input || !results) return;
  let index = null;
  let chosen = -1;

  const load = async () => {
    if (index) return index;
    try {
      index = await (await fetch(searchIndex)).json();
    } catch {
      index = [];
    }

    return index;
  };

  const escape = (text) => text.replace(/[&<>"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[character]);
  const markWords = (text, words) => {
    let html = escape(text);
    for (const word of words) {
      html = html.replace(new RegExp(`(${word.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")})`, "gi"), "<mark>$1</mark>");
    }

    return html;
  };

  const show = (open) => {
    results.hidden = !open;
    input.setAttribute("aria-expanded", String(open));
  };

  const pick = (at) => {
    const options = [...results.querySelectorAll("a")];
    if (!options.length) return;
    chosen = (at + options.length) % options.length;
    options.forEach((option, position) => option.setAttribute("aria-selected", String(position === chosen)));
    options[chosen].scrollIntoView({ block: "nearest" });
  };

  input.addEventListener("input", async () => {
    const words = input.value.toLowerCase().split(/\s+/).filter((word) => word.length > 1);
    if (!words.length) {
      show(false);
      return;
    }

    const entries = await load();
    const scored = entries
      .map((entry) => {
        const title = entry.title.toLowerCase();
        const text = entry.text.toLowerCase();
        if (!words.every((word) => title.includes(word) || text.includes(word) || entry.page.toLowerCase().includes(word))) return null;
        const score = words.reduce((total, word) => total + (title.includes(word) ? 10 : 0) + (text.split(word).length - 1), 0);
        const first = Math.max(0, Math.min(...words.map((word) => text.indexOf(word)).filter((at) => at >= 0)) - 40);
        return { entry, score, words: (first > 0 ? "…" : "") + entry.text.slice(first, first + 160) };
      })
      .filter(Boolean)
      .sort((a, b) => b.score - a.score)
      .slice(0, 12);

    chosen = -1;
    results.innerHTML = scored.length
      ? scored.map(({ entry, words: snippet }) => `<a role="option" href="${siteRoot}${entry.url}" aria-selected="false">
          <span class="where">${escape(entry.page)}</span>
          <span class="what">${markWords(entry.title, words)}</span>
          <span class="words">${markWords(snippet, words)}</span></a>`).join("")
      : `<p class="none">Nothing matches “${escape(input.value)}”.</p>`;
    show(true);
  });

  input.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      pick(chosen + 1);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      pick(chosen - 1);
    } else if (event.key === "Enter") {
      const target = results.querySelectorAll("a")[Math.max(chosen, 0)];
      if (target) location.href = target.href;
    } else if (event.key === "Escape") {
      input.value = "";
      show(false);
    }
  });

  input.addEventListener("focus", load);
  document.addEventListener("click", (event) => {
    if (!event.target.closest(".search")) show(false);
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "/" && !/input|textarea|select/i.test(document.activeElement?.tagName ?? "")) {
      event.preventDefault();
      input.focus();
    }
  });
})();
