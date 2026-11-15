# Kalibracja BCI — Unicorn Hybrid Black przez g.Pype

Kalibrator i tor gry to ten sam potok g.Pype, bez LSL i bez pośredniego UDP z surowymi próbkami:

```text
Unicorn → g.Pype → filtrowanie / klasyfikacja → UDP JSON → Unity

calibrate.py      HybridBlack → Highpass 1 Hz → Bandstop 48–52 Hz → SampleTap → kalibracja → profil JSON
bci_pipeline.py   HybridBlack → Highpass 1 Hz → Bandstop 48–52 Hz → SignalClassifier (+ profil) → UdpJsonSender → Unity
```

Oba programy budują początek potoku tą samą funkcją (`build_front` w `bci/gpype_nodes.py`), więc profil powstaje na sygnale po tych samych filtrach, na których później działa klasyfikator. **Opaskę trzyma naraz jeden proces**: po kalibracji wybierz „Rozłącz” albo zamknij kalibrator, potem uruchom `bci_pipeline.py`.

Aplikacja `bci/calibrate.py` ma cztery widoki:

| Widok / sygnał | Zadanie | Cecha | Protokół domyślny |
|---|---|---|---|
| `eyes_closed` | oczy otwarte / zamknięte | alfa względna: mediana z PO7 i PO8 z log(moc 8–13 Hz / moc 4–7 + 15–30 Hz); okno 2 s | 10 par, 6 s spoczynku + 6 s zadania, 1,5 s na ustalenie |
| `right_hand` | spoczynek / wyobrażanie ruchu prawej dłoni | log mocy 8–30 Hz C4 minus log mocy C3; okno 1 s | 12 par, 6 + 6 s |
| `left_hand` | spoczynek / wyobrażanie ruchu lewej dłoni | log mocy 8–30 Hz C3 minus log mocy C4; okno 1 s | 12 par, 6 + 6 s |
| `jaw` | rozluźnienie / lekkie zaciśnięcie szczęki | log10 mocy 55–95 Hz z 8 kanałów EEG; okno 0,25 s | 10 par, 4 + 2 s |

**Detektor oczu (cechy v2).** Zamknięcie oczu podnosi rytm alfa nad korą wzrokową (efekt Bergera). Cecha jest stosunkiem mocy alfa do pasm sąsiednich, a nie mocą bezwzględną:

- gorszy kontakt elektrody mnoży wszystkie pasma tak samo, więc stosunek się nie zmienia;
- EMG szczęki podnosi mianownik, więc nie udaje zamkniętych oczu.

Każdy kanał jest oceniany osobno. Płaski albo przekraczający próg artefaktów kanał wypada tylko z bieżącego okna, a okno jest odrzucane dopiero wtedy, gdy nie zostaje żaden kanał. Oz i Pz nie są używane.

Na nagraniach testowych z tą opaską:

| Wariant | Odrzucone okna | Kalibracja | Fałszywe zamknięcia przy otwartych oczach |
|---|---|---|---|
| poprzednia cecha (Pz, PO7, **Oz**, PO8) | ok. 40% | nie powstawała (`insufficient_data`) | — |
| moc bezwzględna z PO7/PO8 | — | — | ok. 0,9/min |
| alfa względna z PO7/PO8 | 1–13% | — | ok. 0,2/min |

Oz leży na szwie czepka. Pz nie zwiększał rozdzielności.

Odrzucone okno (mrugnięcie, ruch głowy, krótki zacisk szczęki) nie przełącza oczu na „otwarte”. Detektor trzyma ostatnią decyzję do 1,5 s (`hold_s` w profilu) i dopiero potem zgłasza `valid=0`. Czas reakcji wynosi ok. 2,5–3 s od zamknięcia i ok. 0,3–2,5 s od otwarcia. Składają się na niego okno 2 s, wygładzanie 0,4 s i potwierdzenie 0,3 s.

Profile zapisane przed tą zmianą (`unicorn-spectral-v1`) mają inną skalę cechy i są odrzucane. Trzeba wtedy wykonać kalibrację ponownie.

