# Przebudowa UX: SIMPLE / ANALYST (2026-11-14)

Cel: juror otwierający link po raz pierwszy w 10 s rozumie, co robi aplikacja, a w 30 s sam dochodzi do werdyktu. Logika analizy jest bez zmian. Zmieniła się hierarchia i widoczność. Zrzuty „przed” i „po” są w `docs/screenshots/` (`before-*`, `after-*`, 1920×1080 i 1366×768).

## Co się zmieniło

- **Dwa tryby.** SIMPLE (domyślny) pokazuje Księżyc, 3 kroki i jedną kartę werdyktu. ANALYST (przełącznik w górnym pasku, zapamiętywany) pokazuje wszystko, co było: oś czasu, heatmapę, COMPARE, SPECS, what-if, CANDIDATES, różę z liczbami, kafle, LANDER IMPACT, zegar i statusy. Przełączenie trwa ~30 ms, bez przeładowania i bez gubienia stanu.
- **Werdykt raz.** Karta werdyktu zawiera duży napis ✓/⚠/✕, jedno zdanie prostym językiem, 3 liczby i najlepszą datę, a „based on” jako szary dopisek z ⓘ. W SIMPLE wisi nad Księżycem, w ANALYST jest na górze panelu wyników. Pasek wyników jest już tylko uchwytem zwijania, bez liczb. Baner „CONTACT CONFIRMED” jest usunięty, bo dublował kartę.
- **Panel kroków.** 1 Choose a site (lista 9 miejsc z „sunlit X% of the time”), 2 Choose a lander (karty, ikonka jakości danych „● 4/9 sourced”, 3 kluczowe liczby), 3 Run analysis (daty zwinięte pod „Change dates”, ograniczenia tylko w ANALYST). Główny przycisk „Run analysis” jest przypięty do dołu panelu.
- **Stan LANDED.** Po przyziemieniu kliknięcia w Księżyc i w listę miejsc nic nie zmieniają: zwykły kursor, tooltip, lista wyłączona z notką. „New landing” (karta i HUD) usuwa model, odblokowuje wybór i wraca do kroku 1. Zmiana lądownika działa tak samo.
- **Kamera.**
  - Bez bezwładności, oddalenie do 6 000 km, pochylenie −10° … −89°.
  - Z daleka oś widoku zawsze trafia w tarczę Księżyca.
  - Swobodny lot zawsze ≥ 300 m nad terenem (+25 m zapasu na doczytywanie kafli). Próbkowanie terenu jest dławione, co ~60 ms blisko podłogi.
  - Odzysk z NaN (WebKit).
  - ⌂ View i klawisz Home wracają do widoku bieguna. Podwójne kliknięcie płynnie przybliża i nie wybiera miejsca.
  - Ujęcie po lądowaniu: lądownik zajmuje ~30% wysokości kadru, lekko z góry, Słońce za kamerą (widać oświetloną stronę i cień), a lądownik jest nad kartą werdyktu. Etykiety innych pinezek są ukryte.
- **ENHANCE** (domyślnie ON, ustawienia w ANALYST): więcej światła otoczenia i większa jasność wyłącznie na ekranie, kierunek Słońca bez zmian. Tooltip wyjaśnia, że biegun naprawdę jest tak ciemny.
- **Stopka.** Jedna linia z „Credits”. Pełna lista atrybucji jest w panelu Credits, nic nie usunięto, dopisano Zenodo i licencję fontów.
- **Styl.** Rozmiary tekstu 28 / 18 / 14 / 12 px. Sans dla treści, Mono tylko dla liczb i dat. Mniej wersalików i ramek. Ruch 150–250 ms ease-out, przy `prefers-reduced-motion` tylko fade.
- **Wydajność.** Ekran startowy trwa ≤ 1,5 s i zamyka się niezależnie od wczytywania. Usunięty `Cesium3DTileset.enableCollision`: jego raycasty zatrzymywały render na kilka sekund (1 kl./s zamiast ~48).

## Sekcja 10: jeden główny przycisk i widoczne powody blokady

