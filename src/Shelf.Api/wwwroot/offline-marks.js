// Highlights in the offline reader. They are kept on this device beside the book, drawn onto the page the same way
// the shelf's reader draws them, and sent to the shelf when it answers again. A highlight removed offline is
// removed from the shelf then too.
const key = (bookId) => `shelf-marks-${bookId}`;
let localNumber = Date.now();

export function load(bookId) {
  try {
    const saved = JSON.parse(localStorage.getItem(key(bookId)) || "[]");
    return Array.isArray(saved) ? saved : [];
  } catch {
    return [];
  }
}

function save(bookId, marks) {
  try {
    localStorage.setItem(key(bookId), JSON.stringify(marks));
  } catch {
    // Storage full or refused: the highlight still shows until the page closes.
  }
}

export function forget(bookId) {
  try {
    localStorage.removeItem(key(bookId));
  } catch {
  }
}

const fromShelf = (mark) => ({
  id: mark.id,
  chapter: mark.chapterIndex,
  text: mark.text,
  note: mark.note || "",
  prefix: mark.prefix || "",
  suffix: mark.suffix || "",
});

// When a book is kept, and whenever the shelf answers, the shelf's highlights are copied down.
export async function download(bookId) {
  const response = await fetch(`/books/${bookId}/highlights`, { credentials: "same-origin" });
  if (!response.ok) {
    return false;
  }

  const fromServer = (await response.json()).map(fromShelf);
  const waiting = load(bookId).filter((mark) => mark.pending || mark.deleted);
  const removed = new Set(waiting.filter((mark) => mark.deleted).map((mark) => mark.id));
  save(bookId, [...fromServer.filter((mark) => !removed.has(mark.id)), ...waiting]);
  return true;
}

export function add(bookId, mark) {
  const marks = load(bookId);
  const added = { ...mark, id: `here-${localNumber++}`, pending: true };
  save(bookId, [...marks, added]);
  return added;
}

export function remove(bookId, id) {
  const marks = load(bookId);
  const mark = marks.find((item) => item.id === id);
  if (!mark) {
    return;
  }

  // One never sent is simply dropped; one the shelf has is sent as a removal later.
  save(bookId, mark.pending ? marks.filter((item) => item.id !== id) : marks.map((item) => (item.id === id ? { ...item, deleted: true } : item)));
}

export function waiting(bookId) {
  return load(bookId).some((mark) => mark.pending || mark.deleted);
}

// Highlights made or removed offline go to the shelf, then the shelf's own list comes back down.
export async function send(bookId) {
  for (const mark of load(bookId)) {
    if (mark.pending && !mark.deleted) {
      const response = await fetch(`/books/${bookId}/highlights`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text: mark.text, chapterIndex: mark.chapter, note: mark.note || null, prefix: mark.prefix || null, suffix: mark.suffix || null }),
      });
      if (response.ok) {
        const saved = fromShelf(await response.json());
        save(bookId, load(bookId).map((item) => (item.id === mark.id ? saved : item)));
      } else if (response.status === 404) {
        // The book is no longer this reader's to mark, a loan that ended, say.
        save(bookId, load(bookId).filter((item) => item.id !== mark.id));
      } else if (response.status >= 500) {
        return;
      }
    } else if (mark.deleted) {
      if (!mark.pending) {
        const response = await fetch(`/books/${bookId}/highlights/${mark.id}`, { method: "DELETE", credentials: "same-origin" });
        if (!response.ok && response.status !== 404) {
          continue;
        }
      }

      save(bookId, load(bookId).filter((item) => item.id !== mark.id));
    }
  }

  await download(bookId);
}

// The text of a page as one string, with where each text node starts in it.
function textMap(container) {
  const nodes = [];
  let length = 0;
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (node.parentElement?.closest(".endOfContent")) {
      continue;
    }

    nodes.push({ node, start: length });
    length += node.data.length;
  }

  return { nodes, text: nodes.map((item) => item.node.data).join("") };
}

// The words around a passage tell two copies of the same words apart; the passage alone is the fallback.
export function paint(container, marks) {
  for (const old of container.querySelectorAll("mark.shelf-mark")) {
    old.replaceWith(...old.childNodes);
  }

  container.normalize();
  for (const mark of marks.filter((item) => !item.deleted)) {
    const { nodes, text } = textMap(container);
    let at = text.indexOf(mark.prefix + mark.text + mark.suffix);
    at = at >= 0 ? at + mark.prefix.length : text.indexOf(mark.text);
    if (at < 0) {
      continue;
    }

    const pieces = nodes
      .map(({ node, start }) => ({ node, from: Math.max(at, start) - start, to: Math.min(at + mark.text.length, start + node.data.length) - start }))
      .filter((piece) => piece.from < piece.to);
    for (const piece of pieces.reverse()) {
      let target = piece.node;
      if (piece.to < target.data.length) {
        target.splitText(piece.to);
      }

      if (piece.from > 0) {
        target = target.splitText(piece.from);
      }

      const element = document.createElement("mark");
      element.className = "shelf-mark";
      if (mark.note) {
        element.title = mark.note;
      }

      target.replaceWith(element);
      element.append(target);
    }
  }
}

// What is selected inside the page, with the words just before and after it.
export function selected(container) {
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed || selection.rangeCount === 0) {
    return null;
  }

  const range = selection.getRangeAt(0);
  if (!container.contains(range.commonAncestorContainer)) {
    return null;
  }

  const { nodes, text } = textMap(container);
  let from = -1;
  let to = -1;
  for (const { node, start } of nodes) {
    if (!range.intersectsNode(node)) {
      continue;
    }

    if (from < 0) {
      from = start + (node === range.startContainer ? range.startOffset : 0);
    }

    to = start + (node === range.endContainer ? range.endOffset : node.data.length);
  }

  const passage = from >= 0 ? text.slice(from, to).trim() : "";
  if (!passage || passage.length > 1000) {
    return null;
  }

  const begin = from + Math.max(0, text.slice(from).indexOf(passage));
  return {
    text: passage,
    prefix: text.slice(Math.max(0, begin - 32), begin),
    suffix: text.slice(begin + passage.length, begin + passage.length + 32),
  };
}
