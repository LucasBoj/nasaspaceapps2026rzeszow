// Arrow-key navigation for a grid of [data-card] buttons (roving tabindex).
// Left/Right move by one card (wrapping), Up/Down move by one row.

const handlers = new WeakMap();

function columnCount(grid) {
  const cols = getComputedStyle(grid).gridTemplateColumns.split(" ").filter(Boolean);
  return Math.max(1, cols.length);
}

export function attach(grid) {
  if (!grid || handlers.has(grid)) return;

  const onKeyDown = (e) => {
    if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(e.key)) return;
    const cards = [...grid.querySelectorAll("[data-card]")];
    const i = cards.indexOf(document.activeElement);
    if (i < 0) return;

    e.preventDefault(); // keep the panel from scrolling
    const n = cards.length, cols = columnCount(grid);
    let next = i;
    switch (e.key) {
      case "ArrowRight": next = (i + 1) % n; break;
      case "ArrowLeft": next = (i - 1 + n) % n; break;
      case "ArrowDown": next = Math.min(n - 1, i + cols); break;
      case "ArrowUp": next = Math.max(0, i - cols); break;
      case "Home": next = 0; break;
      case "End": next = n - 1; break;
    }
    cards.forEach((c, k) => c.setAttribute("tabindex", k === next ? "0" : "-1"));
    cards[next].focus();
  };

  grid.addEventListener("keydown", onKeyDown);
  handlers.set(grid, onKeyDown);
}

export function detach(grid) {
  const h = grid && handlers.get(grid);
  if (h) {
    grid.removeEventListener("keydown", h);
    handlers.delete(grid);
  }
}
