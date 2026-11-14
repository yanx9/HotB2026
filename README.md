# HotB2026 — BCI w neurorehabilitacji, rozrywce i sztuce

Projekt hackathonowy z wykorzystaniem **g.tec Unicorn Hybrid Black** — 8-kanałowego, bezprzewodowego systemu EEG do rozwoju BCI.

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
- **Strumieniowanie danych:** LSL (Lab Streaming Layer), UDP
- **Integracje:** Python, Unity
- **Paradygmaty BCI:** P300, analiza widmowa sygnału

## Zespół

<!-- 3–4 osoby -->

## Gra: Ogród dźwięków

Spokojna gra 2D do treningu słuchowego. Każda roślina to instrument z własnym motywem. Gracz poznaje motyw, potem rozpoznaje go wśród podobnych sekwencji; po trzech trafieniach roślina rośnie i jej głos dołącza do wspólnej kompozycji.

Rola EEG:

| Sygnał | W grze |
|---|---|
| P300 (`select`) | odpowiedź "to ten motyw" w zadaniu typu auditory oddball |
| Motor imagery (`left` / `right`) | nastrój ogrodu: chłodny i molowy albo ciepły i durowy |
| Skupienie (`focus`) | neurofeedback: liczba świetlików i jasność roślin |

Dopóki klasyfikatory nie są skalibrowane, te same zdarzenia daje klawiatura. Gra wysyła znaczniki bodźców do `bci/`, żeby nagrania z rozgrywki posłużyły do kalibracji.

**Uruchomienie:** otwórz `unity/` w Unity 6000.6.9f1 i wciśnij Play w `Assets/Scenes/SampleScene`. Cała scena, grafika i dźwięk powstają z kodu.

| Klawisz | Działanie |
|---|---|
| klik w roślinę / 1–4 | zacznij lekcję tej rośliny |
| Spacja | symulacja P300: start rozpoznawania, "to ten motyw" |
| ← / → | symulacja motor imagery: nastrój |
| ↑ / ↓ | symulacja skupienia |
| R | powtórz motyw |
| prawy przycisk myszy | wycisz / włącz roślinę |
| Esc | przerwa, powrót do ogrodu |

Trudność rośnie z poziomem rośliny, po jednej zmianie naraz: sekwencje wyraźnie inne → różnica jednej nuty → zamieniona kolejność nut i inna barwa.

**Muzyka:** każda roślina ma motyw zapisany w `unity/Assets/Resources/music_bank.json` (stopnie skali; 4 takty, 4/4, 80 BPM). `PhraseComposer` układa z niego pulę fraz dla każdego poziomu rośliny: motyw wprost, w odwróceniu, od końca i z nutami przejściowymi, przeniesiony na kolejne akordy i zagrany w rytmie właściwym dla instrumentu (bas na „raz” i na „i” trzeciej miary, struna ósemkami z pauzami, dzwonek rzadko i synkopami, flet długimi nutami z ozdobnikami). Nuty mają akcenty i drobny rozrzut w czasie. Ogród gra frazę po frazie według formy A A B A C A D E, a rośliny wchodzą w nią z przesunięciem. Do puli trafiają też frazy wpisane w banku (`phrases`), czyli miejsce na materiał z LLM. Każda fraza przechodzi walidację (`MusicValidator`); dźwięk syntezuje `Synth`.

**Obraz:** pixel art 320×180 powiększany bez wygładzania. Wszystko rysuje kod (`PixelArt`): cztery gatunki roślin w czterech stadiach wzrostu (dzwonki, trzciny, wierzba, grzyby), drzewa tła, wzgórza, trawa. Scena chodzi według zegara muzyki: szader `PixelSprite` kołysze koronami, trzcinami i trawą w rytm uderzeń, świecące części roślin pulsują, każda roślina podskakuje na własne nuty, a szader `PixelSky` rysuje gwiazdy migające w rytm, księżyc i zorzę, która jaśnieje, gdy gra więcej roślin, i przyjmuje kolor nastroju.

**Kod** (`unity/Assets/Scripts/`): `Brain/` to warstwa wejścia (zdarzenia `BrainInput`, symulator klawiatury, odbiornik UDP, znaczniki), `Music/` to bank, kompozytor fraz, walidator i syntezator, `Garden/` to rozgrywka i grafika. Szadery są w `unity/Assets/Resources/Shaders/`.

## Druga gra: Mistrzostwa w krojeniu precla na pół (`precel/`)

