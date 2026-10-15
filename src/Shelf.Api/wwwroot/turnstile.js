// Cloudflare Turnstile, for a form that has a place for it (TurnstileWidget). Cloudflare's script loads only when a
// page has one, and each is drawn once, in the page's language and light or dark: pages change without a reload, so
// the script's own drawing on load would miss forms reached that way.
(() => {
  const SCRIPT = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";
  let loading = null;

  const load = () =>
    (loading ??= new Promise((done, failed) => {
      const script = document.createElement("script");
      script.src = SCRIPT;
      script.async = true;
      script.onload = () => done(window.turnstile);
      script.onerror = failed;
      document.head.append(script);
    }));

  async function draw() {
    const places = [...document.querySelectorAll(".cf-turnstile:not([data-drawn])")];
    if (places.length === 0) return;
    let turnstile;
    try {
      turnstile = await load();
    } catch {
      // Out of reach: the form still goes, and the shelf says it could not tell.
      return;
    }

    const theme = document.documentElement.dataset.theme || "auto";
    for (const place of places) {
      if (place.dataset.drawn || !place.isConnected) continue;
      place.dataset.drawn = "true";
      turnstile.render(place, {
        sitekey: place.dataset.sitekey,
        action: place.dataset.action,
        theme: theme === "dark" || theme === "light" ? theme : "auto",
        language: document.documentElement.lang || "auto",
      });
    }
  }

  const start = () => {
    draw();
    const whenReady = () => window.Blazor?.addEventListener?.("enhancedload", draw);
    if (window.Blazor) whenReady();
    else window.addEventListener("load", whenReady);
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
