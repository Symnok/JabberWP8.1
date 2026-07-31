using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using JabberWP.Core;
using JabberWP.Models;

namespace JabberWP.Services
{
    /// <summary>
    /// The one live connection and the chats built from it.
    ///
    /// The ONLY place XmppConnection events are marshalled onto the UI thread. Core
    /// raises events on the socket's thread and every collection here is bound to
    /// XAML, so doing it anywhere else would mean touching bound state off-thread.
    /// Doing it once here also means the background agent can use Core without a UI
    /// thread existing at all.
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

        private XmppConnection _connection;

        /// <summary>Keeps the agent's "is the app connected?" answer fresh.</summary>
        private System.Windows.Threading.DispatcherTimer _heartbeatTimer;

        /// <summary>JID of the XEP-0363 upload component, discovered once per session.</summary>
        private string _uploadService;

        private AppState()
        {
            Chats = new ObservableCollection<Chat>();
            SubscriptionRequests = new ObservableCollection<SubscriptionRequest>();
        }

        public ObservableCollection<Chat> Chats { get; private set; }

        /// <summary>Unanswered requests to see our presence.</summary>
        public ObservableCollection<SubscriptionRequest> SubscriptionRequests { get; private set; }

        public XmppAccount Account { get; private set; }

        public XmppState State
        {
            get { return _connection == null ? XmppState.Disconnected : _connection.State; }
        }

        public bool IsConnected
        {
            get { return State == XmppState.Connected; }
        }

        /// <summary>Chat on screen, so its messages are not counted unread or toasted.</summary>
        public string ActiveChatJid { get; set; }

        public event EventHandler<XmppState> StateChanged;
        public event EventHandler<string> Closed;

        #region --Connection lifecycle--
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
            _connection.SubscriptionRequested += OnSubscriptionRequested;
            _connection.Closed += OnClosed;

            string error = await _connection.ConnectAsync();
            if (error != null)
            {
                Detach();
                return error;
            }

