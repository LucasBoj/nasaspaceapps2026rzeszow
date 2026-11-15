// Mission replay: endings for HLS / CLPS / failure, light moves, timeline cursor sync, skip, cleanup.
import { chromium, firefox, webkit } from "playwright";
const HERE = new URL(".", import.meta.url).pathname;
const BR = process.env.BR || "chromium";
const eng = BR === "firefox" ? firefox : BR === "webkit" ? webkit : chromium;
const INJ = ";(()=>{const R=Cesium.Scene.prototype.render;Cesium.Scene.prototype.render=function(...a){window.__scene=this;return R.apply(this,a)};})();";
let pass = 0, fail = 0; const errors = [];
const expect = (n, c, x = "") => { c ? pass++ : fail++; console.log(`${c ? "PASS" : "FAIL"}  ${n}${x !== "" ? "  — " + x : ""}`); };
async function open(analyst = false) {
  const b = await eng.launch({ headless: true });
  const ctx = await b.newContext({ viewport: { width: 1366, height: 768 } });
  await ctx.addInitScript((a) => { localStorage.setItem("selene.bootSeen.v1", "1"); localStorage.setItem("selene.welcomeDismissed.v1", "1"); localStorage.setItem("selene.analystMode.v1", a ? "1" : "0"); window.__seleneKeepFx = true; }, analyst);
  const p = await ctx.newPage();
  p.on("pageerror", (e) => errors.push(e.message)); p.on("console", (m) => { if (m.type() === "error" && !/GL Driver|GPU stall/.test(m.text())) errors.push(m.text().slice(0, 160)); });
  await p.route("**/Cesium.js", async (r) => { const res = await r.fetch(); await r.fulfill({ response: res, body: (await res.text()) + INJ }); });
  await p.goto(process.env.URL || "http://localhost:5298/"); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForTimeout(7000);
  return { b, p };
}
const lightDir = (p) => p.evaluate(() => { const d = window.__scene.light.direction; return [d.x, d.y, d.z]; });
const modelHeight = (p) => p.evaluate(() => { const s = window.__scene; for (let i = 0; i < s.primitives.length; i++) { const m = s.primitives.get(i); if (m instanceof Cesium.Model && m.show) return Cesium.Cartographic.fromCartesian(Cesium.Matrix4.getTranslation(m.modelMatrix, new Cesium.Cartesian3()), Cesium.Ellipsoid.MOON).height; } return null; });
const fxCount = (p) => p.evaluate(() => { const s = window.__scene.primitives; let n = 0; for (let i = 0; i < s.length; i++) { const x = s.get(i); if (x instanceof Cesium.ParticleSystem) n++; } return n; });
const setSpeed = (p, k) => p.locator(".replay-speeds .segmented-option", { hasText: `${k}×` }).click();

