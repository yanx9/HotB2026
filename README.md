# Beyond the Darkness — HotB2026, BCI w neurorehabilitacji, rozrywce i sztuce

Projekt hackathonowy z wykorzystaniem **g.tec Unicorn Hybrid Black** — 8-kanałowego, bezprzewodowego systemu EEG do rozwoju BCI. Powstała z niego jedna gra: **Beyond the Darkness**, skradanka w ciemności, w której zamknięcie oczu skanuje otoczenie.

## Wyzwanie

Zamieńcie aktywność mózgu w znaczącą interakcję. Stwórzcie coś, co pomaga ludziom wracać do zdrowia, grać, komunikować się, tworzyć — albo doświadczać technologii w zupełnie nowy sposób.

Możliwe kierunki:

- ćwiczenia rehabilitacyjne sterowane EEG, neurofeedback
- gry sterowane umysłem
- interaktywne instalacje, muzyka, sztuka wizualna
- doświadczenia VR/AR
- nowe formy interakcji człowiek-komputer

## Stack

- **Sprzęt:** g.tec Unicorn Hybrid Black (8 kanałów EEG)
- **Strumieniowanie danych:** Unicorn → g.Pype → filtrowanie / klasyfikacja → UDP JSON → Unity
- **Integracje:** Python, Unity
- **Sygnały:** alfa nad potylicą i potencjał powiek (zamknięte oczy), EMG szczęki (zaciśnięcie)

## Zespół

<!-- 3–4 osoby -->

## Gra: Beyond the Darkness (`mrok/`)

Projekt Unity (3D, URP) w folderze `mrok/`, od roboczej nazwy gry. Skradanka z pierwszej osoby w zupełnej ciemności: nic nie ma wyglądu, widać tylko chmurę punktów zostawioną przez skan. Zamknięte oczy ładują skan; po otwarciu fala punktów odsłania stopklatkę otoczenia sięgającą tym dalej, im dłużej oczy były zamknięte (od 4 m po 0,5 s do 28 m po 6 s). Strażnicy na stopklatce zostają tam, gdzie byli, a naprawdę idą dalej. Trzeba zebrać łup i dojść do zielonych drzwi. Gra jest po angielsku.

| | |
|---|---|
| ![Sala wyboru poziomu](docs/img/mrok/menu.jpg) | ![Samouczek: napis odsłonięty skanem](docs/img/mrok/tutorial.jpg) |
| Sala wyboru poziomu: skanuje się sama, poziom wybiera się, podchodząc do drzwi | Samouczek: wskazówki to napisy z punktów, które odsłania skan |
| ![Rotunda w muzeum](docs/img/mrok/rotunda.jpg) | ![Ogród z altaną](docs/img/mrok/garden.jpg) |
| Muzeum: rotunda z posągami i łupem na postumencie | Ogród: altana, żywopłoty i drzewa pod gołym niebem |
| ![Tor rzutu kamieniem](docs/img/mrok/throw.jpg) | |
| Rzut kamieniem: łuk pokazuje tor, krążek miejsce upadku | |

**Sterowanie z opaski**

| Sygnał | Działanie |
|---|---|
| zamknięte oczy (`eyes_closed_active`) | ładowanie skanu; otwarcie oczu go wypuszcza |
| zaciśnięta szczęka (`jaw_active`) | sprint, zero-jedynkowo: wystarczy lekkie zaciśnięcie. Bieg jest szybki i głośny |

Potok liczy szczękę także bez jej kalibracji (heurystyka względem spoczynku), więc sprint działa z każdym profilem. Powiązania są w `mrok/Assets/Resources/signals.json`, warstwa sygnałów to wspólny pakiet `shared/bci-signals`. Oczy wymagają potoku uruchomionego z profilem kalibracji (`bci_pipeline.py --profile ...`, patrz `docs/calibration.md`).

**Zasady**

- **Strażnicy nie widzą, ale słyszą:** kroki (bieg z 10 m, chód z 6 m, skradanie z 3 m) oraz skan, tym dalej, im większy (55% jego zasięgu). Mur tłumi hałas. Idą w to miejsce, nasłuchują i wracają na trasę. Złapanie to dotknięcie.
- Wzrok strażników (stożek przed twarzą i pogoń) jest w kodzie, ale wyłączony, bo gra była z nim za trudna: `Guard.Sight` w `Guard.cs`.
- **Kamienie** leżą na podłodze (jasnoniebieskie). Podniesione można rzucać: przytrzymany lewy przycisk myszy kołysze siłę rzutu, łuk punktów pokazuje tor i miejsce upadku, puszczenie rzuca. Uderzenie odsłania okolicę jak mały skan (ok. 6 m, tyle co sekunda zamkniętych oczu), a strażnik idzie w to miejsce i dłużej tam nasłuchuje. Wartownik na posterunku schodzi z niego tylko za kamieniem.
- **Kroki strażników** słyszane z bliska zostawiają na podłodze gasnące pomarańczowe ślady (czerwone, gdy strażnik jest zaalarmowany).
- Z zamkniętymi oczami można chodzić. Orientację daje wtedy dźwięk przestrzenny: kroki strażników, dzwonienie łupu i tyknięcia odliczające sekundy ładowania.

