# ROADMAP — SELENE OPS

**Ostatnia aktualizacja:** 2026-11-14 14:30 CET

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

## 🔄 In progress

_(nic przypisanego — wpisz się tutaj, gdy zaczynasz zadanie)_

## 📋 Todo — według priorytetu

P0 = bez tego nie ma zgłoszenia · P1 = mocno podnosi ocenę · P2 = efekt wow, jeśli starczy czasu

### P0

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

- [ ] **Ocena optymalizatora się nasyca** — dla lądowników o małym poborze (np. Nova-C) wiele okien ma 100/100, więc mapa ciepła słabo różnicuje opcje. Dodać składnik rozróżniający (np. zapas energii w Wh, liczba godzin poniżej progu, najdłuższa ciemność) — B
  - Gotowe, gdy: dla Nova-C i Blue Ghost najlepsze okna różnią się oceną, a wzór jest opisany w komentarzu i w teście.
- [ ] **Pole „Time Step [h]” w sekcji 03 nic nie robi** — dane mają krok 1 h. Usunąć je albo zablokować z opisem „data step: 1 h” — C
- [ ] **Nakładka kandydatów na mapie**: pinezki/kropki z `illumination/candidates/{regionId}.json` (kolor wg lit %, odrzucone wyszarzone) i alternatywy z `sites.json` w panelu sekcji 01 — C
  - Gotowe, gdy: po wybraniu regionu widać wszystkich ocenionych kandydatów z wartościami w tooltipie, a kliknięcie alternatywy przełącza miejsce.
- [ ] **Pokazanie źródła miejsca w UI** (`source`, `candidateId`, „best of N”) w sekcji 01, z linkiem do DOI — C

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
- 2026-11-14 — Claude Code (Claude Opus 5.5) — wybór lądownika tylko do odczytu (SPECS z pochodzeniem danych, LANDER IMPACT, what-if), weryfikacja dostępności linków źródłowych, testy w przeglądarce
- 2026-11-14 — Claude Code (Claude Opus 5.5) — odnalezienie danych Wueller et al. (2026) w Zenodo, import kandydatów, wybór miejsc w regionach, walidacja z twierdzeniem NASA i z wynikami autorów, testy
- 2026-11-14 — Claude Code (Claude Opus 5.5) — rdzeń analizy: symulator misji, optymalizator okien, panel wyników (werdykt, oś czasu, mapa ciepła), testy xUnit, testy w przeglądarce i optymalizacja wydajności

## ⚠️ Known issues / ryzyka

- ~~Przybliżone współrzędne miejsc~~ — rozwiązane w `7578ae8` (miejsca z Wueller et al. 2026).
- **Optymistyczny model produkcji energii** — produkcja = moc nominalna paneli × ułamek widocznej tarczy Słońca. Nie uwzględnia orientacji paneli względem nisko stojącego Słońca, temperatury, sprawności ładowania i rozładowania ani degradacji. Wyniki traktować jako górną granicę; na pitchu mówić o tym wprost.
- **Lądowanie zawsze o 00:00 UTC** — optymalizator sprawdza jedną godzinę lądowania na dzień i nie modeluje ograniczeń trajektorii ani startu.
- **Okno w sekcji 03 to daty lądowania** — misja musi zmieścić się w danych, więc ostatni możliwy dzień lądowania to koniec danych minus długość misji.
- **Mapa ciepła to canvas** — czytnik ekranu dostaje tylko opis; obsługa z klawiatury przez przyciski „najlepsza data” pod mapą.
- **Dwie definicje „światła”** — `index.json → pctSunlit` liczy każde światło (`sunFraction > 0`), a wybór miejsc i walidacja używają `sunFraction ≥ 50` (`sunPct` w `candidates/*.json`). W UI i na pitchu zawsze podawać, którą liczbę pokazujemy.
- **Optymizm DEM 120 m** — rozdzielczość 120 m wygładza drobną rzeźbę, co zawyża oświetlenie i zaniża nachylenia (wszyscy kandydaci ≤ 4°). Opcja P2: przeliczyć wybrane miejsca na DEM 20–60 m.
- **Twierdzenie NASA o 6,5 dnia** pochodzi z komunikatu z 2022 (13 regionów) i dosłownie obejmuje tylko 5 z naszych 9 regionów. Na pitchu nie mówić „NASA potwierdza dla wszystkich 9”.
- **Dane lądowników HLS to głównie założenia** — moc paneli, bateria i obciążenia Starship HLS i Blue Moon nie są publikowane (ASSUMPTION). Wyniki dla HLS trzeba na pitchu przedstawiać jako szacunek klasy, nie dane producenta.
- **Dwa linki źródłowe nie odpowiadają automatom** (sprawdzone 2026-11-14 ~12:50): `nasaspaceflight.com` (Griffin, czas misji) zwraca 403 dla zapytań automatycznych, a cały `collectspace.com` (Blue Moon: czas misji, załoga, panele) zwracał 522 (awaria serwera). Sprawdzić ręcznie w przeglądarce przed zgłoszeniem; `nasaspaceflight.com/tag/viper/` to strona tagu, czyli słabe źródło, więc warto podmienić na konkretny artykuł.
- **Demo zależy od internetu** — Cesium z CDN, Google Fonts i kafle Moon Trek są online. Łamie to zasadę „offline”. → P0.
- **Brak tokena lub terenu 3D** — bez tokena działa tylko tryb 2D. Token jest tylko lokalnie (gitignored). Asset Moon Terrain `2684829` nie jest jeszcze dodany do konta Cesium ion, więc aplikacja pokazuje „3D TERRAIN UNAVAILABLE”.
- **Przybliżenie liniowe tarczy Słońca** — maksymalny błąd to 5,8 pkt proc. przy częściowym zakryciu. Statystyka „jakiekolwiek światło” nie jest dotknięta.
- **Uproszczenia modelu** — Księżyc jako sfera R = 1737,4 km, horyzont do 150 km, DEM 120 m/px, okres 2027–28 (nie pełny cykl 18,6 roku). „Ziemia widoczna” to nie to samo co kontakt ze stacją DSN.
- ~~Zagnieżdżone `SeleneFrontend/.git`~~ — usunięte w `1e7a604`.
- **Atrybucja ukryta** — kredyty Cesium są schowane, a stopki ze źródłami nie ma. → P0.
- **Drobne błędy UI** — lista miejsc nie synchronizuje się z wyborem; brak `favicon.ico` (404 w konsoli); statusy w górnym pasku są wpisane na sztywno. (`[class.expanded]` naprawione w `1e7a604`.)
- **GitHub Pages** — `<base href="/">` nie zadziała pod podścieżką repo.
