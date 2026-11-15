// Stage 2: non-modal mission summary, HUD tooltips without overlap, Result step, juror path at 2 sizes.
import { chromium, firefox, webkit } from "playwright";
const BR = process.env.BR || "chromium";
const eng = BR === "firefox" ? firefox : BR === "webkit" ? webkit : chromium;
const SHOTS = process.env.SHOTS || "";   // directory for the "after" screenshots (chromium only)
let pass = 0, fail = 0; const errors = [];
const expect = (n, c, x = "") => { c ? pass++ : fail++; console.log(`${c ? "PASS" : "FAIL"}  ${n}${x !== "" ? "  — " + x : ""}`); };
async function open(w, h, analyst) {
  const b = await eng.launch({ headless: true });
  const ctx = await b.newContext({ viewport: { width: w, height: h } });
  await ctx.addInitScript((a) => { localStorage.setItem("selene.bootSeen.v1", "1"); localStorage.setItem("selene.welcomeDismissed.v1", "1"); localStorage.setItem("selene.analystMode.v1", a ? "1" : "0"); }, analyst);
  const p = await ctx.newPage();
  p.on("pageerror", (e) => errors.push(`${w}: ${e.message}`)); p.on("console", (m) => { if (m.type() === "error" && !/GL Driver|GPU stall|WebGL/.test(m.text())) errors.push(`${w}: ${m.text().slice(0, 160)}`); });
  await p.goto(process.env.URL || "http://localhost:5298/"); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForTimeout(6000);
  return { b, p };
}
const box = (p, sel) => p.locator(sel).first().boundingBox().catch(() => null);
const hit = (a, b) => a && b && a.x < b.x + b.width - 0.5 && b.x < a.x + a.width - 0.5 && a.y < b.y + b.height - 0.5 && b.y < a.y + a.height - 0.5;
/** Every visible control / chip in the Moon view that `sel` overlaps (excluding itself and its own host). */
const overlaps = (p, sel, own) => p.evaluate(([sel, own]) => {
  const t = document.querySelector(sel); if (!t) return ["(missing)"];
  const r = t.getBoundingClientRect(); const out = [];
  for (const e of document.querySelectorAll(".moon-viewer-container button, .moon-viewer-container .viewer-badge, .moon-viewer-container .hud-reason, .moon-viewer-container .compass, .moon-viewer-container .replay-hud, .moon-viewer-container .landing-panel")) {
    if (e === t || e.contains(t) || t.contains(e) || (own && e.closest(own))) continue;
    const q = e.getBoundingClientRect(); if (!q.width || !q.height || getComputedStyle(e).visibility === "hidden") continue;
    if (r.x < q.right - 0.5 && q.x < r.right - 0.5 && r.y < q.bottom - 0.5 && q.y < r.bottom - 0.5) out.push((e.className || e.tagName).toString().slice(0, 40) + ":" + (e.textContent || "").trim().slice(0, 20));
  }
  return out;
}, [sel, own]);
const camHeight = (p) => p.evaluate(() => window.__seleneCameraHeight?.() ?? null);
const shot = async (p, name) => { if (SHOTS && BR === "chromium") await p.screenshot({ path: `${SHOTS}/${name}.png` }); };

