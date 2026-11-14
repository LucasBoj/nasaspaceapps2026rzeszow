# ROADMAP — SELENE OPS

**Ostatnia aktualizacja:** 2026-11-14 17:50 CET

> **Zasada aktualizacji:** po każdym zadaniu przenieś ukończone pozycje do **Done** (data, godzina, commit), dopisz nowo odkryte zadania lub problemy do **Todo** albo **Known issues** i dodaj wpis do **AI usage log**. **Nie usuwaj historii z Done.**
> Commit w Done to commit, w którym jest praca. Wpis do roadmapy może trafić do tego samego commita albo do następnego.

**Właściciele:** A = dane/Python · B = model energii/logika · C = frontend/wideo

---

## ✅ Done

- [x] Szkielet Blazor WASM + Cesium: dwa panele (Księżyc / konfiguracja), pasek wyników (na razie zaślepka) — 2026-11-14 10:45 — `323edd4`, `a2f267d` — zespół (Łukasz)
- [x] Naprawa czarnego ekranu: `index.html` ładował `blazor.web.js` zamiast `blazor.webassembly.js` — 2026-11-14 11:35 — `60778e3` — AI: Claude Code
- [x] Scalenie osobnego repo `SeleneFrontend` z głównym repo, z zachowaniem historii — 2026-11-14 11:35 — `747f5bf` — AI: Claude Code
- [x] Poprawki viewera — 2026-11-14 12:20 — `7dc6e0e` — AI: Claude Code. W zakresie:
  - elipsoida Księżyca (wymagała podbicia Cesium 1.116 → 1.120) i poprawne URL-e Cesium, które dawały 404
  - warstwa NASA Moon Trek LRO WAC (działa bez tokena)
  - wczytywanie tokena z `IConfiguration` i plakietka trybu 2D/3D
  - jeden handler kliknięć, prawdziwe `Entity.id`, własna pinezka CUSTOM
  - `disableDepthTestDistance` na pinezkach i etykietach
  - `flyTo` z offsetem 90 km
  - JSON camelCase (`JsonSerializerDefaults.Web`); wcześniej wszystkie pola były puste
  - siatka 65/35, osobny div dla Cesium, `IAsyncDisposable`