            // Only once there is a session worth keeping alive.
            LocationKeepAlive.Instance.Start();
            StartHeartbeat();
            return null;
        }

        /// <summary>
        /// Publishes "this app has a live session" for the background agent, which
        /// skips its own connect while we are up. That second "-bg" resource is
        /// what makes the account flicker offline in other clients.
        /// </summary>
        private void StartHeartbeat()
        {
            SessionHeartbeat.Beat();

            if (_heartbeatTimer == null)
            {
                _heartbeatTimer = new System.Windows.Threading.DispatcherTimer();
                // Well inside SessionHeartbeat.MAX_AGE, so an ordinary hiccup does
                // not read as a dead app and let the agent connect anyway.
                _heartbeatTimer.Interval = TimeSpan.FromSeconds(60);
                _heartbeatTimer.Tick += OnHeartbeatTick;
            }
            if (!_heartbeatTimer.IsEnabled)
            {
                _heartbeatTimer.Start();
            }
        }

        private void StopHeartbeat()
        {
            if (_heartbeatTimer != null && _heartbeatTimer.IsEnabled)
            {
                _heartbeatTimer.Stop();
            }
            // Cleared rather than left to expire: the agent should be free to run
            // again the moment we are genuinely gone.
            SessionHeartbeat.Clear();
        }

        private void OnHeartbeatTick(object sender, EventArgs e)
        {
            if (IsConnected)
            {
                SessionHeartbeat.Beat();
            }
            else
            {
                StopHeartbeat();
            }
        }

        /// <summary>
        /// Connects using the stored account if the session is down. Null if already
        /// connected or the reconnect worked.
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

            // Before the await: our session is over from here on, and the agent
            // should be free to take over without waiting out the timeout.
            StopHeartbeat();

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
            _connection.SubscriptionRequested -= OnSubscriptionRequested;
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
                chat.Add(message);
                Store(chat.Jid, message);
            }
        }

        /// <summary>
        /// Writes one message to the history file, off the UI thread.
        ///
        /// A save is a read-modify-write of the whole conversation file, which is
        /// far too much to do on the dispatcher for every message. Order does not
        /// matter: MessageStore sorts by timestamp when it reads.
        /// </summary>
        private static void Store(string bareJid, XmppMessage message)
        {
            Task ignored = Task.Run(delegate
            {
                MessageStore.Append(bareJid, message);
            });
        }

        /// <summary>
        /// Renames a contact. Applied locally at once, then pushed to the server; the
        /// roster push that comes back confirms it.
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

        public async Task AcceptSubscriptionAsync(SubscriptionRequest request)
        {
            if (request == null || _connection == null)
            {
                return;
            }

            SubscriptionRequests.Remove(request);
            GetOrCreateChat(request.Jid, null);
            // Also asks to see them: subscriptions are one-directional, so accepting
            // alone would leave the contact permanently "offline" in our list.
            await _connection.AnswerSubscriptionAsync(request.Jid, true, true);
        }

        public async Task DeclineSubscriptionAsync(SubscriptionRequest request)
        {
            if (request == null || _connection == null)
            {
                return;
            }

            SubscriptionRequests.Remove(request);
            await _connection.AnswerSubscriptionAsync(request.Jid, false, false);
        }

        /// <summary>
        /// Shares a picture: get a slot from the server's XEP-0363 component, PUT the
        /// bytes over HTTPS, then send the resulting URL as the message. That is how
        /// file sharing works on XMPP, and it is the same shape as the links other
        /// clients send us - the chat bubble already renders an image URL as a picture.
        ///
        /// Returns null on success or an error to show.
        /// </summary>
        public async Task<string> SendImageAsync(Chat chat, System.IO.Stream content,
            string fileName, string contentType)
        {
            if (chat == null || content == null)
            {
                return "Nothing to send.";
            }
            if (_connection == null || !IsConnected)
            {
                return "Not connected.";
            }

            // Cached: discovery is several round trips and the component does not move
            // while we are connected.
            if (_uploadService == null)
            {
                _uploadService = await _connection.FindUploadServiceAsync();
            }
            if (string.IsNullOrEmpty(_uploadService))
            {
                return "This server does not offer file upload (XEP-0363).";
            }

            long size = 0;
            try
            {
                size = content.Length;
            }
            catch (Exception)
            {
                // A stream that cannot report its length cannot be given a slot: the
                // server needs the size up front to accept or refuse it.
                return "Could not measure the picture.";
            }

            UploadSlot slot = await _connection.RequestUploadSlotAsync(
                _uploadService, fileName, (ulong)size, contentType);
            if (slot == null)
            {
                return "The server refused the upload. The picture may be too large.";
            }

            string error = await HttpUploadService.PutAsync(slot, content, contentType);
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
            Store(chat.Jid, message);
            return null;
        }

        /// <summary>Adds a contact and asks to see their presence.</summary>
        public async Task<string> AddContactAsync(string bareJid)
        {
            if (_connection == null || !IsConnected)
            {
                return "Not connected.";
            }

            Jid parsed = Jid.Parse(bareJid);
            if (parsed == null || string.IsNullOrEmpty(parsed.Local))
            {
                return "Enter a full Jabber ID, like someone@example.com.";
            }

            string jid = parsed.Bare;
            GetOrCreateChat(jid, null);
            await _connection.AddContactAsync(jid, null);
            return null;
        }

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
                Store(chat.Jid, message);
                if (!string.Equals(ActiveChatJid, chat.Jid, StringComparison.OrdinalIgnoreCase))
                {
                    chat.Unread = chat.Unread + 1;
                    ToastHelper.showMessage(chat.Jid, message.Body);
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
                        // This path also carries roster pushes, which is how a rename
                        // made on another client arrives.
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
                    return;
                }
                chat.Presence = item.Presence;
                chat.Status = item.Status;
            });
        }

        private void OnSubscriptionRequested(object sender, string bareJid)
        {
            RunOnUi(() =>
            {
                if (string.IsNullOrEmpty(bareJid))
                {
                    return;
                }

                // Servers resend outstanding requests on every connect.
                foreach (SubscriptionRequest existing in SubscriptionRequests)
                {
                    if (string.Equals(existing.Jid, bareJid, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                SubscriptionRequests.Add(new SubscriptionRequest(bareJid));
            });
        }

        /// <summary>
        /// Runs the action on the UI thread. Deployment.Current.Dispatcher rather than
        /// a captured CoreDispatcher: it is reachable from any thread, including the
        /// background agent's, where it simply has no UI to marshal to.
        /// </summary>
        private void RunOnUi(Action action)
        {
            try
            {
                System.Windows.Threading.Dispatcher dispatcher = Deployment.Current.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }
                if (dispatcher.CheckAccess())
                {
                    action();
                    return;
                }
                dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // No UI thread - running inside the background agent.
            }
        }
        #endregion
    }
}
