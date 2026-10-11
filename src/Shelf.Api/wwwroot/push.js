// Notifications on this device by the Push API: asks the browser's permission, subscribes with the shelf's key,
// and hands the subscription to the page to keep.

function bytes(base64) {
  const padded = (base64 + "===".slice((base64.length + 3) % 4)).replace(/-/g, "+").replace(/_/g, "/");
  return Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
}

export function supported() {
  return "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
}

// "granted", "denied", or "default"; "unsupported" when the browser has no notifications, as on an iPhone outside
// the home screen.
export function permission() {
  return supported() ? Notification.permission : "unsupported";
}

async function registration() {
  return (await navigator.serviceWorker.getRegistration()) || navigator.serviceWorker.register("/sw.js");
}

// This browser's subscription's address, when it has one.
export async function current() {
  if (!supported()) {
    return null;
  }

  const subscription = await (await registration()).pushManager.getSubscription();
  return subscription ? subscription.endpoint : null;
}

// Subscribes, after asking permission; returns the subscription with its keys, or null when refused.
export async function subscribe(key) {
  if (!supported() || (await Notification.requestPermission()) !== "granted") {
    return null;
  }

  const ready = await navigator.serviceWorker.ready.catch(() => null) || await registration();
  let subscription = await ready.pushManager.getSubscription();
  const wanted = bytes(key);
  const same = subscription?.options?.applicationServerKey
    && new Uint8Array(subscription.options.applicationServerKey).every((b, i) => b === wanted[i]);
  if (subscription && !same) {
    await subscription.unsubscribe();
    subscription = null;
  }

  subscription ??= await ready.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: wanted });
  const json = subscription.toJSON();
  return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth, label: label() };
}

export async function unsubscribe() {
  const subscription = supported() ? await (await registration()).pushManager.getSubscription() : null;
  if (!subscription) {
    return null;
  }

  const endpoint = subscription.endpoint;
  await subscription.unsubscribe();
  return endpoint;
}

// A name for the device in the list: its browser and system, from what it says of itself.
function label() {
  const agent = navigator.userAgent;
  const browser = /Edg\//.test(agent) ? "Edge" : /Firefox\//.test(agent) ? "Firefox" : /Chrome\//.test(agent) ? "Chrome" : /Safari\//.test(agent) ? "Safari" : "Browser";
  const system = /Android/.test(agent) ? "Android" : /iPhone|iPad/.test(agent) ? "iOS" : /Mac OS X/.test(agent) ? "macOS" : /Windows/.test(agent) ? "Windows" : /Linux/.test(agent) ? "Linux" : "";
  return system ? `${browser} on ${system}` : browser;
}