- [x] Karty lądowników: własne sylwetki SVG, przełącznik CLPS/HLS, hover/select, obsługa klawiatury, `prefers-reduced-motion`, panel parametrów z Source URL — 2026-11-14 12:20 — `7dc6e0e` — AI: Claude Code
- [x] Auto-przelot kamery po wyborze miejsca (pinezka lub lista), przyciski HUD „SOUTH POLE VIEW” i „ZOOM TO SITE”, hover pinezek — 2026-11-14 12:20 — `7dc6e0e` — AI: Claude Code
- [x] Panel konfiguracji przewija się zamiast ucinać sekcje — 2026-11-14 12:20 — `7dc6e0e` — AI: Claude Code
- [x] Pipeline Python: maska horyzontu z LOLA, efemerydy Skyfield, `illumination/{siteId}.json` dla 9 miejsc + `index.json`, 22 testy — 2026-11-14 12:32 — `780cf1b` — AI: Claude Code. ⚠️ Wyniki dotyczą przybliżonych współrzędnych (patrz Known issues).
- [x] Walidacja liczbowa metody: najlepszy punkt grzbietu Shackleton–de Gerlache wychodzi 89,7% czasu w świetle (2027–28), literatura podaje ~89–92%. Opis w `pipeline/README.md` — 2026-11-14 12:32 — `780cf1b` — AI: Claude Code
- [x] `CLAUDE.md` (kontekst projektu) i `ROADMAP.md` — 2026-11-14 12:45 — `754bafc` — AI: Claude Code
- [x] Parametry lądowników ze źródłami: każda wartość w `landers.json` ma `{value, unit, kind, note, sourceUrl}` (dane od zespołu); usunięte presety CUSTOM — 2026-11-14 13:00 — `f04be25` — dane: zespół, implementacja: AI: Claude Code
- [x] Wybór lądownika tylko do odczytu: tabela SPECS z plakietkami SOURCED/DERIVED/ASSUMPTION, popover ⓘ z notatką i linkiem (obsługa klawiatury), plakietka „DATA: …” na karcie, panel LANDER IMPACT liczony na żywo, zwinięty panel „What-if” z suwakami i „Reset to data”, wybór lądownika ustawia długość misji — 2026-11-14 13:00 — `f04be25` — AI: Claude Code
- [x] Pola liczbowe i daty w sekcjach 03/04 niezależne od kultury przeglądarki (wcześniej 6,5 dnia dawało puste pole) — 2026-11-14 13:00 — `f04be25` — AI: Claude Code
- [x] Prawdziwe miejsca lądowania: 130 kandydatów z Wueller et al. (2026, JGR Planets, doi:10.1029/2025JE009434; dane Zenodo 10.5281/zenodo.17084058, CC-BY-4.0) → `pipeline/data/wueller2026_sites.csv`; wszystkie 9 regionów pokryte (78 kandydatów, fallback siatki niepotrzebny) — 2026-11-14 13:35 — `7578ae8` — AI: Claude Code
- [x] Wybór najlepszego miejsca w każdym regionie (`select_sites.py`): filtr nachylenia z DEM, ocena 2027–28, ranking; `sites.json` z `source`, `candidateId`, `regionCenter`, `alternatives`; `illumination/candidates/*.json` do nakładki; przeliczone `illumination/*.json` — 2026-11-14 13:35 — `7578ae8` — AI: Claude Code. Wyniki np. Malapert 41,5% → 81,9% czasu w świetle, Nobile Rim 2: 0% → 53,8%.
- [x] Walidacja z twierdzeniem NASA o 6,5 dnia ciągłego światła: 9/9 regionów spełnia (test + tabela w `pipeline/README.md`). Uwaga: twierdzenie NASA z 2022 dotyczy dosłownie 5 z 9 regionów; dla 4 dodanych w 2024 to nasze kryterium. Dodatkowo kontrola spójności z wyliczeniami autorów (`SunDays`): 15/18 zgodnych — 2026-11-14 13:35 — `7578ae8` — AI: Claude Code
- [x] Model energii i łączności w C#: `MissionSimulator` (bilans baterii godzina po godzinie, łączność, najdłuższa ciemność, wykrycie wyczerpania baterii, werdykt FEASIBLE / MARGINAL / NOT FEASIBLE z powodem w jednym zdaniu i najsłabszym rodzajem danych, w tym WHAT-IF), `IlluminationRepository` (leniwe wczytywanie i cache) — 2026-11-14 14:25 — `1e7a604` — AI: Claude Code
- [x] Optymalizator okien (`WindowOptimizer`): 9 miejsc × każdy dzień okna, ocena 0–100 (50 × zapas baterii + 30 × łączność + 20 × światło; 0 = niewykonalne), obliczenia w porcjach z paskiem postępu w HUD; przy remisie wygrywa najwcześniejsza data. Czas w WASM: ~2,5 s za pierwszym razem (z wczytaniem danych), ~0,8 s kolejne — 2026-11-14 14:25 — `1e7a604` — AI: Claude Code
- [x] Panel wyników: przycisk RUN ANALYSIS, linia werdyktu, oś czasu SVG (światło / Ziemia / bateria + próg, odczyt pod kursorem), mapa ciepła na canvas (etykiety miesięcy, legenda, BEST WINDOW, klik → miejsce + data + przelot kamery), przyciski „najlepsza data” dla klawiatury, plakietka RESULTS OUTDATED; naprawiona składnia `[class.expanded]`; okno dat przycinane do zakresu danych z komunikatem — 2026-11-14 14:25 — `1e7a604` — AI: Claude Code
- [x] Testy xUnit `SeleneFrontend.Tests` (14): ręcznie policzony przypadek SoC, wyczerpanie baterii w ciemności i w dzień, reguły MARGINAL, wzór oceny, rozstrzyganie remisów — 2026-11-14 14:25 — `1e7a604` — AI: Claude Code
- [x] Usunięte zagnieżdżone `SeleneFrontend/.git` (historia była już scalona w `747f5bf`) — 2026-11-14 14:25 — `1e7a604` — AI: Claude Code
- [x] Kontrakt danych: nowe pole `earthAzCentiDeg` w `illumination/{siteId}.json` (addytywne, opisane w CLAUDE.md), dane przeliczone — 2026-11-14 14:40 — `a428241` — AI: Claude Code
- [x] Pierwsze wrażenie: ekran startowy (pierwsza wizyta, ≤ 2,5 s, pomijalny, linie zależne od prawdziwych pobrań), przelot kamery z kosmosu nad biegun, scenariusz polecany (Nobile Rim 1 + Nova-C) liczony za ekranem startowym, karta powitalna, 3-krokowy stepper — 2026-11-14 15:10 — `2885f11` — AI: Claude Code
- [x] QUICK DEMO (górny pasek + karta powitalna): scenariusz → analiza → przelot → pulsowanie BEST WINDOW; analizę można przerwać i uruchomić ponownie (wcześniej klik w trakcie analizy był ignorowany) — 2026-11-14 15:10 — `2885f11` — AI: Claude Code
- [x] Porównanie do 3 regionów (zakładka COMPARE): werdykt, mini oś czasu, metryki, zwycięzca — 2026-11-14 15:10 — `2885f11` — AI: Claude Code
- [x] Róża Słońca i Ziemi w HUD (horyzont LOLA, azymuty i wysokości z danych, śledzi kursor na osi czasu); nakładka kandydatów CANDIDATES; pulsujący pierścień i linia do etykiety; pinezki przypięte do terenu 3D; słowniczek pojęć i ściąga „?” — 2026-11-14 15:10 — `2885f11` — AI: Claude Code
- [x] Dopracowanie i odporność: tokeny odstępów i ruchu, stany hover/active/focus, animowane liczby, skeletony, neutralna plakietka „2D MOSAIC MODE”, statusy na żywo, baner błędu ładowania, stopka z atrybucją i zastrzeżeniem „Not affiliated…”, favicon, meta + Open Graph + `og.png`; źródło miejsca w sekcji 01; zsynchronizowana lista miejsc; usunięte pole „Time Step” — 2026-11-14 15:10 — `2885f11` — AI: Claude Code
- [x] Responsywność: bez `min-width`, układ kolumnowy < 1024 px, 1366×768 bez ucinania; sprawdzone w Chromium, Firefox i WebKit (23/23 w każdej) — 2026-11-14 15:10 — `2885f11` — AI: Claude Code
- [x] Wydajność i deploy: brakujący `importmap` w `index.html` (bez niego aplikacja nie startowała na statycznym hostingu), Cesium `defer` + `preconnect`, `InvariantGlobalization`, token wykluczony z publikacji. Release + Brotli: wynik po 2,3 s (szybkie łącze) / 3,7 s (20 Mbit/s) — 2026-11-14 15:10 — `2885f11` — AI: Claude Code
- [x] Animacja lądowania (P2 „Animacja lądowania”): osobny moduł `landingAnimation.js`, własny schematyczny model z prymitywów Cesium (CLPS / HLS), opadanie z 15 km z krzywą hamowania, kamera pościgowa i orbita, pierścień pyłu i błysk; HUD „SIMULATED DESCENT” z ALT, V/S, RANGE i fazami z tej samej krzywej; przycisk CONFIRM LANDING SITE i QUICK DEMO z analizą liczoną równolegle; pomijanie (klik, SKIP, Esc); flaga `features.landingAnimation` + przełącznik w HUD; tryb ograniczonego ruchu; wysokość z terenu 3D z fallbackiem na elipsoidę. Testy: Chromium 3D/2D, Firefox, WebKit — 22/22 — 2026-11-14 16:25 — `c726269` — AI: Claude Code
- [x] Model 3D Nova-C (GLB, tylko dla `nova_c`): skalowanie do 4 m z bounding boxa, orientacja glTF Y-up → pion miejsca (nogi w dół, sprawdzone), ładowanie w tle i pula modeli, fallback na model z prymitywów; lądownik i znacznik podążają za faktycznie wyrenderowanym terenem; światło sceny = Słońce z efemeryd (az/el dla miejsca i chwili), miękkie cienie, światło wypełniające i otoczenia; atrybucja w stopce. Testy GLB 11/11 w Chromium (3D i 2D), Firefox, WebKit — 2026-11-14 17:35 — `345d6f8` — model: zespół (Meshy AI + Microsoft TRELLIS.2), integracja: AI: Claude Code
- [x] Przygotowanie wdrożenia na Mikrus: `deploy/nginx-selene.conf` (stable :80 + preview :8080, MIME, gzip_static, cache, nagłówki bezpieczeństwa), `deploy/server-setup.sh` (idempotentny), `deploy/deploy.sh` (publish, kontrole, opcjonalny token z ENV, absolutny `og:image`, kompresja GLB, rsync, curl; tryb `--build-only`), `deploy/README.md`; kompresja i trimming włączone jawnie i sprawdzone. Lokalnie: build Release 39 MB, testy na serwerze zachowującym się jak nginx 23/23 + 11/11 — 2026-11-14 18:25 — `83a84bc` — AI: Claude Code (rsync/ssh nie były uruchamiane)
- [x] Stałe panele i kąt padania Słońca (cosinus Lamberta): geometria paneli w `landers.json → panels` (opis od zespołu, ASSUMPTION), `SolarGeometry` z normalizacją K (opublikowana moc = moc przy najlepszym kierunku Słońca), kurs lądowania AUTO (maksimum energii misji) + what-if kurs ręczny i idealne śledzenie; nowe metryki (energia wyprodukowana / idealna / zużyta, sprawność geometryczna, zakres elewacji Słońca i Ziemi, najdłuższy okres światła, najdłuższa przerwa w łączności), maksymalna wykonalna długość pobytu, powód werdyktu wskazujący geometrię paneli. Na prawdziwych danych: stałe panele utrzymują misję CLPS na biegunie ~7–8 dni (Nobile Rim 1, Nova-C: 7 d → 133/239 dni lądowania FEASIBLE, 10 d → 0), więc QUICK DEMO używa 7 dni. Testy xUnit 28/28 — 2026-11-14 17:34 CET — `94e1d28` — AI: Claude Code (UI w kolejnym kroku)
- [x] Automatyczne wdrażanie przez GitHub Actions (`.github/workflows/deploy.yml`): push `main` → preview, push `stable` → stable, ręczne uruchomienie z wyborem celu; `concurrency` per cel; testy xUnit blokują wdrożenie; token Cesium z sekretu tylko do publikacji (maskowany); klucz SSH + `ssh-keyscan` (opcjonalnie przypięty `MIKRUS_KNOWN_HOSTS`); rsync; smoke test; podsumowanie przebiegu. `deploy.sh` działa w CI (`--upload-only`, zmienne `MIKRUS_*`, brak pytań, kody wyjścia, adnotacje), nowe `smoke-test.sh` i `create-deploy-user.sh` (użytkownik `deploy` bez hasła, klucz z `restrict`), `.gitattributes` (LF dla `*.sh`), sekcja „Automatyczne wdrażanie” w `deploy/README.md`; gałąź `stable` = `main`. Sprawdzone lokalnie: build z atrapą tokena (token nie trafia do logu), smoke test na lokalnym serwerze (OK, inna wersja, brak serwera), ścieżka uploadu na atrapie rsync, logika `create-deploy-user.sh` na atrapach, skrypty z YAML na symulowanych wejściach; testy 14/14 — 2026-11-14 17:01 — `21ca97d`, poprawka składni workflow `32b7e3a` (17:13) — AI: Claude Code (workflow nie był jeszcze uruchomiony z sekretami na prawdziwym serwerze)
- [x] Ocena optymalizatora ze składnikiem nadwyżki energii: `40·minSoC + 25·łączność + 15·światło + 20·clamp(wyprodukowana/zużyta − 1, 0, 1)`; `HeatmapResult` niesie metryki na komórkę (światło, łączność, bilans energii, min. SoC, kurs, sprawność paneli, czas przetrwania). Nova-C 7 d: najlepsze oceny regionów 79–83 zamiast 100 wszędzie. Testy 31/31 — 2026-11-14 17:40 CET — `4ccf12e` — AI: Claude Code
- [x] UI mocy i łączności: kafle POWER & COMMS (zakres elewacji Słońca, godziny światła i najdłuższe okresy światła/ciemności, sprawność paneli vs idealne śledzenie + kurs, energia wyprodukowana/zużyta, łączność z Ziemią + najdłuższa przerwa + elewacja Ziemi, MAX STAY przy tym samym kursie); oś czasu pokazuje rzeczywistą moc paneli na tle dostępnego światła i linię elewacji Słońca; wiersz PANELS w SPECS z własnym schematem; what-if FIXED / IDEAL i ręczny kurs; róża pokazuje normalne paneli, przód lądownika i bieżącą moc; mapa ciepła z trybami SCORE / LIGHT / ENERGY / EARTH i bogatszym tooltipem; gdy nic nie jest wykonalne, pokazuje lądowanie, które trwa najdłużej; słowniczek (kąt padania, kurs, sprawność paneli, elewacja Słońca, przerwa w łączności, max stay). Sprawdzone w przeglądarce (Chromium): QUICK DEMO, what-if, 14 d → NOT FEASIBLE z powodem, COMPARE, brak błędów konsoli — 2026-11-14 17:45 CET — `385b224` — AI: Claude Code (Firefox/WebKit nie sprawdzane)
- [x] Walidacja efemeryd z JPL Horizons: `pipeline/validate_horizons.py`, 9 miejsc × 24 epoki 2027–28, Słońce i Ziemia; Horizons podaje współrzędne miejsca w układzie Mean-Earth (jak nasz `MOON_ME_DE421`), różnica ~0,04″ (pipeline) i ≤ 0,005° (pliki JSON, zaokrąglenie 0,01°); snapshoty odpowiedzi w `pipeline/data/horizons/`, test offline `tests/test_horizons.py`, tabela w `pipeline/README.md`. Pytest 46 passed, 1 skipped (DEM) — 2026-11-14 17:30 CET — `a1e76c6` — AI: Claude Code (osobna sesja)
- [x] Oświetlenie sceny z efemeryd (P2 „Animacja cieni”) — było zrobione w `345d6f8` (`setSunLight`); usunięta martwa zaślepka `setSunDirection`, README frontendu poprawione; `CLAUDE.md`: sekcja o modelu energii i nowe pułapki — 2026-11-14 17:50 CET — commit z tym wpisem — AI: Claude Code

