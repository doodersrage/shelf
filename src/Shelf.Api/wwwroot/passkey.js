// Passkeys in the browser: the server's options become a WebAuthn call, and the device's answer goes back as JSON.
// Binary fields travel as base64url both ways.
const t = (text, ...values) => (window.t ? window.t(text, ...values) : text);
export function supported() {
  return Boolean(window.PublicKeyCredential && navigator.credentials?.create);
}

const toBytes = (text) => {
  const base64 = text.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(text.length / 4) * 4, "=");
  return Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
};

const toText = (buffer) => {
  if (!buffer) {
    return null;
  }

  let binary = "";
  for (const byte of new Uint8Array(buffer)) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
};

// The browser rejects null where it expects a value, so empty fields are left out.
const withoutNulls = (value) => {
  if (Array.isArray(value)) {
    return value.map(withoutNulls);
  }

  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null).map(([key, item]) => [key, withoutNulls(item)]));
  }

  return value;
};

async function post(url, body) {
  const response = await fetch(url, {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    const message = data?.errors ? Object.values(data.errors).flat()[0] : null;
    throw new Error(message || t("That did not work. Try again."));
  }

  return data;
}

function describe(error) {
  if (error?.name === "NotAllowedError" || error?.name === "AbortError") {
    return t("The passkey was not used. Try again when you are ready.");
  }

  if (error?.name === "InvalidStateError") {
    return t("This device already has a passkey for this shelf.");
  }

  return error?.message || t("That did not work. Try again.");
}

// Account page: make a passkey on this device. Returns null when it worked, or what went wrong.
export async function register(name) {
  try {
    const options = withoutNulls(await post("/account/passkeys/options"));
    options.challenge = toBytes(options.challenge);
    options.user.id = toBytes(options.user.id);
    options.excludeCredentials = (options.excludeCredentials || []).map((item) => ({ ...item, id: toBytes(item.id) }));
    const credential = await navigator.credentials.create({ publicKey: options });
    await post("/account/passkeys", {
      name,
      credential: {
        id: credential.id,
        rawId: toText(credential.rawId),
        type: credential.type,
        response: {
          attestationObject: toText(credential.response.attestationObject),
          clientDataJSON: toText(credential.response.clientDataJSON),
          transports: credential.response.getTransports?.() || [],
        },
        clientExtensionResults: credential.getClientExtensionResults?.() || {},
      },
    });
    return null;
  } catch (error) {
    return describe(error);
  }
}

// Sign-in page: whichever passkey the device offers for this shelf.
export async function signIn(returnUrl) {
  const options = withoutNulls(await post("/account/passkey/options"));
  options.challenge = toBytes(options.challenge);
  options.allowCredentials = (options.allowCredentials || []).map((item) => ({ ...item, id: toBytes(item.id) }));
  const credential = await navigator.credentials.get({ publicKey: options });
  const result = await post("/account/passkey/signin", {
    returnUrl,
    credential: {
      id: credential.id,
      rawId: toText(credential.rawId),
      type: credential.type,
      response: {
        authenticatorData: toText(credential.response.authenticatorData),
        clientDataJSON: toText(credential.response.clientDataJSON),
        signature: toText(credential.response.signature),
        userHandle: toText(credential.response.userHandle),
      },
      clientExtensionResults: credential.getClientExtensionResults?.() || {},
    },
  });
  return result.redirect || "/";
}

// The sign-in page is not interactive, so its button is wired here, on load and after enhanced navigation.
function wire() {
  const button = document.querySelector("[data-passkey-signin]");
  if (!button || button.dataset.wired) {
    return;
  }

  button.dataset.wired = "true";
  if (!supported()) {
    return;
  }

  button.closest("[data-passkey-panel]")?.removeAttribute("hidden");
  const status = document.querySelector("[data-passkey-status]");
  button.addEventListener("click", async () => {
    button.disabled = true;
    status.hidden = true;
    try {
      window.location.assign(await signIn(button.dataset.returnUrl || "/"));
    } catch (error) {
      status.textContent = describe(error);
      status.hidden = false;
      button.disabled = false;
    }
  });
}

wire();
document.addEventListener("DOMContentLoaded", wire);
window.Blazor?.addEventListener?.("enhancedload", wire);
