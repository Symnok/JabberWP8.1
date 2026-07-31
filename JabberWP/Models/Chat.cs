using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using JabberWP.Core;

namespace JabberWP.Models
{
    /// <summary>
    /// One conversation, bindable. Wraps a roster entry plus the messages exchanged
    /// with it during this run.
    ///
    /// Messages are in-memory only right now - closing the app loses the history.
    /// Persisting them is a separate piece of work, deliberately not started until
    /// the wire protocol is proven.
    /// </summary>
    public class Chat : INotifyPropertyChanged
    {
        private string _name;
        private Presence _presence;
        private string _status;
        private string _lastMessage;
        private int _unread;

        public string Jid { get; private set; }
        public ObservableCollection<XmppMessage> Messages { get; private set; }

        public Chat(RosterItem item)
        {
            Jid = item.Jid;
            Messages = new ObservableCollection<XmppMessage>();
            _name = item.Name ?? "";
            _presence = item.Presence;
            _status = item.Status ?? "";
            _lastMessage = "";
        }

        /// <summary>
        /// Roster nickname. Empty unless the contact has been renamed - this is the
        /// server-side name, so a rename follows the account to other clients.
        /// </summary>
        public string Name
        {
            get { return _name; }
            set
            {
                string incoming = value ?? "";
                if (_name == incoming) return;
                _name = incoming;
                Notify("Name");
                Notify("HasName");
                Notify("Title");
                Notify("Subtitle");
            }
        }

        public bool HasName
        {
            get { return !string.IsNullOrEmpty(_name); }
        }

        /// <summary>Primary line: the nickname if renamed, otherwise the full JID.</summary>
        public string Title
        {
            get { return HasName ? _name : Jid; }
        }

        /// <summary>
        /// Second line: the JID when a nickname is showing above it (so the address
        /// is never hidden), otherwise the presence.
        /// </summary>
        public string Subtitle
        {
            get { return HasName ? Jid : PresenceText; }
        }

        public Presence Presence
        {
            get { return _presence; }
            set
            {
                if (_presence == value) return;
                _presence = value;
                Notify("Presence");
                Notify("PresenceText");
                Notify("Subtitle");
            }
        }

        public string Status
        {
            get { return _status; }
            set
            {
                string incoming = value ?? "";
                if (_status == incoming) return;
                _status = incoming;
                Notify("Status");
                Notify("PresenceText");
                Notify("Subtitle");
            }
        }

        /// <summary>Preview line for the contact list.</summary>
        public string LastMessage
        {
            get { return _lastMessage; }
            private set
            {
                if (_lastMessage == value) return;
                _lastMessage = value ?? "";
                Notify("LastMessage");
            }
        }

        public int Unread
        {
            get { return _unread; }
            set
            {
                if (_unread == value) return;
                _unread = value;
                Notify("Unread");
                Notify("UnreadText");
                Notify("HasUnread");
            }
        }

        public bool HasUnread
        {
            get { return _unread > 0; }
        }

        public string UnreadText
        {
            get { return _unread > 0 ? _unread.ToString() : ""; }
        }

        /// <summary>Status text if the contact set one, otherwise the availability.</summary>
        public string PresenceText
        {
            get
            {
                if (!string.IsNullOrEmpty(_status))
                {
                    return _status;
                }
                switch (_presence)
                {
                    case Presence.Chat: return "free to chat";
                    case Presence.Away: return "away";
                    case Presence.ExtendedAway: return "away for a while";
                    case Presence.DoNotDisturb: return "busy";
                    case Presence.Offline: return "offline";
                    default: return "online";
                }
            }
        }

        public void Add(XmppMessage message)
        {
            if (message == null)
            {
                return;
            }
            Messages.Add(message);
            LastMessage = message.Body;
        }

        /// <summary>
        /// Stored history has been read for this conversation. Loading is lazy -
        /// doing it for every chat as the roster arrives would read every file on
        /// screen one, which is what we are trying to avoid on a phone.
        /// </summary>
        public bool HistoryLoaded { get; set; }

        /// <summary>
        /// Puts stored messages in FRONT of whatever arrived during this run.
        ///
        /// Not routed through Add: that is the path which writes to the store, and
        /// re-saving what was just read would be pointless work. Messages already
        /// present are skipped by id, because anything received this session has
        /// been written to the file as well.
        /// </summary>
        public void InsertHistory(IList<XmppMessage> history)
        {
            if (history == null || history.Count == 0)
            {
                return;
            }

            HashSet<string> present = new HashSet<string>();
            foreach (XmppMessage existing in Messages)
            {
                if (!string.IsNullOrEmpty(existing.Id))
                {
                    present.Add(existing.Id);
                }
            }

            int at = 0;
            for (int i = 0; i < history.Count; i++)
            {
                XmppMessage message = history[i];
                if (message == null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(message.Id) && present.Contains(message.Id))
                {
                    continue;
                }
                Messages.Insert(at, message);
                at++;
            }

            if (string.IsNullOrEmpty(_lastMessage) && Messages.Count > 0)
            {
                LastMessage = Messages[Messages.Count - 1].Body;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