**Poziomy** (`Levels.cs`: mapa znakowa, sale, patrole, posterunki, napisy)

- **Tutorial:** sześć sal uczących po kolei skanu, łupu, skradania i rzutu kamieniem. Ostatniego wartownika nie da się minąć bez odciągnięcia go kamieniem.
- **Museum:** hol, galeria z kolumnadą pod sklepieniem kolebkowym, rotunda z posągami pod kopułą, korytarz, biblioteka, dziedziniec z fontanną, magazyn ze skarbcem. Trzy łupy, czterech strażników.
- **Garden:** labirynt żywopłotów, drzewa, głazy, altana z kopułą. Trzy łupy, czterech strażników.

W czasie rozgrywki nie ma napisów. Tekst jest tylko na ekranie ładowania i po zakończeniu poziomu; łup pokazują romby u dołu ekranu, kamienie w kieszeni kropki pod nimi.

**Budowa.** Cały świat to zderzacze budowane z mapy przez `Level.cs` (bryły w `Shapes.cs`, sylwetki ze stawami w `Figure.cs`, napisy i kamienie z punktów w `Sign.cs`). Chmurę punktów rysuje `PointCloud.cs` z shaderem `PointCloud.shader`; kolor punktu zależy od rodzaju powierzchni. Skan to 54 000 promieni liczonych na wątkach roboczych (ok. 40 ms).

**Uruchomienie:** otwórz `mrok/` w Unity 6000.6.9f1 i wciśnij Play w `Assets/Scenes/SampleScene`. Najlepiej w słuchawkach.

| Klawisz | Działanie |
|---|---|
| WASD, mysz (po kliknięciu w okno) albo Q / E | ruch, obrót |
| Shift | skradanie |
| lewy przycisk myszy (trzymaj i puść) | rzut kamieniem |
| Spacja (trzymaj) | „mam zamknięte oczy”: zawsze zapisuje prawdę do nagrania (`truth_closed` / `truth_open`); w trybie KEY albo bez potoku BCI steruje też grą |
| F (trzymaj) | „zaciskam szczękę”: zawsze zapisuje prawdę do nagrania (`truth_jaw_on` / `truth_jaw_off`); w trybie KEY albo bez potoku BCI włącza też sprint |
| F2 | tryb KEY (steruje klawiatura, EEG tylko się nagrywa) / EEG (steruje opaska) |
| F1 | napisy diagnostyczne (sygnały, tryb, klatki/s), domyślnie ukryte |
| Esc | powrót do sali z drzwiami |
| 1 / 2 / 3 | w sali z drzwiami: od razu otwiera poziom |
| R | ponowienie po wpadce (albo zamknięcie oczu na 1,5 s) |

Pad: lewa gałka ruch, prawa gałka obrót, **prawy spust (trzymaj i puść) rzut kamieniem**, B albo lewy bumper skradanie, A jak spacja (zamknięte oczy), lewy spust jak F (zaciśnięta szczęka), Start ponowienie, Select powrót do sali z drzwiami.

## Warstwa sygnałów (`shared/bci-signals`)

Lokalny pakiet Unity, przez który gra odbiera sygnały (wpis `com.hotb.bci-signals` w `mrok/Packages/manifest.json`). Nie zna on gry: kolejną podłącza się tym samym wpisem i własnym plikiem `Resources/signals.json`.

- `SignalHub`: tablica nazwanych sygnałów (nazwa, liczba, źródło). Gra nie wie, skąd pochodzą.
- `UdpSignalSource`: wpisuje do tablicy wszystko, co nadaje potok g.Pype (`bci/bci_pipeline.py`), bez listy znanych nazw.
- `KeyboardSignalSource`: udaje te same sygnały klawiszami.
- `SignalBindings`: wiąże czynność gry z sygnałem i ustawia wzmocnienie, martwą strefę, wygładzanie i próg. Respektuje flagi `<sygnał>_valid` z potoku: sygnał oznaczony jako nieważny liczy się jak brak sygnału. Pole `fallback` wskazuje sygnał zastępczy.
- `MarkerSender`: znaczniki zdarzeń gry odsyłane do potoku.

## Struktura

