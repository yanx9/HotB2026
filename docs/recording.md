# Nagrywanie sesji i ocena detektora oczu (Mrok)

Każde uruchomienie kalibratora i potoku gry zapisuje EEG. Gra wysyła prawdziwy stan oczu (spacja), więc po sesji można policzyć, jak często detektor się myli, i porównywać kolejne wersje algorytmu na tych samych nagraniach.

```text
Unicorn ─► g.Pype ─┬─ surowe EEG ───────────┐
                   ├─ filtry ─ EEG po filtrach ┤
                   │      └─ SignalClassifier ─┼─ wyjście detektora ──► UDP 5005 ─► Mrok
                   └─ gp.Marker ◄── UDP 5006 ◄─┼──────────────── znaczniki gry (spacja = prawda)
                                               ▼
                         data/raw/<sesja>_<czas>/eeg_*.h5   (jedna oś: próbka N = ta sama chwila)
                                                events.csv, session.json
```

Znacznik z gry trafia na próbkę EEG z chwili, w której potok go odebrał. Na jednym komputerze UDP dokłada < 1 ms, w sieci lokalnej kilka ms, a Bluetooth opaski ~40 ms (stałe). Przy decyzjach o oczach, które zapadają po 1–3 s, wszystkie te opóźnienia są pomijalne.

## Co jest w folderze nagrania

| Plik | Zawartość |
|---|---|
| `eeg_<czas>.h5` | zbiór `data`: wiersz 0 = czas [s], potem kolumny z `session.json` → `columns` |
| `events.csv` | każde zdarzenie: nazwa, kod, wartość (np. zasięg skanu), czas gry, czas odbioru |
| `session.json` | kolumny, kody znaczników, profil, filtry, ustawienia cech, rodzaj sesji |
| `eval_eyes.json`, `eval_eyes.png`, `eval_jaw.json`, `eval_jaw.png` | wynik `evaluate.py` (po ocenie) |

Kolumny w `eeg_*.h5`:

| Kolumna | Opis |
|---|---|
| `raw_Fz … raw_PO8` | surowe EEG w µV, przed filtrami |
| `flt_Fz … flt_PO8` | po filtrze 1 Hz i zaporowym 50 Hz, czyli dokładnie to, co widzi detektor |
| `marker` | kod zdarzenia w próbce, w której nadeszło; 0 w pozostałych |
| `out_*` | sygnały wysyłane do gry (tylko potok gry) |

Odczyt w Pythonie: `from recording import load_recording; data, events, meta = load_recording(folder)`. Każde zdarzenie ma wtedy pole `sample`, czyli indeks próbki w pliku.

Pliki zapisują się na bieżąco i są czytelne także po nagłym zamknięciu programu. Godzina gry to ok. 230 MB. `data/raw/` nie trafia do Gita.

## Tryby Mroka (F2)

**Spacja zawsze oznacza prawdę: trzymaj ją, gdy masz zamknięte oczy.** Gra wysyła wtedy znaczniki `truth_closed` i `truth_open`. Tak samo **F trzymaj, gdy zaciskasz szczękę** (`truth_jaw_on` / `truth_jaw_off`). Tryb decyduje tylko o tym, co steruje grą.

| Tryb | Grą steruje | Po co |
|---|---|---|
| **KLAWISZ** | spacja | zbieranie danych: detektor działa i się nagrywa, ale nie wpływa na grę |
| **EEG** | opaska (`eyes_closed_active`) | prawdziwa gra i końcowy sprawdzian; spacja niczego w grze nie zmienia |

Tryb widać w lewym górnym rogu. Zmiana trybu trafia do nagrania (`mode_keyboard` / `mode_eeg`). W trybie EEG bez danych z potoku spacja steruje jak dawniej, żeby dało się grać bez opaski.

## Przebieg sesji

Raz na komputerze z Pythonem: `pip install -r bci/requirements.txt`, opaska sparowana przez Bluetooth.

**1. Załóż opaskę.** Dociśnij PO7 i PO8. Na nich opiera się detektor oczu.

**2. Kalibracja** (ok. 3 min dla oczu):

```sh
python bci/calibrate.py --subject s01 --profile data/calibration/s01.json
```

Połącz, sprawdź dźwięki, w widoku **Zamykanie oczu** uruchom kalibrację i wykonuj polecenia: wysoki, długi ton = zamknij; dwa krótkie, niskie = otwórz. Wynik musi mieć status `ready`. Potem wybierz **Rozłącz**, bo opaskę trzyma naraz jeden program. Kalibracja nagrywa się do `data/raw/kalibracja_s01_<czas>/`.

**3. Potok gry:**

```sh
python bci/bci_pipeline.py --profile data/calibration/s01.json --session mrok_s01
```

Jeśli Unity działa na innym komputerze, dodaj `--host <IP komputera z grą>`. Na komputerze z potokiem zapora Windows musi wpuszczać UDP 5006 (znaczniki), a na komputerze z grą UDP 5005. Konsola wypisuje folder nagrania.

**4. Mrok.** W Unity Play. W panelu w prawym dolnym rogu ma być widać `potok BCI: <adres>`.

