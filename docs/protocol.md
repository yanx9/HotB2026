# Protokół

Kontrakt między częścią pythonową (`bci/`) a dowolną grą Unity (`unity/`, `precel/` i kolejnymi). Zmieniając format, zmieniajcie ten plik w tym samym commicie co kod po obu stronach. Kalibracja i profile: [calibration.md](calibration.md).

```
Unicorn Hybrid Black → g.Pype HybridBlack → UDPSender → UDP 56000 (float64)
                                                          │
                        calibrate.py → profil JSON         │
                                           │              ▼
                                           └──────→ bci_bridge.py → UDP 5005 (JSON) → Unity
                                                          ▲                           │
                             data/raw/*.jsonl ← znacznik ─┴──── UDP 5006 (JSON) ──────┘
```

Kalibrator odbiera bezpośrednio ten sam strumień EEG co most; na danym porcie działa jeden z nich. Po kalibracji należy go rozłączyć, a następnie uruchomić most z profilem. Most może działać na innym komputerze niż Unity (`--host IP_GRY`). Kalibracja, przetwarzanie i profile pozostają niezależne od gry.

## 1. Wejście: Unicorn → `bci/`

| | |
|---|---|
| Kanały EEG | 8 — domyślny montaż: Fz, C3, Cz, C4, Pz, PO7, Oz, PO8 |
| Próbkowanie | 250 Hz |
| Transport | standardowy `gp.UDPSender`, domyślnie `127.0.0.1:56000` |
| Dane | surowe EEG, `float64`, little-endian, układ `(próbki, kanały)` w kolejności C, jednostki EEG µV |
| Ramka | domyślnie 4 próbki × 8 kanałów × 8 bajtów = 256 bajtów; inna ramka wymaga zgodnego `--frame-samples` |
| Metadane | brak na łączu; kanały, ramka, próbkowanie, jednostki i kolejność bajtów są ustawiane w kalibratorze i zapisywane w profilu |
| Kanały dodatkowe | trzeba wymienić wszystkie etykiety w kolejności transmisji; do cech trafia wyłącznie osiem kanałów EEG |
| Czas / utrata pakietów | siatka czasu szacowana w odbiorniku, bez czasu akwizycji i numerów sekwencyjnych; nie każda utrata lub zmiana kolejności jest wykrywalna |

