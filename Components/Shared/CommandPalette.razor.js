/* Lucian UX/UI Prompt #046 · MIT License (c) 2026 Seokwoooo · https://lucianlabs.dev/uxui-prompt/#046 */
import { toggleTheme } from "./ThemeSkySwitch.razor.js";

const instances = new WeakMap();

const ICONS = {
  bolt: '<path d="M13 2L4 14h7l-1 8 9-12h-7l1-8z"/>',
  grid: '<rect x="3.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.5"/>',
  doc: '<path d="M7 3.5h7l4 4V20a.5.5 0 0 1-.5.5h-10A.5.5 0 0 1 7 20z"/><path d="M14 3.5v4h4"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  moon: '<path d="M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z"/>',
  user: '<circle cx="10" cy="8.5" r="3.5"/><path d="M3.5 19.5c.8-3.4 3.4-5 6.5-5 1.6 0 3 .4 4.2 1.3"/>',
  check: '<path d="M4.5 12.5l4.5 4.5 10.5-10.5"/>'
};

function esc(s) {
  return String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

export function init(root, dotNetRef, initialItems) {
  if (!root || instances.has(root)) return;
  const input = root.querySelector(".u046-in");
  const list = root.querySelector(".u046-list");
  const hl = root.querySelector(".u046-hl");
  const key = root.querySelector(".u046-key");
  const done = root.querySelector(".u046-done");
  const field = root.querySelector(".u046-field");
  const body = root.querySelector(".u046-body");
  const scrim = root.querySelector(".u046-scrim");
  const uid = "u046-" + Math.random().toString(36).slice(2, 7);
  const mac = /Mac|iPhone|iPad/.test(navigator.platform || "");

  let data = Array.isArray(initialItems) ? initialItems : [];
  let opts = [];
  let act = 0;
  let closed = true;
  let doneT = 0;

  function hit(name, q) {
    if (!q) return esc(name);
    const i = name.toLowerCase().indexOf(q);
    if (i < 0) return esc(name);
    return esc(name.slice(0, i)) + "<mark>" + esc(name.slice(i, i + q.length)) + "</mark>" + esc(name.slice(i + q.length));
  }

  function render() {
    const q = input.value.trim().toLowerCase();
    const found = data.filter((d) => !q || (d.name && d.name.toLowerCase().includes(q)) || (d.group && d.group.toLowerCase().includes(q)));
    let html = "";
    let g = "";
    let n = 0;
    found.forEach((d) => {
      if (d.group !== g) {
        g = d.group;
        html += '<li class="u046-g" role="presentation">' + esc(g) + "</li>";
      }
      const ic = ICONS[d.icon] || ICONS.grid;
      html +=
        '<li class="u046-op" role="option" id="' + uid + "-" + n + '" data-href="' + esc(d.href) + '" data-n="' + esc(d.name) + '" aria-selected="false">' +
        '<span class="u046-ic" aria-hidden="true"><svg viewBox="0 0 24 24">' + ic + "</svg></span>" +
        "<span>" + hit(d.name, q) + "</span>" +
        (d.shortcut ? '<kbd class="u046-kbd">' + esc(d.shortcut) + "</kbd>" : "") +
        "</li>";
      n++;
    });
    if (!n) {
      html = '<li class="u046-empty" role="presentation"><b>未找到“' + esc(input.value.trim()) + "”</b>换个关键词试试</li>";
    }
    list.innerHTML = html;
    opts = Array.from(list.querySelectorAll(".u046-op"));
    opts.forEach((o, i) => {
      o.addEventListener("pointermove", () => { if (act !== i) mark(i); });
      o.addEventListener("click", () => { mark(i); run(); });
    });
    hl.style.opacity = n ? "1" : "0";
    mark(0, true);
  }

  function mark(i, instant) {
    if (!opts.length) { input.removeAttribute("aria-activedescendant"); return; }
    act = (i + opts.length) % opts.length;
    opts.forEach((o, k) => o.setAttribute("aria-selected", String(k === act)));
    input.setAttribute("aria-activedescendant", opts[act].id);
    if (instant) hl.style.transition = "none";
    const o = opts[act];
    const top = o.offsetTop + 6;
    hl.style.setProperty("--y", top + "px");
    if (instant) { void hl.offsetWidth; hl.style.transition = ""; }
    if (top + o.offsetHeight > body.scrollTop + body.clientHeight) {
      body.scrollTo({ top: top + o.offsetHeight - body.clientHeight + 6, behavior: instant ? "auto" : "smooth" });
    } else if (top < body.scrollTop + 30) {
      body.scrollTo({ top: act === 0 ? 0 : top - 30, behavior: instant ? "auto" : "smooth" });
    }
  }

  function setClosed(v) {
    closed = v;
    root.classList.toggle("is-closed", v);
    input.setAttribute("aria-expanded", String(!v));
    key.textContent = v ? (mac ? "⌘K" : "Ctrl K") : "ESC";
    if (v) {
      input.value = "";
      input.blur();
      done.classList.remove("show");
    } else {
      done.classList.remove("show");
      render();
      window.requestAnimationFrame(() => input.focus());
    }
  }

  function run() {
    if (!opts[act]) return;
    const href = opts[act].getAttribute("data-href");
    const name = opts[act].getAttribute("data-n");
    done.textContent = "已执行：" + name;
    done.classList.add("show");
    window.clearTimeout(doneT);
    if (href === "__toggle_theme__") {
      toggleTheme();
      doneT = window.setTimeout(() => setClosed(true), 220);
      return;
    }
    setClosed(true);
    if (dotNetRef && href) {
      dotNetRef.invokeMethodAsync("ExecuteCommand", href).catch(() => {});
    }
  }

  const onInput = () => { if (closed) setClosed(false); render(); };
  const onInputKey = (e) => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      if (closed) setClosed(false);
      else mark(act + (e.key === "ArrowDown" ? 1 : -1));
    } else if (e.key === "Enter" && !e.isComposing && e.keyCode !== 229) {
      e.preventDefault();
      if (closed) setClosed(false);
      else run();
    } else if (e.key === "Escape" && !closed) {
      e.preventDefault();
      e.stopPropagation();
      setClosed(true);
    }
  };
  const onFieldDown = () => { if (closed) { window.clearTimeout(doneT); setClosed(false); } };
  const onScrimClick = () => { if (!closed) setClosed(true); };
  const onDocKey = (e) => {
    if (!root.isConnected) return;
    if ((e.metaKey || e.ctrlKey) && (e.key === "k" || e.key === "K")) {
      e.preventDefault();
      setClosed(!closed);
    } else if (e.key === "Escape" && !closed) {
      e.preventDefault();
      setClosed(true);
    }
  };

  input.addEventListener("input", onInput);
  input.addEventListener("keydown", onInputKey);
  field.addEventListener("pointerdown", onFieldDown);
  scrim.addEventListener("click", onScrimClick);
  document.addEventListener("keydown", onDocKey);
  key.textContent = mac ? "⌘K" : "Ctrl K";

  instances.set(root, {
    updateItems: (items) => { data = Array.isArray(items) ? items : []; if (!closed) render(); },
    dispose: () => {
      window.clearTimeout(doneT);
      input.removeEventListener("input", onInput);
      input.removeEventListener("keydown", onInputKey);
      field.removeEventListener("pointerdown", onFieldDown);
      scrim.removeEventListener("click", onScrimClick);
      document.removeEventListener("keydown", onDocKey);
    }
  });
}

export function updateItems(root, items) {
  const inst = instances.get(root);
  if (inst) inst.updateItems(items);
}

export function dispose(root) {
  const inst = instances.get(root);
  if (inst) {
    inst.dispose();
    instances.delete(root);
  }
}
