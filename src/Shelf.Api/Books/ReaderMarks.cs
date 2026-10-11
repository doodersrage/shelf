using System.Text.Json;

namespace Shelf.Api.Books;

public static class ReaderMarks
{
    public static string Inject(string html, IReadOnlyList<Highlight> marks)
    {
        var payload = JsonSerializer.Serialize(marks.Select(mark => new
        {
            id = mark.Id,
            text = mark.Text,
            prefix = mark.Prefix ?? "",
            suffix = mark.Suffix ?? "",
            note = mark.Note ?? "",
        }));
        payload = payload.Replace("<", "\\u003c", StringComparison.Ordinal);
        var snippet = Style + """<script type="application/json" id="shelf-marks">""" + payload + "</script>" + Script;
        var close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return close >= 0 ? html.Insert(close, snippet) : html + snippet;
    }

    // The reader's own type settings go last in the head, so they win over the book's sizes and spacing.
    public static string Typeset(string html, ReaderType type)
    {
        var css = type.Css();
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            return html.Insert(head, css);
        }

        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        return body >= 0 ? html.Insert(body, css) : css + html;
    }

    private const string Style = """
        <style>
        mark.shelf-mark { background: #f0d78c; color: inherit; }
        mark.shelf-mark.shelf-current { background: #e2b657; }
        </style>
        """;

    private const string Script = """
        <script>
        (function () {
          const data = document.getElementById("shelf-marks");
          const marks = data ? JSON.parse(data.textContent || "[]") : [];
          function textMap(root) {
            const nodes = [];
            const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
              acceptNode(node) {
                const parent = node.parentElement;
                if (!parent) return NodeFilter.FILTER_REJECT;
                const name = parent.tagName;
                if (name === "SCRIPT" || name === "STYLE") return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
              }
            });
            let full = "";
            let node;
            while ((node = walker.nextNode())) {
              if (!node.nodeValue) continue;
              nodes.push({ node: node, start: full.length });
              full += node.nodeValue;
            }
            return { nodes: nodes, full: full };
          }
          function point(nodes, offset) {
            for (let i = 0; i < nodes.length; i++) {
              const item = nodes[i];
              const len = item.node.nodeValue.length;
              if (offset <= item.start + len) return { node: item.node, offset: offset - item.start };
            }
            return null;
          }
          function locate(full, mark) {
            const needle = (mark.prefix || "") + mark.text + (mark.suffix || "");
            const at = needle ? full.indexOf(needle) : -1;
            if (at >= 0) return at + (mark.prefix || "").length;
            return full.indexOf(mark.text);
          }
          function paint() {
            const original = textMap(document.body);
            const placed = [];
            for (const mark of marks) {
              if (!mark.text) continue;
              const at = locate(original.full, mark);
              if (at < 0) continue;
              placed.push({ mark: mark, start: at, end: at + mark.text.length });
            }
            placed.sort((a, b) => b.start - a.start);
            for (const item of placed) {
              const map = textMap(document.body);
              const start = point(map.nodes, item.start);
              const end = point(map.nodes, item.end);
              if (!start || !end) continue;
              const range = document.createRange();
              const el = document.createElement("mark");
              el.className = "shelf-mark";
              el.dataset.id = String(item.mark.id);
              if (item.mark.note) el.title = item.mark.note;
              try {
                range.setStart(start.node, start.offset);
                range.setEnd(end.node, end.offset);
                range.surroundContents(el);
              } catch (error) {
                try {
                  range.setStart(start.node, start.offset);
                  range.setEnd(end.node, end.offset);
                  el.appendChild(range.extractContents());
                  range.insertNode(el);
                } catch (again) {}
              }
            }
            document.querySelectorAll("mark.shelf-mark").forEach((el) => {
              el.addEventListener("click", () => {
                parent.postMessage({ source: "shelf-mark", id: Number(el.dataset.id) }, "*");
              });
            });
          }
          paint();
          // How far into the chapter the reader is, for carrying the place to the audiobook; and the way in from it.
          const paged = !!document.getElementById("shelf-pages");
          const room = () => Math.max(1, document.documentElement.scrollHeight - window.innerHeight);
          const at = Number(new URLSearchParams(location.search).get("at"));
          if (!paged && at > 0 && at <= 1) {
            const go = () => window.scrollTo(0, at * room());
            go();
            window.addEventListener("load", go, { once: true });
          }
          // Pages: the chapter is laid out in columns a window wide, and turning moves a window at a time. Past the
          // last page, or before the first, the page around it opens the next chapter or the one before.
          if (paged) {
            const view = () => document.scrollingElement || document.documentElement;
            const width = () => window.innerWidth;
            const count = () => Math.max(1, Math.round(view().scrollWidth / width()));
            let page = 0;
            const show = (wanted) => {
              const pages = count();
              page = Math.max(0, Math.min(pages - 1, wanted));
              view().scrollLeft = page * width();
              parent.postMessage({ source: "shelf-page", page: page + 1, pages: pages }, "*");
              parent.postMessage({ source: "shelf-place", at: pages > 1 ? page / (pages - 1) : 0 }, "*");
            };
            const turn = (step) => {
              const next = page + step;
              if (next < 0 || next >= count()) {
                parent.postMessage({ source: "shelf-turn", step: step }, "*");
                return;
              }
              show(next);
            };
            const open = () => show(at > 0 ? Math.round(Math.min(1, at) * (count() - 1)) : 0);
            open();
            window.addEventListener("load", open, { once: true });
            window.addEventListener("resize", () => show(page));
            document.addEventListener("keydown", (event) => {
              if (["ArrowRight", "PageDown", " "].includes(event.key)) { event.preventDefault(); turn(1); }
              if (["ArrowLeft", "PageUp"].includes(event.key)) { event.preventDefault(); turn(-1); }
            });
            // A tap on the left or right of the page turns it, unless it is on a link or a highlight, or ends a selection.
            document.addEventListener("click", (event) => {
              const sel = window.getSelection();
              if ((sel && !sel.isCollapsed) || event.target.closest("a, mark")) return;
              if (event.clientX < width() * 0.3) turn(-1);
              else if (event.clientX > width() * 0.7) turn(1);
            });
            let touch = null;
            document.addEventListener("touchstart", (event) => { touch = event.touches[0]; }, { passive: true });
            document.addEventListener("touchend", (event) => {
              const end = event.changedTouches[0];
              if (!touch || !end) return;
              const dx = end.clientX - touch.clientX;
              const dy = end.clientY - touch.clientY;
              touch = null;
              if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy)) turn(dx < 0 ? 1 : -1);
            }, { passive: true });
            let wheeled = 0;
            document.addEventListener("wheel", (event) => {
              event.preventDefault();
              if (Date.now() - wheeled < 450) return;
              wheeled = Date.now();
              turn((event.deltaY || event.deltaX) > 0 ? 1 : -1);
            }, { passive: false });
            window.addEventListener("message", (event) => {
              if (event.source !== parent || !event.data) return;
              if (event.data.source === "shelf-go") turn(event.data.step);
              // The page around may start listening after this page first said where it was; it asks again.
              if (event.data.source === "shelf-ask") show(page);
            });
          }
          // Any touch, key, or wheel inside the chapter tells the page someone is reading.
          let nudged = 0;
          const nudge = () => {
            if (Date.now() - nudged < 5000) return;
            nudged = Date.now();
            parent.postMessage({ source: "shelf-active" }, "*");
          };
          for (const name of ["keydown", "pointerdown", "wheel", "touchstart"]) window.addEventListener(name, nudge, { passive: true });
          let told = 0;
          window.addEventListener("scroll", () => {
            if (paged) return;
            clearTimeout(told);
            told = setTimeout(() => parent.postMessage({ source: "shelf-place", at: Math.min(1, window.scrollY / room()) }, "*"), 300);
          }, { passive: true });
          document.addEventListener("mouseup", () => {
            const sel = window.getSelection();
            if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return;
            const range = sel.getRangeAt(0);
            const map = textMap(document.body);
            function offsetOf(container, offset) {
              if (container.nodeType !== Node.TEXT_NODE) return null;
              const item = map.nodes.find((entry) => entry.node === container);
              return item ? item.start + offset : null;
            }
            let start = offsetOf(range.startContainer, range.startOffset);
            let end = offsetOf(range.endContainer, range.endOffset);
            if (start === null || end === null) return;
            if (end < start) { const swap = start; start = end; end = swap; }
            while (start < end && /\s/.test(map.full[start])) start++;
            while (end > start && /\s/.test(map.full[end - 1])) end--;
            const text = map.full.slice(start, end);
            if (!text) return;
            parent.postMessage({
              source: "shelf-selection",
              text: text,
              prefix: map.full.slice(Math.max(0, start - 48), start),
              suffix: map.full.slice(end, end + 48)
            }, "*");
          });
        })();
        </script>
        """;
}