Szczęka jest detektorem aktywności **mięśniowej (EMG)**. Kalibracje rąk wymagają wyobrażania odczucia ruchu bez fizycznego poruszania dłonią i napinania mięśni. Oczy są pomiarem reaktywności alfa, nie miernikiem relaksu. Są to eksperymentalne cechy sterujące, nie diagnoza ani walidacja terapeutyczna.

## Instalacja

Python **3.10 lub nowszy**, jedno środowisko dla kalibratora i potoku:

```sh
python -m venv bci/.venv
source bci/.venv/bin/activate          # Windows: bci\.venv\Scripts\activate
python -m pip install -r bci/requirements.txt
python bci/calibrate.py
```

`requirements.txt` instaluje `gpype[gui,devices]`: g.Pype, Qt (PySide6) i sterownik Unicorna (Windows x64, Linux x86_64, macOS arm64). Opaskę trzeba wcześniej sparować z komputerem przez Bluetooth. Stare środowisko `bci/.venv-calibration` (Python 3.9, Qt 6.8) nie jest już używane; można je usunąć.

## Przebieg

1. Podaj identyfikator osoby/sesji i ścieżkę JSON; domyślnie `data/calibration/participant.json`.
2. Przy kilku opaskach w zasięgu wpisz numer seryjny; puste pole oznacza ostatnio używaną albo jedyną widoczną. Wybierz częstotliwość sieci (w Europie 50 Hz). Wybierz **Połącz** i poczekaj na status „dane aktualne”; łączenie przez Bluetooth trwa kilka sekund.
3. Licznik ramek powinien rosnąć. Liczba resynchronizacji czasu powinna pozostać mała; rośnie, gdy Bluetooth gubi dane.
4. Sprawdź oba tony i potwierdź, że je słyszysz. **Wysoki, długi ton = zadanie; dwa krótkie, niskie = spoczynek**. W kalibracji oczu oznacza to odpowiednio zamknięcie i otwarcie oczu. Osobny końcowy ton oznacza ukończenie.
5. Wybierz dowolny z czterech widoków i uruchom kalibrację. Po przygotowaniu następują naprzemienne fazy spoczynku i zadania. Nie musisz wykonywać wszystkich czterech naraz. Przycisk **Przerwij** zachowuje poprzednio zapisany wynik.
6. Po pełnym przebiegu program pokazuje parametry, liczbę przyjętych prób i wynik walidacji oraz zapisuje profil automatycznie. Kolejne kalibracje aktualizują tylko swój wpis w tym samym profilu.
7. Po ukończeniu można obserwować podgląd 0–1 / AKTYWNY w każdym widoku. Powtórzenie kalibracji tego samego sygnału zastępuje poprzedni wynik po pełnym przebiegu — również wtedy, gdy nowy wynik jest negatywny. Nieudany wynik wyłącza detektor.

Czas 1 s po instrukcji (0,5 s dla szczęki) jest wyłączony z analizy. Zbierane są niepokrywające się okna. Wykryte przerwy, płaskie kanały i duże amplitudy są odrzucane lub zerują okno analizy. Brak świeżych danych przez ponad 2 s przerywa sesję bez zapisywania częściowego wyniku.

**Czas próbek.** Kalibrator nadaje próbkom równą siatkę 250 Hz na zegarze komputera i zakotwicza ją na nowo po przerwie albo dryfie większym niż 0,15 s. Bluetooth oddaje dane paczkami, więc dokładność jest rzędu dziesiątek milisekund. To wystarcza dla faz trwających sekundy; nie jest to tor do ERP / P300.

Filtr zaporowy usuwa wąskie pasmo wokół częstotliwości sieci, a pasmo szczęki 55–95 Hz pozostaje nietknięte. Pasmo szczęki i próg artefaktów można zmienić w interfejsie; zmiana wymaga nowej kalibracji. Nie podnoś progu artefaktów tylko po to, by uzyskać pozytywny wynik.

## Parametry wynikowe

Profil jest wersjonowanym JSON-em zawierającym:

