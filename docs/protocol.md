# Protokół

Kontrakt między częścią pythonową (`bci/`) a dowolną grą Unity (`unity/`, `precel/` i kolejnymi). Zmieniając format, zmieniajcie ten plik w tym samym commicie co kod po obu stronach. Kalibracja i profile: [calibration.md](calibration.md).

```
Unicorn Hybrid Black
        │ Bluetooth
        ▼
 g.Pype: HybridBlack → Highpass 1 Hz → Bandstop 48–52 Hz → SignalClassifier → UdpJsonSender
                                                              ▲                    │
                 calibrate.py (ten sam początek potoku) → profil JSON              ▼
                                                                         UDP 5005 (JSON) → Unity
                                  data/raw/*.jsonl ← bci_pipeline.py ← UDP 5006 (JSON) ←──┘
```

Cały tor od opaski do datagramu JSON działa w jednym procesie g.Pype (`bci/bci_pipeline.py`, węzły w `bci/gpype_nodes.py`). Potok może działać na innym komputerze niż Unity (`--host IP_GRY`). Kalibracja, przetwarzanie i profile pozostają niezależne od gry.

## 1. Wejście: Unicorn → g.Pype

| | |
|---|---|
| Źródło | `gp.HybridBlack`, ramki po 4 próbki; bez opaski `SyntheticUnicorn` (`--fake`) |
| Kanały EEG | 8 — montaż Unicorna: Fz, C3, Cz, C4, Pz, PO7, Oz, PO8, w µV |
| Próbkowanie | 250 Hz |
| Filtry | górnoprzepustowy 1 Hz i zaporowy na częstotliwość sieci (48–52 Hz, `--mains 60` dla 58–62 Hz) |
| Klasyfikacja | `SignalClassifier`: okna 0,25–2 s, cechy widmowe, progi z profilu kalibracji; wynik odświeżany 10 razy na sekundę |
| Czas | liczony w próbkach; przerwa w danych dłuższa niż 0,5 s zeruje okna analizy |

Kalibrator buduje ten sam początek potoku (`build_front` w `gpype_nodes.py`), więc profil powstaje na sygnale po tych samych filtrach. Ustawienia filtrów zapisują się w profilu i potok odmawia użycia profilu z innego toru. Opaskę trzyma naraz jeden proces: kalibrator albo `bci_pipeline.py`.

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

Potok nie wie nic o grach: wysyła wszystkie sygnały, a każda gra wybiera, których słucha. Bez profilu (`bci_pipeline.py`) są to nieskalibrowane heurystyki, każda z flagą `<nazwa>_valid`:

| `name` | Zakres | Znaczenie |
|---|---|---|
| `focus` | 0–1 | skupienie z widma EEG: beta / (alfa + theta) z okna 2 s, względem ostatniej minuty (0.5 = przeciętny poziom tej osoby); okna z artefaktami są pomijane |
| `laterality` | −1–1 | różnica mocy 8–30 Hz między C4 a C3 z okna 1 s, względem ostatniej minuty. Ruch albo wyobrażenie ruchu prawej ręki tłumi rytm nad C3, więc wartość rośnie; lewej ręki maleje. Okna z artefaktami są pomijane |
| `jaw` | 0–1 | zaciśnięcie szczęki: moc 55–95 Hz (mięśnie) ze wszystkich kanałów EEG z okna 0,25 s, względem spoczynku. 0 = do 3× mocy spoczynkowej, 1 = 30× i więcej |

### Sygnały z profilu kalibracji

`bci_pipeline.py --profile <plik.json>` używa parametrów z czterech niezależnych widoków `calibrate.py`. Ten sam profil działa z każdym odbiornikiem Unity; nie zawiera czynności ani nazw gier. Z profilem `jaw` i `laterality` pochodzą z kalibracji, nie z heurystyk.

| `name` | Zakres | Znaczenie |
|---|---|---|
| `eyes_closed` | 0–1 | wynik reaktywności alfa: alfa względna z PO7 i PO8. Krótki artefakt (do 1,5 s) nie zmienia stanu; dopiero dłuższy daje `valid=0` |
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

Gry z pakietem `shared/bci-signals` przyjmują każdy sygnał o dowolnej nazwie i respektują flagi `<sygnał>_valid`: sygnał z `valid=0` liczy się jak brak sygnału. Kilka gier naraz obsługuje `--target host:port` w potoku (każda gra na własnym porcie z `signals.json`).

## 3. Znaczniki: Unity → `bci/`

Gra wysyła znacznik przy każdym zdarzeniu rozgrywki. Potok kładzie każdy znacznik na próbce EEG z chwili odbioru, w nagraniu sesji `data/raw/<sesja>_<czas>/` ([recording.md](recording.md)). Dokładność to kilka ms plus stałe ~40 ms Bluetooth opaski. Wystarcza to do oceny sygnałów trwających sekundy (oczy, szczęka). Cięcie na epoki P300 wymaga jeszcze pomiaru opóźnienia dźwięku w grze.

- **Transport:** UDP na port `5006` komputera, z którego przychodzi sygnał BCI (dopóki nic nie przyszło: `127.0.0.1`). Ten sam format JSON, `type` = `"marker"`.
- **Odbiór, jeden proces na porcie**:
  - `bci_pipeline.py` zapisuje je do `data/raw/bci_markers_*.jsonl`. Pola: `marker`, `value`, `t_game_unix` (czas z gry) i `t_received_unix`. Ścieżkę zmienia `--marker-file`.
  - `python bci/marker_logger.py` zapisuje CSV w `data/raw/`. Jeśli działa obok potoku, potok musi mieć `--no-markers`.
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

Czas `t` znacznika to chwila zlecenia odtworzenia dźwięku, nie chwila, w której dźwięk wyszedł z głośnika. Plik znaczników przechowuje czas z gry i czas odbioru; nie synchronizuje zegarów dwóch komputerów ani toru audio. Przed kalibracją P300 potrzebny jest pomiar opóźnień i synchronizacja z czasami próbek.

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
| `truth_closed` | 0 | gracz wcisnął spację: ma zamknięte oczy (prawda do oceny detektora, w obu trybach) |
| `truth_open` | czas trzymania w s | gracz puścił spację |
| `mode_keyboard` / `mode_eeg` | 0 | tryb sterowania oczami (F2); wysyłany też po każdym `session_start` |
| `scan` | zasięg skanu w m | skan po otwarciu oczu |
| `alert` | numer strażnika | strażnik usłyszał hałas |
| `loot` | liczba zebranych | gracz zabrał łup |
| `caught` / `win` | 0 | koniec gry |
