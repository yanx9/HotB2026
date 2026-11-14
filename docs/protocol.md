# Protokół

Kontrakt między częścią pythonową (`bci/`) a aplikacją (`unity/`). Zmieniając format, zmieniajcie ten plik w tym samym commicie co kod po obu stronach.

```
Unicorn Hybrid Black ──Bluetooth──▶ Unicorn Suite ──LSL──▶ bci/ (Python) ──UDP, JSON──▶ Unity
```

## 1. Wejście: Unicorn → `bci/`

| | |
|---|---|
| Kanały EEG | 8 — domyślny montaż: Fz, C3, Cz, C4, Pz, PO7, Oz, PO8 |
| Próbkowanie | 250 Hz |
| Transport | LSL |
| Nazwa i typ strumienia LSL | TODO: sprawdzić po podłączeniu (`pylsl.resolve_streams()`) |
| Liczba i kolejność kanałów w strumieniu | TODO: sprawdzić — oprócz EEG mogą być akcelerometr, żyroskop, bateria, licznik próbek |

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

| `name` | Znaczenie |
|---|---|
| `focus` | skupienie 0–1 z widma EEG; im wyższe, tym więcej świetlików i jaśniejsze rośliny |

## 3. Znaczniki: Unity → `bci/`

Gra wysyła znacznik przy każdym zdarzeniu rozgrywki. Służą do cięcia EEG na epoki (kalibracja P300) i do oceny wyników gracza.

- **Transport:** UDP na `127.0.0.1:5006`, ten sam format JSON, `type` = `"marker"`.
- **Odbiór:** `python bci/marker_logger.py` zapisuje je do `data/raw/markers_<data>.csv`. Gra zapisuje też własną kopię CSV w `Application.persistentDataPath`.
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

Czas `t` znacznika to chwila zlecenia odtworzenia dźwięku, nie chwila, w której dźwięk wyszedł z głośnika. Opóźnienie toru audio (rzędu kilkudziesięciu ms) trzeba zmierzyć przed kalibracją P300.
