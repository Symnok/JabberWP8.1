# JabberWP

An XMPP (Jabber) chat client for **Windows Phone 8.1**, written against the
**Silverlight 8.1** app model.

Tested against xabber.org, talking to Conversations on Android and to UWPX on
Windows 10 Mobile.

## What works

- Connect: TCP, STARTTLS, SASL PLAIN, resource binding, legacy session
- Roster and live presence, with an availability dot per contact
- Send and receive chat messages
- Send pictures (from the library) via XEP-0363 HTTP upload
- Received image links render inline; any link in a message is tappable
- Presence subscriptions in both directions: accept/decline incoming requests, and
  add a contact (which sends one)
- Rename a contact by long-pressing it — a server-side roster rename, so it follows
  the account to other clients
- Toast notifications for messages that arrive while the app is in the background;
  tapping one opens that conversation
- Keeps running in the background (see below), plus a periodic agent that checks for
  messages when the app is not running at all
- Editable account, and a settings switch to turn background mode off

## Not implemented

- **No end-to-end encryption.** The connection is TLS-protected; message content is
  not encrypted. No OMEMO.
- No group chat (MUC)
- **Message history is in memory only** — closing the app loses the conversation.
  The roster survives; the messages do not.
- No account backup/export, no contact removal, single account only

## Requirements

**Visual Studio 2015** with the Windows Phone 8.1 SDK. No later Visual Studio can
build this project type, and VS 2015 caps the language at **C# 6** — the source
deliberately avoids anything newer.

## Building and deploying

Open `JabberWP.sln`. It contains two projects, and **both must be in the solution**:

| Project | What it is |
|---|---|
| `JabberWP` | The app |
| `JabberAgent` | The periodic background agent (a class library) |

`JabberWP` has a project reference to `JabberAgent`. If the agent project is missing
from the solution, the build fails with `CS0006: Metadata file 'JabberAgent.dll'
could not be found` — the reference cannot resolve because nothing built it. Re-add it
with **Add → Existing Project**, and check **Build → Configuration Manager** maps it
for the configuration you are building.

Select **Release / ARM** for a device, then build. The package lands at:

```
JabberWP\Bin\ARM\Release\JabberWP_Release_ARM.xap
```

Note `Bin` with a capital B, directly under the project folder — Silverlight projects
do not use an `AppPackages` folder. Deploy that `.xap` with the **Windows Phone
Application Deployment 8.1** tool, or press F5 in Visual Studio with the phone
connected. No signing certificate is needed for sideloading a XAP.

## Layout

```
JabberWP/
  Core/         Protocol. No XAML, no dispatcher, no navigation.
    Xmpp                 namespaces and constants
    Jid                  bare/full JID parsing
    XmppAccount          connection settings
    XmppStreamParser     splits the never-ending XML stream into stanzas
    XmppConnection       the state machine, and every stanza sent or received
    XmppMessage          a message, plus link/image detection
    UploadSlot           XEP-0363 PUT/GET pair
    HttpUploadService    the HTTP half of XEP-0363
  Services/
    AccountStore         the account, in IsolatedStorageSettings
    AppSettings          preferences (background mode on/off)
    AppState             the one live connection; the only place events reach the UI
    LocationKeepAlive    the background-execution trick
    BackgroundAgentHelper  registers the periodic agent
    ToastHelper          ShellToast notifications
  Models/       Chat, SubscriptionRequest — bindable view state
  Pages/        LoginPage, ContactsPage, ChatPage, AccountPage, SettingsPage
  Converters/   value converters (Silverlight has no x:Bind)

JabberAgent/
  ScheduledAgent         runs every ~30 min when the app is not running
```

`Core` has no UI dependency at all: it raises events on the socket's thread and never
touches a dispatcher. `AppState` marshals every event to the UI thread in one place.
That is what lets `Core` be used from the background agent, where there is no UI
thread to marshal to.

`MainPage.xaml`, `LocalizedStrings.cs` and `Resources\AppResources.resx` are leftovers
from the project template. `MainPage` is not in the project (`LoginPage` is the start
page, set as `NavigationPage` in `WMAppManifest.xml`) and can be deleted.

## Background operation

Two independent mechanisms, both switchable off in **settings → run in the
background**:

**1. Continuous background execution.** `WMAppManifest.xml` declares

```xml
<DefaultTask Name="_default" NavigationPage="Pages/LoginPage.xaml" ActivationPolicy="Resume">
  <BackgroundExecution>
    <ExecutionType Name="LocationTracking" />
  </BackgroundExecution>
</DefaultTask>
```

