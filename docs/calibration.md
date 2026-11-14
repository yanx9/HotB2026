# Kalibracja BCI — Unicorn Hybrid Black przez g.Pype

Domyślne wejście to **standardowy `gp.UDPSender`**, bez LSL. Kalibrator i most używają tego samego adaptera `gpype_source.py`, ekstrakcji cech oraz profilu. Każda gra Unity dostaje wspólny, niezależny od źródła protokół UDP/JSON.

```text
Unicorn → g.Pype HybridBlack → UDPSender → UDP 56000
                                            ├─ calibrate.py → profil JSON
                                            └─ bci_bridge.py + profil → UDP/JSON 5005 → Unity
```

Kalibrator i most są **alternatywnymi odbiornikami tego samego portu**: po kalibracji wybierz „Rozłącz”, następnie uruchom most. Nie uruchamiaj ich równocześnie na porcie 56000.

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

Odbiór g.Pype wymaga tylko NumPy/SciPy, a interfejs dodatkowo Qt. **Nie wymaga `pylsl`, liblsl ani instalacji g.Pype w środowisku kalibratora.** g.Pype i sterownik urządzenia działają w procesie nadajnika, który może mieć własne środowisko.

## Konfiguracja g.Pype

Wyślij pełnopasmowe EEG z `HybridBlack` do standardowego `UDPSender`. Przykładowy skrypt dla API g.Pype 4.x:

```sh
# W środowisku, w którym już działa g.Pype z obsługą GUI i Unicorna:
python bci/gpype_unicorn_sender.py
# W środowisku kalibratora, drugi terminal:
python bci/calibrate.py
```

Skrypt ustawia `channel_count=8`, `frame_size=4`, wyłącza dodatkowe kanały i łączy źródło z `gp.UDPSender(ip="127.0.0.1", port=56000, frame_size_out=4)`. Otwiera także podgląd g.Pype; główna pętla `MainApp.run()` jest potrzebna sterownikowi Bluetooth na macOS. Przykład oparto na dokumentacji; uruchomienie z fizycznym urządzeniem pozostaje do sprawdzenia.

Domyślne ustawienia odbiornika:

| Ustawienie | Wartość |
|---|---|
| Adres / port wejścia EEG | `127.0.0.1:56000` |
| Format | binarny `float64`, little-endian; kolejno próbki, wewnątrz próbki kanały |
| Kanały | `Fz,C3,Cz,C4,Pz,PO7,Oz,PO8` |
| Próbkowanie / jednostki EEG | `250 Hz`, `µV` |
| Ramka | `4` próbki, czyli `4 × 8 × 8 = 256` bajtów na datagram |

