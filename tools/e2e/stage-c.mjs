// Package C (ANALYST readability): every visible text ≥ 12 px, Sans headings (no mono caps), no repeated verdict
// line, one COMPARE (tab only), robustness in two groups with the ellipse map beside its text, battery axis
// 0/50/100 % and "Day N" on the timeline, heatmap labels without overlap.
import { chromium, firefox, webkit } from "playwright";
const BR = process.env.BR || "chromium";
const eng = BR === "firefox" ? firefox : BR === "webkit" ? webkit : chromium;
let pass = 0, fail = 0; const errors = [];
const expect = (n, c, x = "") => { c ? pass++ : fail++; console.log(`${c ? "PASS" : "FAIL"}  ${n}${x !== "" ? "  — " + x : ""}`); };
for (const [W, H] of [[1366, 768], [1920, 1080]]) {
  const tag = `${W}x${H}`;
  const b = await eng.launch({ headless: true });
  const ctx = await b.newContext({ viewport: { width: W, height: H } });
  await ctx.addInitScript(() => { localStorage.setItem("selene.bootSeen.v1", "1"); localStorage.setItem("selene.welcomeDismissed.v1", "1"); localStorage.setItem("selene.analystMode.v1", "1"); });
  const p = await ctx.newPage();
  p.on("pageerror", (e) => errors.push(e.message)); p.on("console", (m) => { if (m.type() === "error" && !/GL Driver|GPU stall|WebGL/.test(m.text())) errors.push(m.text().slice(0, 160)); });
  await p.goto(process.env.URL || "http://localhost:5298/"); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForTimeout(5000);
  await p.locator(".run-main").click(); await p.waitForSelector(".verdict-card", { timeout: 60000 });
  await p.waitForSelector("button.best-date", { timeout: 90000 }); await p.waitForTimeout(1500);

  const small = await p.evaluate(() => {
    const out = [];
    for (const el of document.querySelectorAll("body *")) {
      if (!el.childNodes.length || ![...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim())) continue;
      const cs = getComputedStyle(el); const r = el.getBoundingClientRect();
      if (cs.display === "none" || cs.visibility === "hidden" || !r.width || !r.height) continue;
      if (el.closest("svg")) continue;   // SVG text scales with its view box (checked separately)
      if (parseFloat(cs.fontSize) < 11.99) out.push(`${el.className || el.tagName}:${parseFloat(cs.fontSize)}px "${el.textContent.trim().slice(0, 20)}"`);
    }
    return out;
  });
  expect(`${tag} every visible text ≥ 12 px`, small.length === 0, small.slice(0, 6).join(" · "));
  const heads = await p.evaluate(() => [...document.querySelectorAll(".results-panel h3, .metric-label, .robust-group-title, .robust-label")].map((e) => ({ t: e.textContent.trim(), f: getComputedStyle(e).fontFamily })));
  expect(`${tag} section headings and labels in Sans, not all caps`, heads.length > 10 && heads.every((h) => !/Mono/.test(h.f) && h.t !== h.t.toUpperCase()), heads.filter((h) => /Mono/.test(h.f) || h.t === h.t.toUpperCase()).map((h) => h.t).slice(0, 5).join(", "));
  const reason = await p.locator(".verdict-reason").innerText();
  expect(`${tag} the verdict is not repeated under the card`, !/battery never below|NOT FEASIBLE|— /.test(reason) && /Terms:/.test(reason), reason.replace(/\s+/g, " "));
  const compares = await p.locator("button", { hasText: /compare/i }).count();
  expect(`${tag} one Compare control (the tab)`, compares === 1, `${compares}`);
  const groups = await p.locator(".robust-group-title").allInnerTexts();
  expect(`${tag} robustness in two groups`, groups.join("|") === "Power assumptions|Landing accuracy", groups.join("|"));
  const map = await p.locator(".robust-group.accuracy .ellipse-map").boundingBox().catch(() => null);
  const etext = await p.locator(".robust-group.accuracy .ellipse-item").boundingBox().catch(() => null);
  expect(`${tag} ellipse map beside its text (same group, overlapping rows)`, map && etext && map.y < etext.y + etext.height && etext.y < map.y + map.height, JSON.stringify([map?.y, etext?.y]));
  const axis = await p.locator(".timeline-labels .tl-axis").allInnerTexts();
  const days = await p.locator(".timeline-days span").allInnerTexts();
  expect(`${tag} timeline: battery axis 0/50/100 % and “Day N”`, axis.join(",") === "100%,50%,0%" && days[0] === "Day 1" && days.length >= 7, `${axis.join(",")} · ${days.join(" ")}`);
  const lab = await p.evaluate(() => { const r = [...document.querySelectorAll(".timeline-labels span")].map((e) => e.getBoundingClientRect()); for (let i = 0; i < r.length; i++) for (let j = i + 1; j < r.length; j++) { const a = r[i], c = r[j]; if (a.x < c.right && c.x < a.right && a.y < c.bottom && c.y < a.bottom) return false; } return true; });
  expect(`${tag} timeline labels do not overlap`, lab);
  await b.close();
}
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("CONSOLE ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail || errors.length ? 1 : 0);
