# WinSMS

WinSMS is a native Windows desktop application for sending, receiving, and managing SMS messages through an SMS-capable cellular modem exposed by the Windows SMS APIs.

Built with **WinUI 3**, **.NET 8**, and an MVVM-oriented architecture, WinSMS keeps its message archive and user configuration locally on the Windows account.

> [!IMPORTANT]
> WinSMS requires Windows to expose a compatible SMS device through `Windows.Devices.Sms`. A cellular modem being present in Device Manager does not by itself guarantee that Windows makes SMS functionality available to applications.

## Features

- **Send SMS** through the default Windows `SmsDevice2`.
- **Receive SMS** using a Windows `SmsMessageRegistration`.
- **Conversation view** groups incoming and outgoing messages by remote number.
- **Subscription separation by ICCID** keeps conversations associated with the actual SIM/eSIM subscription even when Windows does not expose its phone number.
- **Reply from a conversation** without opening a separate compose workflow.
- **Persistent local archive** stores conversations as XML files.
- **Number blocking** discards future incoming messages from blocked numbers before WinSMS archives or displays them.
- **SIM/eSIM profiles** are keyed by ICCID and can store a WinSMS name, colour, Windows connection-profile name, SIM type, and optional phone number.
- **ICCID-based SIM/eSIM detection** reads fresh Windows Mobile Broadband subscriber identity and treats phone number as optional metadata, avoiding stale `SmsDevice2.AccountPhoneNumber` values after a SIM/eSIM switch.
- **Title-bar SIM/eSIM switcher** opens Windows Cellular settings from the current-profile pill and watches the active ICCID until Windows completes the requested subscription change. The active item is checked in WinSMS.
- **Protected switching workflow** shows a full-window `Switching...` overlay while Windows changes the active SIM/eSIM, preventing navigation, sending, profile editing, or other interaction until the operation completes.
- **System tray support** keeps WinSMS running when the main window is minimized **or closed with the title-bar X button**.
- **Incoming-message popup** is shown while WinSMS is running in the notification area.
- **Dedicated Exit command** appears above Settings and asks for confirmation before actually terminating WinSMS.
- **Run at Windows sign-in** can be enabled or disabled from Settings.
- **SMS and subscription diagnostics** inspect the Windows SMS device, MBN subscriber ICCID/IMSI, SIM/eSIM slot state and mapping, connection-profile metadata, telephone-number availability, and the effective WinSMS subscription identity.
- **Open Data Folder** provides direct access to WinSMS local application data.

## Requirements

| Requirement | Details |
| --- | --- |
| Operating system | Windows 10 or Windows 11 |
| Target framework | `.NET 8` / `net8.0-windows10.0.19041.0` |
| UI framework | WinUI 3 / Windows App SDK |
| Hardware | SMS-capable cellular/mobile-broadband modem |
| Windows integration | Modem must be exposed through `Windows.Devices.Sms` |
| Mobile service | Active SIM/eSIM and carrier service capable of SMS |
| Architectures | x86, x64, ARM64 |

The application runs with the current user's privileges. WinSMS no longer writes Mobile Broadband slot mappings directly; Windows Cellular settings performs SIM/eSIM activation and switching.

## Getting started

### Clone the repository

```powershell
git clone https://github.com/taylorkobani/WinSMS.git
cd WinSMS
```

Open `WinSMS.sln` in Visual Studio, restore NuGet packages, select the architecture matching the Windows device, and run the `WinSMS` project.

### Build from the command line

With a compatible .NET/Windows development environment installed:

```powershell
dotnet restore WinSMS.sln
dotnet build WinSMS.sln
```

> [!NOTE]
> Actual SMS operation must be tested on Windows hardware for which `SmsDevice2.GetDefault()` returns an accessible SMS device.

## Using WinSMS

### Conversations

The **Conversations** page displays conversations for the currently detected cellular subscription (ICCID). Selecting a conversation opens its message history and reply composer. Phone number is not used as the local subscription identity.