## 🔄 In progress

_(nic przypisanego — wpisz się tutaj, gdy zaczynasz zadanie)_

## 📋 Todo — według priorytetu

P0 = bez tego nie ma zgłoszenia · P1 = mocno podnosi ocenę · P2 = efekt wow, jeśli starczy czasu

### P0

- [ ] **Demo działa offline** — C
  - Gotowe, gdy: aplikacja z laptopa bez internetu ładuje się z lokalnym Cesium (nie z CDN), lokalnymi czcionkami i podręcznym zestawem kafli Moon Trek dla bieguna (albo ma fallback bez zdjęć), co jest sprawdzone przy wyłączonym Wi-Fi.
- [ ] **Uruchomić wdrożenie na Mikrusie** (skrypty i CI gotowe: `deploy/README.md` → „Automatyczne wdrażanie”) — C
  - Gotowe, gdy: `server-setup.sh` i `create-deploy-user.sh` uruchomione; port/subdomena z panelu wpisane w `nginx-selene.conf`; w GitHubie ustawione sekrety `MIKRUS_SSH_KEY`, `MIKRUS_HOST`, `MIKRUS_PORT`, `MIKRUS_USER=deploy` i zmienne `PREVIEW_URL`, `STABLE_URL`; przebieg „Deploy” dla preview ma zielony smoke test → `git push origin main:stable` → zielony smoke test stable; strona działa z telefonu po LTE; czasy ładowania zapisane.
  - Decyzja: token 3D w produkcji (token z ograniczeniem URL jako sekret `CESIUM_ION_TOKEN`) czy tryb 2D.
  - Opcjonalnie: sekret `MIKRUS_KNOWN_HOSTS` (przypięty klucz hosta) i ochrona gałęzi `stable` (bez usuwania; force-push zostawić, bo służy do wycofania).
