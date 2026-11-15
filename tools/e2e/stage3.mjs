// Stage 3: QUICK DEMO shows a real decision — battery visibly drops but the mission works; HUD narration in
// darkness and after it; the counter-example ends in MISSION LOST; the best landing window is one click away.
import { chromium, firefox, webkit } from "playwright";
const BR = process.env.BR || "chromium";
const eng = BR === "firefox" ? firefox : BR === "webkit" ? webkit : chromium;
const SHOTS = process.env.SHOTS || "";
let pass = 0, fail = 0; const errors = [];
const expect = (n, c, x = "") => { c ? pass++ : fail++; console.log(`${c ? "PASS" : "FAIL"}  ${n}${x !== "" ? "  — " + x : ""}`); };
const b = await eng.launch({ headless: true });
const ctx = await b.newContext({ viewport: { width: 1366, height: 768 } });
await ctx.addInitScript(() => { localStorage.setItem("selene.bootSeen.v1", "1"); localStorage.setItem("selene.welcomeDismissed.v1", "1"); localStorage.setItem("selene.analystMode.v1", "0"); });
const p = await ctx.newPage();
p.on("pageerror", (e) => errors.push(e.message)); p.on("console", (m) => { if (m.type() === "error" && !/GL Driver|GPU stall|WebGL/.test(m.text())) errors.push(m.text().slice(0, 160)); });
await p.goto(process.env.URL || "http://localhost:5298/"); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForTimeout(6000);
const shot = async (name) => { if (SHOTS && BR === "chromium") await p.screenshot({ path: `${SHOTS}/${name}.png` }); };

await p.locator(".top-bar .quick-demo").click();
await p.waitForSelector(".verdict-card.overlay", { timeout: 90000 });
const card = p.locator(".verdict-card.overlay");
const minSoc = Number(await card.getAttribute("data-min-soc"));
const verdict = (await card.locator(".verdict-big").innerText()).trim();
const sentence = await card.locator(".verdict-sentence").innerText();
expect("QUICK DEMO: battery visibly drops (lowest < 100 %) and the mission is not NOT FEASIBLE", minSoc < 100 && minSoc > 0 && !/NOT FEASIBLE/.test(verdict), `${verdict} · lowest battery ${minSoc} %`);
expect("QUICK DEMO: Blue Ghost at Nobile Rim 1, landing 26 Dec 2028", /Nobile Rim 1/.test(sentence) && /26 Dec 2028/.test(sentence), sentence);
await shot("stage3-quickdemo-verdict");

// Replay: darkness narration, then sunlight back; the battery in the HUD falls while dark.
await p.waitForSelector(".replay-hud", { timeout: 20000 });
await p.waitForSelector(".replay-note[data-kind='dark']:not([hidden])", { timeout: 40000 });
const darkText = await p.locator(".replay-note").innerText();
const bat1 = parseFloat(await p.locator(".replay-battery .num").innerText());
const cur1 = await p.locator(".replay-strip-cursor").evaluate((e) => parseFloat(e.style.left));
await shot("stage3-replay-dark");
await p.waitForSelector(".replay-note[data-kind='back']:not([hidden])", { timeout: 40000 });
const backText = await p.locator(".replay-note").innerText();
const bat2 = parseFloat(await p.locator(".replay-battery .num").innerText());
const cur2 = await p.locator(".replay-strip-cursor").evaluate((e) => parseFloat(e.style.left));
expect("HUD: “Sun hidden behind terrain — running on battery” when darkness starts", /running on battery/.test(darkText) && /Sun (hidden behind terrain|below the horizon)/.test(darkText), darkText);
expect("HUD: “Sunlight back — recharging” when it ends", backText === "Sunlight back — recharging", backText);
expect("battery fell during the darkness and the timeline cursor moved on with it", bat2 < bat1 && cur2 > cur1, `${bat1} → ${bat2} %, cursor ${cur1.toFixed(1)} → ${cur2.toFixed(1)} %`);
await p.locator(".replay-speeds .segmented-option", { hasText: "4×" }).click();
await p.waitForSelector(".replay-summary", { timeout: 60000 });
const title = await p.locator(".replay-title").innerText();
expect("demo ends MISSION COMPLETE", title.startsWith("MISSION COMPLETE"), title);
const counter = p.locator(".replay-summary .counter-example");
expect("summary offers the counter-example with a true sentence", (await counter.innerText()).trim() === "The same site 4 weeks later would fail →", await counter.innerText());
expect("summary offers “Show the best landing window”", await p.locator(".replay-summary .best-window").isVisible());
await shot("stage3-summary");

// Counter-example → MISSION LOST
await counter.click();
await p.waitForSelector(".replay-hud", { timeout: 30000 });
await p.locator(".replay-speeds .segmented-option", { hasText: "4×" }).click();
await p.waitForSelector(".replay-note[data-kind='dark']:not([hidden])", { timeout: 40000 });
await p.waitForSelector(".replay-summary", { timeout: 60000 });
const lost = await p.locator(".replay-title").innerText();
expect("counter-example (same site, 23 Jan 2029) ends MISSION LOST", lost.startsWith("MISSION LOST"), lost);
await shot("stage3-counter-lost");

// Best window
await p.locator(".replay-summary .best-window").click();
await p.waitForSelector(".verdict-card.overlay", { timeout: 60000 }); await p.waitForTimeout(800);
const best = await p.locator(".verdict-card.overlay .verdict-sentence").innerText();
expect("“Show the best landing window” → the optimizer's day (not the pinned dates)", !/26 Dec 2028|23 Jan 2029/.test(best), best);
await b.close();
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("CONSOLE ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail || errors.length ? 1 : 0);