To **surowe próbki**, nie wyjście klasyfikatora, moc pasm ani JSON. Parametry są jawne, ponieważ standardowy pakiet nie przenosi metadanych, czasu akwizycji ani numeru sekwencyjnego. Format potwierdzają [dokumentacja UDPSendera](https://gpype.gtec.at/content/7_sdk_reference/backend_sinks/udp_sender.html) i [przykład odbiornika g.tec](https://gpype.gtec.at/content/2_gpype_training/season4_crossing_borders/s4e3_sending_udp.html). Konfigurację urządzenia opisuje [HybridBlack](https://gpype.gtec.at/_modules/gpype/backend/sources/hybrid_black.html).

Jeśli Twój istniejący pipeline wysyła inną ramkę, zmień „Próbki / pakiet” lub `--frame-samples`. Wartość `0` dopuszcza zmienną ramkę, ale utrudnia wykrywanie pomylonego układu. Ramka może zawierać najwyżej 250 ms danych; dla Unicorna zalecane są 4 próbki. Dodatkowe kanały trzeba wpisać w „Kanały w pakiecie” w dokładnej kolejności, np. `Fz,C3,Cz,C4,Pz,PO7,Oz,PO8,ACC_X,ACC_Y,ACC_Z,GYRO_X,GYRO_Y,GYRO_Z,BATTERY,COUNTER,VALIDATION`. Same EEG są wybierane po etykietach; reszta nie wchodzi do cech. Odbiornik nie interpretuje obecnie licznika ani flagi walidacji urządzenia.

Przy nadajniku na innym komputerze ustaw jego `UDPSender.ip` na adres komputera z kalibratorem; w kalibratorze użyj `--udp-host 0.0.0.0`. `--udp-host` oznacza **lokalny adres nasłuchu**, a `--host` w moście oznacza adres gry. Te adresy pełnią różne role.

## Przebieg

1. Wybierz wejście **g.Pype UDPSender**. Podaj identyfikator osoby/sesji i ścieżkę JSON; domyślnie `data/calibration/participant_gpype.json`.
2. Sprawdź adres, port, próbkowanie, liczbę próbek w pakiecie, kolejność wszystkich kanałów i jednostki względem pipeline'u g.Pype. Wybierz **Połącz**, uruchom nadajnik i poczekaj na status „dane aktualne”.
3. Licznik pakietów powinien rosnąć, licznik odrzuceń pozostać zerowy. Błędny format lub zajęty port daje komunikat. Dane w V są przeliczane na µV. Odbiornik akceptuje jednego nadawcę; po restarcie procesu g.Pype rozłącz i połącz kalibrator ponownie.
4. Sprawdź oba tony i potwierdź, że je słyszysz. **Wysoki ton = zadanie; niski = spoczynek**. W kalibracji oczu oznacza to odpowiednio zamknięcie i otwarcie oczu. Osobny końcowy ton oznacza ukończenie.
5. Wybierz dowolny z czterech widoków i uruchom kalibrację. Po przygotowaniu następują naprzemienne fazy spoczynku i zadania. Nie musisz wykonywać wszystkich czterech naraz. Przycisk **Przerwij** zachowuje poprzednio zapisany wynik.
6. Po pełnym przebiegu program pokazuje parametry, liczbę przyjętych prób i wynik walidacji oraz zapisuje profil automatycznie. Kolejne kalibracje aktualizują tylko swój wpis w tym samym profilu.
7. Po ukończeniu można obserwować podgląd 0–1 / AKTYWNY w każdym widoku. Powtórzenie kalibracji tego samego sygnału zastępuje poprzedni wynik po pełnym przebiegu — również wtedy, gdy nowy wynik jest negatywny. Nieudany wynik wyłącza detektor.

Czas 1 s po instrukcji (0,5 s dla szczęki) jest wyłączony z analizy. Zbierane są niepokrywające się okna. Błędne datagramy, wykryte przerwy, płaskie kanały i duże amplitudy są odrzucane lub zerują okno analizy. Brak świeżych danych przez ponad 2 s przerywa sesję bez zapisywania częściowego wyniku.

**Czas UDP jest szacowany po stronie odbiornika.** Próbki dostają równomierną siatkę 250 Hz; po przerwie lub nadmiernym dryfie odbiornik zaczyna nowe okno. Kolejki ponad 250 ms i dane po długiej pauzie odczytu są odrzucane. Bez numerów sekwencyjnych nie da się rozpoznać każdej utraty, duplikacji lub zmiany kolejności pakietów ani wykryć każdego błędnego ustawienia kanałów/jednostek. Nie należy traktować tej ścieżki jako precyzyjnej synchronizacji ERP/P300; cztery bieżące kalibracje opierają się na dłuższych fazach spoczynku i zadania.

Przy eksporcie filtrowanym poniżej 55 Hz standardowa cecha szczęki nie zadziała prawidłowo. Najpierw sprawdź ustawienia eksportera. Można zmienić pasmo w interfejsie, lecz niższe pasmo jest mniej specyficzne dla EMG; wymaga nowej kalibracji. Nie podnoś progu artefaktów tylko po to, by uzyskać pozytywny wynik.

## Parametry wynikowe

Profil jest wersjonowanym JSON-em zawierającym:

- `schema_version`, `feature_version`, identyfikator osoby/sesji, czas, znacznik `simulated`;
- dane źródła (`stream.transport=gpype_udp`, `stream.input`, opis szacowania czasu) i `feature_config`: montaż, kanały, próbkowanie, skala do µV, pasmo szczęki, próg artefaktów;
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
python bci/bci_bridge.py --profile data/calibration/participant_gpype.json
# Ta sama implementacja dostępna pod dotychczasową nazwą:
python bci/lsl_bridge.py --source gpype --profile data/calibration/participant_gpype.json
```

Najpierw **rozłącz kalibrator**, aby zwolnić port EEG. Most wczytuje parametry oraz ustawienia wejścia UDP raz, przy starcie. Po ponownej kalibracji lub restarcie nadajnika uruchom most ponownie. Montaż, próbkowanie, jednostki i format ramki muszą odpowiadać profilowi. Można zmienić adres / port odbioru bez zmiany modelu, np. `--udp-port 56010`. Zmiana konfiguracji pomiaru wymaga nowej kalibracji. Profil uzyskany z rzeczywistego źródła LSL nie jest automatycznie przenoszony na g.Pype UDP.

W trybie profilu ustawienia cech pochodzą z JSON, a stare opcje `--c3`, `--c4`, `--channels`, `--jaw-channels`, `--jaw-band`, `--artifact-uv` dotyczą wyłącznie opcjonalnego trybu LSL bez profilu. g.Pype zawsze wymaga `--profile`.

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
- **Mrok**: `eyes_closed_active` steruje zamykaniem oczu i ładowaniem skanu przez ten sam pakiet `shared/bci-signals`.
- **Nowe gry**: odbierają ten sam protokół i przypisują nazwy do swoich akcji. Kod kalibracji, profile i most pozostają bez zmian.

Przy dwóch grach jednocześnie użyj różnych portów odbiorczych:

```sh
python bci/bci_bridge.py --profile data/calibration/participant_gpype.json --port 5005 --target 127.0.0.1:5015
```

Drugi odbiornik Unity musi mieć ustawiony port 5015. `--host`/`--port` są aliasami dotychczasowych `--game-host`/`--game-port`.

## Znaczniki

W trybie g.Pype most przyjmuje znaczniki gier na UDP **5006** i zapisuje je do `data/raw/bci_markers_*.jsonl` (zmiana ścieżki: `--marker-file`). Zapisy zawierają `marker`, szacowany `t_monotonic_estimate` i `t_received_unix`; nie są nagraniem EEG. `--no-markers` zwalnia ten port, np. dla `marker_logger.py`. Opcjonalne `--marker-output lsl` włącza stary eksport **BciMarkers**, jeśli zainstalowano zależności LSL.

Kalibrator wysyła do g.Pype osobne, **numeryczne kody ASCII**, po jednym na datagram, na `127.0.0.1:56001`. Można je odebrać przez `gp.UDPReceiver` w osobnej gałęzi nagrania; nie należy doklejać jej do ośmiokanałowego wyjścia EEG bez aktualizacji konfiguracji odbiornika. Adres zmieniają `--trigger-host` i `--trigger-port`.

| Sygnał | Przygotowanie | Spoczynek | Zadanie | Koniec | Przerwanie |
|---|---|---|---|---|---|
| `eyes_closed` | 100 | 101 | 102 | 103 | 104 |
| `right_hand` | 110 | 111 | 112 | 113 | 114 |
| `left_hand` | 120 | 121 | 122 | 123 | 124 |
| `jaw` | 130 | 131 | 132 | 133 | 134 |

Numer próby i czasy faz są zapisywane w profilu (`events`), nie w datagramie. Znaczniki oznaczają zlecenie wskazówki, nie zmierzony moment wyjścia audio. UDP nie potwierdza ich dostarczenia.

## Demonstracja bez urządzenia

```sh
python bci/calibrate.py --demo
```

Każdy widok dostaje syntetyczne zadanie/spoczynek zgodne z instrukcjami. Wynik zapisuje się domyślnie do `data/calibration/demo.json` i jest oznaczony `simulated=true`. Nie potwierdza możliwości użytkownika ani urządzenia.

Po zakończeniu czterech kalibracji można sprawdzić cały most (dwa terminale):

```sh
python bci/fake_gpype.py
python bci/bci_bridge.py --profile data/calibration/demo.json --synthetic-input --allow-simulated
```

`fake_gpype.py` emuluje format `UDPSender`, nie uruchamia g.Pype. Domyślnie cyklicznie nadaje cztery zadania do sprawdzania **gotowego** profilu. Do sprawdzenia kalibracji przez prawdziwe gniazdo UDP można zamiast wbudowanego DEMO uruchomić:

```sh
python bci/fake_gpype.py --follow-triggers
python bci/calibrate.py --synthetic-input --profile data/calibration/fake_gpype.json
```

Nadajnik przełącza syntetyczne zadania według znaczników z kalibratora. Po zakończeniu rozłącz kalibrator, uruchom nadajnik w trybie cyklicznym i most z tym profilem. `--synthetic-input` oznacza **zewnętrzny nadajnik testowy**; `--demo` omija transport. Oba tworzą wyłącznie profile syntetyczne. Most wymaga dla nich zarówno `--synthetic-input`, jak i `--allow-simulated`. Surowy UDP nie pozwala samoczynnie rozpoznać symulacji, więc nie zaznaczaj nadajnika testowego przy fizycznym urządzeniu.

## Opcjonalna zgodność z LSL

Dotychczasowy adapter pozostaje dostępny:

```sh
python -m pip install -r bci/requirements-lsl.txt
python bci/calibrate.py --source lsl --profile data/calibration/participant_lsl.json
python bci/lsl_bridge.py --profile data/calibration/participant_lsl.json --name NAZWA_LSL
```

W tym trybie wybierz „Znajdź LSL”. Metadane LSL określają etykiety i jednostki; brakujące trzeba potwierdzić ręcznie. `pylsl` potrzebuje również liblsl. Znaczniki gier trafiają domyślnie do **BciMarkers**, kalibracji do **BciCalibrationMarkers** (`calibration/<sygnał>/<faza>:<numer_próby>`). `--marker-name OgrodMarkers` przywraca dawną nazwę. `lsl_bridge.py` bez profilu zachowuje stare heurystyki.

## Testy

```sh
python -m unittest discover -s bci/tests -v
```

Testy obejmują rzeczywistą ekstrakcję cech na sygnałach syntetycznych, oddzielne kalibracje, odrzucenie braku efektu, progi i histerezę, brakujące modele, zapis profilu, układ float64, kolejność bajtów, jednostki, izolację kanałów dodatkowych, opóźniony odczyt i błędne ramki. Testy Qt sprawdzają cztery widoki oraz pełną kalibrację przez gniazdo UDP z wirtualnym zegarem. Testy Qt i LSL są pomijane, jeśli nie ma ich opcjonalnych zależności.

```sh
python bci/tests/integration_gpype.py
# Tylko dla zachowanej ścieżki LSL:
python bci/tests/integration_lsl.py
```

Test g.Pype uruchamia prawdziwy proces mostu, emulowany nadajnik surowych ramek i dwa odbiorniki UDP. Sprawdza cztery aktywacje, kierunki, znaczniki JSONL, wyłączność portu, neutralizację po zatrzymaniu EEG i powrót po wznowieniu. Celowo blokuje import LSL. Trwa około 32 s i wymaga lokalnych gniazd sieciowych. Nie zastępuje testu sprzętowego z g.Pype i Unicornem.