Odbiera `gpype_source.py`, wspólny dla `calibrate.py` i `bci_bridge.py`. Wejście g.Pype wymaga profilu. Błędne ramki i wykryte przerwy zerują okna, a utrata świeżych danych neutralizuje wyniki. Rozmiar pakietu można sprawdzić, lecz nie da się automatycznie potwierdzić znaczenia kanałów lub jednostek bez metadanych. Źródło formatu: [oficjalny przykład UDPSendera](https://gpype.gtec.at/content/2_gpype_training/season4_crossing_borders/s4e3_sending_udp.html).

Zachowana ścieżka opcjonalna: `calibrate.py --source lsl` i `lsl_bridge.py` odbierają LSL z metadanymi i korekcją zegara. Wymaga `requirements-lsl.txt`; nie jest zależnością toru g.Pype. Wspólny kod mostu można też wywołać jako `lsl_bridge.py --source gpype --profile ...`.

## 2. Wyjście: `bci/` → Unity

- **Transport:** UDP na `127.0.0.1:5005`. Jeśli gra działa na innym komputerze, podajcie jego IP.
- **Wiadomość:** jeden datagram = jeden obiekt JSON w UTF-8.
- Pola są płaskie i mają stałe typy, żeby Unity mogło je parsować przez `JsonUtility.FromJson`.

| Pole | Typ | Opis |
|---|---|---|
| `type` | string | `"command"` albo `"metric"` |
| `name` | string | nazwa komendy lub metryki (listy niżej) |
| `value` | float | `command`: wynik detektora 0–1 (niekoniecznie prawdopodobieństwo); `metric`: zakres zależy od nazwy, np. `laterality` −1–1 |
| `t` | float | czas wysłania w sekundach Unix, do mierzenia opóźnień. W C# deklarujcie jako `double` — `float` zgubi precyzję |

```json
{"type": "command", "name": "left", "value": 0.87, "t": 1794614400.12}
{"type": "metric", "name": "focus", "value": 0.62, "t": 1794614400.15}
```

- `command` — dyskretne zdarzenie (wybór P300, decyzja klasyfikatora). Wysyłane raz, w chwili, gdy zajdzie.
- `metric` — wartość ciągła (np. neurofeedback z mocy pasm). Wysyłana regularnie, ok. 10 Hz.

### Komendy

| `name` | Paradygmat | Co robi w grze |
|---|---|---|
| `select` | P300 (auditory oddball) | "to ten motyw" w trakcie rozpoznawania; w ogrodzie zaczyna kolejną lekcję |
| `left` | motor imagery, lewa ręka | nastrój chłodny, muzyka przechodzi w moll |
| `right` | motor imagery, prawa ręka | nastrój ciepły, muzyka durowa i jaśniejsza |

Nastrój utrzymuje się 4 s po ostatniej komendzie `left`/`right`, potem wraca do neutralnego. Klasyfikator może więc wysyłać decyzję co ok. 0,5–1 s, dopóki stan trwa.

### Metryki

Most nie wie nic o grach: wysyła wszystkie sygnały, a każda gra wybiera, których słucha.

| `name` | Zakres | Znaczenie |
|---|---|---|
| `focus` | 0–1 | skupienie z widma EEG: beta / (alfa + theta) z okna 2 s, względem ostatniej minuty (0.5 = przeciętny poziom tej osoby); okna z artefaktami są pomijane |
| `laterality` | −1–1 | różnica mocy 8–30 Hz między C4 a C3 z okna 1 s, względem ostatniej minuty. Ruch albo wyobrażenie ruchu prawej ręki tłumi rytm nad C3, więc wartość rośnie; lewej ręki maleje. Okna z artefaktami są pomijane |
| `jaw` | 0–1 | zaciśnięcie szczęki: moc 55–95 Hz (mięśnie) ze wszystkich kanałów EEG z okna 0,25 s, względem spoczynku. 0 = do 3× mocy spoczynkowej, 1 = 30× i więcej |

### Sygnały z profilu kalibracji

`bci_bridge.py --profile <plik.json>` używa parametrów z czterech niezależnych widoków `calibrate.py`. Ten sam profil działa z każdym odbiornikiem Unity; nie zawiera czynności ani nazw gier. Dla wejścia LSL działa również `lsl_bridge.py --profile <plik.json>`.

| `name` | Zakres | Znaczenie |
|---|---|---|
| `eyes_closed` | 0–1 | wynik reaktywności alfa z tylnych kanałów |
| `right_hand`, `left_hand` | 0–1 | wynik kalibrowanego kontrastu sensomotorycznego względem spoczynku |
| `jaw` | 0–1 | kalibrowany wynik aktywności mięśniowej |
| `<sygnał>_active` | 0/1 | aktywacja po histerezie i czasie potwierdzenia, dla powyższych czterech sygnałów |
| `<sygnał>_valid` | 0/1 | poprawny model i świeże okno bez wykrytych problemów |
| `laterality` | −1–1 | `right_hand - left_hand`, tylko gdy oba modele są gotowe i wyniki nie są sprzeczne |
| `laterality_valid`, `focus_valid` | 0/1 | ważność osi / dotychczasowej heurystyki focus |

Format datagramu nie zmienia się. Brak modelu, nieudana kalibracja i nieprawidłowe dane dają zero oraz `valid=0`. `focus` nie jest kalibrowany przez te widoki. Komendy `left` i `right` są emitowane co 0,5 s podczas aktywnego stanu, chyba że podano `--no-commands`. Kalibrator nie generuje `select`/P300. Cechy i parametry opisuje [calibration.md](calibration.md).

### Kto czego słucha

| Gra | Sygnały |
|---|---|
| Ogród dźwięków (`unity/`) | komendy `select`, `left`, `right`; metryka `focus` |
| Krojenie precla (`precel/`) | `laterality` przesuwa nóż, `jaw_active` tnie (bez profilu kalibracji zastępczo `jaw`). Powiązania są w `precel/Assets/Resources/signals.json` |
| Mrok (`mrok/`) | `eyes_closed_active` z profilu kalibracji: zamknięte oczy ładują skan. Powiązanie jest w `mrok/Assets/Resources/signals.json` |

Gry z pakietem `shared/bci-signals` przyjmują każdy sygnał o dowolnej nazwie i respektują flagi `<sygnał>_valid`: sygnał z `valid=0` liczy się jak brak sygnału. Kilka gier naraz obsługuje `--target host:port` w moście (każda gra na własnym porcie z `signals.json`).

## 3. Znaczniki: Unity → `bci/`

Gra wysyła znacznik przy każdym zdarzeniu rozgrywki. Służą do oceny wyników gracza. Użycie do cięcia EEG na epoki P300 wymaga osobnego, zweryfikowanego toru synchronizacji; sam surowy strumień g.Pype UDP go nie zapewnia.

- **Transport:** UDP na port `5006` komputera, z którego przychodzi sygnał BCI (dopóki nic nie przyszło: `127.0.0.1`). Ten sam format JSON, `type` = `"marker"`.
- **Odbiór, jeden proces na porcie**:
  - `bci_bridge.py --profile ...` zapisuje je domyślnie do `data/raw/bci_markers_*.jsonl`. Pola: `marker` (`"<name>:<value>"`), `t_monotonic_estimate` i `t_received_unix`. Ścieżkę zmienia `--marker-file`.
  - `lsl_bridge.py` lub jawne `--marker-output lsl` publikuje **BciMarkers** (typ `Markers`, kanał tekstowy `"<name>:<value>"`). `--marker-name OgrodMarkers` zachowuje dawną nazwę; nagrywanie przez LabRecorder wymaga też strumienia EEG LSL.
  - `python bci/marker_logger.py` zapisuje CSV w `data/raw/`. Jeśli działa obok mostu, most musi mieć `--no-markers`.
- Gra zapisuje też własną kopię CSV w `Application.persistentDataPath`.
- `value` to numer rośliny (0–3), a dla `grow` nowy poziom rośliny.

| `name` | Kiedy |
|---|---|
| `session_start` | start gry |
| `learn` | początek lekcji (słuchanie motywu) |
| `pattern` | odtworzenie motywu wzorcowego |
| `recognize` | początek fazy rozpoznawania |
| `target` / `distractor` | początek sekwencji: motyw albo sekwencja myląca |
| `hit` / `false_alarm` / `miss` | odpowiedź gracza: trafienie, reakcja na złą sekwencję, brak reakcji na motyw |
| `grow` | roślina urosła |
| `mute` / `unmute` | gracz wyciszył lub włączył roślinę |
| `garden` | powrót do ogrodu |

Czas `t` znacznika to chwila zlecenia odtworzenia dźwięku, nie chwila, w której dźwięk wyszedł z głośnika. Most przelicza go na lokalny zegar monotoniczny (lub LSL przy eksporcie LSL). Gdy różnica względem czasu odbioru przekracza 1 s, używa czasu odbioru. To heurystyka: nie synchronizuje zegarów dwóch komputerów ani toru audio. Przed kalibracją P300 potrzebny jest pomiar opóźnień i synchronizacja z właściwymi czasami próbek.

Kalibrator ma osobny kanał zwrotny do `gp.UDPReceiver`: domyślnie UDP **56001**, jeden numeryczny kod ASCII na datagram. Kody 100–104 dotyczą oczu, 110–114 prawej ręki, 120–124 lewej i 130–134 szczęki; końcówka oznacza przygotowanie / spoczynek / zadanie / koniec / przerwanie. Nie jest to JSON. Szczegóły i tabela: [calibration.md](calibration.md#znaczniki). W opcjonalnym trybie LSL kalibrator zachowuje **BciCalibrationMarkers** (`calibration/<sygnał>/<faza>:<próba>`).

### Znaczniki gry Krojenie precla

| `name` | `value` | Kiedy |
|---|---|---|
| `session_start` | 0 | start gry |
| `round_start` | numer precla (1–5) | precel spada na deskę |
| `cut_start` | pozycja noża | sygnał cięcia przekroczył próg, nóż zaczyna opadać |
| `cut` | pozycja noża | precel przecięty |
| `result` | różnica masy połówek w % | połówki zważone |
| `summary` | suma punktów | koniec zawodów |

### Znaczniki gry Mrok

| `name` | `value` | Kiedy |
|---|---|---|
| `session_start` | 0 | start gry i każdy restart |
| `eyes_closed` | 0 | gra uznała oczy za zamknięte |
| `eyes_open` | czas zamknięcia w s | gra uznała oczy za otwarte |
| `scan` | zasięg skanu w m | skan po otwarciu oczu |
| `alert` | numer strażnika | strażnik usłyszał hałas |
| `loot` | liczba zebranych | gracz zabrał łup |
| `caught` / `win` | 0 | koniec gry |
