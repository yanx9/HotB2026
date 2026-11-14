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

## Struktura

| Folder | Zawartość |
|---|---|
| `bci/` | Python: odczyt EEG z Unicorna (LSL), przetwarzanie, klasyfikacja, wysyłka do gry |
| `unity/` | projekt Unity (do utworzenia, patrz niżej) |
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

### Unity (`unity/`)

Projekt jeszcze nie istnieje. Jedna osoba tworzy go w Unity Hub: *New project* → **Location**: katalog repo, **Project name**: `unity`, i commituje. `.gitignore` już pomija `Library/`, `Temp/` itd.

Cały zespół powinien używać tej samej wersji Unity — zapisuje się ona w `unity/ProjectSettings/ProjectVersion.txt`.