- `schema_version`, `feature_version`, identyfikator osoby/sesji, czas, znacznik `simulated`;
- dane toru (`stream.transport=gpype`, `stream.filters`: filtry potoku) i `feature_config`: montaż, kanały, próbkowanie, pasmo szczęki, próg artefaktów;
- niezależne wpisy w `calibrations`: `eyes_closed`, `right_hand`, `left_hand`, `jaw`;
- dla każdego wpisu protokół, czasy faz, cechy z poszczególnych prób i status;
- dla modeli możliwych do dopasowania: `rest_center`, `active_center`, `scale`, progi aktywacji/zwolnienia, czas potwierdzenia `dwell_s`, wygładzanie `smoothing_s`;
- wyniki walidacji i jawne listy prób treningowych/kontrolnych.

Plik nie zawiera surowego EEG ani powiązań z czynnościami gier. Zapisy są atomowe. `data/calibration/` jest ignorowane przez Git. Dla innej osoby wybierz nowy plik; program odrzuca profil z innego toru (LSL, surowe UDP, inne filtry) zamiast łączyć różne konfiguracje. Zalecana jest kontrola działania po każdym ponownym założeniu czepka.

Statusy:

- `ready`: wstępne kryteria rozdzielności spełnione; można sprawdzić sterowanie;
- `insufficient_data`: mniej niż 6 pełnych par lub mniej niż 60% pełnych par; każda wymaga przynajmniej dwóch poprawnych okien na fazę;
- `not_separable`: brak oczekiwanego kierunku zmiany lub zbyt słabe wyniki.

Ostatnie 25% pełnych par (minimum 2) służy wyłącznie do walidacji. Progi są dopasowywane na wcześniejszych próbach i nie są ponownie dostrajane na zbiorze kontrolnym. Kryteria `ready`: trafność zbalansowana prób ≥75%, okien ≥70%, swoistość ≥75%, czułość ≥50%, trafność treningowa ≥75%, rozdzielenie środków co najmniej jedno odchylenie standardowe i poprawny kierunek zmiany. To mały test wewnątrz jednej sesji, nie wiarygodna estymacja skuteczności poza sesją.

**Dwie osobne kalibracje rąk porównują każdą rękę ze spoczynkiem. Nie stanowią testu klasyfikacji lewa kontra prawa.** Skalarna cecha może nie działać dla danej osoby. Bez wystarczającej rozdzielności potrzebny jest inny model / montaż, a nie odwracanie progu. Ruchy, napięcie i zmiany kontaktu elektrod nadal mogą naśladować pożądane zmiany; sam wynik kalibracji nie dowodzi pochodzenia korowego.

## Potok i dowolna gra Unity

```sh
python bci/bci_pipeline.py --profile data/calibration/participant.json
python bci/bci_pipeline.py --profile data/calibration/participant.json --host 192.168.1.20   # gra na innym komputerze
python bci/bci_pipeline.py                                   # bez profilu: nieskalibrowane heurystyki
```

Potok wczytuje profil raz, przy starcie; po ponownej kalibracji uruchom go ponownie. Częstotliwość filtru sieciowego bierze z profilu. Okno g.Pype pokazuje EEG po filtrach i sygnały idące do gry; `--no-gui` je wyłącza (na macOS sterownik Unicorna potrzebuje pętli zdarzeń okna). Bez profilu potok wysyła heurystyki `focus`, `laterality` i `jaw` liczone względem ostatniej minuty, każdą z flagą `_valid`.

Niezależny od gier format UDP pozostaje płaski: `type`, `name`, `value`, `t`. Z profilem:

| Metryka | Zakres | Znaczenie |
|---|---|---|
| `eyes_closed`, `right_hand`, `left_hand`, `jaw` | 0–1 | znormalizowana odległość od spoczynku; nie prawdopodobieństwo |
| `<nazwa>_active` | 0 albo 1 | próg z histerezą i wymaganym czasem potwierdzenia |
| `<nazwa>_valid` | 0 albo 1 | dostępny model i aktualne, zaakceptowane okno |
| `laterality` | −1–1 | prawy minus lewy wynik; wymaga dwóch gotowych modeli |
| `laterality_valid` | 0 albo 1 | ważność osi; równoczesna aktywacja obu rąk daje nieważne zero |
| `focus`, `focus_valid` | 0–1 | dotychczasowa, **nieskalibrowana** heurystyka i ważność |

