# Kalibracja BCI — Unicorn Hybrid Black

Osobna aplikacja desktopowa `bci/calibrate.py` ma cztery widoki:

| Widok / sygnał | Zadanie | Cecha | Protokół domyślny |
|---|---|---|---|
| `eyes_closed` | oczy otwarte / zamknięte | log mocy alfa 8–13 Hz z Pz, PO7, Oz, PO8; okno 2 s | 10 par, 6 s spoczynku + 6 s zadania |
| `right_hand` | spoczynek / wyobrażanie ruchu prawej dłoni | log mocy 8–30 Hz C4 minus log mocy C3; okno 1 s | 12 par, 6 + 6 s |
| `left_hand` | spoczynek / wyobrażanie ruchu lewej dłoni | log mocy 8–30 Hz C3 minus log mocy C4; okno 1 s | 12 par, 6 + 6 s |
| `jaw` | rozluźnienie / lekkie zaciśnięcie szczęki | log10 mocy 55–95 Hz z 8 kanałów EEG; okno 0,25 s | 10 par, 4 + 2 s |

Szczęka jest detektorem aktywności **mięśniowej (EMG)**. Kalibracje rąk wymagają wyobrażania odczucia ruchu bez fizycznego poruszania dłonią i napinania mięśni. Oczy są pomiarem reaktywności alfa, nie miernikiem relaksu. Są to eksperymentalne cechy sterujące, nie diagnoza ani walidacja terapeutyczna.

## Instalacja

Kalibrator: Python **3.9–3.13**, zalecany 3.11–3.13. Qt 6.8 jest przypięte dla zgodności m.in. ze starszym macOS; nie wspiera Pythona 3.14. Sam most i testy obliczeniowe mogą działać w dotychczasowym środowisku Python 3.14.

Z katalogu repozytorium (Windows: `py -3.12` zamiast `python3.12`):

```sh
python3.12 -m venv bci/.venv-calibration
# macOS / Linux
source bci/.venv-calibration/bin/activate
# Windows PowerShell: bci\.venv-calibration\Scripts\Activate.ps1
python -m pip install -r bci/requirements-calibration.txt
python bci/calibrate.py
```

Na tym komputerze środowisko `bci/.venv-calibration` jest utworzone z dostępnego `/usr/bin/python3` (3.9). Uruchomienie bez aktywowania środowiska:

```sh
bci/.venv-calibration/bin/python bci/calibrate.py
```

`pylsl` potrzebuje biblioteki natywnej liblsl tak jak dotychczasowy most. W przypadku braku biblioteki użyj instalacji opisanej w głównym README. Aplikacja nie łączy się bezpośrednio przez Bluetooth: najpierw włącz eksport LSL z Unicorna.

## Przebieg

1. Włącz Unicorn LSL, wybierz **Znajdź LSL**, wybierz nazwę i podaj identyfikator osoby/sesji oraz ścieżkę JSON. Ścieżka domyślna to `data/calibration/participant.json`.
2. Wybierz **Połącz**. Sprawdź częstotliwość, indeksy kanałów i jednostki. Automatyczne mapowanie rozpoznaje etykiety kanałów, więc radzi sobie także ze zmienioną kolejnością i strumieniami z kanałami dodatkowymi. IMU/bateria/licznik nie trafiają do cech EEG.
3. Jeżeli eksporter nie podaje etykiet, dopiero po sprawdzeniu kolejności zaznacz jej potwierdzenie: `Fz,C3,Cz,C4,Pz,PO7,Oz,PO8` jako pierwszych osiem kanałów. Gdy brakuje jednostek, wybierz jawnie µV lub V. Dane w V są przeliczane na µV.
4. Sprawdź oba tony i potwierdź, że je słyszysz. **Wysoki ton = zadanie; niski = spoczynek**. W kalibracji oczu oznacza to odpowiednio zamknięcie i otwarcie oczu. Osobny końcowy ton oznacza ukończenie.
5. Wybierz dowolny z czterech widoków i uruchom kalibrację. Po przygotowaniu następują naprzemienne fazy spoczynku i zadania. Nie musisz wykonywać wszystkich czterech naraz. Przycisk **Przerwij** zachowuje poprzednio zapisany wynik.
6. Po pełnym przebiegu program pokazuje parametry, liczbę przyjętych prób i wynik walidacji oraz zapisuje profil automatycznie. Kolejne kalibracje aktualizują tylko swój wpis w tym samym profilu.
7. Po ukończeniu można obserwować podgląd 0–1 / AKTYWNY w każdym widoku. Powtórzenie kalibracji tego samego sygnału zastępuje poprzedni wynik po pełnym przebiegu — również wtedy, gdy nowy wynik jest negatywny. Nieudany wynik wyłącza detektor.

