/* Lucian UX/UI Prompt #021 · MIT License (c) 2026 Seokwoooo · https://lucianlabs.dev/uxui-prompt/#021 */
const cleanups = new WeakMap();

function applyTheme(dark, persist) {
  const theme = dark ? "dark" : "light";
  document.documentElement.setAttribute("data-theme", theme);
  if (persist) {
    try { localStorage.setItem("examops-theme", theme); } catch {}
  }
  window.dispatchEvent(new CustomEvent("examops-theme-changed", { detail: { dark } }));
}

export function init(root) {
  if (!root) return;
  const sw = root.querySelector(".u021-sw");
  if (!sw) return;

  const sync = () => {
    const isNight = document.documentElement.getAttribute("data-theme") === "dark";
    root.classList.toggle("is-night", isNight);
    sw.setAttribute("aria-checked", String(isNight));
  };

  const onClick = () => {
    const nextNight = !root.classList.contains("is-night");
    applyTheme(nextNight, true);
    sync();
  };

  const onExternal = () => sync();

  sw.addEventListener("click", onClick);
  window.addEventListener("examops-theme-changed", onExternal);
  sync();

  cleanups.set(root, () => {
    sw.removeEventListener("click", onClick);
    window.removeEventListener("examops-theme-changed", onExternal);
  });
}

export function toggleTheme() {
  const isNight = document.documentElement.getAttribute("data-theme") === "dark";
  applyTheme(!isNight, true);
}

export function dispose(root) {
  const fn = cleanups.get(root);
  if (fn) {
    fn();
    cleanups.delete(root);
  }
}
