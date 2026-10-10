// Reads a book's ISBN from the barcode on its back. Where the page is secure (https, or this computer), the camera
// shows live and the code is read as soon as it is in view; elsewhere browsers keep the camera to themselves, so the
// button takes a photo instead. The browser's own barcode reader is used when it has one, and otherwise ZXing,
// compiled to WebAssembly and served by the shelf itself (lib/barcode).
const formats = ["ean_13", "upc_a"];
let detector;

function loadScript(src) {
  return new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = src;
    script.onload = resolve;
    script.onerror = () => reject(new Error(`${src} did not load`));
    document.head.append(script);
  });
}

function reader() {
  detector ??= (async () => {
    if ("BarcodeDetector" in window) {
      try {
        const supported = await window.BarcodeDetector.getSupportedFormats();
        if (supported.includes("ean_13")) {
          return new window.BarcodeDetector({ formats });
        }
      } catch {
        // Fall through to ZXing.
      }
    }

    await loadScript("/lib/barcode/barcode-detector.js");
    const api = window.BarcodeDetectionAPI;
    const wasm = new URL("/lib/barcode/zxing_reader.wasm", location.href).href;
    api.prepareZXingModule({
      overrides: { locateFile: (path, prefix) => (path.endsWith(".wasm") ? wasm : prefix + path) },
    });
    return new api.BarcodeDetector({ formats });
  })();
  return detector;
}

// A book's barcode is an EAN-13 that starts 978 or 979, with a check digit that adds up.
function isbn(found) {
  for (const code of found) {
    const digits = (code.rawValue ?? "").replace(/\D/g, "");
    if (digits.length === 13 && /^97[89]/.test(digits)) {
      const sum = [...digits.slice(0, 12)].reduce((total, digit, at) => total + Number(digit) * (at % 2 ? 3 : 1), 0);
      if ((10 - (sum % 10)) % 10 === Number(digits[12])) {
        return digits;
      }
    }
  }

  return null;
}

function live(t) {
  return new Promise((resolve, reject) => {
    const dialog = document.createElement("dialog");
    dialog.className = "scanner";
    dialog.setAttribute("aria-label", t("Scan the barcode"));
    const video = document.createElement("video");
    video.muted = true;
    video.playsInline = true;
    video.setAttribute("aria-hidden", "true");
    const hint = document.createElement("p");
    hint.className = "hint";
    hint.setAttribute("role", "status");
    hint.textContent = t("Hold the barcode on the back of the book inside the frame.");
    const close = document.createElement("button");
    close.type = "button";
    close.textContent = t("Cancel");
    dialog.append(video, hint, close);
    document.body.append(dialog);

    let stream;
    let done = false;
    const finish = (value, error) => {
      if (done) {
        return;
      }
      done = true;
      stream?.getTracks().forEach((track) => track.stop());
      dialog.close();
      dialog.remove();
      error ? reject(error) : resolve(value);
    };
    close.addEventListener("click", () => finish(null));
    dialog.addEventListener("cancel", () => finish(null));
    dialog.showModal();

    (async () => {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } }, audio: false });
        video.srcObject = stream;
        await video.play();
        const scanner = await reader();
        while (!done) {
          if (video.readyState >= 2) {
            const found = isbn(await scanner.detect(video).catch(() => []));
            if (found) {
              finish(found);
              return;
            }
          }
          await new Promise((wait) => setTimeout(wait, 200));
        }
      } catch (error) {
        finish(null, error);
      }
    })();
  });
}

async function photo(file) {
  const scanner = await reader();
  const image = await createImageBitmap(file);
  try {
    return isbn(await scanner.detect(image));
  } finally {
    image.close?.();
  }
}

// Wires the scan button inside a book form. The click stays in the page, not the server's round trip, so the
// browser still counts it as the reader's own when it opens the camera or the photo picker.
export function attach(root, form) {
  const button = root.querySelector("[data-scan]");
  const input = root.querySelector("[data-scan-photo]");
  const status = root.querySelector("[data-scan-status]");
  if (!button || !input || button.dataset.attached) {
    return;
  }

  button.dataset.attached = "true";
  button.hidden = false;
  const t = window.t ?? ((text) => text);
  const tell = (text) => {
    status.textContent = text;
  };
  const found = async (code) => {
    if (code) {
      tell("");
      await form.invokeMethodAsync("Scanned", code);
    } else {
      tell(t("No ISBN barcode was found. Try again closer, with more light, or type the number in."));
    }
  };

  let canWatch = Boolean(window.isSecureContext && navigator.mediaDevices?.getUserMedia);
  button.addEventListener("click", async () => {
    if (!canWatch) {
      input.click();
      return;
    }
    tell("");
    try {
      const code = await live(t);
      if (code) {
        await found(code);
      }
    } catch {
      // No camera, or permission refused. The picker must open from a click of its own, so the next press takes a photo.
      canWatch = false;
      tell(t("The camera could not be opened here. Press Scan the barcode again to take a photo of it instead."));
    }
  });

  input.addEventListener("change", async () => {
    const file = input.files?.[0];
    input.value = "";
    if (!file) {
      return;
    }
    tell(t("Reading the barcode…"));
    try {
      await found(await photo(file));
    } catch {
      tell(t("That photo could not be read. Try again, or type the number in."));
    }
  });
}