`active=0` nie oznacza błędu pomiaru; do tego służy `valid=0`. Przy braku modelu albo artefakcie potok wysyła neutralne zero zamiast starej wartości. Wyjątek: detektor z `hold_s > 0` (oczy) przez ten czas trzyma ostatnią decyzję z `valid=1`; gdy dane z opaski ustaną, przestaje nadawać i gra po sekundzie uznaje sygnał za nieobecny. Kalibrowana aktywność szczęki blokuje wyniki oczu i rąk, lecz ten mechanizm nie jest pełnym systemem usuwania EMG. Potok dodatkowo wysyła komendy `left`/`right` co 0,5 s podczas potwierdzonej aktywacji, zgodne z odbiornikiem ogrodu. `--no-commands` pozostawia same metryki. Wartość komendy to wynik 0–1, nie skalibrowane prawdopodobieństwo.

- **Ogród**: obsługuje `left`, `right`, `focus`. Kalibracja nie generuje P300 ani komendy `select`.
- **Precel**: `laterality` i `jaw_active` (bez profilu zastępczo `jaw`); mapowanie na akcje jest w `Resources/signals.json`.
- **Mrok**: `eyes_closed_active` steruje zamykaniem oczu i ładowaniem skanu; wymaga profilu z kalibracją oczu.
- **Nowe gry**: odbierają ten sam protokół i przypisują nazwy do swoich akcji. Kod kalibracji, profile i potok pozostają bez zmian.

Przy dwóch grach jednocześnie użyj różnych portów odbiorczych:

```sh
python bci/bci_pipeline.py --profile data/calibration/participant.json --port 5005 --target 127.0.0.1:5015
```

Drugi odbiornik Unity musi mieć ustawiony port 5015.

## Znaczniki

Potok przyjmuje znaczniki gier na UDP **5006** i zapisuje je do `data/raw/bci_markers_*.jsonl` (zmiana ścieżki: `--marker-file`). Wiersz zawiera `marker`, `value`, `t_game_unix` i `t_received_unix`; to nie jest nagranie EEG. `--no-markers` zwalnia ten port, np. dla `marker_logger.py`.

Fazy kalibracji (numer próby, czasy) zapisują się w profilu w polu `events`.

## Bez urządzenia

`--fake` podmienia `HybridBlack` na węzeł `SyntheticUnicorn`; reszta potoku jest ta sama. Źródło dodaje przydźwięk sieci i powolny dryf, więc filtry mają co usuwać.

```sh
python bci/calibrate.py --fake                 # profil data/calibration/fake.json, oznaczony simulated=true
python bci/bci_pipeline.py --fake --allow-simulated --profile data/calibration/fake.json
```

W kalibratorze źródło syntetyczne udaje zadanie, o które kalibrator właśnie prosi. W `bci_pipeline.py --fake` zadania zmieniają się w 27-sekundowym cyklu: oczy, prawa ręka, lewa ręka, szczęka. Profil syntetyczny nie potwierdza możliwości użytkownika ani urządzenia; potok przyjmuje go tylko z `--fake --allow-simulated`, a profilu rzeczywistego nie przyjmuje ze źródłem syntetycznym.

## Testy

```sh
python -m unittest discover -s bci/tests -v
```

Testy obejmują ekstrakcję cech na sygnałach syntetycznych, oddzielne kalibracje, odrzucenie braku efektu, progi i histerezę, brakujące modele, zapis profilu, zgodność profilu z torem, węzły potoku (klasyfikator z profilem i bez, przerwę w danych, jednostki, format wiadomości i komendy) oraz krótki przebieg prawdziwego potoku g.Pype ze źródłem syntetycznym. Testy Qt sprawdzają cztery widoki i pełną kalibrację na wirtualnym zegarze. Testy wymagające g.Pype albo Qt są pomijane, gdy ich nie ma.

```sh
python bci/tests/integration_pipeline.py
```

Test całego toru w czasie rzeczywistym (około minuty): kalibracja szczęki przez potok g.Pype, zapis profilu, osobny proces `bci_pipeline.py`, odbiór JSON tak jak w grze i zapis znacznika. Wszystko na danych syntetycznych; nie zastępuje testu z opaską.
