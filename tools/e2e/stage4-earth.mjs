// Stage 4 "Earth in the sky": the Earth and the Sun are drawn where the illumination data puts them (az/el ±0.5°),
// the 3D terrain hides the Earth in hours the data says it is behind terrain, Surface view stays above the
// terrain and keeps its pitch limits, Play mission can be watched from the surface with labelled bodies.
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
await p.goto(URL_); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForFunction(() => !document.querySelector(".terrain-loading"), null, { timeout: 40000 }).catch(() => {}); await p.waitForTimeout(6000);

// 1) Hours from the data: Earth clearly above / clearly behind the LOLA horizon, always above the flat horizon.
const pick = await p.evaluate(async () => {
  const sites = await (await fetch("data/sites.json")).json();
  const hz = (h, az) => { const a = ((az % 360) + 360) % 360, k = Math.floor(a), f = a - k; return h[k] * (1 - f) + h[(k + 1) % 360] * f; };
  for (const s of sites) for (const y of [2027, 2028]) {
    const d = await (await fetch(`data/illumination/${s.id}/${y}.json`)).json();
    const vis = [], hid = [];
    for (let i = 0; i < d.count; i += 7) {
      const el = d.earthElevCentiDeg[i] / 100, az = d.earthAzCentiDeg[i] / 100, clr = el - hz(d.horizonDeg, az);
      const rec = { i, el, az, clr, sunAz: d.sunAzCentiDeg[i] / 100, sunEl: d.sunElevCentiDeg[i] / 100, sunPct: d.sunFraction[i], vis: d.earthVisible[i] };
      if (el > 1 && clr > 1.5 && vis.length < 2) vis.push(rec);
      if (el > 0.3 && clr < -1 && hid.length < 3) hid.push(rec);
    }
    if (vis.length && hid.length) return { site: s, year: y, vis, hid };
  }
  return null;
});
expect("data: a site with the Earth both above and behind its terrain horizon", !!pick, pick ? `${pick.site.id} ${pick.year}` : "none");