- **Zbieranie danych:** tryb KLAWISZ (F2). Graj normalnie, przytrzymując spację zawsze, gdy zamykasz oczy, i puszczając, gdy otwierasz. Zamykaj oczy także na różny czas (1–8 s), bo detektor ma działać w każdej sytuacji. 5–10 min na sesję.
- **Sprawdzian:** tryb EEG. Graj oczami, nadal trzymając spację przy zamkniętych oczach.

**5. Zakończ potok:** Ctrl+C w konsoli albo zamknij okno g.Pype. Konsola wypisze `Zapisano nagranie: …`.

**6. Ocena:**

```sh
python bci/evaluate.py data/raw/mrok_s01_<czas>                    # oczy i szczęka
python bci/evaluate.py data/raw/mrok_s01_<czas> --signal jaw       # albo --signal eyes
python bci/evaluate.py data/raw/mrok_s01_<czas> --profile data/calibration/inny.json
python bci/evaluate.py data/raw/kalibracja_s01_<czas> --profile data/calibration/s01.json
```

Oczy i szczękę ocenia ten sam kod i raport ma te same pola. Różnią się tylko nazwy znaczników i skala czasu zdarzeń:

| | oczy | szczęka |
|---|---|---|
| prawda (klawisz w grze) | spacja: `truth_closed` / `truth_open` | F: `truth_jaw_on` / `truth_jaw_off` |
| decyzja potoku | `out_eyes_closed_active` | `out_jaw_active` |
| potok wysłał zmianę | `pipe_eyes_closed` / `pipe_eyes_open` | `pipe_jaw_on` / `pipe_jaw_off` |
| reakcja gry | `eyes_closed` / `eyes_open` | `sprint_on` / `sprint_off` |
| najkrótsze liczone zdarzenie | 1 s | 0,5 s |
| wynik | `eval_eyes.json`, `eval_eyes.png` | `eval_jaw.json`, `eval_jaw.png` |

W raporcie `closures` to zdarzenia „włączone” (zamknięcia oczu albo zaciśnięcia), a `close` / `open` to włączenie / wyłączenie. `evaluate_eyes.py` zostało jako skrót do `evaluate.py --signal eyes`. Test całej ścieżki bez opaski: `python bci/tests/integration_evaluate.py`.


Pierwsze polecenie ocenia decyzje, które zapadły na żywo. Drugie liczy detektor od nowa z kolumn `flt_*` tym samym kodem, którego używa potok, np. dla nowego profilu albo po zmianie w `processing.py`. Trzecie ocenia nagranie kalibracji, a prawdą są wtedy fazy kalibracji.

Przykładowy wynik:

```text
mrok_s01_20261115_120000: 8.4 min · prawda: spacja (truth_*) · decyzje z nagrania (na żywo)
  zamknięcia ≥1 s wykryte: 23/24  (pominięte w s: [312.4])
  opóźnienie: zamknięcie 2.6 s, otwarcie 1.4 s (mediana)
  fałszywe zamknięcia: 0.3 na minutę z otwartymi oczami
  zgodność poza przejściami: 0.94 · sygnał ważny: 97%
```

| Miernik | Znaczenie | Cel |
|---|---|---|
| wykryte | zamknięcia ≥ 1 s, na które detektor zareagował przed otwarciem albo do 1 s po nim | ≥ 95% |
| opóźnienie zamknięcia / otwarcia | od wciśnięcia / puszczenia spacji do zmiany decyzji | ≤ 3 s / ≤ 1,5 s |
| fałszywe zamknięcia | włączenia detektora poza prawdziwym zamknięciem, na minutę z otwartymi oczami | < 0,5 |
| zgodność poza przejściami | odsetek chwil zgodnych z prawdą, bez 3 s po każdej zmianie | > 0,9 |
| sygnał ważny | odsetek decyzji z `valid=1` | > 95% |

Na wykresie `eval_eyes.png` niebieskie tło oznacza prawdę (spację), pomarańczowa linia decyzję detektora, a czerwone kropki fałszywe zamknięcia. Opóźnienie mierzone od spacji zawiera też refleks gracza, ok. 0,2–0,4 s.

## Sprawdzenie synchronizacji (raz, przed pierwszym zbieraniem danych)

W trybie KLAWISZ zaciśnij kilka razy mocno szczękę, za każdym razem w tej samej chwili wciskając spację. W nagraniu wybuch EMG na `raw_C3` / `raw_C4` powinien zaczynać się 0–0,3 s od znacznika `truth_closed`, w każdym powtórzeniu tak samo. Duże albo pływające przesunięcie oznacza błąd synchronizacji, który trzeba usunąć przed zbieraniem danych.

## Zasady, żeby dane były coś warte

- Kalibracja przed każdym założeniem opaski, nagrania gry z tym samym profilem co kalibracja.
- Kilka osób i kilka sesji. Do strojenia używajcie części sesji, a wynik podawajcie na pozostałych (innych dniach albo osobach), nigdy na tych, na których stroiliście.
- Zapisujcie w nazwie sesji osobę i tryb, np. `--session mrok_s02_klawisz`.
