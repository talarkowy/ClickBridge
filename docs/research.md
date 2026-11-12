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

## 6. Obserwacje ze sprzętu

- 2026-11-11: Rouvy na Windows obsługuje **oba** pucki bez zainstalowanego Zwifta → Rouvy sam wykonuje
  handshake odblokowujący (oficjalna integracja). Wniosek: odblokowanie da się zrobić bez aplikacji Zwift.
- 2026-11-11, `click-diag` (logi `logs/click-diag-20261111-1856*.log`, `...-1859*.log`):
  - **Lewy puck (`0B`, FW 1.0.2) jest hubem** — po połączeniu tylko z nim dostajemy przyciski z OBU pucków
    (A/B/Y/Z i "+" prawego też). Prawy puck nie rozgłaszał się w tym czasie (sparowany z lewym).
  - Serwis Zwift jest widoczny jako `FC82` (nie ukryty), dodatkowe charakterystyki `...0100/0101/0102-19CA...`.
  - `RideOn 02 03` → odpowiedź `RideOn 02 03 0B` (bez klucza publicznego).
  - Co ~5 s: `19 10 3C` (bateria 60%) + `LOG_DATA` z tekstem `"Batt. Volt. 2910 mV"`. GATT 0x2A19 pokazuje 90%.
  - Po połączeniu: `LOG_DATA {1=3, 2={2={1=6, 2=0, 3=600, 4=[FF FF FF FF 1F], 5=251}}}` (i drugi z `1=12`).
  - W 1. sesji ~5 s po handshake: **vendor message** `FF 03 00 0A 21 02 F5 AF ...` (~85 B) — pole 1 to 33 B
    zaczynające się od `02` → wygląda na skompresowany klucz publiczny EC (P-256). Prawdopodobnie wyzwanie
    "blokady", na które odpowiada Zwift/Rouvy. W 2. sesji (bez restartu pucka) nie wystąpiło.
  - Użytkownik potwierdza: wszystkie przyciski działały. Ostatnie naciśnięcia były 44 s / 27 s / 10 s po handshake,
    bo potem przestał naciskać. **Blokady jeszcze nie zaobserwowano**, ale żaden test nie trwał z naciskaniem > 1 min.
  - Po każdej serii naciśnięć kontroler przez ~1 s powtarza ramkę "puszczone" `23 08 FF FF FF FF 0F`
    (co ~50–100 ms) — to normalne, nie maskowanie.
  - Wyzwanie `FF 03 00 0A 21 ...` pojawiło się też w sesji 3 (~5 s po handshake, inny klucz: `03 49 82 ...`).
  - Struktura wyzwania (85 B, oba egzemplarze identyczne strukturalnie):
    `FF` vendor, `03 00` nagłówek, potem protobuf:
    `1` = 33 B zaczynające się od `02`/`03` → skompresowany punkt EC (P-256), losowy w każdej sesji (efemeryczny);
    `2` = varint `0x02030000` (wersja? "2.3.0.0");
    `3` = 40 B wysokiej entropii (nonce + MAC / szyfrogram — do ustalenia).
- 2026-11-11 20:17, `logs/click-diag-20261111-201720.log` — **test długi (4 min 18 s)**:
  - Połączenie tylko z lewym puckiem, wyzwanie `FF 03 00 ...` przyszło 5 s po handshake i **zostało bez odpowiedzi**.
  - Mimo to przyciski **obu** pucków działały do samego końca (~590 naciśnięć, równomiernie w każdej minucie,
    ostatnie 2 s przed wyjściem). Żadnych rozłączeń, pustych ramek ani wzorca STOPPED.
  - Ok. 1 min po połączeniu interwał ramek baterii wydłużył się z ~5,05 s do ~5,6 s (prawdopodobnie tryb
    oszczędzania LED) — przyciski działały dalej.
  - **Blokada nie wystąpiła.** Dwa możliwe wyjaśnienia (do rozróżnienia):
    1. Click był niedawno (< 24 h) używany w Rouvy, które odświeżyło odblokowanie;
    2. firmware **1.0.2** (BikeControl zna 1.2.0) jeszcze nie egzekwuje blokady.
    Test rozstrzygający: ten sam test po > 24 h bez Rouvy. **Nie aktualizować firmware Clicka** bez potrzeby.
  - **Ramki przycisków, baterii i LOG_DATA nie są szyfrowane** — czysty protobuf. Kryptografia występuje tylko w
    wyzwaniu (wymiana kluczy / uwierzytelnienie aplikacji).

- 2026-11-11 20:37: `clickbridge` na PC — serwer TCP odpowiada statusem `02 FF 00`, parsuje App Info,
  rejestracja mDNS przez `DnsServiceRegister` działa: surowe zapytanie PTR `_openbikecontrol._tcp.local` dostaje
  PTR + SRV + TXT + A. Windows odpowiada rekordem A z adresem interfejsu, na którym przyszło zapytanie
  (np. na vEthernet — adres przełącznika wirtualnego), więc klient z LAN-u dostanie adres LAN. **Do sprawdzenia: realne połączenie MyWhoosh.**

- 2026-11-11 20:53: **MyWhoosh działa z `clickbridge` na tym samym PC** (`logs/clickbridge-20261111-204426.log`):
  - MyWhoosh (Windows, ze Sklepu) wymaga Bonjour („Dircon Services”, instalator `bonjoursdksetup.exe` w paczce).
    Mimo że mostek ogłasza się przez natywny responder Windows, MyWhoosh go znalazł ("OpenBikeControl instance found").
  - App Info od MyWhoosh: `app='mywhoosh' version='5.7.0'`, przyciski
    `01 02 03 10 11 12 13 14 15 16 17 18 19 40` (ShiftUp/Down, GearSet, nawigacja, Select/Back/Menu/Home, Steer L/R, Camera).
  - MyWhoosh co ~4 s wysyła pojedynczy bajt `00` (keep-alive, spoza specyfikacji) — mostek go teraz rozpoznaje.
  - Zmiana biegów z Clicka działa; Click był połączony od 20:44, przyciski działały po 9+ min (brak blokady).

## 7. Hipotezy do sprawdzenia na sprzęcie

- H1: prawy puck sam działa stabilnie dłużej niż kilka minut po samym `RideOn`.
- H2: lewy puck milknie po ~1 min bez odblokowania.
- H3: krótkie połączenie Click v2 z **Rouvy** odblokowuje go na ~24 h dla innych aplikacji (jak Zwift).
- H4: MyWhoosh na docelowym urządzeniu wykrywa nasz serwis mDNS i reaguje na `0x01`/`0x02`.
- H5: mDNS działa, gdy mostek i MyWhoosh są na **tym samym** komputerze z Windows.