Czas 1 s po instrukcji (0,5 s dla szczęki) jest wyłączony z analizy. Zbierane są niepokrywające się okna. Próbki sprzed fazy, przerwy w LSL, niefinitywne wartości, płaskie kanały i duże amplitudy nie powinny tworzyć poprawnych okien. Brak świeżych danych przez ponad 2 s przerywa sesję bez zapisywania częściowego wyniku.

Przy eksporcie filtrowanym poniżej 55 Hz standardowa cecha szczęki nie zadziała prawidłowo. Najpierw sprawdź ustawienia eksportera. Można zmienić pasmo w interfejsie, lecz niższe pasmo jest mniej specyficzne dla EMG; wymaga nowej kalibracji. Nie podnoś progu artefaktów tylko po to, by uzyskać pozytywny wynik.

## Parametry wynikowe

Profil jest wersjonowanym JSON-em zawierającym:

- `schema_version`, `feature_version`, identyfikator osoby/sesji, czas, znacznik `simulated`;
- dane źródła i `feature_config`: montaż, kanały, próbkowanie, skala do µV, pasmo szczęki, próg artefaktów;
- niezależne wpisy w `calibrations`: `eyes_closed`, `right_hand`, `left_hand`, `jaw`;
- dla każdego wpisu protokół, czasy faz, cechy z poszczególnych prób i status;
- dla modeli możliwych do dopasowania: `rest_center`, `active_center`, `scale`, progi aktywacji/zwolnienia, czas potwierdzenia `dwell_s`, wygładzanie `smoothing_s`;
- wyniki walidacji i jawne listy prób treningowych/kontrolnych.

Plik nie zawiera surowego EEG ani powiązań z czynnościami gier. Zapisy są atomowe. `data/calibration/` jest ignorowane przez Git. Dla innej osoby lub innego montażu wybierz nowy plik; program odrzuca niezgodny profil zamiast łączyć różne konfiguracje. Zalecana jest kontrola działania po każdym ponownym założeniu czepka.

Statusy:

- `ready`: wstępne kryteria rozdzielności spełnione; można sprawdzić sterowanie;
- `insufficient_data`: mniej niż 6 pełnych par lub mniej niż 60% pełnych par; każda wymaga przynajmniej dwóch poprawnych okien na fazę;
- `not_separable`: brak oczekiwanego kierunku zmiany lub zbyt słabe wyniki.

Ostatnie 25% pełnych par (minimum 2) służy wyłącznie do walidacji. Progi są dopasowywane na wcześniejszych próbach i nie są ponownie dostrajane na zbiorze kontrolnym. Kryteria `ready`: trafność zbalansowana prób ≥75%, okien ≥70%, swoistość ≥75%, czułość ≥50%, trafność treningowa ≥75%, rozdzielenie środków co najmniej jedno odchylenie standardowe i poprawny kierunek zmiany. To mały test wewnątrz jednej sesji, nie wiarygodna estymacja skuteczności poza sesją.

**Dwie osobne kalibracje rąk porównują każdą rękę ze spoczynkiem. Nie stanowią testu klasyfikacji lewa kontra prawa.** Skalarna cecha może nie działać dla danej osoby. Bez wystarczającej rozdzielności potrzebny jest inny model / montaż, a nie odwracanie progu. Ruchy, napięcie i zmiany kontaktu elektrod nadal mogą naśladować pożądane zmiany; sam wynik kalibracji nie dowodzi pochodzenia korowego.

## Most i dowolna gra Unity

```sh
python bci/lsl_bridge.py --profile data/calibration/participant.json --name NAZWA_LSL
```

Most wczytuje parametry raz, przy starcie. Po ponownej kalibracji uruchom go ponownie. Montaż, próbkowanie i jednostki muszą odpowiadać profilowi. W trybie profilu ustawienia cech pochodzą z JSON, a stare opcje `--c3`, `--c4`, `--channels`, `--jaw-channels`, `--jaw-band`, `--artifact-uv` dotyczą wyłącznie trybu bez profilu.

Niezależny od gier format UDP pozostaje płaski: `type`, `name`, `value`, `t`. Kalibrator nie zajmuje portu UDP gry.

