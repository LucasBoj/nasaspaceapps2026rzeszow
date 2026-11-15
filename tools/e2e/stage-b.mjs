// Package B (SIMPLE readability): site list sorted by sunlight with bars and "suggested", hover lights up the
// pin, names only for the selected / hovered pin, steps 1–2 "suggested" until confirmed, lander cards with three
// numbers and one sentence, custom card with "quick estimate", pole view framing.
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
await p.goto(process.env.URL || "http://localhost:5298/"); await p.waitForSelector(".run-main", { timeout: 60000 }); await p.waitForTimeout(8000);

const heads = await p.locator(".step-head").allInnerTexts();
expect("pre-selected steps 1–2 read “suggested”, not ✓", /suggested/.test(heads[0]) && /suggested/.test(heads[1]) && !/✓/.test(heads[0] + heads[1]), heads.slice(0, 2).map((x) => x.replace(/\s+/g, " ")).join(" | "));
const labels0 = await p.evaluate(() => window.__seleneSiteLabels());
expect("globe: only the selected site shows its name", labels0.length === 1, labels0.join(", "));

await p.locator(".step-head button", { hasText: "Choose a site" }).click(); await p.waitForTimeout(500);
const pcts = (await p.locator(".site-row .site-row-fact").allInnerTexts()).map((t) => parseFloat(t.match(/([\d.]+)%/)[1]));
expect("site list sorted by sunlight, best first", pcts.length === 9 && pcts.every((v, i) => i === 0 || pcts[i - 1] >= v), pcts.join(" ≥ "));
expect("every row has a sunlight bar", (await p.locator(".site-row .site-row-bar").count()) === 9);
expect("one row marked “suggested”", (await p.locator(".site-row .site-suggested").count()) === 1);
const hoverRow = p.locator(".site-row", { hasText: "Haworth" });
await hoverRow.hover(); await p.waitForTimeout(600);
const labels1 = await p.evaluate(() => window.__seleneSiteLabels());
expect("hovering a row lights up that pin (its name appears)", labels1.includes("haworth") && labels1.length === 2, labels1.join(", "));
await p.mouse.move(5, 300); await p.waitForTimeout(500);
expect("leaving the row hides it again", (await p.evaluate(() => window.__seleneSiteLabels())).length === 1);
await hoverRow.click(); await p.waitForTimeout(500);
await p.locator(".step-head button", { hasText: "Choose a lander" }).click(); await p.waitForTimeout(500);
const h1 = await p.locator(".step-head").first().innerText();
expect("after the user picks a site, step 1 shows ✓ (no “suggested”)", /✓/.test(h1) && !/suggested/.test(h1), h1.replace(/\s+/g, " "));

const cards = await p.locator(".lander-card").allInnerTexts();
expect("lander cards: three numbers (W · kWh · d) and one sentence each", cards.length === 3 && cards.every((c) => /\d[\d,]* W · [\d.]+ kWh · [\d.]+ d/.test(c) && /(reserve|solar power|on battery)/.test(c)), cards.map((c) => c.replace(/\s+/g, " ").slice(0, 90)).join(" | "));
await p.locator(".lander-card", { hasText: "Blue Ghost" }).click(); await p.waitForTimeout(400);
const h2 = await p.locator(".step-head").nth(1).innerText();
expect("after the user picks a lander, step 2 is no longer “suggested”", !/suggested/.test(h2), h2.replace(/\s+/g, " "));

await p.evaluate(() => window.__seleneTestClick(-86.0, -100.0));
await p.waitForFunction(() => document.querySelector(".custom-card .analyse-point"), null, { timeout: 60000 });
const cc = await p.locator(".custom-card").innerText();
expect("custom card: same language, tagged “quick estimate”, 240 m terrain named", /Custom site/.test(cc) && /quick estimate/.test(cc) && /240 m/.test(cc), cc.replace(/\s+/g, " ").slice(0, 140));

await b.close();
console.log(`\n${pass} passed, ${fail} failed [${BR}]`);
if (errors.length) console.log("CONSOLE ERRORS:\n" + [...new Set(errors)].join("\n"));
process.exit(fail || errors.length ? 1 : 0);
