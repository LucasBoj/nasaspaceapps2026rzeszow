# ROADMAP — SELENE OPS

**Ostatnia aktualizacja:** 2026-11-14 12:45 CET

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
- [x] `CLAUDE.md` (kontekst projektu) i `ROADMAP.md` — 2026-11-14 12:45 — commit „Add CLAUDE.md and ROADMAP.md” — AI: Claude Code

## 🔄 In progress

_(nic przypisanego — wpisz się tutaj, gdy zaczynasz zadanie)_

## 📋 Todo — według priorytetu

P0 = bez tego nie ma zgłoszenia · P1 = mocno podnosi ocenę · P2 = efekt wow, jeśli starczy czasu

### P0

- [ ] **Weryfikacja współrzędnych 9 regionów Artemis III ze źródła NASA i uzupełnienie pola `source`** — A
  - Gotowe, gdy: każde miejsce w `sites.json` ma współrzędne ze źródłem NASA (URL) i `approximate` zgodne z prawdą, a pipeline jest przeliczony ponownie.
  - Uwaga: obecne punkty leżą na zboczach i w kraterach. Opcja: w każdym regionie automatycznie wybierać najlepiej oświetlony punkt i opisać to jako metodę.
- [ ] **Parametry lądowników ze źródłami** (moc paneli, bateria, obciążenie, maksymalny czas ciemności) dla presetów CLPS i HLS — B
  - Gotowe, gdy: każdy preset ma liczby z publicznego źródła (`sourceUrl`), a tam, gdzie danych brak, wartość jest jawnie oznaczona jako założenie (ASSUMPTION) z uzasadnieniem. Teraz wszystkie mają 0.
- [ ] **Model energii i łączności w C#**: bilans baterii godzina po godzinie z `illumination/{siteId}.json` i parametrów lądownika — B
  - Gotowe, gdy: serwis liczy dla wybranego okna serię stanu baterii (SoC) i łączności, zwraca werdykt (wykonalne/niewykonalne + powód) oraz ma testy jednostkowe z ręcznie policzonym przypadkiem.
  - Uwaga: frontend nie czyta jeszcze plików `illumination/*.json`.
- [ ] **Panel wyników**: oś czasu (światło / łączność / bateria) i werdykt w jednym zdaniu; podpięcie przycisku „RUN ANALYSIS” — C
  - Gotowe, gdy: po kliknięciu „RUN ANALYSIS” pasek wyników pokazuje 3 ścieżki dla wybranego okna i jedno zdanie werdyktu. Przy okazji naprawić składnię `[class.expanded]` w `ResultsStrip.razor` (to Angular, nie Blazor).
- [ ] **Mapa ciepła „miejsce × data lądowania”** z oceną wykonalności (optymalizator) — B + C
  - Gotowe, gdy: dla 9 miejsc i każdego dnia okna widać kolor oceny, a kliknięcie komórki ustawia miejsce i datę.
- [ ] **Demo działa offline** — C
  - Gotowe, gdy: aplikacja z laptopa bez internetu ładuje się z lokalnym Cesium (nie z CDN), lokalnymi czcionkami i podręcznym zestawem kafli Moon Trek dla bieguna (albo ma fallback bez zdjęć), co jest sprawdzone przy wyłączonym Wi-Fi.
- [ ] **Atrybucja danych w stopce** (NASA/JPL Moon Trek, LRO LOLA, Cesium ion, NAIF) — C
  - Gotowe, gdy: stopka pokazuje źródła. Teraz `creditContainer` Cesium jest ukryty, a stopki nie ma; wymagają tego zasady Cesium ion i dobre praktyki NASA.
- [ ] **Deploy na Mikrus + lustro GitHub Pages**, sprawdzone po IPv4 z telefonu — C
  - Gotowe, gdy: oba adresy działają z telefonu na danych komórkowych. Na GitHub Pages trzeba zmienić `<base href>` na podścieżkę repo i zdecydować o tokenie (wersja produkcyjna go nie ma).
