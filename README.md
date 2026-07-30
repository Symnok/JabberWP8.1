# JabberWP — XMPP client for Windows Phone 8.1

A minimal XMPP/Jabber client targeting the **Windows Phone 8.1**

## Status

First cut. What works end to end:

- TCP connect, STARTTLS, SASL PLAIN, resource binding, session
- Roster fetch, presence in/out
- Send and receive chat messages
- Three screens: Login, Contacts, Chat

Deliberately **not** implemented yet:

- **Background operation.** Nothing runs when the app is not in the foreground. See
  "Background, later" below — this is the next decision, not an oversight.
- OMEMO / any end-to-end encryption. The connection is TLS-protected; message
  content is not encrypted end to end.
- MUC (group chat), file transfer, message archive (MAM), carbons, avatars.

## Build

Requires **Visual Studio 2015 Update 3** with the Windows Phone 8.1 SDK. No later
VS can build this project type.

1. Open `JabberWP.sln`.
2. First build will complain about a missing signing certificate. Open
   `Package.appxmanifest` → **Packaging** → **Choose Certificate…** → **Create test
   certificate…**. This generates `JabberWP_TemporaryKey.pfx` and wires it into the
   project. One-time step; the pfx is intentionally not in the repo.
3. Build for `ARM` (device) or `x86` (emulator).

If VS refuses to load the project at all, the fallback is: create a new
**Blank App (Windows Phone)** from the VS template, then add the existing files
from `JabberWP\` (Core, Services, Models, Pages, App.xaml). Nothing in the source
depends on how the project file is written.

## Design

### One project, on purpose

Everything lives in a single app project, with the protocol code in `Core\`.

WP8.1 makes cross-project references genuinely painful — your own
`GoogleContactSyncWP` notes this ("duplicates HTTP and storage logic to avoid
cross-project DLL dependency issues on WP8.1"). When a background task project is
added later, the intended approach is to **link** the `Core\*.cs` files into it
(Add Existing Item → Add As Link) rather than reference a DLL. That keeps one copy
of the source without a binary dependency.

### Layers

```
Core/         Protocol. No UI, no XAML, no page navigation.
  Jid                  bare/full JID parsing
  XmppAccount          connection settings
  XmppStreamParser     splits the never-ending XML stream into stanzas
  XmppConnection       the state machine: connect -> TLS -> auth -> bind -> ready
  RosterItem           one roster entry
  XmppMessage          one chat message
Services/     App-wide glue.
  AccountStore         credentials (PasswordVault) + settings (LocalSettings)
  AppState             the single live XmppConnection + the open chats
Models/       Bindable view models (INotifyPropertyChanged).
  Chat                 one conversation: contact + ObservableCollection<XmppMessage>
Pages/        UI. Thin: everything happens through AppState.
  LoginPage, ContactsPage, ChatPage
```

`Core` talks to the network and raises events. It never touches the dispatcher —
marshalling to the UI thread is the caller's job (`AppState` does it once, so pages
never have to). This is deliberate: it is what makes `Core` reusable from a
background task, which has no UI thread at all.

### Connection sequence

`XmppConnection.ConnectAsync` walks RFC 6120 in order:

1. `StreamSocket.ConnectAsync` to `host:5222` (plain TCP)
2. Send the stream header, read `<stream:features>`
3. `<starttls/>` → `<proceed/>` → `UpgradeToSslAsync(Tls12)` → **restart the stream**
4. SASL `PLAIN` (only over TLS — refused otherwise) → `<success/>` → **restart the stream**
5. Resource bind → full JID
6. Legacy `<session/>` if the server advertises it (older ejabberd/Openfire want it)
7. Initial `<presence/>` + roster request, then the read loop

Stream restarts matter: after TLS and after SASL the XML stream is thrown away and
begins again, including a fresh `DataReader`/`DataWriter`. Getting this wrong is the
usual reason a hand-written XMPP client hangs after auth.

### Why the stream parser is hand-written

An XMPP session is a single XML document that never ends, so `XDocument.Load` cannot
be used — it would block forever waiting for the closing tag.
`XmppStreamParser` scans incoming text for complete top-level elements and emits
them one at a time. It tracks quoting inside attributes so a `>` inside an attribute
value cannot end a tag early, and treats the unclosed `<stream:stream>` header as a
special case.

Each stanza is then wrapped in a synthetic root carrying the `stream` and
`jabber:client` namespace declarations before `XElement.Parse`, otherwise elements
like `<stream:features>` fail to parse as standalone fragments.

## Background, later

Nothing here runs in the background yet. When that work starts, the choice is:

- **`ControlChannelTrigger`** — the OS holds the TCP socket and wakes the app when a
  stanza arrives. The right answer for a chat client, and the main reason to be on
  the WinRT model at all. Device-wide slot limits apply, and the socket must be
  created *through* the trigger, which is why `XmppConnection` keeps socket creation
  in one place (`ConnectSocketAsync`) rather than scattered.
- **Periodic `TimeTrigger`** — simple, 15-minute floor, misses messages between wakes.
- **XEP-0357 push** — best battery life, needs a server-side component.

Note: `GoogleContactSyncWP`'s `ScheduledAgent` is a **Silverlight 8.1** background
agent (`Microsoft.Phone.Scheduler.ScheduledTaskAgent`). That API does not exist in
the WinRT model, so it is a useful reference for *structure* — periodic work,
credentials in shared storage, tile updates — but not code that can be reused
directly here.
