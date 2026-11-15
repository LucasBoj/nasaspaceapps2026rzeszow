// Space Apps "Use of Super Intelligence": the "AI-generated 3D model" badge is visible whenever a lander model is in
// the scene (descent, landed, Play mission), covers no control, and the published index.html carries the meta tag.
import { chromium, firefox, webkit } from "playwright";
const BR = process.env.BR || "chromium";
const eng = BR === "firefox" ? firefox : BR === "webkit" ? webkit : chromium;
const URL_ = process.env.URL || "http://localhost:5298/";
let pass = 0, fail = 0; const errors = [];
const expect = (n, c, x = "") => { c ? pass++ : fail++; console.log(`${c ? "PASS" : "FAIL"}  ${n}${x !== "" ? "  — " + x : ""}`); };
const b = await eng.launch({ headless: true });
const ctx = await b.newContext({ viewport: { width: 1366, height: 768 } });
await ctx.addInitScript(() => { localStorage.setItem("selene.bootSeen.v1", "1"); localStorage.setItem("selene.welcomeDismissed.v1", "1"); localStorage.setItem("selene.analystMode.v1", "0"); });
const p = await ctx.newPage();
p.on("pageerror", (e) => errors.push(e.message)); p.on("console", (m) => { if (m.type() === "error" && !/GL Driver|GPU stall|WebGL/.test(m.text())) errors.push(m.text().slice(0, 160)); });
const html = await (await fetch(URL_)).text();
expect("published index.html has <meta name=\"ai-assistance\">", /<meta name="ai-assistance" content="[^"]*AI-generated[^"]*"/.test(html));
await p.goto(URL_); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForTimeout(5000);
expect("no badge before a lander is in the scene", (await p.locator(".ai-model-badge").count()) === 0);
const overlaps = () => p.evaluate(() => {
  const t = document.querySelector(".ai-model-badge"); if (!t) return ["(missing)"];
  const r = t.getBoundingClientRect(), out = [];
  for (const e of document.querySelectorAll(".moon-viewer-container button, .moon-viewer-container .viewer-badge, .moon-viewer-container .landing-panel, .moon-viewer-container .replay-hud, .moon-viewer-container .contact-banner, .moon-viewer-container .compass")) {
    const q = e.getBoundingClientRect(); if (!q.width) continue;
    if (r.x < q.right && q.x < r.right && r.y < q.bottom && q.y < r.bottom) out.push((e.className || e.tagName).toString().slice(0, 30));
  }
  return out;
});
await p.locator(".run-main").click(); await p.waitForSelector(".landing-panel", { timeout: 20000 }); await p.waitForTimeout(800);
const t1 = await p.locator(".ai-model-badge").innerText().catch(() => "");
expect("descent: “AI-generated 3D model” badge visible", /AI-generated 3D model/.test(t1) && (await p.locator(".ai-model-badge").isVisible()), t1);
const o1 = await overlaps();
expect("descent: badge covers no control", o1.length === 0, o1.join(", "));
await p.waitForSelector(".verdict-card.overlay", { timeout: 60000 }); await p.waitForTimeout(1500);
expect("landed: badge still visible", await p.locator(".ai-model-badge").isVisible());
const o2 = await overlaps();
expect("landed: badge covers no control (incl. the contact banner)", o2.length === 0, o2.join(", "));
await p.locator(".verdict-card .play-mission").click(); await p.waitForSelector(".replay-hud"); await p.waitForTimeout(800);
expect("Play mission: badge visible", await p.locator(".ai-model-badge").isVisible());
await p.locator(".replay-skip").click(); await p.waitForSelector(".replay-summary");
await p.locator(".hud-btn.new-landing").click(); await p.waitForTimeout(800);
expect("New landing (no lander in the scene): badge gone", (await p.locator(".ai-model-badge").count()) === 0);
await b.close();
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("CONSOLE ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail || errors.length ? 1 : 0);