- **Główny przycisk.** Jeden przycisk „Land & analyze” (lądowanie i analiza równolegle; z wyłączoną animacją: przelot i analiza). W ANALYST jest też drugorzędny „Re-run analysis” (bez animacji, np. po what-if lub zmianie dat).
- **Widoczne powody.** Gdy przycisk jest nieaktywny, pod nim zawsze widać listę braków z linkami do kroków, np. „To continue: ① Choose one of the 9 landing regions · ② Choose a lander”. Łączy je `aria-describedby`. Jedno źródło prawdy: `UiService.LandBlockers()`.
- **Punkt CUSTOM.** Komunikat „Custom points can't be analysed yet — our sunlight data is precomputed for the 9 Artemis regions…” i przycisk „Nearest region: …”, który wybiera najbliższy z 9 regionów (odległość po wielkim kole).
- **Pozostałe nieaktywne akcje z widocznym powodem:**
  - Zoom to site bez miejsca;
  - Candidates poza 9 regionami;
  - New landing w trakcie lądowania („available after touchdown”);
  - Fly to dla punktu CUSTOM;
  - „Next” w krokach 1–2;
  - Re-run analysis;
  - Reset to data (what-if);
  - Quick demo w trakcie ładowania („Loading data…”).
- **Zakładka COMPARE i okno dat.** COMPARE pokazuje się dopiero z wynikami. Okno dat poza zakresem danych jest przycinane z komunikatem „Dates adjusted — …”.
- **Nieaktualne wyniki.** Po zmianie ustawień przycisk zmienia się na „Update results”. Karta werdyktu pokazuje „Results are out of date — settings changed” (stare liczby przyciemnione) z przyciskiem „Update results”. Zmiana lądownika nie chowa już karty.
- **Testy:** `blockers.mjs` 16/16 w Chromium, Firefoksie i WebKicie. Obejmuje każdy warunek blokady z widocznym komunikatem i `aria-describedby`, CUSTOM → Nearest region, jeden przycisk główny w SIMPLE, „Update results” oraz axe bez błędów critical/serious. Regresja UX: 38/38.

## Zmienione teksty (wybór; jedno pojęcie = jedno słowo)

| Było | Jest |
|---|---|
| Lunar Landing Window Planner | Find when and where a lunar lander can survive at the Moon's south pole |
| ▶ QUICK DEMO | ▶ Quick demo |
| 01 TARGET SITE / 02 LANDER / 03 MISSION WINDOW / 04 CONSTRAINTS | 1 Choose a site / 2 Choose a lander / 3 Run analysis / Constraints (tylko Analyst) |
| Pick a site · Pick a lander | Choose a site · Choose a lander |
| ▼ CONFIRM LANDING SITE + RUN ANALYSIS | Land & analyze (jeden główny) · Re-run analysis (Analyst) · Update results (po zmianie ustawień) |
| RESULTS OUTDATED — re-run | Results are out of date — settings changed |
| Landing Window Start / End (UTC) | Earliest / Latest landing date (UTC) |
| Mission Duration [Earth days] | Days on the surface (Earth days) |
| Window clipped — … | Dates adjusted — … |
| CLPS · ROBOTIC / HLS · CREWED | Robotic / Crewed |
| DATA: 2 SOURCED · 1 DERIVED · 4 ASSUMED | ● 2/7 sourced (tooltip z rozbiciem) |
| SPECS (zawsze) | Solar power · Battery · Built to last (SIMPLE), SPECS w Analyst |
| RESULTS — FEASIBLE Nobile Rim 1 · land … · min battery 97% · Earth 100% | Analysis details · Nobile Rim 1 · timeline, landing-window heatmap, comparison |
| FEASIBLE — … · min battery 97% · Earth comms 100% · based on: ASSUMPTION | ✓ FEASIBLE + „Land at Nobile Rim 1 on 8 Nov 2027 — sunlight all 7 days, battery never below 99%, constant contact with Earth.” + ⓘ based on estimated lander values |
| min battery / Earth comms (w karcie) | Lowest battery / Earth contact |
| SOUTH POLE VIEW · ZOOM TO SITE · CANDIDATES | ⌂ View · Zoom to site · Candidates |
| ANALYZING · 28% | Analyzing all 9 sites · 28% |
| LOADING 3D MODEL · … (zawsze) | tylko podczas ładowania w SIMPLE; pełny stan w Analyst |
| CONTACT CONFIRMED · … | (usunięte, informacja jest w karcie werdyktu) |
| Karta powitalna: 4 zdania + 1-2-3 | 3 zdania + Quick demo + Try it yourself + „How to read the results · glossary” |
| „?” → słowniczek | „?” → karta powitalna (słowniczek z karty) |
| Róża: SUN az 33° · el 5.3° · horizon … (zawsze) | SIMPLE: ikonka → „Sun: shining on the site / Earth: in view — radio contact”; liczby w Analyst |
| Stopka: pełna lista | Data: NASA LRO LOLA · JPL ephemerides · NASA Moon Trek · Wueller et al. 2026 · Credits |