which lets the app keep running after HOME is pressed, instead of being suspended and
having its socket torn down. **This is the reason the app is a Silverlight app**: the
element has no equivalent in an appx manifest, so a WP8.1 WinRT app cannot do it.

The declaration alone does nothing. The system honours it only while the app holds a
Geolocator subscription that is *actively reporting* — hence
`MovementThreshold = 0` and `ReportInterval = 5000` in `LocationKeepAlive`. A frugal
subscription (long interval, large threshold) is not treated as tracking and the app
gets suspended anyway. The position is never read, stored or transmitted; the cost is
the location indicator staying on and worse battery life.

**2. A periodic agent** (`JabberAgent`) for when the app is not running at all. The OS
schedules it roughly every 30 minutes at its own discretion, allows it about 25
seconds, and caps its memory. It connects, drains for 12 seconds, toasts one
notification per sender, updates the tile count, and stops.

Notes on the agent, all learned the hard way:

- It must be declared as an `ExtendedTask` **inside** `<Tasks>`, after `DefaultTask`.
  The manifest validator rejects it anywhere else.
- `Name`/`Source` must match the agent assembly and `Type` its full class name, or the
  OS silently refuses to schedule it.
- `BackgroundAgentHelper.Register()` runs on every launch and activation on purpose: a
  `PeriodicTask` expires after at most 14 days, after which the OS stops running it.
- Debug builds call `ScheduledActionService.LaunchForTest(…, 30 s)` so it fires half a
  minute after launch instead of whenever the OS feels like it.
- `Core` is **linked** into the agent project (`<Compile Include="..\JabberWP\Core\…">`
  with `<Link>`), not referenced as a DLL — one copy of the source, compiled into both,
  which avoids WP8.1's cross-project agent dependency trouble.
- The agent connects with the resource suffix `-bg` so it cannot collide with the
  foreground session.

## Things worth knowing before changing this

**Stanza ids must be unique across sessions, not just within one.** `NextId()` uses a
random per-connection prefix plus a counter. It originally used a bare counter, so the
third message of every session was `jw3` — and receivers that key stored messages on
the sender's stanza id (UWPX uses id + chat id as a primary key, written with
`InsertOrReplace`) silently *overwrote* an older message instead of adding a new one.
The far end stopped showing new messages while notifications still arrived.

**The stream restarts after STARTTLS and after SASL.** Each restart is a brand new XML
document and needs a fresh `DataReader`/`DataWriter`; the parser buffer is reset too.
Getting this wrong is the usual reason a hand-written XMPP client hangs after auth.

**The stream parser is hand-written** because an XMPP session is one XML document that
never ends, so `XDocument.Load` would block forever. It splits on tag boundaries,
respecting quoted attributes, and treats the unclosed `<stream:stream>` header as a
special case. Each stanza is then parsed inside a synthetic root that declares the
`stream` and `jabber:client` namespaces, without which fragments like
`<stream:features>` do not parse.

**Toasts never appear while the app is in the foreground** — the platform suppresses an
app's own toasts. They are a background feature, so testing them means backgrounding
the app.

**A second resource confuses some clients.** The agent's short-lived `-bg` session
broadcasts available then unavailable on every run. Clients that collapse presence to
the bare JID and take the last stanza will show this account as *offline* while the
foreground session is still connected. That was a bug in UWPX (since fixed there by
tracking presence per resource), but other clients may behave the same way.

**Choosers must be constructed in the page constructor.** `PhotoChooserTask` takes the
app away and the page can be recreated before the result arrives, so a handler attached
later never fires.

**Back-stack handling is deliberate.** `ContactsPage` clears the back stack on arrival,
so it is always the app's root: BACK there leaves the app rather than returning to the
login page. `ChatPage` overrides `OnBackKeyPress` and redirects to the contact list when
it has nothing to go back to, which is the case when a toast deep-linked straight into
a conversation.

**Accepting a subscription also requests one back.** Subscriptions are one-directional;
answering `subscribed` alone would let the contact see you while they stayed
permanently "offline" in your list.

## Capabilities

| Capability | Why |
|---|---|
| `ID_CAP_NETWORKING` | the XMPP connection |
| `ID_CAP_LOCATION` | the background-execution trick, not for finding the user |
| `ID_CAP_MEDIALIB_PHOTO` | picking a picture to send |
| `ID_CAP_ISV_CAMERA` | taking a photo from inside the picker |

The location capability is declared at install time and cannot be conditional. With
background mode switched off nothing ever calls the location API, so no location is
accessed and no indicator appears.