Incoming messages are added to the corresponding conversation while the application is running. Outgoing messages are archived with their send status, including failed attempts.

A conversation can be deleted from the conversation list. Deleting a conversation removes its matching archived messages from WinSMS storage.

### New Message

Use **New Message** to enter a destination phone number and message body. Sending is performed through the Windows `SmsDevice2.SendMessageAndGetResultAsync` API.

### Blocking numbers

The block control beside a conversation toggles that remote number in the persistent blocked-number list.

When an incoming message is from a blocked number, WinSMS acknowledges the Windows delivery event and then discards the message before:

- writing it to the WinSMS message archive;
- adding it to a conversation; or
- raising the WinSMS incoming-message event used by its UI and popup.

> [!NOTE]
> Blocking is implemented by WinSMS. It is **not carrier-level or modem-level blocking**, and does not prevent the underlying Windows SMS subsystem from receiving the message.

### Profiles

The **Profiles** page identifies each local cellular subscription by **ICCID**, not by phone number. A profile stores:

- a user-defined WinSMS profile name;
- profile colour;
- SIM/eSIM classification when Windows exposes enough slot information;
- Windows mobile-broadband connection profile name (display metadata only);
- subscriber ID/IMSI metadata; and
- an optional phone number.

When Windows reports the phone number for the current ICCID, WinSMS overwrites the stored number with that Windows value and makes it read-only. When Windows does not report a number (common with some eSIM/carrier combinations), the user can enter the phone number manually.

The current profile is reflected in the application title area. ICCID remains the identity key regardless of whether a phone number is available.

Click the current-profile pill to open the SIM/eSIM selector. The active subscription is checked and disabled. Selecting the other type opens **Windows Settings > Network & internet > Cellular** so Windows can perform the subscription/profile change safely.

During the switch, WinSMS:
1. shows a full-window **Switching...** overlay;
2. blocks interaction with the rest of WinSMS;
3. opens Windows Cellular settings;
4. watches fresh Mobile Broadband subscriber information for the requested ICCID/SIM type; and
5. updates the current profile, title pill, and conversation scope only after Windows exposes the new active subscription.

WinSMS intentionally does **not** call `netsh mbn set slotmapping` to switch subscriptions. On some eSIM/DSSA hardware, raw executor-to-slot mapping is not equivalent to enabling the eSIM profile selected by Windows Settings and can leave the modem mapped to an eUICC with no active profile.

### Notification area

Minimizing the main window moves WinSMS to the Windows notification area and removes it from normal task switching. The title-bar **Close (X)** button intentionally behaves the same way: it hides WinSMS to the notification area instead of terminating the process. Clicking the tray icon restores the application.

While WinSMS is in the notification area, an incoming SMS can display the WinSMS popup. Selecting the popup restores WinSMS and opens the relevant conversation.

To terminate WinSMS, use the red **Exit** command above Settings. WinSMS asks for confirmation and, after confirmation, closes its auxiliary notification window and exits the process.

### Settings

Settings currently provides:

- **Run WinSMS when I sign in to Windows** — manages the current user's Windows startup entry.
- **Open Data Folder** — opens the WinSMS application-data directory in File Explorer.
- **Detect Windows SMS Device** — enumerates available Windows SMS devices and reports the effective current WinSMS subscription.
- **Run Diagnostics** — reports detailed Windows SMS and Mobile Broadband information, including ICCID, subscriber ID, SIM/eSIM slot state, slot mapping, Windows profile metadata, telephone-number availability, and the identity selected by WinSMS.

## Data storage

WinSMS stores persistent user data under:

```text
%AppData%\WinSMS\
```

This normally resolves to:

```text
C:\Users\<username>\AppData\Roaming\WinSMS\
```

Current data includes:

| Data | Location |
| --- | --- |
| Conversations/messages | `%AppData%\WinSMS\Messages\conversation-sub-<subscription-hash>-<remote>.xml` (new ICCID-scoped files; legacy phone-scoped files remain readable) |
| Phone profiles | `%AppData%\WinSMS\phone-profiles.json` |
| Blocked numbers | `%AppData%\WinSMS\blocked-numbers.json` |

