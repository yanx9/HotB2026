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

| `name` | Znaczenie |
|---|---|
| TODO | |

### Metryki

| `name` | Znaczenie |
|---|---|
| TODO | |

Komendy `left`, `right`, `select` i metryka `focus` w atrapie (`bci/mock_sender.py`) to tylko przykład — podmieńcie je na ustalone tutaj.
