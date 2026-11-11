# Zwift Click v2 — dane do analizy kryptograficznej

Zebrane 2026-11-11. Wszystkie bajty jako hex, czas lokalny (HH:MM:SS.mmm).
Źródłowe logi: `logs/click-diag-20261111-*.log` (surowe ramki BLE z narzędzia diagnostycznego).

## 0. Najważniejsze zastrzeżenie — u nas komunikacja NIE ustaje

Pytanie rozdzielające transport od kryptografii: **zapis BLE się udaje, kontroler reaguje, a my
nie wiemy tylko, czym odpowiedzieć na „wyzwanie”.** Dokładniej:

- Zapis `RideOn` do charakterystyki `...0003` zwraca `GattCommunicationStatus.Success`.
- Kontroler odpowiada (echo `RideOn`), wysyła zdarzenia przycisków, baterię i logi — wszystko jawnym protobuf.
- ~5 s po handshake kontroler przysyła jednorazową ramkę „wyzwania” (`FF 03 00 ...`), na którą **nie odpowiadamy**.
- Mimo braku odpowiedzi **przyciski działały dalej przez cały test** (4 min 18 s w sesji 20:17, 9+ min przez
  `clickbridge`). **Nie zaobserwowaliśmy ustania komunikacji.** Dlatego nie mamy logu „do ustania” — bo nie ustała.

Hipotezy, czemu blokada się nie uruchomiła (niepotwierdzone):
1. firmware **1.0.2** jeszcze nie egzekwuje blokady (BikeControl wspomina 1.2.0);
2. Click był niedawno (< 24 h) odblokowany przez Rouvy (oficjalny partner Zwifta), co ponoć trzyma ~24 h.

Egzemplarz: **Zwift Click v2, lewy puck** (`D8:D1:5A:F5:3A:CF`, manufacturer data zaczyna się `0B`).
Lewy puck jest „hubem” — przekazuje przyciski obu pucków, więc łączymy się tylko z nim. Prawy puck (`0A`)
nie był osobno badany (sparowany z lewym, nie rozgłasza się).

## 1. Pełne ramki wyzwania (3 egzemplarze, każdy 85 bajtów)

Charakterystyka `00000002-19CA-4651-86E5-FA29DCDD09D1` (ASYNC, Notify), kierunek: device → host.

```
2026-11-11 18:56:34.911
FF 03 00 0A 21 02 F5 AF D8 58 95 F7 CE AF CC EC 74 9C FD 4A AF 94 EA 2F B6 35 90 80 B4 EE 29 96 E4 91 67 69 12 69 10 80 80 8C 10 1A 28 68 F5 9A E1 97 4F 30 43 E8 01 63 14 51 20 BF D2 70 12 D4 4A 82 8B 1B A5 F3 9E 77 05 C6 B2 63 C8 80 53 B1 C9 0A B7 67 FA

2026-11-11 19:03:20.945
FF 03 00 0A 21 03 49 82 F4 FA F2 A7 54 12 D6 26 02 65 F1 79 54 41 B9 74 E3 19 7F 28 1C C7 B8 04 73 EA 2B B1 4C 30 10 80 80 8C 10 1A 28 8A 01 03 15 69 D4 F7 B3 6F A6 DF E3 2D FB B7 CE FA 7A 69 D6 15 52 1D C5 70 1F B0 3D 7D A8 E1 CC 32 C4 5E 7A D9 67 2F 27

2026-11-11 20:17:31.080
FF 03 00 0A 21 02 5F 70 6F A5 90 B0 1C 3A 3D AE CB 58 38 27 DD 42 D4 29 FA 12 03 EC E9 A2 A2 89 B1 EC 5C 31 A1 21 10 80 80 8C 10 1A 28 52 CE 44 FE 3C 1F 8A 36 B7 FC F8 79 3B 7F F2 FF B4 0E C2 E9 4F BD 11 5B 32 56 08 39 86 DE 07 D8 DD A0 05 12 6F 48 34 20
```

### Nasza interpretacja struktury

`FF` = opcode VENDOR_MESSAGE, `03 00` = 2-bajtowy nagłówek, reszta = protobuf:

| Pole | Tag | Typ | Zawartość |
|---|---|---|---|
| 1 | `0A` | len=`0x21` (33 B) | zaczyna się od `02`/`03` → **skompresowany punkt EC P-256** (SEC1), inny w każdej sesji (efemeryczny) |
| 2 | `10` | varint `80 80 8C 10` | = `0x02030000` (wersja? „2.3.0.0”), stała we wszystkich 3 sesjach |
| 3 | `1A` | len=`0x28` (40 B) | dane wysokiej entropii — nonce + tag/MAC albo szyfrogram (do ustalenia) |

Pole 1 wygląda na publiczny klucz EC urządzenia (ECDH). Pole 3 (40 B = np. 8 B nonce + 32 B,
albo 12+16+12) nie jest przez nas rozpoznane.

## 2. Log połączenia (sesja 20:17:20, od skanowania do pełnej komunikacji)

Kierunek: `=>` host→device (write), `<=` device→host (notify/indicate/read).
Mapowanie etykiet na charakterystyki: `ASYNC` = `...0002`, `SYNCTX` = `...0004`, write `SyncRx` = `...0003`.

