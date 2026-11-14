# Wdrożenie SELENE OPS na Mikrus (nginx, pliki statyczne)

Aplikacja to statyczny Blazor WebAssembly: na serwerze nie ma .NET, tylko nginx i pliki z `dotnet publish`.

| Plik | Do czego |
|---|---|
| `nginx-selene.conf` | konfiguracja nginx: **stable** (port 80, link dla jury) i **preview** (port 8080, testy) |
| `server-setup.sh` | jednorazowy setup **na serwerze** (nginx, rsync, katalogi, konfiguracja); można go uruchamiać ponownie |
| `deploy.sh` | build lokalnie + wysyłka `rsync` na serwer: `./deploy/deploy.sh preview` albo `stable` |

Serwer: `kamil177.mikrus.xyz`, SSH na porcie `10177`, użytkownik `root`.
Wymagania lokalne: .NET 10 SDK, `rsync`, `ssh`, `python3`, `gzip`. Opcjonalnie `brotli`.

## Krok po kroku

### 1. Test logowania

```bash
ssh root@kamil177.mikrus.xyz -p10177
```

### 2. Setup serwera (raz)

```bash
scp -P 10177 deploy/server-setup.sh deploy/nginx-selene.conf root@kamil177.mikrus.xyz:/root/
ssh root@kamil177.mikrus.xyz -p10177 "bash /root/server-setup.sh"
```

Skrypt instaluje nginx i rsync oraz tworzy katalogi `/var/www/selene/stable` i `/var/www/selene/preview` (z tymczasową stroną „not deployed yet”). Włącza też konfigurację `selene`, usuwa domyślną stronę nginx i kończy na `nginx -t`.
Po każdej zmianie `nginx-selene.conf` powtórz oba polecenia.

### 3. Adres strony (port, subdomena)

Na Mikrusie publiczny dostęp po IPv4 zwykle nie trafia na port 80 Twojego serwera. Sprawdź w **panelu Mikrusa**, jaka subdomena i jaki port są przypisane do serwera, i dopasuj do tego konfigurację:

- jeśli subdomena kieruje na inny port niż 80, zmień **oba** `listen` w pierwszym bloku `nginx-selene.conf` i powtórz krok 2,
- ustaw w `deploy/deploy.sh` (albo jednorazowo przez zmienne środowiskowe) prawdziwe adresy:
  ```bash
  STABLE_URL=https://<subdomena-z-panelu>/  PREVIEW_URL=http://<host>:<port-preview>/  ./deploy/deploy.sh preview
  ```
  `STABLE_URL` trafia też do `og:image`. Podgląd linku w komunikatorach potrzebuje pełnego adresu.

### 4. Wdrożenie: najpierw preview, potem stable

```bash
./deploy/deploy.sh preview --build-only   # opcjonalnie: sam build i kontrole, bez wysyłania
./deploy/deploy.sh preview                # build + rsync na /var/www/selene/preview
# … test (checklista niżej) …
./deploy/deploy.sh stable                 # ten sam build na /var/www/selene/stable (link dla jury)
```

`deploy.sh` wykonuje kolejno:
1. `dotnet publish -c Release` (trimming i kompresja `.gz`/`.br`),
2. sprawdza `index.html`, `_framework`, `data/illumination/index.json` i `appsettings.json`,
3. **przerywa**, jeśli w publikacji znalazłby się `appsettings.Development.json` z lokalnym tokenem,
4. ostrzega, gdy token Cesium jest pusty (wtedy strona działa w trybie 2D),
5. wpisuje pełny adres do `og:image` i kompresuje `nova_c.glb` (13,3 MB → 8,6 MB `.gz`),
6. wypisuje rozmiar i 10 największych plików, robi `rsync --delete`, a na koniec `curl -I` na adres strony.

### 5. Teren 3D (opcjonalnie)

Bez tokena strona działa w trybie **2D MOSAIC MODE**. To pełna funkcjonalność, tylko bez rzeźby terenu. Żeby włączyć 3D:

1. Cesium ion → **Access Tokens** → nowy token tylko z zakresem `assets:read`.
2. **Allowed URLs:** wpisz **oba** adresy, stable i preview. Na adresie spoza listy Cesium zwraca 401 i strona wraca do 2D.
3. Wdróż z tokenem w zmiennej. Token trafia **tylko** do opublikowanej kopii `appsettings.json`, nigdy do repo:
   ```bash
   CESIUM_ION_TOKEN='<token>' ./deploy/deploy.sh stable
   ```

## Checklista po wdrożeniu

- [ ] Subdomena z panelu Mikrusa wskazuje na właściwy port i strona otwiera się pod `STABLE_URL`.
- [ ] Adres strony (stable i preview) jest dopisany w **Cesium ion → Access Tokens → Allowed URLs**, jeśli używamy tokena.
- [ ] Konsola przeglądarki (F12) bez czerwonych błędów: brak 404 dla `_framework`, brak błędów MIME `application/wasm`.
- [ ] W ciągu kilku sekund widać Księżyc i wynik. QUICK DEMO i CONFIRM LANDING SITE działają do końca.
- [ ] **Telefon po LTE (wyłączone Wi-Fi):** strona się ładuje, układ jest czytelny, nic nie jest ucięte.
- [ ] Czas ładowania: zmierzony lokalnie wynik to ~2,3 s na szybkim łączu i ~3,7 s przy 20 Mbit/s. Zapisz, ile wyszło na serwerze.
- [ ] Podgląd linku (wklej adres na Messengerze/Slacku) pokazuje tytuł, opis i obrazek.

## Rozmiary (pomiar 2026-11-14)

- `out/publish/wwwroot` na dysku: **39 MB** (z wariantami `.gz`/`.br`).
- Największe pliki: `models/nova_c.glb` 13,3 MB (8,6 MB po gzip), `dotnet.native.wasm` 2,9 MB (1,2 MB gzip), `System.Private.CoreLib.wasm` 1,5 MB (0,6 MB gzip).
- Pierwsza wizyta przesyła ok. 3–4 MB. Model Nova-C (8,6 MB) pobiera się w tle dopiero po kilku sekundach.

## Wycofanie wersji

```bash
git checkout <poprzedni-commit>
./deploy/deploy.sh stable
git checkout main
```

## Problemy

| Objaw | Co sprawdzić |
|---|---|
| Biała strona, w konsoli `Failed to fetch dynamically imported module … dotnet.js` | `index.html` musi mieć `<script type="importmap"></script>` (patrz CLAUDE.md). |
| Błąd MIME przy `.wasm` | Czy aktywna jest konfiguracja `selene` (`ls -l /etc/nginx/sites-enabled/`, `nginx -t`). |
| Stara wersja po wdrożeniu | `Ctrl+Shift+R`. `index.html` i `appsettings.json` mają `no-cache`, a pliki `_framework` mają hash w nazwie. |
| `curl` na końcu skryptu nic nie zwraca | Port lub subdomena (krok 3); test na serwerze: `curl -I http://localhost/`. |
| 401 z `api.cesium.com` | Adres strony nie jest w Allowed URLs tokena albo token jest zły. Strona działa wtedy dalej w 2D. |