for (const [W, H] of [[1366, 768], [1920, 1080]]) {
  const tag = `${W}x${H}`;
  // ---------------------------------------------------------------- SIMPLE: landing, Result step, summary
  {
    const { b, p } = await open(W, H, false);
    await p.locator(".run-main").click();
    await p.waitForSelector(".landing-panel", { timeout: 20000 }); await p.waitForTimeout(1200);
    const lp = await box(p, ".landing-panel"), hb = await box(p, ".viewer-hud-buttons");
    expect(`${tag} descent panel does not cover the HUD buttons`, lp && hb && !hit(lp, hb), JSON.stringify([lp?.y, hb && hb.y + hb.height]));
    await p.locator(".landing-badge").hover(); await p.waitForTimeout(200);
    const lt = await overlaps(p, ".landing-tip:not([hidden])", ".landing-panel");
    expect(`${tag} descent tooltip opens beside the panel, over no control`, lt.length === 0, lt.join(", "));
    await shot(p, `after-${tag}-descent`);
    await p.waitForSelector(".verdict-card.overlay", { timeout: 60000 }); await p.waitForTimeout(2500);
    const vc = await box(p, ".verdict-card.overlay");
    const vh = await p.evaluate(() => innerHeight);
    const btnsOk = await p.evaluate(() => [...document.querySelectorAll(".verdict-card.overlay .verdict-actions button")].every((x) => { const r = x.getBoundingClientRect(); const e = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2); return r.bottom <= innerHeight && (e === x || x.contains(e)); }));
    expect(`${tag} verdict card fits the screen with all buttons visible`, vc && vc.y + vc.height <= vh && btnsOk, `${vc && Math.round(vc.y + vc.height)} ≤ ${vh}`);
    const nums = await p.locator(".verdict-card.overlay .verdict-numbers").innerText();
    expect(`${tag} verdict numbers: energy ×, longest stay, landing area`, /Energy made vs\. used\s*[\d.]+\s*×/.test(nums) && /Longest possible stay\s*≥?[\d.]+\s*days/.test(nums) && /Landing area OK/.test(nums), nums.replace(/\s+/g, " "));
    const res = await p.locator("#step-result").innerText().catch(() => "");
    expect(`${tag} after touchdown the right panel shows the Result step`, /Result/.test(res) && /Try another date/.test(res) && /Change lander/.test(res) && /New landing/.test(res) && (await p.locator(".run-main").count()) === 0, res.replace(/\s+/g, " ").slice(0, 120));
    await shot(p, `after-${tag}-landed`);
    // Play → summary
    await p.locator(".verdict-card .play-mission").click(); await p.waitForSelector(".replay-hud"); await p.locator(".replay-skip").click();
    await p.waitForSelector(".replay-summary", { timeout: 20000 }); await p.waitForTimeout(600);
    expect(`${tag} compass hidden while the summary card is open`, (await p.locator(".viewer-compass").count()) === 0);
    await shot(p, `after-${tag}-summary`);
    // ⌂ View: folds the card and moves the camera at once
    const h0 = await p.evaluate(() => { const c = window.__scene?.camera; return null; });
    await p.locator(".hud-btn", { hasText: "View" }).click(); await p.waitForTimeout(400);
    expect(`${tag} ⌂ View folds the summary into “Mission summary”`, (await p.locator(".replay-summary").count()) === 0 && (await p.locator(".replay-summary-chip").isVisible()));
    expect(`${tag} compass back when the summary is folded`, (await p.locator(".viewer-compass").count()) === 1);
    await p.locator(".replay-summary-chip").click(); await p.waitForTimeout(300);
    expect(`${tag} the chip opens the summary again`, await p.locator(".replay-summary").isVisible());
    await p.keyboard.press("Escape"); await p.waitForTimeout(400);
    expect(`${tag} Esc folds the summary`, (await p.locator(".replay-summary").count()) === 0 && (await p.locator(".replay-summary-chip").count()) === 1);
    await p.locator(".replay-summary-chip").click(); await p.waitForTimeout(300);
    const cv = await box(p, ".moon-viewer-canvas");
    await p.mouse.move(cv.x + 40, cv.y + cv.height / 2); await p.mouse.down(); await p.mouse.move(cv.x + 120, cv.y + cv.height / 2, { steps: 5 }); await p.mouse.up(); await p.waitForTimeout(500);
    expect(`${tag} dragging the camera folds the summary`, (await p.locator(".replay-summary").count()) === 0);
    await p.locator(".replay-summary-chip").click(); await p.waitForTimeout(300);
    await p.mouse.move(cv.x + cv.width / 2, cv.y + 80); await p.mouse.wheel(0, 200); await p.waitForTimeout(500);
    expect(`${tag} scrolling over the Moon folds the summary`, (await p.locator(".replay-summary").count()) === 0);
    // Try another date (from the Result step) → Analyst heatmap; New landing; custom point; Land & analyze
    await p.locator("#step-result .result-another").click(); await p.waitForTimeout(1500);
    const hm = await p.evaluate(() => { const r = document.getElementById("results-heatmap")?.getBoundingClientRect(); return r ? r.top < innerHeight && r.bottom > 0 : false; });
    expect(`${tag} Try another date → heatmap in view`, hm);
    await p.waitForSelector(".best-date", { timeout: 60000 });
    const chip = await p.locator("button.best-date").first().innerText();
    expect(`${tag} best-date chips carry the year`, /\d{1,2} [A-Z][a-z]{2} 20\d\d/.test(chip), chip);
    await shot(p, `after-${tag}-heatmap`);
    await p.evaluate(() => window.scrollTo(0, 0));
    // HUD tooltips in Analyst: View / settings / Candidates and the chips
    for (const [name, sel] of [["⌂ View", ".hud-btn:has-text('View')"], ["⚙", ".hud-btn[aria-label='Viewer settings']"], ["Candidates", ".hud-btn:has-text('Candidates')"]]) {
      await p.locator(sel).first().hover(); await p.waitForTimeout(250);
      const o = await overlaps(p, ".hud-tip");
      expect(`${tag} tooltip for ${name} covers no control or chip`, o.length === 0 && (await p.locator(".hud-tip").count()) === 1, o.join(", "));
    }
    for (const chipSel of [".viewer-badge.mode", ".viewer-badge.model-status"]) {
      if (!(await p.locator(chipSel).count())) continue;
      await p.locator(chipSel).hover(); await p.waitForTimeout(250);
      const o = await overlaps(p, `${chipSel} > .tip`);
      expect(`${tag} tooltip of ${chipSel} covers no control or chip`, o.length === 0, o.join(", "));
    }
    await p.mouse.move(5, 5);
    await p.locator(".replay-summary-chip").click().catch(() => {});
    await p.locator("#step-result .result-new").click(); await p.waitForTimeout(800);
    expect(`${tag} New landing: Result step gone, main button back`, (await p.locator("#step-result").count()) === 0 && (await p.locator(".run-main").count()) === 1 && (await p.locator(".replay-summary, .replay-summary-chip").count()) === 0);
    await p.evaluate(() => window.__seleneTestClick(-86.0, -100.0));
    await p.waitForFunction(() => document.querySelector(".custom-card .analyse-point"), null, { timeout: 60000 });
    await p.locator(".custom-card .analyse-point").click();
    await p.waitForSelector(".verdict-card", { timeout: 90000 }); await p.waitForTimeout(2500);
    expect(`${tag} custom point → Land & analyze → verdict + Result step`, (await p.locator("#step-result").isVisible()));
    await shot(p, `after-${tag}-custom`);
    await b.close();
  }
  // ---------------------------------------------------------------- QUICK DEMO path (SIMPLE)
  {
    const { b, p } = await open(W, H, false);
    await p.locator(".top-bar .quick-demo").click();
    await p.waitForSelector(".replay-hud", { timeout: 90000 });
    const rh = await overlaps(p, ".replay-hud");
    expect(`${tag} QUICK DEMO replay HUD covers no control`, rh.length === 0, rh.join(", "));
    await shot(p, `after-${tag}-quickdemo-replay`);
    await p.locator(".replay-speeds .segmented-option", { hasText: "4×" }).click();
    await p.waitForSelector(".replay-summary", { timeout: 60000 }); await p.waitForTimeout(500);
    const sb = await overlaps(p, ".replay-summary");
    expect(`${tag} summary card covers no other control`, sb.length === 0, sb.join(", "));
    await b.close();
  }
}
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("CONSOLE ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail || errors.length ? 1 : 0);