```
20:17:23.824  SCAN   advertisement: ClickV2Left D8:D1:5A:F5:3A:CF rssi=-69 manufData(Zwift 0x094A)=0B 3A CF
20:17:23.925  link   Connected
20:17:23.926  gatt   GATT session Active (Success)
20:17:25.370  gatt   Services: 1800, 1801, 180A, 0000fc82-0000-1000-8000-00805f9b34fb, 180F
20:17:25.455  read   Device Information / Firmware Revision (0x2A26) = "1.0.2"
20:17:25.522  read   Battery Level (0x2A19) = 90%
              # charakterystyki usługi fc82 (= skrót usługi Zwift 00000001-19CA-...):
              #   00000002-19CA-4651-86E5-FA29DCDD09D1  [Notify]
              #   00000003-19CA-4651-86E5-FA29DCDD09D1  [WriteWithoutResponse]
              #   00000004-19CA-4651-86E5-FA29DCDD09D1  [Read, Indicate]
              #   00000100 / 00000101 / 00000102-19CA-... [Write/Notify]  (nieużywane przez nas)
20:17:25.629  =>CCCD subscribe 00000002 (Notify)   -> Success
20:17:25.672  =>CCCD subscribe 00000004 (Indicate) -> Success
20:17:25.679  =>0003 52 69 64 65 4F 6E 02 03        -> Success        ("RideOn" + 02 03)
20:17:25.697  <=0004 52 69 64 65 4F 6E 02 03 0B     (echo RideOn, header 02 03, payload 0B)
20:17:26.198  <=0002 2A 08 03 12 12 12 10 08 06 10 00 18 D8 04 22 05 FF FF FF FF 1F 28 00   (LOG_DATA)
20:17:26.924  <=0002 2A 08 03 12 12 12 10 08 0C 10 00 18 D8 04 22 05 FF FF FF FF 1F 28 00   (LOG_DATA)
20:17:28.695  <=0002 19 10 5A                        (battery 90%)
20:17:28.695  <=0002 2A 08 02 12 15 22 13 42 61 74 74 2E 20 56 6F 6C 74 2E 20 32 39 31 30 20 6D 56  (LOG "Batt. Volt. 2910 mV")
20:17:31.080  <=0002 FF 03 00 0A 21 02 5F 70 ...     (WYZWANIE, 85 B — pełny hex w sekcji 1)
              # brak odpowiedzi z naszej strony
20:17:xx..20:21:44  <=0002 zdarzenia przycisków (23 08 ..), bateria (19 10 ..), logi (2A ..) — nieprzerwanie
20:21:44.067  APP    zamknięcie przez użytkownika (nie rozłączenie przez urządzenie)
```

Ramki przycisków: `23 08 <varint>` — pole 1 to bitmapa, **odwrócona** (bit = 0 → wciśnięty).
Przykłady: `23 08 FF FD FF FF 0F` = „−” lewego; `23 08 FF DF FF FF 0F` = „+” prawego; `23 08 FF FF FF FF 0F` = nic.
Pełny ciągły log każdej sesji jest w plikach `logs/click-diag-20261111-*.log` (dołączone osobno).

## 3. Kod BLE (subskrypcja + zapis)

System: **Windows 11 Pro Insider Preview, 10.0.26340**. Adapter BLE: Intel (LE + central, radio On).
Biblioteka: **WinRT / Windows SDK** (`Windows.Devices.Bluetooth.GenericAttributeProfile`), .NET 10, C#.
Pełne źródło: `src/ClickBridge.Ble/ZwiftController.cs`.

```csharp
// Subskrypcja (ASYNC -> Notify, SYNCTX -> Indicate):
var value = (preferIndicate && canIndicate) || !canNotify
    ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
    : GattClientCharacteristicConfigurationDescriptorValue.Notify;
var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(value);

// Handshake — zapis do SyncRx (00000003):
byte[] command = ZwiftConstants.ClickV2StartCommand;          // "RideOn" + 0x02 0x03
var option = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
    ? GattWriteOption.WriteWithoutResponse
    : GattWriteOption.WriteWithResponse;
var status = await characteristic.WriteValueAsync(command.AsBuffer(), option);   // -> Success

// Odbiór: subskrybujemy ValueChanged na 0002 (Notify) i 0004 (Indicate):
_async.ValueChanged += OnAsyncValueChanged;    // 00000002, Notify
_syncTx.ValueChanged += OnSyncTxValueChanged;  // 00000004, Indicate
```

UUID (usługa Zwift):
```
usługa:  00000001-19CA-4651-86E5-FA29DCDD09D1  (rozgłaszana też jako 16-bit 0xFC82)
0002  Notify                 — zdarzenia (ASYNC): przyciski, bateria, log, wyzwanie
0003  WriteWithoutResponse   — komendy host→device (SyncRx): tu piszemy "RideOn"
0004  Read, Indicate         — odpowiedzi (SyncTx): echo "RideOn"
```

## 4. Udane połączenie Zwift / Rouvy

**Nie mamy tego zapisu.** Nasze narzędzie jest centralą BLE — gdy Click jest połączony z Rouvy/Zwift,
nie rozgłasza się i nie możemy go równolegle podsłuchać. Zdjęcie wymiany Rouvy↔Click wymaga biernego
sniffera (np. nRF52840 + nRF Sniffer) albo logu HCI Windows (`btvs.exe` z WDK) z komputera, na którym
działa Rouvy. Możemy to zrobić, jeśli to potrzebne — damy znać.

## 5. Odpowiedź na pytanie rozdzielające

| Warstwa | Stan |
|---|---|
| Transport BLE (zapis `0003`) | **OK** — zapis zwraca `Success` |
| Reakcja kontrolera | **OK** — echo `RideOn`, przyciski, bateria, logi płyną |
| Odpowiedź na wyzwanie krypto | **brak** — nie wiemy, czym odpowiedzieć ani przez którą charakterystykę |
| Skutek braku odpowiedzi | **na razie żaden** (fw 1.0.2) — przyciski działają dalej, blokady nie odtworzyliśmy |

Czyli: to nie problem transportu. Brakuje nam odpowiedzi kryptograficznej, ale na tym egzemplarzu/firmware
jej brak jeszcze niczego nie psuje.