if (pick) {
  const { lat, lon } = pick.site;
  const show = async (r) => {
    await p.evaluate(([lat, lon, r]) => window.__seleneSetSky(lat, lon, r.sunAz, r.sunEl, r.sunPct, r.az, r.el), [lat, lon, r]);
    await p.evaluate(([lat, lon]) => window.__seleneEnterSurface(lat, lon), [lat, lon]);
    await p.waitForTimeout(7000);   // terrain tiles towards the horizon
    // The app may have re-synced the sky to the selected site meanwhile (e.g. its start-up analysis finished):
    // set this hour again right before measuring.
    await p.evaluate(([lat, lon, r]) => window.__seleneSetSky(lat, lon, r.sunAz, r.sunEl, r.sunPct, r.az, r.el), [lat, lon, r]);
    await p.waitForTimeout(400);
  };
  // 2) direction ±0.5°
  await show(pick.vis[0]);
  // Set the hour and read back in one step: the app may re-sync the sky to its own site at any moment.
  const sky = await p.evaluate(([lat, lon, r]) => { window.__seleneSetSky(lat, lon, r.sunAz, r.sunEl, r.sunPct, r.az, r.el); return window.__seleneSky(lat, lon); }, [lat, lon, pick.vis[0]]);
  const dAz = Math.abs(((sky.earth.az - pick.vis[0].az + 540) % 360) - 180), dEl = Math.abs(sky.earth.el - pick.vis[0].el);
  const sAz = Math.abs(((sky.sun.az - pick.vis[0].sunAz + 540) % 360) - 180), sEl = Math.abs(sky.sun.el - pick.vis[0].sunEl);
  expect("Earth drawn at the data's azimuth / elevation (±0.5°)", dAz <= 0.5 && dEl <= 0.5, `Δaz ${dAz.toFixed(3)}°, Δel ${dEl.toFixed(3)}°`);
  expect("Sun drawn at the data's azimuth / elevation (±0.5°)", sAz <= 0.5 && sEl <= 0.5, `Δaz ${sAz.toFixed(3)}°, Δel ${sEl.toFixed(3)}°`);
  // 3) occlusion agrees with the data
  for (const r of pick.vis) {
    await show(r);
    const v = await p.evaluate(() => window.__seleneEarthVisible());
    expect(`hour ${r.i}: Earth ${r.clr.toFixed(1)}° above the terrain → visible`, v?.onScreen && v.visible, JSON.stringify(v));
  }
  for (const r of pick.hid) {
    await show(r);
    const v = await p.evaluate(() => window.__seleneEarthVisible());
    expect(`hour ${r.i}: Earth ${(-r.clr).toFixed(1)}° behind the terrain → hidden`, v?.onScreen && !v.visible, JSON.stringify(v));
  }
  // 4) camera stays above the terrain, pitch limits hold
  const s0 = await p.evaluate(() => window.__seleneSurface());
  expect("Surface view: eye 1–6 m above the rendered terrain", s0.active && s0.ground != null && s0.height - s0.ground > 1 && s0.height - s0.ground < 6, `${(s0.height - s0.ground).toFixed(2)} m`);
  const cv = await p.locator(".moon-viewer-canvas").boundingBox();
  await p.mouse.move(cv.x + cv.width / 2, cv.y + cv.height / 2); await p.mouse.down();
  await p.mouse.move(cv.x + cv.width / 2 + 200, cv.y + 40, { steps: 8 }); await p.mouse.up(); await p.waitForTimeout(400);
  const s1 = await p.evaluate(() => window.__seleneSurface());
  await p.mouse.move(cv.x + cv.width / 2, cv.y + 60); await p.mouse.down();
  await p.mouse.move(cv.x + cv.width / 2, cv.y + cv.height - 10, { steps: 8 }); await p.mouse.up(); await p.waitForTimeout(400);
  const s2 = await p.evaluate(() => window.__seleneSurface());
  const dh = Math.abs(((s1.heading - s0.heading + 540) % 360) - 180);
  expect("drag turns around the vertical; pitch stays within −10…+30°", dh > 5 && s1.pitch >= -10.01 && s1.pitch <= 30.01 && s2.pitch >= -10.01 && s2.pitch <= 30.01, `Δheading ${dh.toFixed(1)}°, pitch ${s1.pitch.toFixed(1)} / ${s2.pitch.toFixed(1)}°`);
  expect("eye still above the terrain after looking around", s2.ground == null || s2.height - s2.ground > 1, `${s2.ground == null ? "n/a" : (s2.height - s2.ground).toFixed(2) + " m"}`);
  await p.locator(".hud-btn[aria-label^='Back to the south pole']").first().click(); await p.waitForTimeout(500);
  expect("⌂ View leaves Surface view (orbit controls back)", !(await p.evaluate(() => window.__seleneSurface().active)));
}

// 5) Play mission → Watch from the surface: labels, Back to orbit view
await p.locator(".top-bar .quick-demo").click();
await p.waitForSelector(".replay-hud", { timeout: 90000 });
await p.locator(".replay-surface").click(); await p.waitForTimeout(4000);
const act = await p.evaluate(() => window.__seleneSurface().active);
const btn = await p.locator(".hud-btn.surface-view").innerText().catch(() => "");
expect("Play mission → “Watch from the surface” switches the camera to the surface", act && /Back to orbit view/.test(btn), btn);
await p.waitForTimeout(4000);   // tiles refine near the eye; the lander is put back on the refined ground
const sv = await p.evaluate(() => window.__seleneSurface());
expect("the lander stands on the rendered ground and is in the foreground", sv.landerPx && sv.landerPx[1] > 0 && sv.landerPx[1] < 768 && Math.abs(sv.landerBase - sv.ground) < 6, JSON.stringify({ px: sv.landerPx, base: sv.landerBase?.toFixed(1), eyeGround: sv.ground?.toFixed(1), d: sv.toLander?.toFixed(1) }));
const vis = await p.evaluate(() => window.__seleneEarthVisible());
expect("the Earth is in view while the demo plays (contact 100 %)", vis?.visible === true, JSON.stringify(vis));
await p.locator(".hud-btn.surface-view").click(); await p.waitForTimeout(800);
expect("“Back to orbit view” leaves the surface", !(await p.evaluate(() => window.__seleneSurface().active)));

await b.close();
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("CONSOLE ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail || errors.length ? 1 : 0);
