using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using JabberWP.Core;
using JabberWP.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI.Core;

namespace JabberWP.Services
{
    /// <summary>
    /// The one live connection and the chats built from it.
    ///
    /// This is the ONLY place XmppConnection events are marshalled onto the UI
    /// thread. Core raises events on the socket's thread; every collection here is
    /// bound to XAML, so touching one off-thread would throw RPC_E_WRONG_THREAD.
    /// Doing it once here means no page ever has to think about it.
    /// </summary>
    public class AppState
    {
        private static AppState _instance;
        public static AppState Instance
        {
            get { return _instance ?? (_instance = new AppState()); }
        }

        private readonly Dictionary<string, Chat> BY_JID =
            new Dictionary<string, Chat>(StringComparer.OrdinalIgnoreCase);

        private CoreDispatcher _dispatcher;
        private XmppConnection _connection;

        /// <summary>JID of the XEP-0363 upload component, discovered once per session.</summary>
        private string _uploadService;

        private AppState()
        {
            Chats = new ObservableCollection<Chat>();
        }

        /// <summary>Contacts, bound directly by the contacts page.</summary>
        public ObservableCollection<Chat> Chats { get; private set; }

        public XmppAccount Account { get; private set; }

        public XmppState State
        {
            get { return _connection == null ? XmppState.Disconnected : _connection.State; }
        }

        public bool IsConnected
        {
            get { return State == XmppState.Connected; }
        }

        /// <summary>Chat currently open, so incoming messages there are not counted unread.</summary>
        public string ActiveChatJid { get; set; }

        /// <summary>
        /// True from the moment the file picker is launched until its continuation
        /// arrives. The picker suspends the app on this platform, and the suspend
        /// handler must not close the XMPP stream for what is really a round trip
        /// inside our own flow.
        /// </summary>
        public bool IsPickingFile { get; set; }

        public event EventHandler<XmppState> StateChanged;
        public event EventHandler<string> Closed;

        /// <summary>
        /// Captures the UI dispatcher. Call once from the first page that loads -
        /// there is no ambient way to get it from a non-UI thread later.
        /// </summary>
        public void AttachDispatcher(CoreDispatcher dispatcher)
        {
            if (_dispatcher == null)
            {
                _dispatcher = dispatcher;
            }
        }

        #region --Connection lifecycle--
        /// <summary>
        /// Connects with the given account. Returns null on success or an error to
        /// show the user.
        /// </summary>
        public async Task<string> ConnectAsync(XmppAccount account)
        {
            await DisconnectAsync();

            // Belongs to the old session's server; rediscovered on first use.
            _uploadService = null;

            Account = account;
            _connection = new XmppConnection(account);
            _connection.StateChanged += OnStateChanged;
            _connection.MessageReceived += OnMessageReceived;
            _connection.RosterReceived += OnRosterReceived;
            _connection.PresenceChanged += OnPresenceChanged;
            _connection.Closed += OnClosed;

            string error = await _connection.ConnectAsync();
            if (error != null)
            {
                Detach();
            }
            return error;
        }

        /// <summary>
        /// Connects using the stored account if the session is down. Returns null if
        /// already connected or the reconnect worked, otherwise an error.
        /// Used after the app comes back from the file picker, where the socket may
        /// not have survived being suspended.
        /// </summary>
        public async Task<string> EnsureConnectedAsync()
        {
            if (IsConnected)
            {
                return null;
            }

            XmppAccount account = Account;
            if (account == null || !account.IsUsable)
            {
                account = AccountStore.Load();
            }
            if (account == null || !account.IsUsable)
            {
                return "No account configured.";
            }
            return await ConnectAsync(account);
        }

        public async Task DisconnectAsync()
        {
            if (_connection == null)
            {
                return;
            }

            XmppConnection connection = _connection;
            Detach();
            await connection.DisconnectAsync();
            connection.Dispose();
        }

        private void Detach()
        {
            if (_connection == null)
            {
                return;
            }
            _connection.StateChanged -= OnStateChanged;
            _connection.MessageReceived -= OnMessageReceived;
            _connection.RosterReceived -= OnRosterReceived;
            _connection.PresenceChanged -= OnPresenceChanged;
            _connection.Closed -= OnClosed;
            _connection = null;
        }
        #endregion

        #region --Actions--
        public async Task SendAsync(Chat chat, string body)
        {
            if (chat == null || _connection == null || string.IsNullOrEmpty(body))
            {
                return;
            }

            XmppMessage message = await _connection.SendMessageAsync(chat.Jid, body);
            if (message != null)
            {
                // Already on the UI thread here (a button press), so add directly.
                chat.Add(message);
            }
        }