| Metryka | Zakres | Znaczenie |
|---|---|---|
| `eyes_closed`, `right_hand`, `left_hand`, `jaw` | 0–1 | znormalizowana odległość od spoczynku; nie prawdopodobieństwo |
| `<nazwa>_active` | 0 albo 1 | próg z histerezą i wymaganym czasem potwierdzenia |
| `<nazwa>_valid` | 0 albo 1 | dostępny model i aktualne, zaakceptowane okno |
| `laterality` | −1–1 | prawy minus lewy wynik; wymaga dwóch gotowych modeli |
| `laterality_valid` | 0 albo 1 | ważność osi; równoczesna aktywacja obu rąk daje nieważne zero |
| `focus`, `focus_valid` | 0–1 | dotychczasowa, **nieskalibrowana** heurystyka i ważność |

`active=0` nie oznacza błędu pomiaru; do tego służy `valid=0`. Przy braku modelu, artefakcie lub utracie danych bridge wysyła neutralne zero zamiast starej wartości. Kalibrowana aktywność szczęki blokuje wyniki oczu i rąk, lecz ten mechanizm nie jest pełnym systemem usuwania EMG. Most dodatkowo wysyła komendy semantyczne `left`/`right` co 0,5 s podczas potwierdzonej aktywacji, zgodne z obecnym odbiornikiem ogrodu. `--no-commands` pozostawia same metryki. Pewność w starym polu `value` komendy jest tutaj wynikiem 0–1, nie skalibrowanym prawdopodobieństwem.

- **Ogród**: obecny odbiornik już obsługuje `left`, `right`, `focus`. Kalibracja nie generuje P300 ani komendy `select`.
- **Precel**: obecny odbiornik już obsługuje `laterality` i `jaw`. Pozostałe metryki są dostępne w `SignalHub`; mapowanie na akcje należy do `Resources/signals.json`.
- **Nowe gry**: odbierają ten sam protokół i przypisują nazwy do swoich akcji. Kod kalibracji, profile i most pozostają bez zmian.

Przy dwóch grach jednocześnie użyj różnych portów odbiorczych:

```sh
python bci/lsl_bridge.py --profile data/calibration/participant.json --port 5005 --target 127.0.0.1:5015
```

Drugi odbiornik Unity musi mieć ustawiony port 5015. `--host`/`--port` są aliasami dotychczasowych `--game-host`/`--game-port`.

Znaczniki gier są publikowane domyślnie w LSL **BciMarkers**. Dla zgodności ze starszymi sesjami podaj `--marker-name OgrodMarkers`. Kalibrator publikuje osobny strumień **BciCalibrationMarkers** z wartościami `calibration/<sygnał>/<faza>:<numer_próby>`. Zapisz oba strumienie w LabRecorderze, jeśli potrzebujesz surowych nagrań. Znaczniki faz to czas zlecenia wskazówki; nie mierzą opóźnienia wyjścia audio.

## Demonstracja bez urządzenia

```sh
python bci/calibrate.py --demo
```

Każdy widok dostaje syntetyczne zadanie/spoczynek zgodne z instrukcjami. Wynik zapisuje się domyślnie do `data/calibration/demo.json` i jest oznaczony `simulated=true`. Nie potwierdza możliwości użytkownika ani urządzenia.

Po zakończeniu czterech kalibracji można sprawdzić cały most (dwa terminale):

```sh
python bci/fake_unicorn.py --calibration-cycle
python bci/lsl_bridge.py --profile data/calibration/demo.json --name FakeUnicorn --allow-simulated
```

Źródło cykliczne nie podąża za instrukcjami kalibratora; służy do testu **gotowego** profilu. Do kalibracji demonstracyjnej używaj wbudowanego DEMO. Profil demonstracyjny nie jest dopuszczany do rzeczywistego źródła nawet z `--allow-simulated`.

## Testy

```sh
python -m unittest discover -s bci/tests -v
```

Testy obejmują rzeczywistą ekstrakcję cech na sygnałach syntetycznych, oddzielne kalibracje, odrzucenie braku efektu, progi i histerezę, straty danych, brakujące modele, zapis profilu, etykiety/jednostki i izolację kanałów dodatkowych. Test interfejsu wymaga osobno zależności Qt. Test strumieniowania LSL/UDP wymaga lokalnych gniazd sieciowych.

```sh
python bci/tests/integration_lsl.py
```

Ten test uruchamia prawdziwy proces mostu, syntetyczny strumień LSL, dwa odbiorniki UDP oraz kanał znaczników. Sprawdza cztery aktywacje, kierunki i neutralizację sygnałów po zatrzymaniu EEG. Trwa około 30 sekund.
