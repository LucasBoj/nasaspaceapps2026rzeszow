// LANDED: a click on a pin or the terrain offers "Start a new landing at …?"; Land here lands there, Cancel and Esc
// change nothing, dragging the camera never opens the card; the first offer pulses the New landing button.
import { chromium, firefox, webkit } from "playwright";
const BR = process.env.BR || "chromium";
const eng = BR === "firefox" ? firefox : BR === "webkit" ? webkit : chromium;
let pass = 0, fail = 0; const errors = [];
const expect = (n, c, x = "") => { c ? pass++ : fail++; console.log(`${c ? "PASS" : "FAIL"}  ${n}${x !== "" ? "  — " + x : ""}`); };
const b = await eng.launch({ headless: true });
const ctx = await b.newContext({ viewport: { width: 1366, height: 768 } });
await ctx.addInitScript(() => { localStorage.setItem("selene.bootSeen.v1", "1"); localStorage.setItem("selene.welcomeDismissed.v1", "1"); localStorage.setItem("selene.analystMode.v1", "0"); });
const p = await ctx.newPage();
p.on("pageerror", (e) => errors.push(e.message)); p.on("console", (m) => { if (m.type() === "error" && !/GL Driver|GPU stall|WebGL/.test(m.text())) errors.push(m.text().slice(0, 160)); });
await p.goto(process.env.URL || "http://localhost:5298/"); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForTimeout(5000);
await p.locator(".run-main").click(); await p.waitForSelector(".verdict-card.overlay", { timeout: 60000 }); await p.waitForTimeout(3000);
const site0 = (await p.locator(".step-head").first().innerText()).replace(/\s+/g, " ");
await p.locator(".hud-btn[aria-label^='Back to the south pole']").click(); await p.waitForTimeout(3500);
const canvas = await p.locator(".moon-viewer-canvas").boundingBox();
const pinAt = async (id) => { const px = await p.evaluate((id) => window.__seleneSitePx(id), id); return px && { x: canvas.x + px.x, y: canvas.y + px.y }; };
/** A site whose pin is on screen and not under a card or button (the verdict card covers the lower middle). */
const freePin = async () => {
  for (const id of ["haworth", "slater_plain", "de_gerlache_rim_2", "peak_near_cabeus_b", "malapert_massif", "nobile_rim_2", "mons_mouton", "mons_mouton_plateau"]) {
    const at = await pinAt(id);
    if (!at) continue;
    const free = await p.evaluate(([x, y]) => document.elementFromPoint(x, y)?.tagName === "CANVAS", [at.x, at.y]);
    if (free) return { id, ...at };
  }
  return null;
};

// drag: no card
let a = await freePin();
expect("a free pin to click is on screen", !!a, a?.id ?? "none");
const NAME = { haworth: "Haworth", slater_plain: "Slater Plain", de_gerlache_rim_2: "de Gerlache Rim 2", peak_near_cabeus_b: "Peak near Cabeus B", malapert_massif: "Malapert Massif", nobile_rim_2: "Nobile Rim 2", mons_mouton: "Mons Mouton", mons_mouton_plateau: "Mons Mouton Plateau" }[a.id];
await p.mouse.move(a.x + 40, a.y + 40); await p.mouse.down(); await p.mouse.move(a.x + 140, a.y + 90, { steps: 6 }); await p.mouse.up(); await p.waitForTimeout(800);
expect("dragging the camera while landed opens no card", (await p.locator(".landed-offer").count()) === 0);
await p.locator(".hud-btn[aria-label^='Back to the south pole']").click(); await p.waitForTimeout(3500);

// click a pin → card (+ first-time pulse), Cancel changes nothing
a = await pinAt(a.id);
await p.mouse.click(a.x, a.y); await p.waitForTimeout(900);
const card = await p.locator(".landed-offer").innerText().catch(() => "");
expect("click on a pin while landed → “Start a new landing at <site>?”", new RegExp(`Start a new landing at\\s+${NAME}\\?`).test(card) && /Land here/.test(card) && /Cancel/.test(card), card.replace(/\s+/g, " "));
expect("first offer in the session pulses the New landing button", (await p.locator(".hud-btn.new-landing.pulse").count()) === 1);
await p.locator(".landed-offer button", { hasText: "Cancel" }).click(); await p.waitForTimeout(500);
const site1 = (await p.locator(".step-head").first().innerText()).replace(/\s+/g, " ");
expect("Cancel: card gone, still landed at the same site", (await p.locator(".landed-offer").count()) === 0 && site1 === site0 && (await p.locator("#step-result").count()) === 1, site1);

// Esc closes
await p.mouse.click(a.x, a.y); await p.waitForTimeout(900);
await p.keyboard.press("Escape"); await p.waitForTimeout(400);
expect("Esc closes the card", (await p.locator(".landed-offer").count()) === 0);

// Land here → new landing at Haworth
await p.mouse.click(a.x, a.y); await p.waitForTimeout(900);
await p.locator(".landed-offer .land-here").click();
await p.waitForSelector(".landing-panel", { timeout: 20000 });
const title = await p.locator(".landing-title").innerText().catch(() => "");
expect("Land here starts a new landing at the clicked site", title.includes(NAME), title);
await p.waitForSelector(".verdict-card.overlay", { timeout: 60000 }); await p.waitForTimeout(1500);
const sentence = await p.locator(".verdict-card.overlay .verdict-sentence").innerText();
expect("…and the verdict is for that site", sentence.includes(NAME), sentence.slice(0, 80));
await b.close();
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("CONSOLE ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail || errors.length ? 1 : 0);