        /// <summary>
        /// Shares a picture: get a slot from the server's XEP-0363 component, PUT the
        /// bytes over HTTPS, then send the resulting URL as the message. This is how
        /// file sharing works on XMPP - there is no in-band image transfer worth
        /// having - and it is the same shape as the links currently arriving from
        /// other clients.
        ///
        /// Returns null on success, or an error to show the user.
        /// </summary>
        public async Task<string> SendImageAsync(Chat chat, StorageFile file)
        {
            if (chat == null || file == null)
            {
                return "Nothing to send.";
            }
            if (_connection == null || !IsConnected)
            {
                return "Not connected.";
            }

            // Cached: discovery is two round trips and the component does not move
            // during a session.
            if (_uploadService == null)
            {
                _uploadService = await _connection.FindUploadServiceAsync();
            }
            if (string.IsNullOrEmpty(_uploadService))
            {
                return "This server does not offer file upload (XEP-0363).";
            }

            BasicProperties properties = await file.GetBasicPropertiesAsync();
            UploadSlot slot = await _connection.RequestUploadSlotAsync(
                _uploadService, file.Name, properties.Size, file.ContentType);
            if (slot == null)
            {
                return "The server refused the upload - the file may be too large.";
            }

            string error = await HttpUploadService.PutAsync(slot, file, file.ContentType);
            if (error != null)
            {
                return error;
            }

            XmppMessage message = await _connection.SendFileUrlAsync(chat.Jid, slot.GetUrl);
            if (message == null)
            {
                return "Upload finished but the message could not be sent.";
            }

            chat.Add(message);
            return null;
        }

        /// <summary>
        /// Renames a contact. Applied locally straight away so the list reacts
        /// immediately, then pushed to the server; the roster push that comes back
        /// confirms it (and would correct it if the server disagreed).
        /// </summary>
        public async Task RenameAsync(Chat chat, string name)
        {
            if (chat == null)
            {
                return;
            }

            chat.Name = name == null ? "" : name.Trim();
            if (_connection != null)
            {
                await _connection.SetContactNameAsync(chat.Jid, chat.Name);
            }
        }

        /// <summary>
        /// The chat for a JID, creating one if the message came from somebody who is
        /// not in the roster.
        /// </summary>
        public Chat GetOrCreateChat(string bareJid, string displayName)
        {
            if (string.IsNullOrEmpty(bareJid))
            {
                return null;
            }

            Chat chat;
            if (BY_JID.TryGetValue(bareJid, out chat))
            {
                return chat;
            }

            RosterItem item = new RosterItem();
            item.Jid = bareJid;
            item.Name = displayName;
            chat = new Chat(item);
            BY_JID[bareJid] = chat;
            Chats.Add(chat);
            return chat;
        }
        #endregion

        #region --Connection events (arrive off the UI thread)--
        private void OnStateChanged(object sender, XmppState state)
        {
            RunOnUi(() =>
            {
                EventHandler<XmppState> handler = StateChanged;
                if (handler != null)
                {
                    handler(this, state);
                }
            });
        }

        private void OnClosed(object sender, string reason)
        {
            RunOnUi(() =>
            {
                EventHandler<string> handler = Closed;
                if (handler != null)
                {
                    handler(this, reason);
                }
            });
        }

        private void OnMessageReceived(object sender, XmppMessage message)
        {
            RunOnUi(() =>
            {
                Chat chat = GetOrCreateChat(message.ContactJid, null);
                if (chat == null)
                {
                    return;
                }
                chat.Add(message);
                if (!string.Equals(ActiveChatJid, chat.Jid, StringComparison.OrdinalIgnoreCase))
                {
                    chat.Unread = chat.Unread + 1;
                }
            });
        }

        private void OnRosterReceived(object sender, IList<RosterItem> items)
        {
            RunOnUi(() =>
            {
                foreach (RosterItem item in items)
                {
                    Chat existing;
                    if (BY_JID.TryGetValue(item.Jid, out existing))
                    {
                        // Keep the chat and its history, but take the new name: this
                        // path also carries roster pushes, which is how a rename made
                        // on another client (or by us) arrives.
                        existing.Name = item.Name;
                        continue;
                    }
                    Chat chat = new Chat(item);
                    BY_JID[item.Jid] = chat;
                    Chats.Add(chat);
                }
            });
        }

        private void OnPresenceChanged(object sender, RosterItem item)
        {
            RunOnUi(() =>
            {
                Chat chat;
                if (!BY_JID.TryGetValue(item.Jid, out chat))
                {
                    return;                        // presence from a non-contact
                }
                chat.Presence = item.Presence;
                chat.Status = item.Status;
            });
        }

        /// <summary>
        /// Runs the action on the UI thread. Everything below this point touches
        /// bound collections, so nothing may skip it.
        /// </summary>
        private void RunOnUi(Action action)
        {
            CoreDispatcher dispatcher = _dispatcher;
            if (dispatcher == null)
            {
                return;                            // no UI attached yet
            }
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                () => action());
        }
        #endregion
    }
}