{ // CLPS success via QUICK DEMO (auto replay), with the timeline cursor in sync (Analyst)
  const { b, p } = await open(true);
  await p.locator(".top-bar .quick-demo").click();
  await p.waitForSelector(".replay-hud", { timeout: 90000 });
  expect("QUICK DEMO ends by playing the mission (replay HUD shown)", true);
  const compass0 = await p.locator(".compass").first().getAttribute("aria-label").catch(() => null);
  const cur0 = await p.locator(".replay-strip-cursor").evaluate((e) => parseFloat(e.style.left)).catch(() => NaN);
  const d0 = await lightDir(p);
  await p.waitForTimeout(3000);
  const d1 = await lightDir(p);
  const moved = Math.hypot(d0[0] - d1[0], d0[1] - d1[1], d0[2] - d1[2]);
  expect("scene light (Sun direction) changes during the replay", moved > 1e-3, moved.toFixed(4));
  const moonVis = await p.evaluate(() => { const r = document.querySelector(".moon-panel").getBoundingClientRect(); return r.top >= -1 && r.top < innerHeight * 0.3; });
  expect("the Moon view (lander) is on screen during the replay", moonVis);
  const cur1 = await p.locator(".replay-strip-cursor").evaluate((e) => parseFloat(e.style.left)).catch(() => NaN);
  const stripW = await p.locator(".replay-strip canvas").evaluate((c) => c.width).catch(() => 0);
  expect("HUD mini timeline (Sun / Earth / battery) drawn and its cursor moves", stripW > 0 && cur1 > cur0, `${cur0} → ${cur1} %, canvas ${stripW}px`);
  const compass1 = await p.locator(".compass").first().getAttribute("aria-label").catch(() => null);
  expect("Sun/Earth compass follows replay time", compass0 != null && compass1 !== compass0, `${compass0} → ${compass1}`);
  const clock = await p.locator(".replay-clock").innerText();
  const cursorX = await p.locator(".tl-cursor.replay").getAttribute("x").catch(() => null);
  const readout = await p.locator(".timeline-readout").innerText();
  const day = Number(clock.match(/DAY (\d+)/)?.[1]);
  const cursorDay = cursorX != null ? Math.floor(Number(cursorX) / 24) + 1 : NaN;
  expect("timeline replay cursor follows the replay clock (same mission day ±1)", cursorX != null && Math.abs(day - cursorDay) <= 1 && readout.startsWith("▶ replay"), `${clock} · cursor step ${cursorX} (day ${cursorDay})`);
  await setSpeed(p, 4);
  await p.waitForSelector(".replay-summary", { timeout: 60000 });
  const title = await p.locator(".replay-title").innerText();
  expect("CLPS success → MISSION COMPLETE · end of operations (lander stays)", title === "MISSION COMPLETE · end of operations", title);
  const sum = await p.locator(".replay-summary").innerText();
  expect("summary lists mission time, sunlight, lowest battery, Earth contact, longest darkness",
    ["Mission time", "Sunlight", "Lowest battery", "Earth contact", "Longest darkness", "Replay", "Try another date", "New landing"].every((k) => sum.includes(k)));
  await p.locator(".replay-summary button", { hasText: "Try another date" }).click(); await p.waitForTimeout(800);
  const hm = await p.evaluate(() => { const r = document.getElementById("results-heatmap")?.getBoundingClientRect(); return r ? r.top < innerHeight && r.bottom > 0 : false; });
  expect("“Try another date” scrolls to the heatmap", hm);
  await b.close();
}

{ // HLS success: lift-off
  const { b, p } = await open(false);
  await p.locator(".step-head button", { hasText: "Choose a lander" }).click();
  await p.locator(".segmented-option", { hasText: "Crewed" }).click(); await p.waitForTimeout(300);
  await p.locator(".lander-card", { hasText: "Starship" }).click(); await p.waitForTimeout(500);
  await p.locator(".run-main").click();
  await p.waitForSelector(".verdict-card .play-mission", { timeout: 90000 });
  await p.waitForTimeout(9000);   // the landed marker follows refining terrain tiles for ~8 s
  const h0 = await modelHeight(p);
  await p.locator(".verdict-card .play-mission").click();
  await p.waitForSelector(".replay-hud", { timeout: 10000 });
  await setSpeed(p, 4);
  await p.waitForSelector(".replay-banner:not([hidden])", { timeout: 60000 });
  await p.waitForTimeout(2500);
  const h1 = await modelHeight(p);
  const fxDuring = await fxCount(p);
  expect("HLS success: the lander lifts off (above its own height) with an engine plume", h1 - h0 > 50 && fxDuring >= 1, `+${(h1 - h0).toFixed(0)} m, plume systems ${fxDuring}`);
  await p.waitForSelector(".replay-summary", { timeout: 30000 });
  expect("HLS → MISSION COMPLETE · lift-off", (await p.locator(".replay-title").innerText()).startsWith("MISSION COMPLETE · lift-off"));
  await p.locator(".replay-summary button", { hasText: "Close" }).click(); await p.waitForTimeout(600);
  const h2 = await modelHeight(p);
  expect("closing the summary puts the lander back and removes the plume", Math.abs(h2 - h0) < 1 && (await fxCount(p)) === 0, `${(h2 - h0).toFixed(1)} m`);
  await b.close();
}

