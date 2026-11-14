# Wdrożenie SELENE OPS na Mikrus (nginx, pliki statyczne)

Aplikacja to statyczny Blazor WebAssembly: na serwerze nie ma .NET, tylko nginx i pliki z `dotnet publish`.
Wdrażamy na dwa sposoby: **automatycznie z GitHub Actions** (zalecane, sekcja [Automatyczne wdrażanie](#automatyczne-wdrażanie)) albo ręcznie z laptopa przez `deploy.sh`.

| Plik | Do czego |
|---|---|
| `nginx-selene.conf` | konfiguracja nginx: **stable** (port 80, link dla jury) i **preview** (port 8080, testy) |
| `server-setup.sh` | jednorazowy setup **na serwerze** (nginx, rsync, katalogi, konfiguracja); można go uruchamiać ponownie |
| `create-deploy-user.sh` | jednorazowo **na serwerze**: użytkownik `deploy` dla CI (bez hasła, zapis tylko do `/var/www/selene/{stable,preview}`) |
| `deploy.sh` | build + kontrole + wysyłka `rsync`: `./deploy/deploy.sh preview` albo `stable` (lokalnie i w CI) |
| `smoke-test.sh` | test wdrożonej strony (HTTP 200 dla `/`, skryptu Blazor, danych); CI uruchamia go po każdym wdrożeniu |
| `../.github/workflows/deploy.yml` | GitHub Actions: push na `main` → preview, push na `stable` → stable |

Serwer: `kamil177.mikrus.xyz`, SSH na porcie `10177`, użytkownik `root` (albo `deploy` po `create-deploy-user.sh`).
Wymagania do wdrożenia ręcznego: .NET 10 SDK, `rsync`, `ssh`, `curl`, Python 3, `gzip`; opcjonalnie `brotli`. Git Bash na Windows **nie ma** `rsync`, więc z Windowsa wygodniej wdrażać przez GitHub Actions (lokalnie działa `--build-only`).

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

> Skrypty `*.sh` mają w repo wymuszone końce linii LF (`.gitattributes`), bo Git na Windows (`core.autocrlf=true`) zapisywał je z CRLF, a `bash` na serwerze tego nie przyjmuje (`$'\r': command not found`). Kopia sklonowana przed dodaniem `.gitattributes` może mieć jeszcze CRLF; odśwież te pliki: `rm deploy/*.sh && git checkout -- deploy/` (nadpisuje lokalne zmiany w tych skryptach).

### 3. Adres strony (port, subdomena)

Na Mikrusie publiczny dostęp po IPv4 zwykle nie trafia na port 80 Twojego serwera. Sprawdź w **panelu Mikrusa**, jaka subdomena i jaki port są przypisane do serwera, i dopasuj do tego konfigurację:

- jeśli subdomena kieruje na inny port niż 80, zmień **oba** `listen` w pierwszym bloku `nginx-selene.conf` i powtórz krok 2,
- wpisz prawdziwe adresy: dla CI jako **zmienne repo** `STABLE_URL` / `PREVIEW_URL` (krok 3 automatycznego wdrażania), a dla wdrożenia ręcznego w `deploy/deploy.sh` albo jednorazowo przez zmienne środowiskowe:
  ```bash
  STABLE_URL=https://<subdomena-z-panelu>/  PREVIEW_URL=http://<host>:<port-preview>/  ./deploy/deploy.sh preview
  ```
  `STABLE_URL` trafia też do `og:image`. Podgląd linku w komunikatorach potrzebuje pełnego adresu.

### 4. Wdrożenie ręczne: najpierw preview, potem stable

```bash
./deploy/deploy.sh preview --build-only   # sam build i kontrole, bez wysyłania (działa też w Git Bash na Windows)
./deploy/deploy.sh preview                # build + rsync na /var/www/selene/preview + smoke test
# … test (checklista niżej) …
./deploy/deploy.sh stable                 # build + rsync na /var/www/selene/stable (link dla jury)
```

`deploy.sh` wykonuje kolejno:
1. `dotnet publish -c Release` (trimming i kompresja `.gz`/`.br`),
2. sprawdza `index.html` (w tym `importmap` i skrypt Blazor), `_framework`, `data/illumination/index.json` i `appsettings.json`,
3. **przerywa**, jeśli w publikacji znalazłby się `appsettings.Development.json` z lokalnym tokenem,
4. wstrzykuje `CESIUM_ION_TOKEN` (jeśli ustawiony) **tylko** do opublikowanej kopii `appsettings.json`; ostrzega, gdy token jest pusty (wtedy strona działa w trybie 2D),
5. wpisuje pełny adres do `og:image` i kompresuje `nova_c.glb` (13,3 MB → 8,6 MB `.gz`),
6. wypisuje rozmiar i 10 największych plików,
7. robi `rsync` (`--delete-after --delay-updates`: nowe pliki wchodzą razem na końcu, więc odwiedzający nie trafią na pół-wgraną wersję; uprawnienia zawsze 755/644, bez właściciela, więc działa jako `root` i jako `deploy`),
8. uruchamia `smoke-test.sh` na adresie celu (lokalnie tylko ostrzeżenie, gdy coś nie gra).

Tryby: `--build-only` (kroki 1–6) i `--upload-only` (tylko krok 7: wysyła istniejące `out/publish`; tak robi CI). `--upload-only` odmawia, jeśli `out/publish` zbudowano dla innego celu (inny `og:image`).
Zmienne (opcjonalne): `CESIUM_ION_TOKEN`, `STABLE_URL`, `PREVIEW_URL`, `MIKRUS_HOST`, `MIKRUS_PORT`, `MIKRUS_USER`.
Kody wyjścia: `0` OK · `1` błąd buildu, kontroli albo wysyłki · `2` złe argumenty · `3` brak narzędzia. W CI (`CI=true`) `ssh` działa w trybie `BatchMode` (żadnych pytań o hasło czy klucz hosta), a ostrzeżenia i błędy są adnotacjami na stronie przebiegu.

### 5. Teren 3D (opcjonalnie)

Bez tokena strona działa w trybie **2D MOSAIC MODE**. To pełna funkcjonalność, tylko bez rzeźby terenu. Żeby włączyć 3D:

1. Cesium ion → **Access Tokens** → nowy token tylko z zakresem `assets:read`.
2. **Allowed URLs:** wpisz **oba** adresy, stable i preview. Na adresie spoza listy Cesium zwraca 401 i strona wraca do 2D.
3. CI: sekret `CESIUM_ION_TOKEN` (krok 3 niżej). Ręcznie: token w zmiennej. W obu przypadkach trafia **tylko** do opublikowanej kopii `appsettings.json`, nigdy do repo:
   ```bash
   CESIUM_ION_TOKEN='<token>' ./deploy/deploy.sh stable
   ```

## Automatyczne wdrażanie

Workflow `.github/workflows/deploy.yml` (GitHub Actions):

| Zdarzenie | Cel | Katalog na serwerze | Adres (zmienna repo) |
|---|---|---|---|
| push na `main` | **preview** | `/var/www/selene/preview/` | `PREVIEW_URL` |
| push na `stable` | **stable** (link dla jury) | `/var/www/selene/stable/` | `STABLE_URL` |
| Actions → Deploy → **Run workflow** | do wyboru: preview / stable | jak wyżej | jak wyżej |

Kolejne kroki joba: sprawdzenie konfiguracji → .NET 10 (cache NuGet) → `dotnet test SeleneFrontend.Tests` (**błąd testów przerywa wdrożenie**) → `deploy.sh <cel> --build-only` (z tokenem Cesium z sekretu) → klucz SSH i `known_hosts` → `deploy.sh <cel> --upload-only` (rsync z `--delete`) → `smoke-test.sh` → podsumowanie przebiegu (cel, commit, link, rozmiar paczki, tryb 2D/3D).
Dwa wdrożenia tego samego celu nie nadpisują się: nowszy przebieg anuluje starszy (`concurrency: deploy-<cel>`). Bez sekretu `MIKRUS_SSH_KEY` workflow tylko testuje i buduje, a na stronie przebiegu pojawia się ostrzeżenie „Deploy skipped”.

### 1. Osobny klucz SSH tylko dla CI

W Git Bash (w Windows PowerShell 5.1 pusty argument `-N ""` nie dochodzi do programu):

```bash
ssh-keygen -t ed25519 -f ~/.ssh/mikrus_deploy -N "" -C "github-actions-selene"
```

Powstają `~/.ssh/mikrus_deploy` (prywatny → tylko do sekretu GitHuba) i `~/.ssh/mikrus_deploy.pub` (publiczny → na serwer). Klucz nie ma hasła, bo CI nie może go wpisać, dlatego służy **tylko** do wdrożeń.

### 2. Klucz publiczny na serwer

**Wariant A (najprostszy): użytkownik `root`.**

```bash
ssh-copy-id -i ~/.ssh/mikrus_deploy.pub -p 10177 root@kamil177.mikrus.xyz
```

Jeśli nie ma `ssh-copy-id`:

```bash
cat ~/.ssh/mikrus_deploy.pub | ssh -p 10177 root@kamil177.mikrus.xyz 'mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys'
```

**Wariant B (zalecany): osobny użytkownik `deploy`.** Klucz z CI nie daje wtedy roota: `deploy` nie ma hasła, ma zapis tylko do `/var/www/selene/stable` i `/var/www/selene/preview`, a klucz jest dodany z opcją `restrict` (bez terminala i przekierowań portów). Zamiast `ssh-copy-id` dla roota (logujesz się swoim zwykłym kluczem albo hasłem roota):

```bash
scp -P 10177 deploy/create-deploy-user.sh root@kamil177.mikrus.xyz:/root/
ssh -p 10177 root@kamil177.mikrus.xyz 'bash /root/create-deploy-user.sh -' < ~/.ssh/mikrus_deploy.pub
ssh -i ~/.ssh/mikrus_deploy -p 10177 deploy@kamil177.mikrus.xyz 'ls -ld /var/www/selene/*'   # test: oba katalogi należą do deploy
```

Potem w kroku 3 ustaw **`MIKRUS_USER=deploy`** zamiast `root`. Skrypt można uruchamiać ponownie (np. po ręcznym wdrożeniu jako root, żeby oddać pliki użytkownikowi `deploy`). Jeśli wcześniej dodałeś ten klucz rootowi (wariant A), skrypt to wykryje i poda polecenie, które go usuwa.

### 3. Sekrety i zmienne w GitHubie

GitHub → repozytorium → **Settings → Secrets and variables → Actions**. Zakładka *Settings* wymaga roli **Admin** w repozytorium (np. właściciel); osoba z samym prawem zapisu może pushować, ale nie ustawi sekretów.

Zakładka **Secrets** → *New repository secret*:

| Sekret | Wartość | |
|---|---|---|
| `MIKRUS_SSH_KEY` | **cała** zawartość `~/.ssh/mikrus_deploy` (z liniami `BEGIN`/`END`): `cat ~/.ssh/mikrus_deploy` | wymagany |
| `MIKRUS_HOST` | `kamil177.mikrus.xyz` | wymagany |
| `MIKRUS_PORT` | `10177` | wymagany |
| `MIKRUS_USER` | `root` albo `deploy` (wariant B); bez sekretu: `root` | opcjonalny |
| `CESIUM_ION_TOKEN` | token z ograniczeniem do adresów strony (krok 5 wyżej); bez niego tryb 2D | opcjonalny |
| `MIKRUS_KNOWN_HOSTS` | wynik `ssh-keyscan -p 10177 kamil177.mikrus.xyz` (przypięty klucz hosta zamiast pobierania go przy każdym wdrożeniu) | opcjonalny |

Zakładka **Variables** → *New repository variable*: `PREVIEW_URL` i `STABLE_URL`, **pełne** adresy razem z `https://` (krok 3 wyżej), np. `https://selene.byst.re/`. Każdy cel potrzebuje własnego adresu: subdomena z panelu Mikrusa wskazuje na jeden port, czyli albo na stable (80), albo na preview (8080). Adres bez `https://` workflow uzupełni sam (z ostrzeżeniem). Bez zmiennych wdrożenie działa, ale smoke test jest pomijany (z ostrzeżeniem), a `og:image` dostaje domyślny adres z `deploy.sh`.

To samo z [GitHub CLI](https://cli.github.com/) (wartości nie lądują w historii powłoki):

```bash
gh secret set MIKRUS_SSH_KEY < ~/.ssh/mikrus_deploy
gh secret set MIKRUS_HOST --body kamil177.mikrus.xyz
gh secret set MIKRUS_PORT --body 10177
gh secret set MIKRUS_USER --body deploy
gh variable set PREVIEW_URL --body http://kamil177.mikrus.xyz:8080/
gh variable set STABLE_URL --body http://kamil177.mikrus.xyz/
```

Po zapisaniu kluczy usuń `~/.ssh/mikrus_deploy` z laptopa albo trzymaj go tylko tam, gdzie jest potrzebny: do wdrożeń wystarczy sekret w GitHubie.

### 4. Wdrożenie

```bash
git push origin main            # → preview
```

```bash
git push origin main:stable     # → stable (link dla jury), po sprawdzeniu preview
```

Postęp: zakładka **Actions** → przebieg „Deploy …”. Na stronie przebiegu jest podsumowanie: cel, commit, link, rozmiar paczki, tryb 2D/3D i wynik smoke testu. Ręcznie: **Actions → Deploy → Run workflow**, wybierz gałąź i cel.

### 5. Wycofanie wersji

- Przestaw `stable` na dobry commit (nie trzeba nic budować lokalnie):
  ```bash
  git push -f origin <dobry-commit>:stable
  ```
  Kolejny `git push origin main:stable` znów przesunie `stable` do przodu.
- Albo **Actions** → stary, udany przebieg dla `stable` → **Re-run all jobs**. Ponowne uruchomienie buduje dokładnie ten sam commit co wtedy (GitHub pozwala na to przez ok. 30 dni).

### Bezpieczeństwo

- Repo jest publiczne, więc logi Actions też są publiczne. Sekrety są w logach maskowane, a `deploy.sh` dodatkowo maskuje token (`::add-mask::`) i nigdy go nie wypisuje. Workflow nie publikuje artefaktów z buildem.
- Token Cesium trafia tylko do `out/publish` na maszynie CI i do plików na serwerze (i tak jest widoczny w przeglądarce, dlatego musi mieć ograniczenie *Allowed URLs*).
- Workflow uruchamia się tylko na push i ręcznie, nie na pull requesty z forków, więc obcy kod nie dostaje sekretów.
- Klucz z CI jest osobny i bez hasła: przy wycieku usuń jego linię z `authorized_keys` na serwerze i wygeneruj nowy (kroki 1–3).
- Bez `MIKRUS_KNOWN_HOSTS` klucz hosta jest pobierany przez `ssh-keyscan` przy każdym wdrożeniu (zaufanie przy pierwszym połączeniu). Przypięcie go sekretem chroni przed podszyciem się pod serwer.

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

## Wycofanie wersji (ręcznie)

Przy wdrażaniu przez CI patrz [Automatyczne wdrażanie → 5](#5-wycofanie-wersji). Ręcznie:

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
| Smoke test nie dostaje odpowiedzi | Port lub subdomena (krok 3); test na serwerze: `curl -I http://localhost/`. |
| 401 z `api.cesium.com` | Adres strony nie jest w Allowed URLs tokena albo token jest zły. Strona działa wtedy dalej w 2D. |
| Actions: ostrzeżenie „Deploy skipped” | Brak sekretu `MIKRUS_SSH_KEY` (krok 3). Testy i build przeszły, nic nie wysłano. |
| Sekrety dodane, a kroki SSH/rsync dalej „skipped” | Przebieg wystartował przed dodaniem sekretów; samo dodanie sekretu nie uruchamia workflow. **Actions → Deploy → Run workflow** albo kolejny push. |
| Actions: `MIKRUS_SSH_KEY: Not usable: …` | Komunikat mówi, co jest nie tak: klucz publiczny (`.pub`), format PuTTY, brak linii `BEGIN`/`END`, klucz z hasłem. Sprawdź plik lokalnie: `ssh-keygen -y -P "" -f ~/.ssh/mikrus_deploy` musi wypisać klucz publiczny bez pytania o hasło. Skopiuj go dokładnie: `cat ~/.ssh/mikrus_deploy \| clip` (Git Bash) i wklej jako nową wartość sekretu. Spacje, CRLF i wcięcia workflow poprawia sam. Po udanym kroku log pokazuje odcisk klucza; porównaj z `ssh-keygen -l -f ~/.ssh/mikrus_deploy.pub`. |
| Actions: `No SSH host key from the server` | Zły `MIKRUS_HOST`/`MIKRUS_PORT` albo serwer nie odpowiada; z laptopa: `ssh-keyscan -p 10177 kamil177.mikrus.xyz`. |
| Actions: `Host key verification failed` | Zmienił się klucz hosta (np. reinstalacja serwera). Zaktualizuj `MIKRUS_KNOWN_HOSTS` albo usuń ten sekret. |
| Actions: `Permission denied (publickey)` | Klucz `.pub` nie trafił do `authorized_keys` właściwego użytkownika (krok 2) albo `MIKRUS_USER` jest inny niż ten, któremu go dodano. |
| Actions: rsync `Permission denied` / `failed to set permissions` | Użytkownik `deploy` nie jest właścicielem plików (np. po ręcznym wdrożeniu jako root): uruchom ponownie `create-deploy-user.sh`. |
| Smoke test: `server serves another build` | `STABLE_URL`/`PREVIEW_URL` wskazuje na drugi port (stable ↔ preview) albo nginx serwuje inny katalog. |
