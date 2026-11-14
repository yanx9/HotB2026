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

**Muzyka:** `unity/Assets/Resources/music_bank.json` to zapis nutowy w formacie przewidzianym dla LLM (4 takty, 4/4, 80 BPM, stopnie skali). Każda fraza przechodzi walidację (`MusicValidator`) przed odtworzeniem; dźwięk syntezuje `Synth`.

**Kod** (`unity/Assets/Scripts/`): `Brain/` to warstwa wejścia (zdarzenia `BrainInput`, symulator klawiatury, odbiornik UDP, znaczniki), `Music/` to bank fraz, walidator i syntezator, `Garden/` to rozgrywka i wygląd.

## Struktura

| Folder | Zawartość |
|---|---|
| `bci/` | Python: odczyt EEG z Unicorna (LSL), przetwarzanie, klasyfikacja, wysyłka do gry |
| `unity/` | projekt Unity z grą |
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

### Znaczniki z gry

```
python bci/marker_logger.py              # zapisuje zdarzenia gry do data/raw/
```

### Unity (`unity/`)

Cały zespół używa tej samej wersji Unity: 6000.6.9f1 (zapisana w `unity/ProjectSettings/ProjectVersion.txt`).
