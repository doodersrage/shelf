// Starts Blazor, and watches the live connection that interactive pages use. When it cannot start (an extension or
// a privacy setting blocking WebSockets, say), Blazor only writes to the console and the page looks fine while its
// buttons do nothing; this shows a banner instead. Pages without interactive parts never open one, and are left be.
(() => {
  // The state is kept on the page as data-live, for tests and for anyone looking in the browser's tools.
  const state = (value) => {
    document.documentElement.dataset.live = value;
  };

  const unavailable = () => {
    state("failed");
    const banner = document.getElementById("live-unavailable");
    if (banner) {
      banner.hidden = false;
    }
  };

  // Interactive parts leave a marker comment in the page, <!--Blazor:{"type":"server",...}-->.
  const interactive = () => {
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_COMMENT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      if (node.data.startsWith("Blazor:") && node.data.includes('"type":"server"')) {
        return true;
      }
    }
    return false;
  };

  let waiting = 0;
  state("page");
  // Looked for now, since Blazor takes the markers out as it sets the page up. However Blazor then fails, a page
  // with interactive parts that has not connected in this time will not.
  if (interactive()) {
    setTimeout(() => {
      if (document.documentElement.dataset.live !== "connected") {
        unavailable();
      }
    }, 15000);
  }
  window.Blazor.start({
    circuit: {
      configureSignalR: (builder) => {
        const build = builder.build.bind(builder);
        builder.build = () => {
          const connection = build();
          const start = connection.start.bind(connection);
          connection.start = (...args) => {
            state("starting");
            clearTimeout(waiting);
            // Long polling, the fallback, can take a while; past this it is not coming.
            waiting = setTimeout(unavailable, 15000);
            return start(...args).then(
              (result) => {
                clearTimeout(waiting);
                state("connected");
                return result;
              },
              (error) => {
                clearTimeout(waiting);
                unavailable();
                throw error;
              });
          };
          return connection;
        };
      },
    },
  }).catch(unavailable); // Blazor could not even begin: its own start-up files were blocked too.
})();