| Folder | Zawartość |
|---|---|
| `bci/` | Python: potok g.Pype (Unicorn, filtry, klasyfikator, nadajnik UDP JSON), kalibrator, profile |
| `mrok/` | projekt Unity z grą Beyond the Darkness |
| `shared/bci-signals/` | pakiet Unity: warstwa sygnałów, przez którą gra odbiera dane z potoku |
| `docs/protocol.md` | kontrakt między `bci/` a grą: potok, porty, format wiadomości |
| `docs/calibration.md` | obsługa kalibratora, profile, uruchomienie potoku i testy |
| `docs/recording.md` | nagrywanie sesji, tryby gry, ocena detektorów oczu i szczęki (`evaluate.py`), szybkie detektory i ich strojenie (`tune_fast.py`) |
| `data/` | profile kalibracji (`data/calibration/`) i nagrania EEG (`data/raw/`, nie trafia do repo) |

## Uruchomienie

### Python (`bci/`)

Python 3.10 lub nowszy. Jedno środowisko dla potoku i kalibratora:

```
cd bci
python -m venv .venv
source .venv/bin/activate        # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

`requirements.txt` instaluje g.Pype z oknami (`gui`) i sterownikiem Unicorna (`devices`). Opaskę trzeba wcześniej sparować z komputerem przez Bluetooth.

### Potok BCI (`bci/bci_pipeline.py`)

```text
Unicorn → g.Pype → filtrowanie / klasyfikacja → UDP JSON → Unity
```

Cały tor działa w jednym procesie g.Pype: `HybridBlack → Highpass 1 Hz → Bandstop 48–52 Hz → SignalClassifier → UdpJsonSender`. Węzły są w `bci/gpype_nodes.py`. Nie ma pośredniego LSL ani surowego UDP.

```sh
python bci/calibrate.py                                              # kalibracja, zapisuje profil
python bci/bci_pipeline.py --profile data/calibration/participant.json   # gra z profilem
python bci/bci_pipeline.py                                           # bez profilu: nieskalibrowane heurystyki
python bci/bci_pipeline.py --host <IP komputera z grą>
```

- **Z profilem** potok wysyła `eyes_closed`, `right_hand`, `left_hand`, `jaw` z flagami `_active` i `_valid`, `laterality` i `focus`. Brakująca lub nieudana kalibracja daje neutralne zero z `valid=0`; szczękę potok liczy wtedy heurystycznie. Gra używa `eyes_closed_active` i `jaw_active`.
- **Bez profilu** wysyła heurystyki `focus`, `laterality` i `jaw` liczone względem ostatniej minuty. Do gry to za mało: oczy wymagają profilu z kalibracją.
- **Profil z szybkimi detektorami** (`tune_fast.py`) skraca opóźnienie oczu z ok. 1,5–2 s do ułamka sekundy; opis w [docs/recording.md](docs/recording.md).
- Okno g.Pype pokazuje EEG po filtrach i sygnały idące do gry. `--no-gui` je wyłącza (na macOS sterownik Unicorna potrzebuje pętli zdarzeń okna, więc tam zostawcie okno).
- Znaczniki gier z portu 5006 zapisują się do `data/raw/bci_markers_*.jsonl`.
- Każda sesja nagrywa się do `data/raw/<sesja>_<czas>/`: surowe i przefiltrowane EEG, znaczniki gry na próbkach EEG i wyjście detektora (`--session mrok_s01`, `--no-record` wyłącza). Kalibrator nagrywa się tak samo. Ocena: `python bci/evaluate.py <folder>`. Pełna instrukcja: [docs/recording.md](docs/recording.md).
- Kilku odbiorców naraz: `--target host:port`.

**Kalibracja:** `bci/calibrate.py` ma cztery widoki: oczy zamknięte, wyobrażanie ruchu prawej dłoni, lewej dłoni oraz szczęka (EMG). Uruchamia w swoim procesie ten sam początek potoku (`HybridBlack → filtry`) i zapisuje profil JSON. Gra korzysta z oczu i szczęki. Opaskę trzyma naraz jeden proces: po kalibracji wybierzcie „Rozłącz” albo zamknijcie kalibrator, potem uruchomcie `bci_pipeline.py`. Pełna instrukcja: [docs/calibration.md](docs/calibration.md).

Bez opaski, na syntetycznym EEG (ten sam potok, źródło `SyntheticUnicorn` zamiast `HybridBlack`):

```sh
python bci/calibrate.py --fake                  # profil data/calibration/fake.json
python bci/bci_pipeline.py --fake --allow-simulated --profile data/calibration/fake.json
python bci/bci_pipeline.py --fake               # heurystyki; zadania zmieniają się w 27-sekundowym cyklu
```

### Znaczniki do CSV

```
python bci/marker_logger.py              # opcjonalny CSV; potok uruchomcie wtedy z --no-markers (ten sam port)
```

### Unity (`mrok/`)

Cały zespół używa tej samej wersji Unity: 6000.6.9f1 (zapisana w `mrok/ProjectSettings/ProjectVersion.txt`).
