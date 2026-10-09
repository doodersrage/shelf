let handler = null;

export function listen(dotNet) {
  if (handler) {
    window.removeEventListener("message", handler);
  }

  handler = (event) => {
    const data = event.data;
    if (!data || (data.source !== "shelf-selection" && data.source !== "shelf-mark")) {
      return;
    }

    if (data.source === "shelf-selection") {
      dotNet.invokeMethodAsync("Selected", data.text ?? "", data.prefix ?? "", data.suffix ?? "");
      return;
    }

    dotNet.invokeMethodAsync("OpenMark", data.id);
  };

  window.addEventListener("message", handler);
}

export function stop() {
  if (handler) {
    window.removeEventListener("message", handler);
    handler = null;
  }
}
