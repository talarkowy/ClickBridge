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
| `src/ClickBridge.Tray` | `clickbridge-tray.exe` — mostek z ikoną przy zegarze i logowaniem do Zwift |
| `src/ClickBridge.Hosting` | Wspólne uruchamianie mostka i obsługa zapisanej sesji Zwift |
| `src/ClickBridge.Diagnostics` | `click-diag.exe` — narzędzie do badania zachowania Clicka |
| `tests/ClickBridge.Core.Tests` | Testy xUnit (protokoły, mapowanie, serwer TCP na loopbacku) |

Wymagania: Windows 10 2004+ z Bluetooth LE, .NET SDK 10.

```bash
dotnet build ClickBridge.slnx
dotnet test
```

## clickbridge-tray — aplikacja w obszarze powiadomień

```powershell
dotnet run --project src/ClickBridge.Tray -c Release
```

Po kompilacji możesz też uruchomić `clickbridge-tray.exe` z katalogu `src/ClickBridge.Tray/bin/Release/net10.0-windows10.0.19041.0`. Zamknij wersję konsolową przed uruchomieniem wersji z ikoną, żeby jeden mostek obsługiwał Clicka.

Kliknij ikonę ClickBridge przy zegarze. Jeśli jej nie widać, rozwiń ukryte ikony strzałką `^`. W oknie wpisz e-mail i hasło do konta Zwift, a potem wybierz **Zaloguj się**. Obie wersje używają tego samego zaszyfrowanego tokenu zapisanego przez Windows. Przycisk **Wyloguj się** usuwa lokalną sesję.

Lista **Tryb pracy** pozwala wybrać konto Zwift, tylko prawy kontroler albo **Oba bez konta — reset lewego (eksperymentalny)**. Kliknij **Zastosuj tryb**, aby zapisać wybór i ponownie uruchomić mostek. Może być potrzebne ponowne połączenie MyWhoosh. Ustawienia trafiają do wczytanego `clickbridge.json`, a jeśli go nie było — do `%LOCALAPPDATA%\ClickBridge\clickbridge.json`. Przy uruchamianiu konfiguracja obok EXE ma pierwszeństwo przed konfiguracją z bieżącego katalogu, a ta przed konfiguracją użytkownika. Konsola może użyć pliku użytkownika przez `--config`.

Zamknięcie okna `×` chowa formularz, a mostek działa dalej. Ponowne uruchomienie `clickbridge-tray.exe` otwiera okno działającej aplikacji. Prawym przyciskiem na ikonie otworzysz menu z ponownym połączeniem kontrolera i opcją **Zakończ**. Logi wersji z ikoną są w `%LOCALAPPDATA%\ClickBridge\logs`.

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

Opcje: `--right-only`, `--offline-reset`, `--zwift-login`, `--zwift-logout`, `--name <nazwa>`, `--port <n>`, `--advertise-address <ip>`, `--no-mdns`, `--config <plik>`, `--log-dir <ścieżka>`.

### Odblokowanie lewego Clicka v2 przez konto Zwift

```powershell
dotnet run --project src/ClickBridge.App -- --zwift-login
```

Podaj e-mail i hasło do konta Zwift w konsoli. Hasło jest ukryte podczas wpisywania i trafia bezpośrednio do `secure.zwift.com`. Mostek zapisuje wyłącznie token odświeżania, zaszyfrowany przez Windows dla bieżącego użytkownika. Przy kolejnych uruchomieniach używa zapisanej sesji automatycznie.

Gdy lewy Click v2 wyśle wyzwanie BLE, mostek pobierze podpisaną odpowiedź z usługi Zwift i przekaże ją kontrolerowi. Wymaga to dostępu do internetu. Wpis `Signed unlock reply sent to SyncRx` oznacza udany zapis BLE. Działanie odblokowania sprawdzisz, naciskając lewy `−` także po upływie pierwszej minuty połączenia.

Jeśli Zwift odrzuci zapisaną sesję, uruchom mostek ponownie z `--zwift-login`. Aby usunąć lokalną sesję, zamknij mostek i uruchom `dotnet run --project src/ClickBridge.App -- --zwift-logout`.

Format wymiany pochodzi z [implementacji Zwiftboard](https://github.com/thomaschampagne/zwiftboard/blob/ee4b1f2156d316398f71aeb09b67282901456b8e/internal/ble/unlock.go).

### Oba kontrolery bez konta Zwift — eksperymentalny reset

```powershell
dotnet run --project src/ClickBridge.App -c Release -- --offline-reset
```

Albo ustaw `"mode": "OfflineReset"` w `clickbridge.json`. Tryb nie wczytuje sesji Zwift przy starcie i nie wysyła żądań autoryzacji, także gdy token zapisano wcześniej. Nie usuwa zapisanej sesji. Mapowanie pozostaje standardowe — lewy `−` zmniejsza bieg, prawy `+` zwiększa.

Mostek łączy się z lewym kontrolerem przekazującym przyciski obu stron. Po `RideOn 02 03` wysyła niepodpisane `FF 04 00` po 300 ms. Po komunikacie `STOPPED` albo po 55 sekundach sesji zwalnia przyciski w MyWhoosh, wysyła `RESET` (`18`), czeka około sekundy i ponawia połączenie oraz handshake. Czas można zmienić przez `"offlineResetSeconds": 55` (10–60 sekund). Licznik mierzy czas sesji, nie brak naciskania przycisków. Zwykłe rozłączenie, ręczne ponowne połączenie i zamknięcie aplikacji anulują reset należący do poprzedniej sesji.

W logu szukaj `Offline reset armed`, `Offline RESET (session time limit)` albo `Offline RESET (STOPPED received)`, a następnie nowego `Ready.`. Po błędzie zapisu mostek czeka na kolejne rozgłoszenie BLE. Jeśli usługa Zwift jest ukryta, nie może wysłać resetu; ten tryb nie gwarantuje działania z każdym firmware.

**Podczas restartu mogą nie reagować oba kontrolery**, ponieważ używamy lewego jako huba. Naciśnięcia z tej przerwy nie są odtwarzane. To obejście wymagające testu z fizycznym Clickiem. Sprawdź lewy `−` i prawy `+` przez co najmniej trzy cykle, również podczas przytrzymania przycisku w momencie restartu. Zwykłe resety kontrolera nie restartują serwera MyWhoosh.

Sekwencja protokołu jest oparta na [jasasonc/clickbridge](https://github.com/jasasonc/clickbridge/blob/main/src/clickbridge/bridge.py); konieczność cyklicznego restartu lewego opisuje też [BikeControl #68](https://github.com/OpenBikeControl/bikecontrol/issues/68). Niepodpisane `FF 04 00` jest używane wyłącznie w trybie eksperymentalnym; tryb `Auto` nadal korzysta z podpisanej odpowiedzi serwera.

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
