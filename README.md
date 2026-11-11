# ClickBridge

Mostek **Zwift Click v2 → MyWhoosh** przez [OpenBikeControl](https://openbikecontrol.org/) (mDNS + TCP).
Notatki z researchu i pomiarów: [docs/research.md](docs/research.md).

```
[Click v2 L] ──┐ (lewy puck przekazuje przyciski obu pucków)
[Click v2 R] ──┘──BLE──> clickbridge.exe ──TCP (OpenBikeControl)──> MyWhoosh
                          └─ mDNS: <nazwa>._openbikecontrol._tcp.local
```

## Struktura

| Projekt | Opis |
|---|---|
| `src/ClickBridge.Core` | Protokoły (Zwift, OpenBikeControl), mapowanie przycisków, konfiguracja, log — bez zależności od Windows |
| `src/ClickBridge.Ble` | Połączenie BLE z kontrolerami Zwift (WinRT) |
| `src/ClickBridge.Obc` | Serwer TCP OpenBikeControl + ogłaszanie mDNS przez wbudowany responder Windows |
| `src/ClickBridge.App` | `clickbridge.exe` — mostek |
| `src/ClickBridge.Diagnostics` | `click-diag.exe` — narzędzie do badania zachowania Clicka |
| `tests/ClickBridge.Core.Tests` | Testy xUnit (protokoły, mapowanie, serwer TCP na loopbacku) |

Wymagania: Windows 10 2004+ z Bluetooth LE, .NET SDK 10.

```bash
dotnet build ClickBridge.slnx
dotnet test
```

## clickbridge — mostek do MyWhoosh

```bash
dotnet run --project src/ClickBridge.App
```

1. Zamknij Rouvy / BikeControl (kontroler połączony gdzie indziej nie jest widoczny).
2. Uruchom `clickbridge`, naciśnij dowolny przycisk na Clicku, żeby go wybudzić — w logu pojawi się `Ready.`
3. Uruchom MyWhoosh i w ustawieniach kontrolerów wybierz urządzenie **ClickBridge** (OpenBikeControl / sieć).
4. W logu zobaczysz `App connected` i `App info: app='...'`, a każde naciśnięcie jako `-> ShiftUp pressed` itp.

Klawisze w trakcie: `U`/`D` — testowa zmiana biegu w górę/dół (bez Clicka), strzałki / `Enter` / `Backspace` —
testowa nawigacja, `S` — status, `R` — ponowne połączenie z kontrolerami, `Q` — wyjście.

Opcje: `--right-only`, `--name <nazwa>`, `--port <n>`, `--advertise-address <ip>`, `--no-mdns`, `--config <plik>`, `--log-dir <ścieżka>`.

### Domyślne mapowanie

| Click v2 | OpenBikeControl |
|---|---|
| prawy „+” | Shift Up |
| lewy „−” | Shift Down |
| strzałki (lewy puck) | Up / Down / Left / Right |
| A / B / Y / Z (prawy puck) | Select / Back / Menu / Camera View |

Tryb `RightOnly` (awaryjny, gdyby lewy puck był zablokowany): łączy się tylko z prawym puckiem, a **B = Shift Down**.
Zmiany mapowania i inne ustawienia: skopiuj [`clickbridge.example.json`](src/ClickBridge.App/clickbridge.example.json)
jako `clickbridge.json` obok `clickbridge.exe` (albo w folderze, z którego uruchamiasz).

### Sieć i zapora

- Komputer musi być w sieci oznaczonej w Windows jako **prywatna** (w publicznej mDNS i połączenia przychodzące są blokowane).
- Gdy MyWhoosh działa na **innym** komputerze, zapora Windows musi wpuszczać połączenia do `clickbridge.exe`
  (przy pierwszym uruchomieniu Windows może zapytać — zezwól dla sieci prywatnych).

## click-diag

```bash
dotnet run --project src/ClickBridge.Diagnostics
```

Opcje: `--scan-only`, `--no-handshake`, `--side left|right`, `--log-dir <ścieżka>`, `--summary <sekundy>`.
Klawisze w trakcie: `S` podsumowanie, `M` znacznik w logu, `H` ponowny `RideOn`, `R` wymuszone ponowne połączenie, `Q` wyjście.
Pełny log (także ramki "idle" i powtórzenia) trafia do `logs/click-diag-*.log`.