## Źródła wartości ASSUMPTION (sekcja 7)

Zasada: wartość zmienia się tylko na podstawie oryginalnego dokumentu z działającym URL. Liczby i jednostki sprawdzono w tekście dokumentu.

| Lądownik · parametr | Znalezione | Decyzja |
|---|---|---|
| Nova-C · bateria | Wniosek FCC IM-1 ([266519](https://apps.fcc.gov/els/GetAtt.html?amp=&id=266519&x=)): „Li-ion batteries, with a total energy capacity of 1554 Wh, and an unregulated 28 VDC bus”. ODAR ([265886](https://apps.fcc.gov/els/GetAtt.html?amp=&id=265886&x=)): „three 518 Wh … battery assemblies … 1554 Wh” | **zmienione** 700 Wh (derived) → **1554 Wh (sourced)** |
| Nova-C · obciążenie przetrwania | wyliczane z baterii (~15 h łączności po zachodzie) | **zmienione** 47 → **104 W (derived)** |
| Nova-C · panele (układ) | FCC: „Three fixed body-mounted solar panels” | potwierdzona liczba i mocowanie (note), kierunki nadal **ASSUMPTION** |
| Nova-C · moc paneli | FCC: „a maximum of 788 W”, bez informacji, czy na powierzchni | **bez zmian** (200 W, sourced, powierzchnia), do decyzji zespołu |
| Nova-C · wysokość modelu | FCC: „2.19 m x 2.385 m x 3.938 m” | **zmienione** 4 → 3,94 m |
| Nova-C · obciążenie dzienne | brak | zostaje ASSUMPTION |
| Blue Ghost · panele, obciążenia | brak liczb w źródłach pierwotnych | zostaje ASSUMPTION |
| Griffin · moc, bateria, obciążenia, noc, szerokość | strona Astrobotic: tylko „625 kg”, „1.0 W/kg”; PDF przewodnika (NASA) nie dał się odczytać tekstowo | zostaje ASSUMPTION (Todo) |
| Starship HLS · moc, bateria, obciążenia | brak danych publicznych | zostaje ASSUMPTION |
| Blue Moon Mk2 · moc, bateria, obciążenia | brak danych publicznych | zostaje ASSUMPTION |

## Testy (2026-11-14, build Release, headless)

- xUnit: 54/54 (nowe `VerdictTextTests`, zdanie werdyktu dla każdego przypadku).
- E2E UX, 38 sprawdzeń × 3 silniki:
  - **Chromium** 38/38, **WebKit** 38/38, **Firefox** 37/38.
  - W Firefoksie karta powitalna pokazuje się po 3,44 s przy limicie 3 s. Przyczyną jest start runtime'u .NET WASM w headless Firefoksie.
  - Pierwsza wizyta: Księżyc 0,5–0,8 s, karta powitalna 2,8–2,9 s (Chromium, WebKit).
  - Ręcznie 3 kroki → werdykt: 8,8–11,1 s.
  - Przełączenie SIMPLE ⇄ ANALYST: ~30 ms.
  - Kamera przy agresywnym sterowaniu: min. 325 m nad terenem, Księżyc zawsze w kadrze, maks. 6 000 km, pochylenie ≤ −10°, 0 błędów renderu.
  - Ujęcie po lądowaniu: lądownik zajmuje 31% wysokości kadru.
  - axe-core: 0 naruszeń critical/serious (SIMPLE start, SIMPLE werdykt, ANALYST). Pozostało 1 minor: `landmark-one-main`.
  - Rozdzielczości 1920×1080, 1366×768, 1280×800, 800×900: bez poziomego scrolla.
- Regresja modeli GLB: 49/49.