These files are **outside the Git working tree** and are not committed to this repository by normal Git operations. They are also separate from the application installation directory.

The **Open Data Folder** button in Settings opens `%AppData%\WinSMS` directly.

> [!WARNING]
> Message archives and profile/blocking data are currently stored as ordinary XML/JSON files and are **not encrypted by WinSMS**. Anyone with sufficient access to the Windows account or its files may be able to read them.

### Windows startup entry

The optional startup setting is not stored in the data folder. It uses the current-user registry key:

```text
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
```

with a value named `WinSMS`.

## Architecture

WinSMS separates UI, presentation state, Windows SMS integration, and persistence:

```text
WinSMS/
├── Models/          Domain and application models
├── Services/        SMS, Mobile Broadband identity/switching, archive, profiles, blocking and startup services
│   └── Interfaces/  Service abstractions
├── ViewModels/      MVVM presentation logic
├── Views/           WinUI pages
├── Helpers/         Shared helper logic
├── App.xaml(.cs)    Application bootstrap and dependency injection
└── MainWindow.*     Navigation, tray integration and application window lifecycle
```

The application registers its services and view models through `Microsoft.Extensions.DependencyInjection`.

For a more detailed technical overview, see [Architecture](docs/ARCHITECTURE.md).

## Technology stack

| Component | Technology |
| --- | --- |
| Language | C# |
| Runtime | .NET 8 |
| Desktop UI | WinUI 3 |
| Windows SDK | Windows App SDK 2.3.1 |
| MVVM | CommunityToolkit.Mvvm 8.3.2 |
| Dependency injection | Microsoft.Extensions.DependencyInjection |
| Logging | Microsoft.Extensions.Logging |
| SMS API | `Windows.Devices.Sms` |
| Subscriber identity | Win32 Mobile Broadband (MBN) subscriber API |
| SIM/eSIM slot inspection | Windows `netsh mbn show slotstatus` / `slotmapping` |
| SIM/eSIM switching | Windows Cellular settings (`ms-settings:network-cellular`) with ICCID verification |
| Mobile-broadband diagnostics fallback | Windows `netsh mbn show readyinfo` |
| Message persistence | XML / LINQ to XML |
| Profile/block persistence | JSON |
| Tests | xUnit |

## SMS receive lifecycle

At startup, `SmsService` recreates the `WinSMS.TextMessages` registration rather than reusing a registration left by a previous process. The registration accepts text SMS messages.

For an accepted incoming text message, WinSMS:

1. reads the sender, recipient/local number when available, body, timestamp, and current subscription ICCID;
2. acknowledges the Windows SMS event;
3. checks the sender against the blocked-number service;
4. discards the message if blocked;
5. otherwise writes it to the XML archive; and
6. raises `MessageReceived` so the active UI can update.

### ICCID identity and eSIMs

Windows can route SMS through a selected eSIM while `SmsDevice2.AccountPhoneNumber` continues to report metadata from another SIM. WinSMS therefore does **not** use `AccountPhoneNumber` as the subscription identity.

WinSMS re-enumerates Windows Mobile Broadband subscriber information and uses the current **ICCID** as the stable identity. Telephone numbers are optional metadata tied to that ICCID. This allows an eSIM to remain a distinct profile even when its MSISDN/phone number is not exposed by Windows.

The phone-number field follows these rules:
- if Windows reports a number for the current ICCID, that value is authoritative, overwrites the stored value, and is read-only in the profile editor;
- if Windows does not report a number, the user can enter one manually and WinSMS stores it as metadata for that ICCID.

The Windows mobile-broadband connection/profile name is also retained for display where available, but it is **not** used as identity. ICCID remains the authoritative subscription key.

Existing phone-number-keyed profiles are migrated when the corresponding SIM is next detected and Windows reports a matching number. Existing message archives without an ICCID remain readable through the profile's legacy phone-number metadata.

