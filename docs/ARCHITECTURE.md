# WinSMS Architecture

This document describes the current WinSMS implementation and the responsibilities of its principal components.

## Overview

WinSMS is an unpackaged WinUI 3 desktop application targeting .NET 8 on Windows. It communicates with cellular hardware through the Windows SMS API rather than through a serial/AT-command transport.

The main runtime flow is:

```text
WinUI Views
    ↓
ViewModels
    ↓
ISmsService / IMessageArchiveService
    ↓
SmsService ───────────────→ Windows.Devices.Sms
    ↓
XmlMessageArchiveService ─→ %AppData%\WinSMS\Messages
```

Supporting singleton services manage ICCID-keyed SIM/eSIM profiles, subscriber discovery, blocked numbers, and Windows startup behavior.

## Application bootstrap

`App.xaml.cs` creates the dependency-injection container.

Singleton services:

- `AppSettings`
- `IMessageArchiveService -> XmlMessageArchiveService`
- `ISmsService -> SmsService`
- `PhoneProfileService`
- `BlockedNumberService`
- `StartupService`

Transient view models:

- `InboxViewModel`
- `ComposeViewModel`
- `SettingsViewModel`
- `ProfilesViewModel`

## SMS transport

`SmsService` is the Windows SMS integration boundary.

### Sending

`SendMessageAsync`:

1. obtains `SmsDevice2.GetDefault()`;
2. creates and archives a pending outgoing message;
3. verifies that the device is ready;
4. creates `SmsTextMessage2`;
5. calls `SendMessageAndGetResultAsync`;
6. records success/failure information; and
7. updates the archived message.

### Receiving

The service registers `WinSMS.TextMessages` with `SmsMessageRegistration` and a text-message filter.

An existing registration with the same ID is unregistered before a fresh registration is created. This prevents the current process from trying to attach to a stale registration created by an earlier WinSMS process.

Incoming messages are acknowledged promptly. Blocked senders are rejected at the WinSMS application layer before archive persistence and before the service raises `MessageReceived`.

## Persistence

### Message archive

`XmlMessageArchiveService` stores messages under:

```text
%AppData%\WinSMS\Messages
```

The current format is one XML document per local/remote conversation:

```text
conversation-sub-<subscription-hash>-<remote-key>.xml
```

Each `Message` records identifiers and message state including direction, phone numbers, body, timestamp, status, read state, modem reference/index when available, and errors.

Writes use a temporary file followed by an overwrite move. A `SemaphoreSlim` serializes archive access inside the process.

The service also contains compatibility logic for legacy date-named XML archives.

### Profiles

`PhoneProfileService` stores profile data in:

```text
%AppData%\WinSMS\phone-profiles.json
```

Profiles are keyed by normalized ICCID. Phone number is optional metadata: Windows-reported numbers are authoritative/read-only, while users may supply a number when Windows does not report one. Profiles also retain subscriber ID, SIM/eSIM classification, and the Windows mobile-broadband connection profile name when available.

### Blocked numbers

`BlockedNumberService` stores normalized blocked numbers in:

```text
%AppData%\WinSMS\blocked-numbers.json
```

### Startup

`StartupService` manages the current user's registry entry:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

The `WinSMS` value contains the current executable path.

## Cellular subscription identity

`CellularSubscription` represents the active Windows mobile-broadband subscription. Its primary key is the SIM/eSIM **ICCID**. Subscriber ID/IMSI, Windows phone number, SIM/eSIM type, mobile-broadband interface ID, and Windows connection-profile name are metadata.

`LegacyMbnSubscriberService` re-enumerates the Win32 Mobile Broadband interfaces and reads fresh `IMbnSubscriberInformation` rather than caching functional interface objects. This is important because Windows can switch the active SIM/eSIM while the SMS device object remains the same.

`MobileBroadbandIdentityService` supplements subscriber information with read-only `netsh mbn` connection/slot diagnostics to determine Windows connection-profile metadata and, when possible, SIM versus eSIM slot classification.

`SmsDevice2.AccountPhoneNumber` is not treated as the subscription identity. On some dual-SIM/eSIM systems it can remain associated with a physical SIM even while Windows routes SMS through another subscription.

When Windows reports a telephone number for the active ICCID, `PhoneProfileService` overwrites that profile's number and marks it Windows-managed/read-only. When Windows does not report a number, the profile retains a user-editable number field.

## Conversation model

`SmsMessage` represents a single incoming or outgoing SMS.

`SmsConversation` groups messages by:

- normalized local subscription ICCID; and
- normalized remote number.

This prevents messages for different SIM/eSIM subscriptions from being merged even when one subscription has no reportable phone number.

## Presentation layer

### Compose

`ComposeViewModel` sends new SMS messages through `ISmsService`.

### Conversations

`InboxViewModel` is the conversation-oriented presentation model. It loads conversations for the current ICCID, sends replies, handles incoming-message events, deletes conversations/messages, and toggles blocking.

### Profiles

`ProfilesViewModel` exposes stored profiles and marks the profile corresponding to the current SMS account.

### Settings

`SettingsViewModel` manages startup state and Windows SMS diagnostics. The Settings page also provides direct access to the WinSMS data directory.

## Main window and tray lifecycle

`MainWindow` owns navigation and native notification-area integration.

On minimize:

1. a notification-area icon is added with `Shell_NotifyIcon`;
2. the window is removed from normal switchers; and
3. the main `AppWindow` is hidden.

Restoring reverses this process.

A separate `SmsNotificationWindow` displays the custom incoming-message popup while WinSMS is in the tray. Clicking it restores the main application and selects the relevant conversation.

Closing the main window is intercepted through `AppWindow.Closing` to request user confirmation. Confirmed shutdown removes the tray icon, detaches the SMS event handler, restores the native window procedure, and explicitly closes the auxiliary notification window so the process can terminate.

## Phone-number normalization

Several current components normalize numbers by:

1. retaining digits only;
2. removing a leading `00`; and
3. converting a sufficiently long number beginning with `0` to a number beginning with `44`.

This is UK-specific behavior and is currently duplicated across components. Internationalization work should consolidate number normalization into one shared implementation.

## Security and privacy considerations

WinSMS data is local but is not application-encrypted. Conversation XML contains message bodies and phone numbers. Profile and blocked-number files also contain phone-number information.

Diagnostic output can include device IDs, account numbers, SMSC addresses, SIM ICCIDs, and radio/modem information. Diagnostic logs should therefore be treated as potentially sensitive.

Blocked-number handling is an application policy only. Windows receives and acknowledges the SMS before WinSMS decides whether to archive or surface it.

## Hardware boundary

The application intentionally depends on Windows' SMS abstraction. It does not hard-code a modem model and does not use a serial COM-port/AT-command implementation.

Compatibility therefore depends on Windows, the modem driver, the mobile-broadband stack, the SIM/carrier, and whether `SmsDevice2` exposes the device to the application.