- [ ] **Strona projektu na spaceappschallenge.org**: opis odhaczający cele wyzwania, lista danych z linkami, deklaracja użycia AI (z sekcji poniżej), link do repo i demo — wszyscy (prowadzi C)
  - Gotowe, gdy: projekt jest wysłany przed terminem, a wszystkie pola są po angielsku.
- [ ] **30-sekundowe wideo demo** (albo maksymalnie 7 slajdów) — C
  - Gotowe, gdy: wideo ≤ 30 s z angielskimi napisami jest podpięte do zgłoszenia.

### P1

- [ ] **Wykres „nasze vs NASA”**: porównanie naszego oświetlenia z opublikowanymi mapami oświetlenia LOLA (PGDA / LROC) — A
  - Gotowe, gdy: dla kilku punktów jest wykres lub tabela z różnicą i opisem źródeł. Walidacja liczbowa grzbietu jest już zrobiona.
- [ ] **Pitch na lokalne jury**: slajdy, scenariusz demo offline, odpowiedzi na trudne pytania — wszyscy
  - Gotowe, gdy: jest próba generalna na czas z demo uruchomionym bez internetu.
- [ ] **Warstwa Moon Trek w odwzorowaniu polarnym** (South Polar Stereographic) zamiast mozaiki walcowej, która przy biegunie jest rozmazana — C
  - Gotowe, gdy: zbliżenie na miejsca przy -85…-89° jest ostre.
- [ ] **Synchronizacja listy „Choose site”** z wybranym miejscem (teraz po kliknięciu pinezki pokazuje „-- Select a site --”) — C
- [ ] **Statusy w górnym pasku** (EPHEMERIS / TERRAIN / DATA) liczone na żywo, a nie wpisane na sztywno — C

### P2

- [ ] Animacja cieni w 3D sterowana efemerydami (`scene.light = DirectionalLight`; zaślepka `setSunDirection` już istnieje) — C
- [ ] Raport misji do druku/PDF — C
- [ ] Dane partnera: JAXA Kaguya (SELENE) DEM jako druga warstwa terenu — A
- [ ] Animacja lądowania — C

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

## ⚠️ Known issues / ryzyka

- **Przybliżone współrzędne miejsc** — wszystkie 9 punktów w `sites.json` ma `approximate: true` i `source: "TODO"`. Punkty leżą na zboczach i w kraterach (średni horyzont 6–12°), więc wyniki są mylące, np. Nobile Rim 2 ma 0% światła, a Slater Plain 0% łączności. → P0 „Weryfikacja współrzędnych”.
- **Brak parametrów lądowników** — wszystkie presety mają moc i baterię = 0, bez źródeł. → P0.
- **Demo zależy od internetu** — Cesium z CDN, Google Fonts i kafle Moon Trek są online. Łamie to zasadę „offline”. → P0.
- **Brak tokena lub terenu 3D** — bez tokena działa tylko tryb 2D. Token jest tylko lokalnie (gitignored). Asset Moon Terrain `2684829` nie jest jeszcze dodany do konta Cesium ion, więc aplikacja pokazuje „3D TERRAIN UNAVAILABLE”.
- **Przybliżenie liniowe tarczy Słońca** — maksymalny błąd to 5,8 pkt proc. przy częściowym zakryciu. Statystyka „jakiekolwiek światło” nie jest dotknięta.
- **Uproszczenia modelu** — Księżyc jako sfera R = 1737,4 km, horyzont do 150 km, DEM 120 m/px, okres 2027–28 (nie pełny cykl 18,6 roku). „Ziemia widoczna” to nie to samo co kontakt ze stacją DSN.
- **Zagnieżdżone `SeleneFrontend/.git`** — git uruchomiony w tym katalogu trafia do starego repo bez remote. Do usunięcia albo przeniesienia (wymaga decyzji).
- **Atrybucja ukryta** — kredyty Cesium są schowane, a stopki ze źródłami nie ma. → P0.
- **Drobne błędy UI** — `[class.expanded]` w `ResultsStrip.razor` nie działa; lista miejsc nie synchronizuje się z wyborem; brak `favicon.ico` (404 w konsoli); statusy w górnym pasku są wpisane na sztywno.
- **GitHub Pages** — `<base href="/">` nie zadziała pod podścieżką repo.