### SIM/eSIM switching

WinSMS uses ICCID to detect which cellular subscription Windows has actually activated. Selecting another SIM/eSIM from the title pill opens the Windows **Cellular** settings page and waits for Windows to expose the requested subscription through fresh Mobile Broadband subscriber information.

This design is deliberate. Windows exposes low-level DSSA slot mapping through `netsh mbn set slotmapping`, but slot mapping only binds an executor to a UICC/eUICC slot. It does not safely replace the Windows eSIM-profile activation workflow on every modem. WinSMS therefore leaves activation/profile coordination to Windows Settings and observes the resulting ICCID.

While the change is in progress, a blocking overlay prevents the user from sending messages or changing WinSMS state against a subscription that Windows is still changing. Incoming-SMS registration is recreated when the active ICCID changes.

## Message archive

Messages are scoped using both the **local subscription ICCID** and **remote phone number**. The local phone number is optional metadata and is not used as the archive identity.

Phone-number normalization currently strips non-digits, converts a leading `00` international prefix, and treats sufficiently long numbers beginning with `0` as UK numbers by replacing the leading zero with country code `44`.

> [!CAUTION]
> The current normalization logic contains a UK-specific assumption. Contributors adding broader international-number support should centralize and revise this behavior before relying on it for other numbering plans.

## Diagnostics and privacy

The diagnostics screen can expose device and telecommunications identifiers such as device IDs, ICCIDs, subscriber IDs/IMSIs, SIM/eSIM slot information, account phone numbers, Windows profile names, and SMSC addresses.

Do not post diagnostic output publicly without reviewing and redacting sensitive identifiers first.

## Development

The solution contains:

- `WinSMS` — the WinUI desktop application.
- `WinSMS.Tests` — the xUnit test project for testable non-UI components.

Useful commands:

```powershell
dotnet restore WinSMS.sln
dotnet build WinSMS.sln
dotnet test WinSMS.Tests/WinSMS.Tests.csproj
```

Hardware-dependent Windows SMS behavior cannot be fully validated by ordinary unit tests and should also be exercised on a compatible Windows cellular device.

## Known limitations

- WinSMS depends on Windows exposing an SMS-capable default device; unsupported modem/driver combinations will not work.
- Number normalization currently contains UK-specific `44` handling.
- Blocking is application-level, not network-level.
- Local archives are not encrypted by WinSMS.
- SMS diagnostics may contain sensitive identifiers.
- SIM/eSIM switching depends on Windows Cellular settings and the modem/WWAN stack successfully activating the requested subscription; WinSMS observes the result by ICCID rather than forcing raw slot mapping.
- Some carriers/eSIMs do not expose an MSISDN/telephone number to Windows; WinSMS therefore treats the number as optional profile metadata.
- Incoming-message notifications are part of the running WinSMS process; WinSMS is not a background Windows service.
- A compatible physical modem/SIM environment is required to validate end-to-end sending, receiving, and SIM/eSIM switching.

## Publishing

WinSMS uses generated WinUI/XAML code during build and publish. If Visual Studio reports a generated-XAML type mismatch after a named control has changed type, clean stale intermediates before publishing:

```powershell
Remove-Item -Recurse -Force .\WinSMS\bin, .\WinSMS\obj -ErrorAction SilentlyContinue
```

Then reopen/rebuild the solution and publish again. Release/Publish can reuse generated files under `obj` that are not exercised by an already-running Debug build.

## Contributing

Issues and pull requests are welcome. When changing SMS behavior, keep hardware-specific assumptions out of the implementation where possible and preserve the separation between:

- Windows SMS transport;
- persistence;
- view models; and
- WinUI presentation.

Changes involving phone-number handling should account for the existing UK-specific normalization behavior and avoid introducing additional duplicated normalization rules.

## License

WinSMS is licensed under the [MIT License](LICENSE).

Copyright © 2026 Taylor Kobani.

## Maintainer

Maintained by [Taylor Kobani](https://github.com/taylorkobani).
