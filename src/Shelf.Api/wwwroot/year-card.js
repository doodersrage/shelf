// The year in books as a picture: draws the card's SVG onto a canvas, makes a PNG of it, and offers it to the
// phone's share sheet, or saves it where sharing files is not offered.
(() => {
  async function picture(button) {
    const response = await fetch(button.dataset.card, { credentials: "same-origin" });
    if (!response.ok) throw new Error("no card");
    const url = URL.createObjectURL(new Blob([await response.text()], { type: "image/svg+xml" }));
    try {
      const image = new Image();
      image.decoding = "async";
      await new Promise((done, failed) => {
        image.onload = done;
        image.onerror = failed;
        image.src = url;
      });
      const canvas = document.createElement("canvas");
      canvas.width = 1080;
      canvas.height = 1350;
      canvas.getContext("2d").drawImage(image, 0, 0, 1080, 1350);
      return await new Promise((done) => canvas.toBlob(done, "image/png"));
    } finally {
      URL.revokeObjectURL(url);
    }
  }

  document.addEventListener("click", async (event) => {
    const button = event.target.closest("[data-card]");
    if (!button) return;
    const status = document.querySelector("[data-card-status]");
    button.disabled = true;
    try {
      const blob = await picture(button);
      const file = new File([blob], button.dataset.name, { type: "image/png" });
      if (navigator.canShare?.({ files: [file] })) {
        await navigator.share({ files: [file], title: button.dataset.title });
      } else {
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = button.dataset.name;
        link.click();
        setTimeout(() => URL.revokeObjectURL(link.href), 10_000);
      }
      if (status) status.textContent = "";
    } catch (error) {
      if (error?.name !== "AbortError" && status) status.textContent = status.dataset.failed;
    } finally {
      button.disabled = false;
    }
  });
})();
