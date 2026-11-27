/* Lucian UX/UI Prompt #070 · MIT License (c) 2026 Seokwoooo · https://lucianlabs.dev/uxui-prompt/#070 */
const instances = new WeakMap();

const ICON = {
  ok: '<svg viewBox="0 0 18 18"><path d="M4 9.4l3.2 3.2L14 5.6"/></svg>',
  err: '<svg viewBox="0 0 18 18"><path d="M9 5v5M9 13h.01"/></svg>',
  info: '<svg viewBox="0 0 18 18"><path d="M3.5 4.5h11v7h-6l-3 2.5v-2.5h-2z"/></svg>',
  wait: "<i></i>"
};

const COLOR = {
  ok: "var(--ux-mint)",
  err: "var(--ux-danger)",
  info: "var(--ux-accent)",
  wait: "var(--ux-warn)"
};

export function init(root) {
  if (!root || instances.has(root)) return;
  const zone = root.querySelector(".u070-zone");
  const live = root.querySelector(".u070-live");
  if (!zone || !live) return;

  const list = [];
  let open = false;
  let rafId = 0;
  let last = performance.now();
  const DUR = 4500;

  function layout() {
    let acc = 0;
    list.forEach((t, i) => {
      const el = t.el;
      el.classList.toggle("back", !open && i > 0);
      if (open) {
        el.style.setProperty("--y", -acc + "px");
        el.style.setProperty("--s", "1");
        el.style.setProperty("--o", "1");
        acc += el.offsetHeight + 8;
      } else {
        el.style.setProperty("--y", -(i * 12) + "px");
        el.style.setProperty("--s", (1 - i * 0.05).toFixed(2));
        el.style.setProperty("--o", i > 2 ? "0" : "1");
      }
      el.style.zIndex = String(50 - i);
      el.inert = !open && i > 0;
    });
  }

  function remove(t, dir) {
    const i = list.indexOf(t);
    if (i < 0) return;
    list.splice(i, 1);
    t.el.classList.add("leave");
    if (dir) {
      t.el.style.setProperty("--dir", String(dir));
      t.el.classList.add("side");
    }
    window.setTimeout(() => t.el.remove(), 420);
    layout();
  }

  function addToast(kind, title, desc) {
    const k = ICON[kind] ? kind : "info";
    const el = document.createElement("div");
    el.className = "u070-t enter";
    el.style.setProperty("--c", COLOR[k]);
    el.setAttribute("role", k === "err" ? "alert" : "status");
    el.innerHTML =
      '<span class="u070-ic" aria-hidden="true">' + ICON[k] + "</span>" +
      '<span class="u070-tx"><b></b><small></small></span>' +
      '<button class="u070-x" type="button" aria-label="关闭通知"><svg viewBox="0 0 14 14"><path d="M3.5 3.5l7 7M10.5 3.5l-7 7"/></svg></button>' +
      '<span class="u070-bar" aria-hidden="true"></span>';
    el.querySelector("b").textContent = title || "";
    const small = el.querySelector("small");
    if (desc) small.textContent = desc;
    else small.remove();

    const t = { el, left: k === "wait" ? Infinity : DUR };
    zone.appendChild(el);
    list.unshift(t);
    void el.offsetWidth;
    el.classList.remove("enter");
    layout();
    live.textContent = title || "";

    el.querySelector(".u070-x").addEventListener("click", () => remove(t));

    let sx = 0, dx = 0, down = false;
    el.addEventListener("pointerdown", (e) => {
      if (e.target.closest("button")) return;
      down = true; sx = e.clientX; dx = 0;
      el.setPointerCapture(e.pointerId);
      el.classList.add("drag");
    });
    el.addEventListener("pointermove", (e) => {
      if (!down) return;
      dx = e.clientX - sx;
      el.style.setProperty("--dx", dx + "px");
      el.style.setProperty("--o", String(Math.max(0.2, 1 - Math.abs(dx) / 260)));
    });
    const end = () => {
      if (!down) return;
      down = false;
      el.classList.remove("drag");
      if (Math.abs(dx) > 90) remove(t, dx > 0 ? 1 : -1);
      else { el.style.setProperty("--dx", "0px"); layout(); }
    };
    el.addEventListener("pointerup", end);
    el.addEventListener("pointercancel", end);
  }

  function loop(now) {
    if (!root.isConnected) return;
    const dt = now - last;
    last = now;
    if (!open && list.length > 0) {
      const top = list[0];
      if (top && top.left !== Infinity) {
        top.left -= dt;
        top.el.style.setProperty("--left", Math.max(0, top.left / DUR).toFixed(3));
        if (top.left <= 0) remove(top);
      }
    }
    rafId = window.requestAnimationFrame(loop);
  }
  rafId = window.requestAnimationFrame(loop);

  const onEnter = (e) => { if (e.pointerType === "mouse") { open = true; layout(); } };
  const onLeave = (e) => { if (e.pointerType === "mouse" && !zone.contains(document.activeElement)) { open = false; layout(); } };
  const onGlobalToast = (e) => {
    const d = e.detail || {};
    addToast(d.kind || "info", d.title || "", d.desc || "");
  };
  zone.addEventListener("pointerenter", onEnter);
  zone.addEventListener("pointerleave", onLeave);
  window.addEventListener("examops-toast", onGlobalToast);

  instances.set(root, {
    push: addToast,
    dispose: () => {
      window.cancelAnimationFrame(rafId);
      zone.removeEventListener("pointerenter", onEnter);
      zone.removeEventListener("pointerleave", onLeave);
      window.removeEventListener("examops-toast", onGlobalToast);
    }
  });
}

export function emit(kind, title, desc) {
  window.dispatchEvent(new CustomEvent("examops-toast", { detail: { kind, title, desc } }));
}

export function push(root, kind, title, desc) {
  const inst = instances.get(root);
  if (inst) inst.push(kind, title, desc);
}

export function dispose(root) {
  const inst = instances.get(root);
  if (inst) {
    inst.dispose();
    instances.delete(root);
  }
}
