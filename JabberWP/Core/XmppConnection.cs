using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace JabberWP.Core
{
    /// <summary>
    /// One XMPP client session over a single TCP connection.
    ///
    /// Deliberately free of any UI dependency: no dispatcher, no XAML types, no
    /// navigation. Events are raised on whatever thread the socket read completed
    /// on, and marshalling them to the UI thread is the caller's job (AppState does
    /// it in one place). That is what allows this class to be linked into a
    /// background task later, where there is no UI thread at all.
    ///
    /// Sequence (RFC 6120 §4-§7): connect, stream header, STARTTLS, restart, SASL,
    /// restart, bind, session, initial presence.
    /// </summary>
    public class XmppConnection : IDisposable
    {
        #region --Fields--
        private const int READ_CHUNK = 4096;
        private const int HANDSHAKE_TIMEOUT_MS = 20000;

        private readonly XmppAccount ACCOUNT;
        private readonly XmppStreamParser PARSER = new XmppStreamParser();
        private readonly Queue<XElement> PENDING = new Queue<XElement>();
        private readonly Decoder UTF8_DECODER = Encoding.UTF8.GetDecoder();

        /// <summary>IQ requests waiting for their reply, keyed by stanza id.</summary>
        private readonly Dictionary<string, TaskCompletionSource<XElement>> PENDING_IQ =
            new Dictionary<string, TaskCompletionSource<XElement>>();

        private StreamSocket _socket;
        private DataReader _reader;
        private DataWriter _writer;
        private CancellationTokenSource _readLoopCts;

        private int _iqCounter;
        private bool _disposed;
        #endregion

        #region --Properties and events--
        public XmppState State { get; private set; }

        /// <summary>Full JID the server assigned at bind time. Null until bound.</summary>
        public Jid BoundJid { get; private set; }

        public event EventHandler<XmppState> StateChanged;
        public event EventHandler<XmppMessage> MessageReceived;
        public event EventHandler<IList<RosterItem>> RosterReceived;
        public event EventHandler<RosterItem> PresenceChanged;

        /// <summary>
        /// Raised with the bare JID of somebody asking to see our presence. Nothing
        /// is sent back until the user answers - see AnswerSubscriptionAsync.
        /// </summary>
        public event EventHandler<string> SubscriptionRequested;

        /// <summary>Raised once when the session ends, with a human-readable reason.</summary>
        public event EventHandler<string> Closed;
        #endregion

        public XmppConnection(XmppAccount account)
        {
            if (account == null)
            {
                throw new ArgumentNullException("account");
            }
            ACCOUNT = account;
            State = XmppState.Disconnected;
        }

        #region --Connect / disconnect--
        /// <summary>
        /// Runs the full handshake and, on success, starts the read loop. Returns
        /// null on success or a human-readable error. Does not throw for ordinary
        /// failures (bad password, host unreachable) - those are expected outcomes.
        /// </summary>
        public async Task<string> ConnectAsync()
        {
            if (State != XmppState.Disconnected && State != XmppState.Failed)
            {
                return "Already connected.";
            }
            if (!ACCOUNT.IsUsable)
            {
                return Fail("Account is incomplete.");
            }

            Jid accountJid = Jid.Parse(ACCOUNT.Jid);
            string domain = accountJid.Domain;

            try
            {
                SetState(XmppState.Connecting);
                await ConnectSocketAsync();

                XElement features = await OpenStreamAsync(domain);
                if (features == null)
                {
                    return Fail("Server did not send stream features.");
                }

                // ---- STARTTLS -------------------------------------------------
                bool serverOffersTls = features.Element(Xmpp.TLS_NS + "starttls") != null;
                if (serverOffersTls)
                {
                    SetState(XmppState.Securing);
                    string tlsError = await StartTlsAsync(domain);
                    if (tlsError != null)
                    {
                        return Fail(tlsError);
                    }
                    features = await OpenStreamAsync(domain);
                    if (features == null)
                    {
                        return Fail("Server did not send stream features after TLS.");
                    }
                }
                else if (ACCOUNT.RequireTls)
                {
                    // SASL PLAIN would put the password on the wire in clear.
                    return Fail("Server does not offer TLS and TLS is required.");
                }

                // ---- SASL PLAIN -----------------------------------------------
                SetState(XmppState.Authenticating);
                string authError = await AuthenticatePlainAsync(accountJid);
                if (authError != null)
                {
                    return Fail(authError);
                }

                features = await OpenStreamAsync(domain);
                if (features == null)
                {
                    return Fail("Server did not send stream features after login.");
                }

                // ---- Resource binding -----------------------------------------
                SetState(XmppState.Binding);
                string bindError = await BindAsync();
                if (bindError != null)
                {
                    return Fail(bindError);
                }

                // Legacy session start (RFC 3921). Obsolete, but Openfire and older
                // ejabberd builds still refuse to route stanzas without it.
                if (features.Element(Xmpp.SESSION_NS + "session") != null)
                {
                    await StartSessionAsync();
                }

                SetState(XmppState.Connected);

                // From here stanzas arrive unsolicited, so hand the socket to the
                // read loop before asking for anything.
                StartReadLoop();

                await SendAsync(new XElement(Xmpp.CLIENT_NS + "presence"));
                await RequestRosterAsync();
                return null;
            }
            catch (Exception ex)
            {
                return Fail(Describe(ex));
            }
        }

        /// <summary>
        /// Closes the stream politely and tears the socket down. Safe to call in any
        /// state and more than once.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_readLoopCts != null)
            {
                _readLoopCts.Cancel();
            }

            if (_writer != null && State == XmppState.Connected)
            {
                try
                {
                    // Closing the stream properly stops the server holding the old
                    // session against our resource until it times out.
                    await SendRawAsync("</stream:stream>");
                }
                catch (Exception)
                {
                }
            }

            Teardown();
            SetState(XmppState.Disconnected);
            RaiseClosed("Disconnected.");
        }

        /// <summary>
        /// Creates and connects the socket. Kept as the single place a StreamSocket
        /// is created: a ControlChannelTrigger-based background connection has to
        /// create its socket through the trigger, and this is the one method that
        /// would need to change.
        /// </summary>
        private async Task ConnectSocketAsync()
        {
            _socket = new StreamSocket();
            _socket.Control.KeepAlive = true;
            await _socket.ConnectAsync(new HostName(ACCOUNT.EffectiveHost),
                                       ACCOUNT.Port.ToString());
            AttachReaderWriter();
        }
        #endregion

        #region --Handshake steps--
        /// <summary>
        /// Sends the opening stream header and returns the server's
        /// &lt;stream:features&gt;. Used for the initial stream and after every
        /// restart (TLS, SASL) - each restart is a brand new XML document.
        /// </summary>
        private async Task<XElement> OpenStreamAsync(string domain)
        {
            PARSER.Reset();
            PENDING.Clear();

            // Raw, not XElement: the opening tag is deliberately unclosed, so it is
            // not well-formed XML on its own and cannot be serialised from a tree.
            string header =
                "<?xml version='1.0' encoding='UTF-8'?>" +
                "<stream:stream to='" + Escape(domain) + "'" +
                " xmlns='jabber:client'" +
                " xmlns:stream='http://etherx.jabber.org/streams'" +
                " version='1.0'>";
            await SendRawAsync(header);

            // The header itself comes back first and is reported as a synthetic
            // element; keep reading until the features arrive.
            for (int i = 0; i < 5; i++)
            {
                XElement element = await ReadStanzaAsync(HANDSHAKE_TIMEOUT_MS);
                if (element == null)
                {
                    return null;
                }
                if (element.Name == Xmpp.STREAM_NS + "features")
                {
                    return element;
                }
            }
            return null;
        }

        private async Task<string> StartTlsAsync(string domain)
        {
            await SendAsync(new XElement(Xmpp.TLS_NS + "starttls"));

            XElement response = await ReadStanzaAsync(HANDSHAKE_TIMEOUT_MS);
            if (response == null || response.Name != Xmpp.TLS_NS + "proceed")
            {
                return "Server refused to start TLS.";
            }

            // The reader and writer own the socket's streams; the upgrade replaces
            // those streams, so they must be detached first or it fails outright.
            DetachReaderWriter();

            try
            {
                await _socket.UpgradeToSslAsync(SocketProtectionLevel.Tls12,
                                                new HostName(domain));
            }
            catch (Exception ex)
            {
                // Certificate problems land here. No IgnorableServerCertificateErrors
                // is set anywhere: a client that silently accepts a bad certificate
                // for a password-carrying connection is worse than one that fails.
                return "TLS failed: " + Describe(ex);
            }

            AttachReaderWriter();
            return null;
        }

        private async Task<string> AuthenticatePlainAsync(Jid accountJid)
        {
            // RFC 4616: authzid \0 authcid \0 password, authzid left empty.
            byte[] payload = Encoding.UTF8.GetBytes(
                "\0" + accountJid.Local + "\0" + ACCOUNT.Password);

            XElement auth = new XElement(Xmpp.SASL_NS + "auth",
                new XAttribute("mechanism", "PLAIN"),
                Convert.ToBase64String(payload));
            await SendAsync(auth);

            XElement response = await ReadStanzaAsync(HANDSHAKE_TIMEOUT_MS);
            if (response == null)
            {
                return "No response to login.";
            }
            if (response.Name == Xmpp.SASL_NS + "success")
            {
                return null;
            }
            if (response.Name == Xmpp.SASL_NS + "failure")
            {
                return "Login rejected: " + DescribeSaslFailure(response);
            }
            return "Unexpected response to login: " + response.Name.LocalName;
        }

        private async Task<string> BindAsync()
        {
            string id = NextId();
            XElement bind = new XElement(Xmpp.BIND_NS + "bind");
            if (!string.IsNullOrEmpty(ACCOUNT.Resource))
            {
                bind.Add(new XElement(Xmpp.BIND_NS + "resource", ACCOUNT.Resource));
            }

            await SendAsync(Iq("set", id, bind));

            XElement result = await ReadIqResultAsync(id, HANDSHAKE_TIMEOUT_MS);
            if (result == null)
            {
                return "Server did not confirm resource binding.";
            }
            if (IsIqError(result))
            {
                return "Resource binding rejected by the server.";
            }

            XElement bound = result.Element(Xmpp.BIND_NS + "bind");
            XElement jidElement = bound == null ? null : bound.Element(Xmpp.BIND_NS + "jid");
            if (jidElement != null)
            {
                // The server may hand back a different resource than we asked for;
                // the bound value is the authoritative one.
                BoundJid = Jid.Parse(jidElement.Value);
            }
            if (BoundJid == null)
            {
                BoundJid = Jid.Parse(ACCOUNT.Jid + "/" + ACCOUNT.Resource);
            }
            return null;
        }

        private async Task StartSessionAsync()
        {
            string id = NextId();
            await SendAsync(Iq("set", id, new XElement(Xmpp.SESSION_NS + "session")));
            await ReadIqResultAsync(id, HANDSHAKE_TIMEOUT_MS);
        }

        private async Task RequestRosterAsync()
        {
            await SendAsync(Iq("get", NextId(), new XElement(Xmpp.ROSTER_NS + "query")));
        }
        #endregion

        #region --Sending--
        /// <summary>Sends a chat message and returns the stanza that was sent.</summary>
        public async Task<XmppMessage> SendMessageAsync(string toBareJid, string body)
        {
            if (State != XmppState.Connected)
            {
                return null;
            }
            if (string.IsNullOrEmpty(toBareJid) || string.IsNullOrEmpty(body))
            {
                return null;
            }

            string id = NextId();
            XElement message = new XElement(Xmpp.CLIENT_NS + "message",
                new XAttribute("to", toBareJid),
                new XAttribute("type", "chat"),
                new XAttribute("id", id),
                new XElement(Xmpp.CLIENT_NS + "body", body));

            await SendAsync(message);

            XmppMessage sent = new XmppMessage(toBareJid, body, true);
            sent.Id = id;
            return sent;
        }

        #region --Presence subscriptions--
        /// <summary>
        /// Answers a subscription request: 'subscribed' lets them see our presence,
        /// 'unsubscribed' refuses.
        ///
        /// When accepting we also ask for THEIR presence, unless we already have it.
        /// Subscriptions are one-directional in XMPP, so without this second stanza
        /// the contact would see us online while showing as permanently offline
        /// themselves - which reads as a bug to everyone who has not read RFC 6121.
        /// </summary>
        public async Task AnswerSubscriptionAsync(string bareJid, bool accepted,
            bool subscribeBack)
        {
            if (State != XmppState.Connected || string.IsNullOrEmpty(bareJid))
            {
                return;
            }

            XElement answer = new XElement(Xmpp.CLIENT_NS + "presence",
                new XAttribute("to", bareJid),
                new XAttribute("type", accepted ? "subscribed" : "unsubscribed"));
            await SendAsync(answer);

            if (accepted && subscribeBack)
            {
                await RequestSubscriptionAsync(bareJid);
            }
        }

        /// <summary>Asks a contact to let us see their presence.</summary>
        public async Task RequestSubscriptionAsync(string bareJid)
        {
            if (State != XmppState.Connected || string.IsNullOrEmpty(bareJid))
            {
                return;
            }

            XElement request = new XElement(Xmpp.CLIENT_NS + "presence",
                new XAttribute("to", bareJid),
                new XAttribute("type", "subscribe"));
            await SendAsync(request);
        }

        /// <summary>
        /// Adds a contact: a roster set followed by a subscription request. The
        /// roster item comes first so the contact has a name and shows up in the
        /// list even while the request is still unanswered.
        /// </summary>
        public async Task AddContactAsync(string bareJid, string name)
        {
            if (State != XmppState.Connected || string.IsNullOrEmpty(bareJid))
            {
                return;
            }

            await SetContactNameAsync(bareJid, name);
            await RequestSubscriptionAsync(bareJid);
        }
        #endregion

        /// <summary>
        /// Sends an IQ and waits for the reply with the matching id. Null on timeout.
        /// Only usable once connected - during the handshake the reply is read
        /// directly instead.
        /// </summary>
        public async Task<XElement> RequestAsync(string to, string type, XElement payload,
            int timeoutMs)
        {
            if (State != XmppState.Connected)
            {
                return null;
            }

            string id = NextId();
            TaskCompletionSource<XElement> waiter = new TaskCompletionSource<XElement>();
            lock (PENDING_IQ)
            {
                PENDING_IQ[id] = waiter;
            }

            try
            {
                XElement iq = Iq(type, id, payload);
                if (!string.IsNullOrEmpty(to))
                {
                    iq.Add(new XAttribute("to", to));
                }
                await SendAsync(iq);

                Task completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs));
                if (completed != waiter.Task)
                {
                    return null;                   // timed out
                }
                return waiter.Task.Result;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                lock (PENDING_IQ)
                {
                    PENDING_IQ.Remove(id);
                }
            }
        }

        /// <summary>
        /// Finds the server's XEP-0363 HTTP upload component, or null when the
        /// server has none - in which case sending files is simply not possible and
        /// the caller has to say so.
        /// </summary>
        public async Task<string> FindUploadServiceAsync()
        {
            if (BoundJid == null)
            {
                return null;
            }

            XElement items = await RequestAsync(BoundJid.Domain, "get",
                new XElement(Xmpp.DISCO_ITEMS_NS + "query"), 15000);
            if (items == null || IsIqError(items))
            {
                return null;
            }

            XElement itemsQuery = items.Element(Xmpp.DISCO_ITEMS_NS + "query");
            if (itemsQuery == null)
            {
                return null;
            }

            foreach (XElement item in itemsQuery.Elements(Xmpp.DISCO_ITEMS_NS + "item"))
            {
                string jid = AttributeOf(item, "jid");
                if (string.IsNullOrEmpty(jid))
                {
                    continue;
                }

                XElement info = await RequestAsync(jid, "get",
                    new XElement(Xmpp.DISCO_INFO_NS + "query"), 15000);
                if (info == null || IsIqError(info))
                {
                    continue;
                }

                XElement infoQuery = info.Element(Xmpp.DISCO_INFO_NS + "query");
                if (infoQuery == null)
                {
                    continue;
                }

                foreach (XElement feature in infoQuery.Elements(Xmpp.DISCO_INFO_NS + "feature"))
                {
                    string var = AttributeOf(feature, "var");
                    if (var == Xmpp.HTTP_UPLOAD_NS.NamespaceName ||
                        var == Xmpp.HTTP_UPLOAD_LEGACY_NS.NamespaceName)
                    {
                        return jid;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Asks the upload component for a PUT/GET URL pair for a file of this name
        /// and size. Null when the server refuses (too large, quota, wrong type).
        /// </summary>
        public async Task<UploadSlot> RequestUploadSlotAsync(string service, string fileName,
            ulong size, string contentType)
        {
            XElement request = new XElement(Xmpp.HTTP_UPLOAD_NS + "request",
                new XAttribute("filename", fileName),
                new XAttribute("size", size.ToString()));
            if (!string.IsNullOrEmpty(contentType))
            {
                request.Add(new XAttribute("content-type", contentType));
            }

            XElement response = await RequestAsync(service, "get", request, 30000);
            if (response == null || IsIqError(response))
            {
                return null;
            }

            // Current namespace: <slot><put url='..'><header .../></put><get url='..'/></slot>
            XElement slot = response.Element(Xmpp.HTTP_UPLOAD_NS + "slot");
            if (slot != null)
            {
                XElement put = slot.Element(Xmpp.HTTP_UPLOAD_NS + "put");
                XElement get = slot.Element(Xmpp.HTTP_UPLOAD_NS + "get");
                if (put == null || get == null)
                {
                    return null;
                }

                UploadSlot result = new UploadSlot();
                result.PutUrl = AttributeOf(put, "url");
                result.GetUrl = AttributeOf(get, "url");
                foreach (XElement header in put.Elements(Xmpp.HTTP_UPLOAD_NS + "header"))
                {
                    string name = AttributeOf(header, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        result.Headers[name] = header.Value;
                    }
                }
                return result.IsUsable ? result : null;
            }

            // Legacy namespace put the URLs in element text rather than attributes.
            XElement legacy = response.Element(Xmpp.HTTP_UPLOAD_LEGACY_NS + "slot");
            if (legacy != null)
            {
                XElement put = legacy.Element(Xmpp.HTTP_UPLOAD_LEGACY_NS + "put");
                XElement get = legacy.Element(Xmpp.HTTP_UPLOAD_LEGACY_NS + "get");
                if (put == null || get == null)
                {
                    return null;
                }

                UploadSlot result = new UploadSlot();
                result.PutUrl = put.Value;
                result.GetUrl = get.Value;
                return result.IsUsable ? result : null;
            }
            return null;
        }

        /// <summary>
        /// Sends a shared-file message: the body is the URL, plus an XEP-0066 oob
        /// element so receiving clients know it is a file rather than someone
        /// pasting a link.
        /// </summary>
        public async Task<XmppMessage> SendFileUrlAsync(string toBareJid, string url)
        {
            if (State != XmppState.Connected ||
                string.IsNullOrEmpty(toBareJid) || string.IsNullOrEmpty(url))
            {
                return null;
            }

            string id = NextId();
            XElement message = new XElement(Xmpp.CLIENT_NS + "message",
                new XAttribute("to", toBareJid),
                new XAttribute("type", "chat"),
                new XAttribute("id", id),
                new XElement(Xmpp.CLIENT_NS + "body", url),
                new XElement(Xmpp.OOB_NS + "x",
                    new XElement(Xmpp.OOB_NS + "url", url)));

            await SendAsync(message);

            XmppMessage sent = new XmppMessage(toBareJid, url, true);
            sent.Id = id;
            return sent;
        }

        /// <summary>
        /// Sets (or clears, with an empty name) the roster nickname for a contact.
        ///
        /// This is a server-side roster set, not a local label: the server stores it
        /// and pushes the change to every other client on the account. The push
        /// comes back as an iq carrying a roster query, which the normal handler
        /// picks up, so the local copy updates through the same path as the initial
        /// roster fetch.
        /// </summary>
        public async Task SetContactNameAsync(string bareJid, string name)
        {
            if (State != XmppState.Connected || string.IsNullOrEmpty(bareJid))
            {
                return;
            }

            XElement item = new XElement(Xmpp.ROSTER_NS + "item",
                new XAttribute("jid", bareJid));
            if (!string.IsNullOrEmpty(name))
            {
                item.Add(new XAttribute("name", name));
            }

            XElement query = new XElement(Xmpp.ROSTER_NS + "query", item);
            await SendAsync(Iq("set", NextId(), query));
        }

        public async Task SendPresenceAsync(Presence presence, string status)
        {
            if (State != XmppState.Connected)
            {
                return;
            }

            XElement stanza = new XElement(Xmpp.CLIENT_NS + "presence");
            string show = ShowValue(presence);
            if (presence == Presence.Offline)
            {
                stanza.Add(new XAttribute("type", "unavailable"));
            }
            else if (show != null)
            {
                stanza.Add(new XElement(Xmpp.CLIENT_NS + "show", show));
            }
            if (!string.IsNullOrEmpty(status))
            {
                stanza.Add(new XElement(Xmpp.CLIENT_NS + "status", status));
            }
            await SendAsync(stanza);
        }

        private Task SendAsync(XElement stanza)
        {
            // XElement serialisation handles attribute and text escaping, which is
            // why stanzas are built as trees rather than concatenated strings.
            return SendRawAsync(stanza.ToString(SaveOptions.DisableFormatting));
        }

        private async Task SendRawAsync(string xml)
        {
            DataWriter writer = _writer;
            if (writer == null)
            {
                throw new InvalidOperationException("Not connected.");
            }
            writer.WriteString(xml);
            await writer.StoreAsync();
        }
        #endregion

        #region --Reading--
        /// <summary>
        /// Returns the next stanza, waiting for more data if needed. Null on
        /// timeout or when the peer closed the stream. Used during the handshake
        /// only - once connected the read loop owns the socket.
        /// </summary>
        private async Task<XElement> ReadStanzaAsync(int timeoutMs)
        {
            if (PENDING.Count > 0)
            {
                return PENDING.Dequeue();
            }

            CancellationTokenSource timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                while (PENDING.Count == 0)
                {
                    if (!await ReceiveOnceAsync(timeout.Token))
                    {
                        return null;
                    }
                }
                return PENDING.Dequeue();
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                timeout.Dispose();
            }
        }

        /// <summary>
        /// Reads one chunk off the socket and queues whatever stanzas it completes.
        /// False when the connection is gone.
        /// </summary>
        private async Task<bool> ReceiveOnceAsync(CancellationToken token)
        {
            DataReader reader = _reader;
            if (reader == null)
            {
                return false;
            }

            uint read = await reader.LoadAsync(READ_CHUNK).AsTask(token);
            if (read == 0)
            {
                return false;                      // peer closed the connection
            }

            byte[] bytes = new byte[read];
            reader.ReadBytes(bytes);

            // A stateful decoder, not Encoding.UTF8.GetString: a multi-byte
            // character can be split across two reads, and the decoder carries the
            // partial sequence over instead of producing a replacement character.
            char[] chars = new char[UTF8_DECODER.GetCharCount(bytes, 0, bytes.Length)];
            int charCount = UTF8_DECODER.GetChars(bytes, 0, bytes.Length, chars, 0);

            IList<XElement> stanzas = PARSER.Push(new string(chars, 0, charCount));
            for (int i = 0; i < stanzas.Count; i++)
            {
                PENDING.Enqueue(stanzas[i]);
            }
            return true;
        }

        /// <summary>Waits for the IQ result carrying the given id.</summary>
        private async Task<XElement> ReadIqResultAsync(string id, int timeoutMs)
        {
            // Anything that is not the awaited result is set aside in a LOCAL list,
            // not pushed back onto PENDING: ReadStanzaAsync drains PENDING first, so
            // re-queueing would hand the same stanza straight back and spin without
            // ever reading from the socket again.
            List<XElement> deferred = new List<XElement>();
            XElement match = null;

            for (int i = 0; i < 20; i++)
            {
                XElement element = await ReadStanzaAsync(timeoutMs);
                if (element == null)
                {
                    break;
                }
                if (element.Name.LocalName == "iq" && AttributeOf(element, "id") == id)
                {
                    match = element;
                    break;
                }
                deferred.Add(element);
            }

            // Put them back ahead of anything newer so the read loop still sees them
            // in arrival order once it starts.
            if (deferred.Count > 0)
            {
                List<XElement> newer = new List<XElement>(PENDING);
                PENDING.Clear();
                foreach (XElement element in deferred)
                {
                    PENDING.Enqueue(element);
                }
                foreach (XElement element in newer)
                {
                    PENDING.Enqueue(element);
                }
            }
            return match;
        }

        private void StartReadLoop()
        {
            _readLoopCts = new CancellationTokenSource();
            Task ignored = ReadLoopAsync(_readLoopCts.Token);
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            string reason = "Connection closed.";
            try
            {
                while (!token.IsCancellationRequested)
                {
                    while (PENDING.Count > 0)
                    {
                        HandleStanza(PENDING.Dequeue());
                    }

                    if (!await ReceiveOnceAsync(token))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;                            // DisconnectAsync raises Closed
            }
            catch (Exception ex)
            {
                reason = Describe(ex);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            Teardown();
            SetState(XmppState.Disconnected);
            RaiseClosed(reason);
        }
        #endregion

        #region --Stanza handling--
        private void HandleStanza(XElement stanza)
        {
            if (stanza == null)
            {
                return;
            }

            switch (stanza.Name.LocalName)
            {
                case "message":
                    HandleMessage(stanza);
                    break;

                case "presence":
                    HandlePresence(stanza);
                    break;

                case "iq":
                    HandleIq(stanza);
                    break;

                case "closed":
                    Teardown();
                    SetState(XmppState.Disconnected);
                    RaiseClosed("Server closed the stream.");
                    break;
            }
        }

        private void HandleMessage(XElement stanza)
        {
            string type = AttributeOf(stanza, "type");
            if (type == "error" || type == "groupchat")
            {
                return;                            // out of scope for now
            }

            XElement body = stanza.Element(Xmpp.CLIENT_NS + "body");
            if (body == null || string.IsNullOrEmpty(body.Value))
            {
                // Typing notifications and receipts have no body. Ignored for now.
                return;
            }

            string from = Jid.BareOf(AttributeOf(stanza, "from"));
            if (string.IsNullOrEmpty(from))
            {
                return;
            }

            XmppMessage message = new XmppMessage(from, body.Value, false);
            message.Id = AttributeOf(stanza, "id");
            Raise(MessageReceived, message);
        }

        private void HandlePresence(XElement stanza)
        {
            string from = Jid.BareOf(AttributeOf(stanza, "from"));
            if (string.IsNullOrEmpty(from))
            {
                return;
            }
            // Our own presence echoed back from other clients of this account.
            if (BoundJid != null && string.Equals(from, BoundJid.Bare,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string type = AttributeOf(stanza, "type");
            if (type == "subscribe")
            {
                // Somebody wants to see our presence. Answering is a decision only
                // the user can make, so hand it up and wait.
                Raise(SubscriptionRequested, from);
                return;
            }
            if (type == "subscribed" || type == "unsubscribe" || type == "unsubscribed" ||
                type == "error")
            {
                // The consequences of these arrive as a roster push, which updates
                // the contact through the normal path.
                return;
            }

            RosterItem item = new RosterItem();
            item.Jid = from;
            item.Presence = type == "unavailable"
                ? Presence.Offline
                : ParseShow(ValueOf(stanza, "show"));
            item.Status = ValueOf(stanza, "status");
            Raise(PresenceChanged, item);
        }

        private void HandleIq(XElement stanza)
        {
            // Anyone awaiting this exact id gets it first. This is how requests made
            // AFTER the handshake (disco, upload slots) get their answers - the read
            // loop owns the socket by then, so they cannot read a reply themselves.
            string id = AttributeOf(stanza, "id");
            if (!string.IsNullOrEmpty(id))
            {
                TaskCompletionSource<XElement> waiter = null;
                lock (PENDING_IQ)
                {
                    if (PENDING_IQ.TryGetValue(id, out waiter))
                    {
                        PENDING_IQ.Remove(id);
                    }
                }
                if (waiter != null)
                {
                    waiter.TrySetResult(stanza);
                    return;
                }
            }

            XElement query = stanza.Element(Xmpp.ROSTER_NS + "query");
            if (query == null)
            {
                return;
            }

            List<RosterItem> items = new List<RosterItem>();
            foreach (XElement element in query.Elements(Xmpp.ROSTER_NS + "item"))
            {
                string jid = Jid.BareOf(AttributeOf(element, "jid"));
                if (string.IsNullOrEmpty(jid))
                {
                    continue;
                }
                string subscription = AttributeOf(element, "subscription");
                if (subscription == "remove")
                {
                    continue;
                }

                RosterItem item = new RosterItem();
                item.Jid = jid;
                item.Name = AttributeOf(element, "name");
                item.Subscription = subscription;
                items.Add(item);
            }
            Raise(RosterReceived, (IList<RosterItem>)items);
        }
        #endregion

        #region --Plumbing--
        private void AttachReaderWriter()
        {
            _reader = new DataReader(_socket.InputStream);
            // Partial: hand back whatever has arrived instead of blocking until the
            // full chunk is there. A stanza is almost never exactly READ_CHUNK long,
            // so the default (complete) would stall the session.
            _reader.InputStreamOptions = InputStreamOptions.Partial;
            _writer = new DataWriter(_socket.OutputStream);
        }

        private void DetachReaderWriter()
        {
            if (_reader != null)
            {
                _reader.DetachStream();
                _reader.Dispose();
                _reader = null;
            }
            if (_writer != null)
            {
                _writer.DetachStream();
                _writer.Dispose();
                _writer = null;
            }
        }

        private void Teardown()
        {
            try
            {
                DetachReaderWriter();
            }
            catch (Exception)
            {
            }
            try
            {
                if (_socket != null)
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }
            catch (Exception)
            {
            }
        }

        private string Fail(string error)
        {
            Teardown();
            SetState(XmppState.Failed);
            return error;
        }

        private void SetState(XmppState state)
        {
            if (State == state)
            {
                return;
            }
            State = state;
            Raise(StateChanged, state);
        }

        private void RaiseClosed(string reason)
        {
            Raise(Closed, reason);
        }

        private void Raise<T>(EventHandler<T> handler, T argument)
        {
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(this, argument);
            }
            catch (Exception)
            {
                // A throwing subscriber must not kill the read loop or abort the
                // rest of the invocation list.
            }
        }

        private string NextId()
        {
            _iqCounter++;
            return "jw" + _iqCounter.ToString();
        }

        private static XElement Iq(string type, string id, XElement payload)
        {
            return new XElement(Xmpp.CLIENT_NS + "iq",
                new XAttribute("type", type),
                new XAttribute("id", id),
                payload);
        }

        private static bool IsIqError(XElement iq)
        {
            return AttributeOf(iq, "type") == "error";
        }

        private static string AttributeOf(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            return attribute == null ? "" : attribute.Value;
        }

        private static string ValueOf(XElement parent, string childName)
        {
            XElement child = parent.Element(Xmpp.CLIENT_NS + childName);
            return child == null ? "" : child.Value;
        }

        private static Presence ParseShow(string show)
        {
            switch (show)
            {
                case "chat": return Presence.Chat;
                case "away": return Presence.Away;
                case "xa": return Presence.ExtendedAway;
                case "dnd": return Presence.DoNotDisturb;
                default: return Presence.Online;
            }
        }

        private static string ShowValue(Presence presence)
        {
            switch (presence)
            {
                case Presence.Chat: return "chat";
                case Presence.Away: return "away";
                case Presence.ExtendedAway: return "xa";
                case Presence.DoNotDisturb: return "dnd";
                default: return null;              // "online" is the absence of <show>
            }
        }

        private static string DescribeSaslFailure(XElement failure)
        {
            foreach (XElement child in failure.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "not-authorized": return "wrong user name or password.";
                    case "account-disabled": return "the account is disabled.";
                    case "credentials-expired": return "the credentials have expired.";
                    case "invalid-mechanism": return "the server rejected PLAIN.";
                    default: return child.Name.LocalName.Replace('-', ' ') + ".";
                }
            }
            return "no reason given.";
        }

        private static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return "Unknown error.";
            }
            // WinRT socket errors carry a useful message but an opaque type name, so
            // report the message and let the HRESULT identify the rest.
            string message = ex.Message;
            if (string.IsNullOrEmpty(message))
            {
                message = ex.GetType().Name;
            }
            return message.Trim();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            return value.Replace("&", "&amp;").Replace("<", "&lt;")
                        .Replace(">", "&gt;").Replace("'", "&apos;")
                        .Replace("\"", "&quot;");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_readLoopCts != null)
            {
                _readLoopCts.Cancel();
                _readLoopCts.Dispose();
                _readLoopCts = null;
            }
            Teardown();
        }
        #endregion
    }
}