- [ ] **Lustro na GitHub Pages** — C
  - Gotowe, gdy: `<base href>` ustawione na podścieżkę repo i adres działa.
- [ ] **Strona projektu na spaceappschallenge.org**: opis odhaczający cele wyzwania, lista danych z linkami, deklaracja użycia AI (z sekcji poniżej), link do repo i demo — wszyscy (prowadzi C)
  - Gotowe, gdy: projekt jest wysłany przed terminem, a wszystkie pola są po angielsku.
- [ ] **30-sekundowe wideo demo** (albo maksymalnie 7 slajdów) — C
  - Gotowe, gdy: wideo ≤ 30 s z angielskimi napisami jest podpięte do zgłoszenia.

### P1

- [ ] **Optymalizacja `nova_c.glb` (13,3 MB → cel < 2 MB)** — gltf-transform: `meshopt` (lub `draco`), uproszczenie siatki (288 tys. trójkątów → ~30–50 tys.), tekstury WebP 1024 px; dodać dekoder meshopt do Cesium (obsługuje `EXT_meshopt_compression`) i sprawdzić, że skala/orientacja się nie zmieniły — A/C
  - Gotowe, gdy: plik < 2 MB, test GLB przechodzi, a pierwsze lądowanie w Firefoksie mieści się w 6 s. Uwaga: publikacja Blazor nie kompresuje `.glb` (13,9 MB transferu).
