/* Lucian UX/UI Prompt #097 · MIT License (c) 2026 Seokwoooo · https://lucianlabs.dev/uxui-prompt/#097 */
const instances = new WeakMap();

export function dispose(root) {
  const inst = instances.get(root);
  if (inst) {
    inst.dispose();
    instances.delete(root);
  }
}

export function mount(root, slices, unit) {
  dispose(root);
  if (!root) return;
  const svg = root.querySelector("svg");
  const center = root.querySelector(".u097-c");
  const cs = center.querySelector("small");
  const cb = center.querySelector("b");
  const cp = center.querySelector("span");
  const lg = root.querySelector(".u097-lg");
  const rm = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  svg.querySelectorAll(".u097-sg").forEach((el) => el.remove());
  lg.innerHTML = "";

  const D = (Array.isArray(slices) ? slices : []).filter((s) => s && s.value >= 0);
  const R = 80;
  const C = 2 * Math.PI * R;
  const GAP = 3;
  const on = D.map(() => true);
  let fr = D.map(() => 0);
  let act = -1;
  let tw = 0;
  let shownTotal = 0;
  let cnt = 0;

  const total = () => D.reduce((s, d, i) => s + (on[i] ? d.value : 0), 0);

  const segs = D.map((d, i) => {
    const c = document.createElementNS("http://www.w3.org/2000/svg", "circle");
    c.setAttribute("class", "u097-sg");
    c.setAttribute("cx", "108");
    c.setAttribute("cy", "108");
    c.setAttribute("r", String(R));
    c.style.stroke = d.colorVar || "var(--ux-accent)";
    c.addEventListener("pointerenter", () => focus(i));
    c.addEventListener("click", () => toggle(i));
    svg.appendChild(c);
    return c;
  });

  const items = D.map((d, i) => {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "u097-it";
    b.style.setProperty("--c", d.colorVar || "var(--ux-accent)");
    b.setAttribute("aria-pressed", "true");
    b.innerHTML = '<i aria-hidden="true"></i><span></span><small></small>';
    b.querySelector("span").textContent = d.label;
    b.querySelector("small").textContent = String(d.value);
    b.addEventListener("pointerenter", () => focus(i));
    b.addEventListener("focus", () => focus(i));
    b.addEventListener("click", () => toggle(i));
    lg.appendChild(b);
    return b;
  });

  const onSvgLeave = () => focus(-1);
  const onLgLeave = () => { if (!lg.contains(document.activeElement)) focus(-1); };
  const onLgFocusOut = (e) => { if (!lg.contains(e.relatedTarget)) focus(-1); };
  svg.addEventListener("pointerleave", onSvgLeave);
  lg.addEventListener("pointerleave", onLgLeave);
  lg.addEventListener("focusout", onLgFocusOut);

  function paint() {
    let acc = 0;
    segs.forEach((c, i) => {
      const len = fr[i] * C;
      const vis = Math.max(0, len - GAP);
      c.setAttribute("stroke-dasharray", vis.toFixed(2) + " " + C.toFixed(2));
      c.setAttribute("stroke-dashoffset", (-acc - GAP / 2).toFixed(2));
      const mid = ((acc + len / 2) / C) * Math.PI * 2;
      c.style.transform = i === act && on[i]
        ? "translate(" + (Math.cos(mid) * 6).toFixed(2) + "px," + (Math.sin(mid) * 6).toFixed(2) + "px)"
        : "";
      acc += len;
    });
  }

  function morph(stagger) {
    window.cancelAnimationFrame(tw);
    const t = total();
    const to = D.map((d, i) => (on[i] && t > 0 ? d.value / t : 0));
    const from = fr.slice();
    const t0 = performance.now();
    const dur = rm ? 1 : stagger ? 950 : 520;

    (function f(n) {
      if (!root.isConnected) return;
      const k = Math.min(1, (n - t0) / dur);
      fr = from.map((v, i) => {
        const kk = stagger ? Math.max(0, Math.min(1, (k - i * 0.1) / 0.6)) : k;
        const e = kk < 0.5 ? 4 * kk * kk * kk : 1 - Math.pow(-2 * kk + 2, 3) / 2;
        return v + (to[i] - v) * e;
      });
      paint();
      if (k < 1) tw = window.requestAnimationFrame(f);
    })(t0);
  }

  function rollTotal(to) {
    const from = shownTotal;
    const t0 = performance.now();
    const dur = rm ? 1 : 500;
    const my = ++cnt;
    (function f(n) {
      if (!root.isConnected || my !== cnt || act >= 0) return;
      const k = Math.min(1, (n - t0) / dur);
      const e = 1 - Math.pow(1 - k, 3);
      shownTotal = from + (to - from) * e;
      cb.textContent = String(Math.round(shownTotal));
      if (k < 1) window.requestAnimationFrame(f);
    })(t0);
  }

  function label() {
    center.classList.remove("sw");
    void center.offsetWidth;
    center.classList.add("sw");
    const t = total();
    if (act >= 0 && on[act]) {
      cs.textContent = D[act].label;
      cb.textContent = String(D[act].value);
      cp.textContent = (t > 0 ? Math.round((D[act].value / t) * 100) : 0) + "% 占比";
    } else {
      const n = on.filter(Boolean).length;
      cs.textContent = n === D.length ? "总计" : "已选 " + n + " 项";
      cb.textContent = String(Math.round(shownTotal));
      cp.textContent = unit || "项";
      rollTotal(t);
    }
  }

  function focus(i) {
    if (i === act) return;
    act = i;
    root.classList.toggle("has", i >= 0 && on[i]);
    segs.forEach((c, k) => c.classList.toggle("on", k === i && on[k]));
    items.forEach((b, k) => b.classList.toggle("on", k === i));
    paint();
    label();
  }

  function toggle(i) {
    if (on[i] && on.filter(Boolean).length === 1) return;
    on[i] = !on[i];
    items[i].setAttribute("aria-pressed", String(on[i]));
    morph(false);
    act = -2;
    focus(on[i] ? i : -1);
  }

  shownTotal = 0;
  label();
  morph(!rm);

  instances.set(root, {
    dispose: () => {
      window.cancelAnimationFrame(tw);
      svg.removeEventListener("pointerleave", onSvgLeave);
      lg.removeEventListener("pointerleave", onLgLeave);
      lg.removeEventListener("focusout", onLgFocusOut);
    }
  });
}
