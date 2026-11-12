# Research: Zwift Click v2 → MyWhoosh (OpenBikeControl)

Stan wiedzy na 2026-11-11. Źródła: kod BikeControl (github.com/OpenBikeControl/bikecontrol, commit `edd1e2a`),
openbikecontrol.org (`/protocol`, `/mdns`), blog bikecontrol.app.

> Licencja BikeControl: nowe wersje = "Non-Commercial License", starsze (przed tagiem `gpl3`) = GPL-3.0.
> Kluczowa logika (pakiet `prop`, m.in. `ClickLogic`) jest **prywatna** — w repo jest tylko stub.
> Czytamy kod jako dokumentację protokołu, piszemy własną implementację.

## 1. Architektura docelowa

```
[Click v2 L] --BLE--\                                    mDNS: _openbikecontrol._tcp
                     >-- nasz mostek (TCP server) <---- TCP ---- MyWhoosh (TCP client)
[Click v2 R] --BLE--/
```

MyWhoosh obsługuje OBC **tylko po sieci** (mDNS + TCP), nie po BLE. Mostek = "urządzenie OBC".

## 2. Zwift Click v2 — BLE

- Dwa osobne urządzenia BLE (lewy i prawy "puck"). Manufacturer ID Zwift = `0x094A` (2378);
  pierwszy bajt manufacturer data: `0x0A` = prawy, `0x0B` = lewy (oznaczone w kodzie jako "unconfirmed").
- Serwis: `00000001-19CA-4651-86E5-FA29DCDD09D1` (lub `0000FC82-0000-1000-8000-00805F9B34FB` w nowszych FW)
  - `...0002...` ASYNC — notify (zdarzenia przycisków)
  - `...0003...` SYNC_RX — write without response (komendy do urządzenia)
  - `...0004...` SYNC_TX — indicate/notify (odpowiedzi)
- Handshake: zapis do SYNC_RX `"RideOn"` (`52 69 64 65 4F 6E`) [+ `02 03` dla Click v2].
  Odpowiedź zaczyna się od `RideOn 02 03` (+ ew. klucz publiczny — obecnie ignorowany, bez szyfrowania).
- Wiadomości: `[opcode][protobuf]`. Przyciski: opcode `0x23` (35, CONTROLLER_NOTIFICATION) → `RideKeyPadStatus`,
  pole `buttonMap` = bitmapa **odwrócona** (bit = 0 → wciśnięty).
  | maska | przycisk | puck |
  |---|---|---|
  | `0x0001` LEFT, `0x0002` UP, `0x0004` RIGHT, `0x0008` DOWN | nawigacja | lewy |
  | `0x0100` SHIFT_UP_L ("−") | zmiana biegu | lewy |
  | `0x0010` A, `0x0020` B, `0x0040` Y, `0x0080` Z | przyciski | prawy |
  | `0x1000` SHIFT_UP_R ("+") | zmiana biegu | prawy |
- Inne: `0x15` pusta wiadomość (idle), `0x19` bateria, `0xFF 05 00 EA/FA 05` = "stopped" (Click v2).

## 3. Dlaczego lewy + prawy nie działa (sedno problemu)

Zwift "blokuje" Click v2 dla innych aplikacji. Wg bloga BikeControl: urządzenie wymaga handshake'u
szyfrowania odświeżanego przez aplikację Zwift ~co 24 h; bez tego **przestaje wysyłać zdarzenia po ~1 min**.
W praktyce dotyczy to głównie **lewego** pucka — BikeControl oferuje 3 tryby:

1. **Right side only** — tylko prawy puck (＋ = bieg w górę, B = bieg w dół), lewy ignorowany.
   To jest dokładnie to, co obserwujesz ("działa tylko z prawym").
2. **Unlock with Zwift** — połączyć Click v2 z aplikacją Zwift (darmowe konto, bez subskrypcji) na 10–30 s,
   zamknąć Zwift → działa ~24 h. BikeControl robi to przez własny mostek (szczegóły w prywatnym `prop`).
3. **Restart co minutę** (legacy) — cykliczny RESET lewego pucka (prywatny `ClickLogic`).

Prawy puck też ma "keep-awake" w prywatnym `ClickLogic` — mechanizm nieznany, do zbadania.

Rouvy działa "od strzała", bo według bloga BikeControl Rouvy zostało przejęte przez Zwift (04.2026)
i ma oficjalną, natywną integrację.

## 4. OpenBikeControl — mDNS + TCP (to, czego używa MyWhoosh)

- Mostek = **TCP server**, MyWhoosh = klient. Port dowolny (BikeControl preferuje 36867), ogłaszany w SRV.
- mDNS: `<Nazwa>._openbikecontrol._tcp.local.`, TXT: `version=1`, `id`, `name`,
  `service-uuids=d273f680-d548-419d-b9d1-fa0472345229`, `manufacturer`, `model`.
- Brak ramkowania — typ w pierwszym bajcie:
  - `0x01` Button State (→ app): `[01][id][state]...`, state `00`=puszczony, `01`=wciśnięty. Tylko na zmianę stanu.
  - `0x02` Device Status (→ app): `[02][battery 0-100|FF][connected 00|01]` — co 30–60 s i przy zmianie.
  - `0x03` Haptic (← app): 4 bajty.
  - `0x04` App Info (← app): `[04][ver][len][app_id][len][app_ver][count][ids...]`.
- MyWhoosh wspiera ID: `01` Shift Up, `02` Shift Down, `03` Gear Set, `10–17` nawigacja,
  `18/19` Steer L/R, `40` Camera View.

## 5. Alternatywa: "MyWhoosh Link"

TCP server na **stałym porcie 21587**, MyWhoosh łączy się sam; JSON zakończony `\n`, np.
`{"MessageType":"Controls","InGameControls":{"GearShifting":"1"}}` (`-1` w dół; też `Steering`, `CameraAngle`,
`Emote`, `UTurn`, `Tuck`). W BikeControl oznaczone jako niekompatybilne z MyWhoosh na tym samym komputerze z Windows.
OBC jest preferowane (oficjalny, udokumentowany protokół).