- [ ] **Licencja obrazu koncepcyjnego z Meshy** — potwierdzić plan konta (darmowy = CC BY 4.0, wymaga atrybucji — jest w stopce) i że obraz nie odwzorowuje grafik producenta — zespół

- [ ] **Źródła geometrii paneli i obciążeń dziennych** — przy stałych panelach to one decydują o wykonalności. Potrzebne: które ściany Nova-C mają panele i podział mocy, rozmieszczenie paneli Blue Ghost, nachylenie panelu Griffina, obciążenia dzienne CLPS (dziś ASSUMPTION, 70–75% mocy szczytowej) oraz źródło dla „~450 W nominalnie / 650 W szczytowo” Blue Ghost (w danych 400 W SOURCED) — zespół
  - Gotowe, gdy: wartości w `landers.json → panels` / `dayLoadW` mają `sourceUrl` albo jawne uzasadnienie.
- [ ] **Ocena optymalizatora — sprawdzić Blue Ghost i COMPARE** — z nadwyżką energii (`4ccf12e`) Nova-C ma oceny 79–83 zamiast 100, ale w COMPARE trzy najlepsze regiony mają po zaokrągleniu po 83, a w trybie IDEAL wszystkie 87. Sprawdzić Blue Ghost i ewentualnie pokazywać ocenę z jednym miejscem po przecinku albo dodać rozstrzygnięcie (np. MAX STAY) — B
  - Gotowe, gdy: dla Nova-C i Blue Ghost najlepsze okna różnią się widocznie oceną.
- [ ] **Kurs w animacji lądowania** — lądownik w `landingAnimation.js` ląduje z kursem 0°; mógłby lądować z kursem z analizy (AUTO), żeby panele w 3D patrzyły tam, gdzie liczy symulacja. Wymaga ustalenia, gdzie jest „przód” modelu GLB — C

- [ ] **Wykres „nasze vs NASA”**: porównanie naszego oświetlenia z opublikowanymi mapami oświetlenia LOLA (PGDA / LROC) — A
  - Gotowe, gdy: dla kilku punktów jest wykres lub tabela z różnicą i opisem źródeł. Walidacja liczbowa grzbietu jest już zrobiona.
- [ ] **Pitch na lokalne jury**: slajdy, scenariusz demo offline, odpowiedzi na trudne pytania — wszyscy
  - Gotowe, gdy: jest próba generalna na czas z demo uruchomionym bez internetu.
- [ ] **Hostować Cesium u siebie (z Brotli)** — dziś ~1,45 MB z CDN; potrzebne też do trybu offline i może zejść poniżej 3 s na łączu 20 Mbit/s — C
- [ ] **Zmierzyć fps animacji lądowania na prawdziwym laptopie** (Chrome/Firefox/Safari, z GPU) — w testach headless z programowym WebGL było 11–44 fps i model się upraszczał; cel ≥ 30 fps bez uproszczeń — C
- [ ] **Sprawdzić prawdziwego Firefoksa** — w headless Firefox (programowe WebGL) mapa ciepła po QUICK DEMO liczy się ~10 s; werdykt pojawia się od razu — C
- [ ] **Alternatywy miejsca w sekcji 01** (`alternatives` z `sites.json`) do przełączania jednym kliknięciem — C
- [ ] **Warstwa Moon Trek w odwzorowaniu polarnym** (South Polar Stereographic) zamiast mozaiki walcowej, która przy biegunie jest rozmazana — C
  - Gotowe, gdy: zbliżenie na miejsca przy -85…-89° jest ostre.

### P2

- [ ] Raport misji do druku/PDF — C
- [ ] Dane partnera: JAXA Kaguya (SELENE) DEM jako druga warstwa terenu — A

## 💡 Ideas / Backlog

- Automatyczny wybór najlepiej oświetlonego punktu w każdym regionie (15×15 km) wraz z mapą oświetlenia regionu.
- Łączność z konkretnymi stacjami DSN (Goldstone / Madryt / Canberra), a nie tylko „Ziemia nad horyzontem”.
- Teren 3D Cesium Moon po dodaniu assetu do konta (sprawdzić zgłaszane ~2,5 km przesunięcia względem LOLA).
- Wyższa wysokość obserwatora (np. maszt 10 m) jako parametr do porównania „co daje maszt”.

## 🤖 AI usage log (do deklaracji w zgłoszeniu Space Apps)

