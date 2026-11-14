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

**Sygnały:** gra nie zna EEG. `SignalHub` to tablica nazwanych sygnałów (nazwa, liczba, źródło), do której piszą `UdpSignalSource` (most LSL) i `KeyboardSignalSource`. `Resources/signals.json` wiąże czynności gry (`steer`, `cut`) z nazwami sygnałów i ustawia wzmocnienie, martwą strefę, wygładzanie i próg. Panel w rogu ekranu pokazuje wszystkie docierające sygnały na żywo.

**Masa:** precel to zamknięta siatka (rura o zmiennej grubości po krzywej). `MeshSlicer` tnie ją płaszczyzną noża, zamyka przekroje denkami i liczy objętość każdej części z sumy czworościanów ze znakiem. Masa to objętość razy gęstość, więc nie zależy od liczby trójkątów. Każdy precel ma inny kształt, grubość i obrót, więc połowa masy nie leży w połowie szerokości.

**Pod EEG:** cięcie bierze pozycję noża sprzed 0,4 s, bo zaciśnięcie szczęki zakłóca sygnał sterujący. Nóż ma bezwładność, która wygładza szum.

## Struktura

| Folder | Zawartość |
|---|---|
| `bci/` | Python: odczyt EEG z Unicorna (LSL), przetwarzanie, klasyfikacja, wysyłka do gry |
| `unity/` | projekt Unity z grą Ogród dźwięków |
| `precel/` | projekt Unity z grą Mistrzostwa w krojeniu precla na pół |
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

Czyta EEG z Unicorna przez LSL, liczy sygnały `focus`, `laterality` i `jaw` i wysyła je do gry, a znaczniki z gry publikuje jako strumień LSL `OgrodMarkers` (do nagrania w LabRecorderze razem z EEG). Szczegóły: `docs/protocol.md`.

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