Osobny projekt Unity (3D, URP). Namiot piwny, deska, precel i nóż. Ruch ręki przesuwa nóż w lewo i w prawo, zaciśnięcie szczęki opuszcza go na precla. Połówki lądują na wadze szalkowej; wygrywa najmniejsza różnica masy. Zawody to pięć precli, po 25 s na każdy.

**Uruchomienie:** otwórz `precel/` w Unity 6000.6.9f1 i wciśnij Play w `Assets/Scenes/SampleScene`. Scena, precle, tekstury i dźwięk powstają z kodu.

| Klawisz | Udaje sygnał | Działanie |
|---|---|---|
| ← / → (albo A / D) | `laterality` | nóż w lewo i w prawo |
| Spacja (trzymaj ok. 0,8 s) | `jaw` | cięcie; puszczona wcześniej cofa nóż |

**Sygnały:** gra nie zna EEG. Korzysta ze wspólnej warstwy `shared/bci-signals` (opis niżej); `Resources/signals.json` wiąże czynności `steer` i `cut` z sygnałami `laterality` i `jaw_active` (zastępczo `jaw`, gdy most działa bez profilu kalibracji). Panel w rogu ekranu pokazuje wszystkie docierające sygnały na żywo.

**Masa:** precel to zamknięta siatka (rura o zmiennej grubości po krzywej). `MeshSlicer` tnie ją płaszczyzną noża, zamyka przekroje denkami i liczy objętość każdej części z sumy czworościanów ze znakiem. Masa to objętość razy gęstość, więc nie zależy od liczby trójkątów. Każdy precel ma inny kształt, grubość i obrót, więc połowa masy nie leży w połowie szerokości.

**Pod EEG:** cięcie bierze pozycję noża sprzed 0,4 s, bo zaciśnięcie szczęki zakłóca sygnał sterujący. Nóż ma bezwładność, która wygładza szum.

## Trzecia gra: Mrok (`mrok/`)

Osobny projekt Unity (3D, URP). Skradanka z pierwszej osoby w zupełnej ciemności: nic nie ma wyglądu, widać tylko chmurę punktów zostawioną przez skan. Zamknięte oczy ładują skan; po otwarciu fala punktów odsłania stopklatkę otoczenia sięgającą tym dalej, im dłużej oczy były zamknięte (od 4 m po 0,5 s do 28 m po 6 s). Strażnicy na stopklatce zostają tam, gdzie byli, a naprawdę idą dalej. Trzeba zebrać trzy łupy i dojść do zielonych drzwi.

Strażnicy nie widzą, ale słyszą: kroki gracza (skradanie jest dużo cichsze) i sam skan, tym dalej, im większy. Idą w miejsce hałasu, nasłuchują i wracają na patrol. Z zamkniętymi oczami można chodzić. Wtedy orientację daje dźwięk przestrzenny: kroki strażników, dzwonienie łupu i tyknięcia odliczające sekundy ładowania.

**Uruchomienie:** otwórz `mrok/` w Unity 6000.6.9f1 i wciśnij Play w `Assets/Scenes/SampleScene`. Najlepiej w słuchawkach.

| Klawisz | Działanie |
|---|---|
| Spacja (trzymaj) | udaje sygnał `eyes_closed_active`; ekran gaśnie jak przy zamkniętych oczach |
| WASD, Shift | ruch, skradanie |
| mysz (po kliknięciu w okno) albo Q / E | obrót |
| R | restart po wpadce (albo zamknięcie oczu na 1,5 s) |

**Sygnał:** gra słucha `eyes_closed_active` z mostu uruchomionego z profilem kalibracji (`lsl_bridge.py --profile ...`, patrz `docs/calibration.md`). Warstwa sygnałów to wspólny pakiet `shared/bci-signals`; powiązanie jest w `mrok/Assets/Resources/signals.json`.

## Wspólna warstwa sygnałów (`shared/bci-signals`)

Lokalny pakiet Unity używany przez `precel/` i `mrok/` (wpis `com.hotb.bci-signals` w `Packages/manifest.json`). Nową grę podłącza się tym samym wpisem i własnym plikiem `Resources/signals.json`.

- `SignalHub`: tablica nazwanych sygnałów (nazwa, liczba, źródło). Gra nie wie, skąd pochodzą.
- `UdpSignalSource`: wpisuje do tablicy wszystko, co nadaje most (`bci/lsl_bridge.py`), bez listy znanych nazw.
- `KeyboardSignalSource`: udaje te same sygnały klawiszami.
- `SignalBindings`: wiąże czynność gry z sygnałem i ustawia wzmocnienie, martwą strefę, wygładzanie i próg. Respektuje flagi `<sygnał>_valid` z mostu z profilem kalibracji: sygnał oznaczony jako nieważny liczy się jak brak sygnału. Pole `fallback` wskazuje sygnał zastępczy.
- `MarkerSender`: znaczniki zdarzeń gry odsyłane do mostu.