- 2026-11-14 — _do uzupełnienia przez zespół_ — narzędzie użyte przy szkielecie aplikacji (`323edd4`, `a2f267d`)
- 2026-11-14 — Claude Code (Claude Opus 5.5) — diagnoza i naprawa czarnego ekranu, scalenie repozytoriów
- 2026-11-14 — Claude Code (Claude Opus 5.5) — research regulaminu Space Apps 2026 (kryteria, terminy) i zwycięzców 2025
- 2026-11-14 — Claude Code (Claude Opus 5.5) — poprawki viewera Cesium, karty lądowników z sylwetkami SVG, nawigacja kamery, automatyczne testy w przeglądarce
- 2026-11-14 — Claude Code (Claude Opus 5.5) — pipeline Python (horyzont LOLA, efemerydy Skyfield), testy, walidacja z literaturą
- 2026-11-14 — Claude Code (Claude Opus 5.5) — `CLAUDE.md` i `ROADMAP.md` na podstawie przeglądu kodu
- 2026-11-14 — Claude Code (Claude Opus 5.5) — wybór lądownika tylko do odczytu (SPECS z pochodzeniem danych, LANDER IMPACT, what-if), weryfikacja dostępności linków źródłowych, testy w przeglądarce
- 2026-11-14 — Claude Code (Claude Opus 5.5) — odnalezienie danych Wueller et al. (2026) w Zenodo, import kandydatów, wybór miejsc w regionach, walidacja z twierdzeniem NASA i z wynikami autorów, testy
- 2026-11-14 — Claude Code (Claude Opus 5.5) — rdzeń analizy: symulator misji, optymalizator okien, panel wyników (werdykt, oś czasu, mapa ciepła), testy xUnit, testy w przeglądarce i optymalizacja wydajności
- 2026-11-14 — Claude Code (Claude Opus 5.5) — przygotowanie wdrożenia (nginx, skrypty setup/deploy, README), weryfikacja buildu Release z trimmingiem na serwerze zachowującym się jak nginx
- 2026-11-14 — Claude Code (Claude Opus 5.5) — model stałych paneli z kątem padania (Lambert), kurs AUTO, metryki mocy i łączności, maksymalna długość pobytu, testy xUnit, analiza wykonalności na prawdziwych danych
- 2026-11-14 — Claude Code (Claude Opus 5.5) — ocena z nadwyżką energii, kafle POWER & COMMS, tryby mapy ciepła, panele w SPECS / what-if / róży, testy w przeglądarce
- 2026-11-14 — Claude Code (Claude Opus 5.5, osobna sesja) — walidacja efemeryd z JPL Horizons (skrypt, snapshoty, test offline, ustalenie układu Mean-Earth)
- 2026-11-14 — Claude Code (Claude Opus 5.5) — automatyczne wdrażanie: workflow GitHub Actions, przeróbka `deploy.sh` pod CI, smoke test, skrypt użytkownika `deploy`, instrukcja po polsku, lokalne testy skryptów (w tym wykrycie błędów: kodowanie Pythona na Windows, CRLF w skryptach, nazwa skryptu Blazor z importmapy)
- 2026-11-14 — Meshy AI — obraz koncepcyjny lądownika Nova-C (wygenerowany przez zespół)
- 2026-11-14 — Microsoft TRELLIS.2 (MIT) — model 3D `nova_c.glb` z obrazu koncepcyjnego (wygenerowany przez zespół)
- 2026-11-14 — Claude Code (Claude Opus 5.5) — integracja modelu GLB (skala, orientacja, ładowanie w tle, fallback, podążanie za terenem), oświetlenie sceny z efemeryd, testy w 3 przeglądarkach
- 2026-11-14 — Claude Code (Claude Opus 5.5) — animacja lądowania (moduł, HUD, integracja z analizą), diagnoza błędu renderowania Cesium (lot kamery vs `lookAt`), testy w 4 konfiguracjach
- 2026-11-14 — Claude Code (Claude Opus 5.5) — pierwsze wrażenie i dopracowanie frontu: ekran startowy, QUICK DEMO, porównanie miejsc, róża Słońca/Ziemi, nakładka kandydatów, słowniczek, responsywność, metadane i `og.png`, poprawki wdrożeniowe, pomiary wydajności, testy w 3 przeglądarkach

## ⚠️ Known issues / ryzyka

