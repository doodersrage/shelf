// Starts Blazor, and watches the live connection that interactive pages use. When it cannot start (an extension or
// a privacy setting blocking WebSockets, say), Blazor only writes to the console and the page looks fine while its
// buttons do nothing; this shows a banner instead. Pages without interactive parts never open one, and are left be.
//
// Only a connection that has never come up is that. One that was up and dropped (a laptop asleep, a phone changing
// networks, a tab long in the background) is Blazor's to reconnect, with its own bar, and says nothing here; a
// connection that comes up late takes the banner away; and time the page spends hidden, when browsers hold its
// timers back, does not count.
(() => {
  // The state is kept on the page as data-live, for tests and for anyone looking in the browser's tools.
  const state = (value) => {
    document.documentElement.dataset.live = value;
  };

  let connected = false;
  const banner = (shown) => {
    const element = document.getElementById("live-unavailable");
    if (element) {
      element.hidden = !shown;
    }
  };

  const unavailable = () => {
    if (connected) {
      state("reconnecting");
      return;
    }

    state("failed");
    banner(true);
  };

  // Fifteen seconds of the page being looked at: hidden, the wait starts again once it is seen.
  const after = (run) => {
    let timer = 0;
    const visible = () => {
      document.removeEventListener("visibilitychange", visible);
      if (!document.hidden) {
        timer = setTimeout(check, 15000);
      } else {
        document.addEventListener("visibilitychange", visible);
      }
    };
    const check = () => {
      if (document.hidden) {
        document.addEventListener("visibilitychange", visible);
      } else {
        run();
      }
    };
    visible();
    return () => {
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", visible);
    };
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

  let stopWaiting = () => {};
  state("page");
  // Looked for now, since Blazor takes the markers out as it sets the page up. However Blazor then fails, a page
  // with interactive parts that has not connected in this time will not.
  if (interactive()) {
    after(() => {
      if (!connected) {
        unavailable();
      }
    });
  }
  window.Blazor.start({
    circuit: {
      configureSignalR: (builder) => {
        const build = builder.build.bind(builder);
        builder.build = () => {
          const connection = build();
          const start = connection.start.bind(connection);
          connection.start = (...args) => {
            state(connected ? "reconnecting" : "starting");
            stopWaiting();
            // Long polling, the fallback, can take a while; past this it is not coming.
            stopWaiting = after(unavailable);
            return start(...args).then(
              (result) => {
                stopWaiting();
                connected = true;
                state("connected");
                banner(false);
                return result;
              },
              (error) => {
                stopWaiting();
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
