let handler = null;
let keys = null;

export function listen(dotNet) {
  if (handler) {
    window.removeEventListener("message", handler);
  }

  handler = (event) => {
    const data = event.data;
    if (!data || !["shelf-selection", "shelf-mark", "shelf-place", "shelf-turn", "shelf-page"].includes(data.source)) {
      return;
    }

    if (data.source === "shelf-turn") {
      dotNet.invokeMethodAsync("Turned", Number(data.step) || 0);
      return;
    }

    if (data.source === "shelf-page") {
      dotNet.invokeMethodAsync("Paged", Number(data.page) || 1, Number(data.pages) || 1);
      return;
    }

    if (data.source === "shelf-place") {
      dotNet.invokeMethodAsync("Scrolled", Number(data.at) || 0);
      return;
    }

    if (data.source === "shelf-selection") {
      dotNet.invokeMethodAsync("Selected", data.text ?? "", data.prefix ?? "", data.suffix ?? "");
      return;
    }

    dotNet.invokeMethodAsync("OpenMark", data.id);
  };

  window.addEventListener("message", handler);
  document.querySelector("iframe.reader-frame")?.contentWindow?.postMessage({ source: "shelf-ask" }, "*");
  if (!keys) {
    // With the focus outside the chapter, the arrow keys still turn its pages.
    keys = (event) => {
      if (event.target.closest?.("input, textarea, select, [contenteditable]") || event.altKey || event.ctrlKey || event.metaKey) return;
      const step = { ArrowRight: 1, PageDown: 1, ArrowLeft: -1, PageUp: -1 }[event.key];
      const frame = document.querySelector("iframe.reader-frame");
      if (step && frame?.dataset.paged === "true" && frame.contentWindow) {
        event.preventDefault();
        frame.contentWindow.postMessage({ source: "shelf-go", step }, "*");
      }
    };
    document.addEventListener("keydown", keys);
  }
}

export function stop() {
  if (handler) {
    window.removeEventListener("message", handler);
    handler = null;
  }

  if (keys) {
    document.removeEventListener("keydown", keys);
    keys = null;
  }
}