{ // Failure: Blue Ghost for 14 days (fixed panels, ~8 days) → MISSION LOST at the depletion hour; Skip; cleanup
  const { b, p } = await open(false);
  await p.locator(".step-head button", { hasText: "Choose a lander" }).click();
  await p.locator(".lander-card", { hasText: "Blue Ghost" }).click(); await p.waitForTimeout(500);
  if (!(await p.locator(".change-dates").count())) await p.locator(".step-head button", { hasText: "When to land" }).click();
  await p.locator(".change-dates summary").click();
  await p.locator("#mission-days").fill("14"); await p.locator("#mission-days").press("Tab"); await p.waitForTimeout(400);
  await p.locator(".run-main").click();
  await p.waitForSelector(".verdict-card .play-mission", { timeout: 90000 });
  const verdict = await p.locator(".verdict-big").innerText();
  await p.locator(".verdict-card .play-mission").click();
  await p.waitForSelector(".replay-hud", { timeout: 10000 });
  await p.waitForTimeout(1500);
  await p.locator(".replay-skip").click();
  await p.waitForSelector(".replay-summary", { timeout: 20000 });
  const title = await p.locator(".replay-title").innerText();
  expect("failure → MISSION LOST · day X · battery depleted (playback stops there)", /NOT FEASIBLE/.test(verdict) && /^MISSION LOST · day \d+ · battery depleted/.test(title), `${verdict.trim()} · ${title}`);
  const missionTime = await p.locator(".replay-numbers div", { hasText: "Mission time" }).locator("dd").innerText();
  const daysFlown = parseFloat(missionTime), dayTitle = Number(title.match(/day (\d+)/)?.[1]);
  expect("playback ends at the depletion hour (mission time matches the MISSION LOST day)", /of 14 days/.test(missionTime) && Math.floor(daysFlown) + 1 === dayTitle, `${missionTime} · ${title}`);
  expect("the replay HUD gives way to the summary card", (await p.locator(".replay-hud").count()) === 0);
  await p.locator(".replay-summary button", { hasText: "New landing" }).click(); await p.waitForTimeout(800);
  expect("New landing removes the replay HUD, summary and effects", (await p.locator(".replay-hud").count()) === 0 && (await p.locator(".replay-summary").count()) === 0 && (await fxCount(p)) === 0);
  await b.close();
}
{ // Analyst: ▶ Play mission from the results panel scrolled below the Moon → the page scrolls back up
  const { b, p } = await open(true);
  await p.locator(".run-main").click();
  await p.waitForSelector(".verdict-card .play-mission", { timeout: 90000 });
  await p.waitForTimeout(9000);
  await p.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await p.waitForTimeout(500);
  const scrolled = await p.evaluate(() => window.scrollY);
  await p.locator(".verdict-card .play-mission").first().click();
  await p.waitForSelector(".replay-hud", { timeout: 10000 });
  await p.waitForTimeout(1500);
  const vis = await p.evaluate(() => { const r = document.querySelector(".moon-panel").getBoundingClientRect(); const h = document.querySelector(".replay-hud").getBoundingClientRect(); return { top: r.top, hud: h.top >= 0 && h.bottom <= innerHeight, y: scrollY }; });
  expect("Analyst: Play mission scrolls the Moon and the replay HUD into view", vis.top >= -1 && vis.top < 200 && vis.hud, `scrollY ${scrolled} → ${vis.y}, moon top ${vis.top.toFixed(0)}`);
  await b.close();
}
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail ? 1 : 0);