Ogród dźwięków (`unity/`) ma starszą, własną warstwę (`Brain/`) opartą na komendach `select` / `left` / `right`, które most nadal nadaje.

## Struktura

| Folder | Zawartość |
|---|---|
| `bci/` | Python: odczyt EEG z Unicorna (LSL), przetwarzanie, klasyfikacja, wysyłka do gry |
| `unity/` | projekt Unity z grą Ogród dźwięków |
| `precel/` | projekt Unity z grą Mistrzostwa w krojeniu precla na pół |
| `mrok/` | projekt Unity z grą Mrok |
| `shared/bci-signals/` | pakiet Unity: warstwa sygnałów wspólna dla gier |
| `docs/protocol.md` | kontrakt między `bci/` a grą: strumienie LSL, porty, format wiadomości |
| `data/` | nagrania EEG (`data/raw/` nie trafia do repo) |

## Uruchomienie

### Python (`bci/`)

```
cd bci
python -m venv .venv
source .venv/bin/activate        # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

Jeśli `import pylsl` zgłasza brak biblioteki liblsl (zdarza się na macOS/Linux): `brew install labstreaminglayer/tap/lsl` albo `conda install -c conda-forge liblsl`.

### Atrapa sygnału (bez headsetu)

`bci/mock_sender.py` wysyła do gry fałszywe komendy i metrykę `focus` w formacie z `docs/protocol.md`. Nie wymaga żadnych pakietów (na macOS zamiast `python` może być potrzebne `python3`).

```
python bci/mock_sender.py                # losowe komendy co 2–4 s + płynnie zmieniający się focus
python bci/mock_sender.py --interactive  # wpisujesz np. "left" albo "focus 0.8"
```

### Most LSL (`bci/lsl_bridge.py`)

**Kalibracja użytkownika:** `bci/calibrate.py` jest niezależną aplikacją z czterema widokami: oczy zamknięte, wyobrażanie ruchu prawej dłoni, lewej dłoni oraz szczęka (EMG). Zapisuje wspólny dla wszystkich gier profil JSON z parametrami i oceną rozdzielności. Instalacja interfejsu: Python 3.9–3.13 i `pip install -r bci/requirements-calibration.txt`. Pełna instrukcja: [docs/calibration.md](docs/calibration.md).

```sh
python bci/calibrate.py                         # rzeczywisty Unicorn przez LSL
python bci/calibrate.py --demo                  # cztery widoki na sygnale syntetycznym
python bci/lsl_bridge.py --profile data/calibration/participant.json --name NAZWA_LSL
```

Z profilem most wysyła `eyes_closed`, `right_hand`, `left_hand`, `jaw`, `laterality` oraz flagi ważności i aktywacji. Zachowuje komendy `left`/`right` dla ogrodu. Brakująca lub nieudana kalibracja daje neutralny sygnał, bez zastępowania jej heurystyką. Dotychczasowy `focus` pozostaje nieskalibrowaną heurystyką.

Bez profilu czyta EEG z Unicorna przez LSL, liczy dotychczasowe heurystyki `focus`, `laterality` i `jaw` i wysyła je do gry. Znaczniki publikuje jako strumień LSL `BciMarkers` (do nagrania w LabRecorderze razem z EEG); dawną nazwę przywraca `--marker-name OgrodMarkers`. Szczegóły: `docs/protocol.md`.

```
python bci/lsl_bridge.py --list                    # jakie strumienie LSL widać w sieci
python bci/lsl_bridge.py                           # pierwszy strumień typu EEG, gra na tym komputerze
python bci/lsl_bridge.py --name <nazwa strumienia> --game-host <IP komputera z grą>
```

Most może działać na komputerze z Unicornem (Unicorn Suite jest tylko na Windowsa), a gra na innym. Wtedy podajcie `--game-host`. Gra sama odsyła znaczniki na adres, z którego przychodzi `focus`.

Bez headsetu: `python bci/fake_unicorn.py` nadaje sztuczny strumień LSL (8 kanałów, 250 Hz), na którym `focus` faluje co 30 s, `laterality` co 20 s, a `jaw` odzywa się co 12 s.

### Znaczniki bez LSL

```
python bci/marker_logger.py              # zapisuje zdarzenia gry do data/raw/ (zamiast mostu, ten sam port)
```

### Unity (`unity/`)

Cały zespół używa tej samej wersji Unity: 6000.6.9f1 (zapisana w `unity/ProjectSettings/ProjectVersion.txt`).