- ~~Przybliżone współrzędne miejsc~~ — rozwiązane w `7578ae8` (miejsca z Wueller et al. 2026).
- **Model produkcji energii** — od `94e1d28` uwzględnia kąt padania Słońca na stałe panele (Lambert) i kurs lądowania. Nadal nie uwzględnia temperatury, sprawności ładowania i rozładowania, degradacji ani nachylenia terenu (panel liczony względem lokalnej poziomicy; kandydaci mają ≤ 4°). Geometria paneli to opis zespołu z założeniami (które ściany, podział mocy, nachylenie panelu Griffina) — potrzebne źródła. HLS liczone jako idealne śledzenie (górna granica).
- **Normalizacja mocy paneli (interpretacja)** — `solarArrayW` traktujemy jako moc przy najlepszym kierunku Słońca dla danej geometrii (el 0–10°), a nie sumę mocy znamionowych ścian. Gdyby to była suma znamionowa, wyniki byłyby jeszcze gorsze (Nova-C ×0,63, Blue Ghost ×0,39).
- **Blue Ghost: moc paneli** — zespół podał „nominalnie ~450 W, szczytowo 650 W”, w `landers.json` jest 400 W (SOURCED, fireflyspace.com). Nie zmienione bez `sourceUrl`.
- **Obciążenia dzienne CLPS to założenia (ASSUMPTION, 70–75% mocy szczytowej)** — przy stałych panelach decydują o wykonalności; warto znaleźć źródła.
- **Lądowanie zawsze o 00:00 UTC** — optymalizator sprawdza jedną godzinę lądowania na dzień i nie modeluje ograniczeń trajektorii ani startu.
- **Okno w sekcji 03 to daty lądowania** — misja musi zmieścić się w danych, więc ostatni możliwy dzień lądowania to koniec danych minus długość misji.
- **Mapa ciepła to canvas** — czytnik ekranu dostaje tylko opis; obsługa z klawiatury przez przyciski „najlepsza data” pod mapą. Tryby LIGHT / ENERGY / EARTH zmieniają tylko kolory; BEST WINDOW zawsze według oceny.
- **Stałe panele skracają misje CLPS na biegunie do ~7–8 dni** — Słońce przesuwa się o ~12° azymutu na dobę i wychodzi z pola widzenia paneli. 14-dniowe misje (źródłowy czas życia) wychodzą NOT FEASIBLE we wszystkich regionach; QUICK DEMO używa 7 dni. Na pitchu to mocna historia (IM-1 działał ~7 dni), ale trzeba ją podać wprost, zanim jury zobaczy 14 d → NOT FEASIBLE.
- **Kurs AUTO maksymalizuje energię, nie przetrwanie** — dla misji niewykonalnych inny kurs mógłby przedłużyć działanie. MAX STAY liczony jest przy tym samym kursie co pokazana misja.
- **Elewacja Słońca może być ujemna przy pełnej tarczy** — na wysokich grzbietach horyzont terenu leży poniżej płaszczyzny poziomej (np. −4,1°); to poprawne, ale warto umieć to wyjaśnić.
- **Dwie definicje „światła”** — `index.json → pctSunlit` liczy każde światło (`sunFraction > 0`), a wybór miejsc i walidacja używają `sunFraction ≥ 50` (`sunPct` w `candidates/*.json`). W UI i na pitchu zawsze podawać, którą liczbę pokazujemy.
- **Optymizm DEM 120 m** — rozdzielczość 120 m wygładza drobną rzeźbę, co zawyża oświetlenie i zaniża nachylenia (wszyscy kandydaci ≤ 4°). Opcja P2: przeliczyć wybrane miejsca na DEM 20–60 m.
- **Twierdzenie NASA o 6,5 dnia** pochodzi z komunikatu z 2022 (13 regionów) i dosłownie obejmuje tylko 5 z naszych 9 regionów. Na pitchu nie mówić „NASA potwierdza dla wszystkich 9”.
- **Dane lądowników HLS to głównie założenia** — moc paneli, bateria i obciążenia Starship HLS i Blue Moon nie są publikowane (ASSUMPTION). Wyniki dla HLS trzeba na pitchu przedstawiać jako szacunek klasy, nie dane producenta.
- **Dwa linki źródłowe nie odpowiadają automatom** (sprawdzone 2026-11-14 ~12:50): `nasaspaceflight.com` (Griffin, czas misji) zwraca 403 dla zapytań automatycznych, a cały `collectspace.com` (Blue Moon: czas misji, załoga, panele) zwracał 522 (awaria serwera). Sprawdzić ręcznie w przeglądarce przed zgłoszeniem; `nasaspaceflight.com/tag/viper/` to strona tagu, czyli słabe źródło, więc warto podmienić na konkretny artykuł.
- **Demo zależy od internetu** — Cesium z CDN, Google Fonts i kafle Moon Trek są online. Łamie to zasadę „offline”. → P0.
- **Teren 3D tylko lokalnie** — asset Moon Terrain jest już na koncie i lokalnie działa „3D TERRAIN”. Token jest tylko w `appsettings.Development.json` (gitignored, wykluczony z publikacji), więc wdrożona wersja pokaże „2D MOSAIC MODE”, dopóki nie zostanie podjęta decyzja o tokenie z ograniczeniem do domeny.
- **Przybliżenie liniowe tarczy Słońca** — maksymalny błąd to 5,8 pkt proc. przy częściowym zakryciu. Statystyka „jakiekolwiek światło” nie jest dotknięta.
- **Uproszczenia modelu** — Księżyc jako sfera R = 1737,4 km, horyzont do 150 km, DEM 120 m/px, okres 2027–28 (nie pełny cykl 18,6 roku). „Ziemia widoczna” to nie to samo co kontakt ze stacją DSN.
- ~~Zagnieżdżone `SeleneFrontend/.git`~~ — usunięte w `1e7a604`.
- ~~Atrybucja ukryta~~ — stopka ze źródłami i zastrzeżeniem dodana w `2885f11`.
- ~~Drobne błędy UI~~ — lista miejsc, favicon i statusy naprawione w `2885f11`; `[class.expanded]` w `1e7a604`.
- **Czas ładowania** — wynik po 3,7 s na łączu 20 Mbit/s (cel ≤ 3 s spełniony tylko na szybkim łączu); ograniczeniem jest transfer runtime'u .NET (~2 MB) i Cesium (~1,45 MB).
- **Zgłaszane przesunięcie terenu Cesium Moon (~2,5 km względem LOLA, niepotwierdzone)** — pinezki są przypięte do terenu 3D, a horyzonty i wyniki liczone są z LOLA, więc wizualizacja może się minimalnie różnić od obliczeń.
- **Animacja lądowania jest ilustracyjna** — telemetria (ALT, V/S, RANGE) wynika z krzywej animacji przeskalowanej do ~10 minut opadania, a nie z dynamiki lotu; model jest schematyczny i ~10× powiększony. Na pitchu mówić „simulated, illustrative” (tak jak plakietka).
- **Teren Cesium Moon leży wysoko nad elipsoidą** — w Nobile Rim 1 próbkowanie daje +6,76 km. Lądownik siada na terenie 3D, ale obliczenia oświetlenia nadal pochodzą z LOLA (spójne z `pipeline/`).
- **Firefox headless wolniejszy przy lądowaniu** — w testach całość 8 s (cel 5–6 s), tryb ograniczonego ruchu 3,2 s; Chromium/WebKit mieszczą się w czasie. Prawdopodobnie programowe WebGL w trybie headless; sprawdzić w prawdziwym Firefoksie (zadanie fps w P1).
- **Teren Cesium Moon dociąga szczegóły z opóźnieniem** — powierzchnia w miejscu lądowania potrafi przeskoczyć o ~68 m ok. 2 s po przyziemieniu; znacznik podąża za nią przez 8 s (pętla „settle”).
- **Oświetlenie bieguna jest ciemne z natury** — Słońce na kilku stopniach (często poniżej lokalnego poziomu) daje bardzo ukośne światło; dodane jednolite światło otoczenia (harmoniki sferyczne) jest uproszczeniem wizualnym, nie modelem fizycznym.
- **Mikrus: port 80 po IPv4 może nie trafiać na serwer** — publiczny IPv4 jest współdzielony; adres strony (subdomena/port z panelu) trzeba ustalić przy pierwszym wdrożeniu i wpisać w `nginx-selene.conf` oraz `STABLE_URL` (instrukcja w `deploy/README.md`).
- **Pierwsze lądowanie na wolnym łączu może użyć modelu schematycznego** — `nova_c.glb` waży 8,6 MB po gzip; jeśli nie zdąży się pobrać w tle, lądowanie wraca do modelu z prymitywów (zaplanowane: optymalizacja GLB, P1).
- **Brotli na serwerze nieaktywne** — standardowy nginx z Debiana serwuje tylko `.gz` (`gzip_static`); moduł brotli (`libnginx-mod-http-brotli-static`) dałby ~20% mniej transferu — opcjonalnie.
- **`InvariantTimezone` wymaga workloadu `wasm-tools`** — nie włączone; opcjonalna dalsza oszczędność rozmiaru.
- **GitHub Pages** — `<base href="/">` nie zadziała pod podścieżką repo.
- **Wysyłka na serwer w CI jeszcze niesprawdzona** — przebieg #2 (`2b67c21`, 1 min) jest zielony: testy 14/14 i build na `ubuntu-latest` przeszły, kroki SSH/rsync/smoke test pominięte, bo nie ma sekretów; sprawdzone są też adnotacje. Ścieżka wysyłki była testowana tylko lokalnie (atrapy rsync/ssh, lokalny serwer HTTP). Pierwszy przebieg (`a69be83`) GitHub odrzucił w całości (`cancelled()` w `env:` jest niedozwolone, działa tylko w `if:`) — poprawione w `32b7e3a`; przed kolejnymi zmianami w workflow warto przepuścić go przez `actionlint`. GitHub zapowiada przejście `ubuntu-latest` na Ubuntu 26 (od 2026-10-19); jeśli zabraknie `rsync`, krok SSH go doinstaluje. Do czasu ustawienia `MIKRUS_SSH_KEY` każdy push na `main`/`stable` tylko testuje i buduje (ostrzeżenie „Deploy skipped”). `create-deploy-user.sh` nie był uruchomiony na Debianie jako root.
- **Każdy push na `main` wdraża preview** — także commity z samym `ROADMAP.md` (~2–3 min Actions, darmowe dla publicznego repo). Nowszy push anuluje starszy przebieg tego samego celu.
- **`ssh-keyscan` ufa kluczowi hosta przy każdym wdrożeniu** — dopóki nie ma sekretu `MIKRUS_KNOWN_HOSTS`, podszycie się pod serwer w trakcie przebiegu jest teoretycznie możliwe.
- **Ręczne wdrożenie z Windows niemożliwe bez `rsync`** — Git Bash go nie ma; lokalnie działa tylko `--build-only`, wdrażać przez CI (albo WSL).
- **Stare kopie repo mogą mieć `*.sh` z CRLF** — `.gitattributes` działa dopiero po odświeżeniu plików (`rm deploy/*.sh && git checkout -- deploy/`); inaczej `server-setup.sh` wysłany przez `scp` nie zadziała na serwerze.
- **Logi Actions są publiczne** (publiczne repo) — sekrety są maskowane; nie dodawać `set -x` ani wypisywania zmiennych w krokach z sekretami.
