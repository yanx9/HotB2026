# Protokół

Kontrakt między częścią pythonową (`bci/`) a aplikacją (`unity/`). Zmieniając format, zmieniajcie ten plik w tym samym commicie co kod po obu stronach.

```
Unicorn Hybrid Black ──Bluetooth──▶ Unicorn Suite ──LSL──▶ bci/lsl_bridge.py ──UDP, JSON──▶ Unity
                                                                 ▲                          │
                                        LabRecorder ◀──LSL───────┴──────UDP (znaczniki)─────┘
```

Most (`bci/lsl_bridge.py`) może działać na innym komputerze niż gra, np. na laptopie z Windowsem, do którego podłączony jest Unicorn. LSL znajduje strumienie w sieci lokalnej sam; jeśli sieć blokuje multicast, adres komputera ze strumieniem trzeba podać w konfiguracji LSL (`KnownPeers`).

## 1. Wejście: Unicorn → `bci/`

| | |
|---|---|
| Kanały EEG | 8 — domyślny montaż: Fz, C3, Cz, C4, Pz, PO7, Oz, PO8 |
| Próbkowanie | 250 Hz |
| Transport | LSL, odbiera `bci/lsl_bridge.py` |
| Nazwa i typ strumienia LSL | TODO: sprawdzić po podłączeniu (`python bci/lsl_bridge.py --list`). Most domyślnie bierze pierwszy strumień typu `EEG`, inny wskazuje `--name` |
| Liczba i kolejność kanałów w strumieniu | TODO: sprawdzić — oprócz EEG mogą być akcelerometr, żyroskop, bateria, licznik próbek. Most liczy `focus` z kanałów 0–4 (Fz, C3, Cz, C4, Pz), inne wskazuje `--channels` |

## 2. Wyjście: `bci/` → Unity

- **Transport:** UDP na `127.0.0.1:5005`. Jeśli gra działa na innym komputerze, podajcie jego IP.
- **Wiadomość:** jeden datagram = jeden obiekt JSON w UTF-8.
- Pola są płaskie i mają stałe typy, żeby Unity mogło je parsować przez `JsonUtility.FromJson`.

| Pole | Typ | Opis |
|---|---|---|
| `type` | string | `"command"` albo `"metric"` |
| `name` | string | nazwa komendy lub metryki (listy niżej) |
| `value` | float | `command`: pewność klasyfikacji 0–1; `metric`: wartość 0–1 |
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

### Kto czego słucha

| Gra | Sygnały |
|---|---|
| Ogród dźwięków (`unity/`) | komendy `select`, `left`, `right`; metryka `focus` |
| Krojenie precla (`precel/`) | `laterality` przesuwa nóż, `jaw` tnie. Powiązania są w `precel/Assets/Resources/signals.json` i można je przepiąć na dowolny inny sygnał bez zmian w kodzie |

## 3. Znaczniki: Unity → `bci/`

Gra wysyła znacznik przy każdym zdarzeniu rozgrywki. Służą do cięcia EEG na epoki (kalibracja P300) i do oceny wyników gracza.

- **Transport:** UDP na port `5006` komputera, z którego przychodzi sygnał BCI (dopóki nic nie przyszło: `127.0.0.1`). Ten sam format JSON, `type` = `"marker"`.
- **Odbiór, jedno z dwóch** (oba słuchają na tym samym porcie):
  - `python bci/lsl_bridge.py` publikuje je jako strumień LSL `OgrodMarkers` (typ `Markers`, jeden kanał tekstowy `"<name>:<value>"`, np. `"target:2"`). LabRecorder nagra go razem z EEG ze wspólnym zegarem.
  - `python bci/marker_logger.py` zapisuje je do `data/raw/markers_<data>.csv`, gdy nie używacie LSL.
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

Czas `t` znacznika to chwila zlecenia odtworzenia dźwięku, nie chwila, w której dźwięk wyszedł z głośnika. Most przelicza go na zegar LSL, gdy gra działa na tym samym komputerze; przy grze na innym komputerze znacznik dostaje czas odbioru, czyli dochodzi opóźnienie sieci. Opóźnienie toru audio (rzędu kilkudziesięciu ms) trzeba zmierzyć przed kalibracją P300.

### Znaczniki gry Krojenie precla

| `name` | `value` | Kiedy |
|---|---|---|
| `session_start` | 0 | start gry |
| `round_start` | numer precla (1–5) | precel spada na deskę |
| `cut_start` | pozycja noża | sygnał cięcia przekroczył próg, nóż zaczyna opadać |
| `cut` | pozycja noża | precel przecięty |
| `result` | różnica masy połówek w % | połówki zważone |
| `summary` | suma punktów | koniec zawodów |
