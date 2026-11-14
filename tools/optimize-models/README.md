# Optymalizacja modeli lądowników (GLB)

Jednorazowe narzędzie. Z oryginałów w `SeleneFrontend/wwwroot/models/source/` robi lekkie modele `SeleneFrontend/wwwroot/models/{id}.glb`, które idą na stronę. Wyniki są w repo, więc **nie trzeba go uruchamiać**, chyba że zmienia się model.

```bash
cd tools/optimize-models
npm ci                    # glTF Transform 4.5.1, meshoptimizer 1.2.0, sharp (gotowe binaria, bez kompilacji)
node optimize.mjs         # wszystkie 5 modeli; albo: node optimize.mjs griffin
```

Kroki (SDK glTF Transform, te same funkcje co `npx @gltf-transform/cli optimize`): `dedup → weld → simplify → prune → tekstury WebP ≤ 1024 px → meshopt` (reorder + kwantyzacja + `EXT_meshopt_compression`, które Cesium 1.120 dekoduje). Skrypt kończy się błędem, jeśli plik ma ponad 3 MB albo bounding box zmienił się o ponad 2%.

Wynik z 2026-11-14:

| model | przed | po | trójkąty przed | po |
|---|---:|---:|---:|---:|
| nova_c | 13,3 MB | 2,2 MB | 288 tys. | 93 tys. |
| blue_ghost | 12,0 MB | 1,6 MB | 278 tys. | 65 tys. |
| griffin | 14,1 MB | 2,6 MB | 300 tys. | 116 tys. |
| starship_hls | 10,5 MB | 1,3 MB | 282 tys. | 70 tys. |
| blue_moon_mk2 | 11,8 MB | 1,6 MB | 287 tys. | 70 tys. |

(MB = MiB.) Nova-C i Griffin mają więcej trójkątów niż cel 30–50 tys., bo szwy UV z TRELLIS są zablokowane. Flaga `Permissive` pozwala dojść do celu, ale rozmazuje tekstury: na zbliżeniach widać plamy na panelu Blue Ghosta i kropki na Starshipie, nawet przy wadze UV 64×. Wybrano jakość. Szczegóły i zmienne do eksperymentów są w nagłówku `optimize.mjs`.

## Oryginały (`models/source/`, poza repo)

Pięć oryginałów ma ~64 MB, więc katalog jest w `.gitignore` i wykluczony z buildu i publikacji (`DefaultItemExcludes` w `SeleneFrontend.csproj`). Pochodzenie: obraz koncepcyjny z **Meshy AI** → model 3D z **Microsoft TRELLIS.2** (MIT, image-to-3D), eksport GLB (Y-up, tekstury WebP 2048 px). Pliki ma zespół (Łukasz) lokalnie. Jeśli ich zabraknie:

- `nova_c.glb` jest w historii git: `git show 85d36e0:SeleneFrontend/wwwroot/models/nova_c.glb > SeleneFrontend/wwwroot/models/source/nova_c.glb`;
- pozostałe trzeba wygenerować ponownie (Meshy → TRELLIS.2) z tych samych obrazów koncepcyjnych albo poprosić autora o kopię.

Po zmianie modelu sprawdź w aplikacji orientację (nogi w dół) i skalę (`landers.json → modelHeightM` / `modelWidthM`), a jeśli trzeba, dodaj `modelRotationDeg`.
