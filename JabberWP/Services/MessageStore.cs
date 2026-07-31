using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using JabberWP.Core;

namespace JabberWP.Services
{
    /// <summary>
    /// Message history on disk: one XML file per conversation, in isolated storage.
    ///
    /// Isolated storage rather than a database because WP8.1 Silverlight has no
    /// SQLite without the retired "SQLite for Windows Phone 8.1" extension SDK,
    /// and because the same store has to be reachable from the background agent,
    /// which runs in its OWN PROCESS. AccountStore already shares state with the
    /// agent exactly this way.
    ///
    /// Every write is guarded by a named Mutex: the app and the agent can both be
    /// appending to the same file at the same moment, and isolated storage gives
    /// no atomicity of its own.
    /// </summary>
    public static class MessageStore
    {
        private const string FOLDER = "Messages";

        /// <summary>Cross-PROCESS lock - the agent is not in our process.</summary>
        private const string MUTEX_NAME = "JabberWP.MessageStore";
        private const int MUTEX_TIMEOUT_MS = 5000;

        /// <summary>Retention: a message is kept only if it satisfies BOTH limits.</summary>
        private const int MAX_PER_CHAT = 500;
        private static readonly TimeSpan MAX_AGE = TimeSpan.FromDays(183);   // ~6 months

        private const string ROOT = "messages";
        private const string ITEM = "m";

        #region --Public API--
        /// <summary>
        /// Stored history for one conversation, oldest first. Empty when there is
        /// nothing stored yet, and empty rather than throwing if the file is
        /// unreadable - history is never worth taking the app down for.
        /// </summary>
        public static List<XmppMessage> Load(string bareJid)
        {
            List<XmppMessage> result = new List<XmppMessage>();
            if (string.IsNullOrEmpty(bareJid))
            {
                return result;
            }

            try
            {
                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    string path = PathFor(bareJid);
                    if (!store.FileExists(path))
                    {
                        return result;
                    }

                    XDocument document;
                    using (IsolatedStorageFileStream stream =
                        store.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        document = XDocument.Load(stream);
                    }
                    result = Parse(document, bareJid);
                }
            }
            catch (Exception)
            {
                // Unreadable or half-written file: start the conversation empty
                // rather than fail to open it at all. The next append rewrites it.
                return new List<XmppMessage>();
            }

            // Sorted here, so the order appends happened in never matters - which is
            // what lets writes run off the UI thread without coordinating them.
            result.Sort(delegate(XmppMessage a, XmppMessage b)
            {
                return a.Timestamp.CompareTo(b.Timestamp);
            });
            return result;
        }

        /// <summary>Adds one message to a conversation's history.</summary>
        public static void Append(string bareJid, XmppMessage message)
        {
            if (message == null)
            {
                return;
            }
            AppendRange(bareJid, new List<XmppMessage> { message });
        }

        /// <summary>
        /// Adds several messages in ONE locked read-modify-write. The agent uses
        /// this: a whole drain becomes a single pass over the file instead of one
        /// per message.
        /// </summary>
        public static void AppendRange(string bareJid, IList<XmppMessage> messages)
        {
            if (string.IsNullOrEmpty(bareJid) || messages == null || messages.Count == 0)
            {
                return;
            }

            Mutex mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, MUTEX_NAME);
                try
                {
                    held = mutex.WaitOne(MUTEX_TIMEOUT_MS);
                }
                catch (AbandonedMutexException)
                {
                    // The other process died holding it. The file is still readable
                    // and we are about to rewrite it, so carry on.
                    held = true;
                }
                if (!held)
                {
                    return;                       // busy: drop rather than corrupt
                }

                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.DirectoryExists(FOLDER))
                    {
                        store.CreateDirectory(FOLDER);
                    }

                    string path = PathFor(bareJid);
                    List<XmppMessage> all = new List<XmppMessage>();

                    if (store.FileExists(path))
                    {
                        try
                        {
                            using (IsolatedStorageFileStream stream =
                                store.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                all = Parse(XDocument.Load(stream), bareJid);
                            }
                        }
                        catch (Exception)
                        {
                            all = new List<XmppMessage>();   // corrupt: start over
                        }
                    }

                    // Skip anything already stored. The agent and the app can both
                    // see the same stanza when both happen to be connected.
                    HashSet<string> known = new HashSet<string>();
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(all[i].Id))
                        {
                            known.Add(all[i].Id);
                        }
                    }
                    for (int i = 0; i < messages.Count; i++)
                    {
                        XmppMessage m = messages[i];
                        if (m == null || string.IsNullOrEmpty(m.Body))
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(m.Id) && !known.Add(m.Id))
                        {
                            continue;
                        }
                        all.Add(m);
                    }

                    Write(store, path, Prune(all));
                }
            }
            catch (Exception)
            {
                // Never let history writing break sending or receiving.
            }
            finally
            {
                if (mutex != null)
                {
                    if (held)
                    {
                        try { mutex.ReleaseMutex(); } catch (Exception) { }
                    }
                    // Close() rather than Dispose(): WaitHandle implements
                    // IDisposable explicitly on Silverlight, so Close is the member
                    // that is reliably callable here.
                    try { mutex.Close(); } catch (Exception) { }
                }
            }
        }
        #endregion

        #region --Retention--
        /// <summary>
        /// Applies both limits: newest MAX_PER_CHAT, and nothing older than
        /// MAX_AGE. A quiet conversation is trimmed by age, a busy one by count -
        /// whichever bites first.
        /// </summary>
        private static List<XmppMessage> Prune(List<XmppMessage> messages)
        {
            messages.Sort(delegate(XmppMessage a, XmppMessage b)
            {
                return a.Timestamp.CompareTo(b.Timestamp);
            });

            DateTime cutoff = DateTime.Now - MAX_AGE;
            List<XmppMessage> kept = new List<XmppMessage>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Timestamp >= cutoff)
                {
                    kept.Add(messages[i]);
                }
            }

            if (kept.Count > MAX_PER_CHAT)
            {
                kept = kept.GetRange(kept.Count - MAX_PER_CHAT, MAX_PER_CHAT);
            }
            return kept;
        }
        #endregion

        #region --Serialization--
        private static List<XmppMessage> Parse(XDocument document, string bareJid)
        {
            List<XmppMessage> result = new List<XmppMessage>();
            if (document == null || document.Root == null)
            {
                return result;
            }

            foreach (XElement element in document.Root.Elements(ITEM))
            {
                XmppMessage message = new XmppMessage();
                message.ContactJid = bareJid;
                message.Body = element.Value;
                message.Id = AttributeOf(element, "id");
                message.IsOutgoing = AttributeOf(element, "out") == "1";

                DateTime stamp;
                if (DateTime.TryParse(AttributeOf(element, "ts"), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out stamp))
                {
                    // Stored as UTC so the history does not shift when the phone
                    // changes time zone; shown in local time.
                    message.Timestamp = stamp.ToLocalTime();
                }
                result.Add(message);
            }
            return result;
        }

        private static void Write(IsolatedStorageFile store, string path, List<XmppMessage> messages)
        {
            XElement root = new XElement(ROOT);
            for (int i = 0; i < messages.Count; i++)
            {
                XmppMessage m = messages[i];
                XElement element = new XElement(ITEM, m.Body ?? "");
                if (!string.IsNullOrEmpty(m.Id))
                {
                    element.Add(new XAttribute("id", m.Id));
                }
                element.Add(new XAttribute("ts",
                    m.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
                if (m.IsOutgoing)
                {
                    element.Add(new XAttribute("out", "1"));
                }
                root.Add(element);
            }

            // Written to a temporary file and moved into place: the agent can be
            // killed mid-write, and a truncated file would take the history with it.
            string temporary = path + ".tmp";
            if (store.FileExists(temporary))
            {
                store.DeleteFile(temporary);
            }
            using (IsolatedStorageFileStream stream =
                store.OpenFile(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
            {
                // Saved through a TextWriter rather than the Stream overload: that
                // is the overload Silverlight's XDocument reliably offers.
                new XDocument(root).Save(writer);
            }
            if (store.FileExists(path))
            {
                store.DeleteFile(path);
            }
            store.MoveFile(temporary, path);
        }

        private static string AttributeOf(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            return attribute == null ? null : attribute.Value;
        }
        #endregion

        #region --File naming--
        private static string PathFor(string bareJid)
        {
            return FOLDER + "\\" + FileNameFor(bareJid);
        }

        /// <summary>
        /// A JID may contain characters that are not legal in a file name, and
        /// sanitising alone would map two different JIDs onto one file. The hash
        /// suffix keeps them apart.
        /// </summary>
        private static string FileNameFor(string bareJid)
        {
            string lower = bareJid.ToLowerInvariant();
            StringBuilder safe = new StringBuilder(lower.Length);
            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                          c == '.' || c == '-' || c == '_';
                safe.Append(ok ? c : '_');
            }
            if (safe.Length > 40)
            {
                safe.Length = 40;
            }
            return safe.ToString() + "-" + StableHash(lower) + ".xml";
        }

        /// <summary>
        /// FNV-1a. String.GetHashCode is deliberately NOT used: it is not
        /// guaranteed to agree between processes, and the agent has to arrive at
        /// the same file name as the app.
        /// </summary>
        private static string StableHash(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            uint hash = 2166136261;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash = (hash ^ bytes[i]) * 16777619;
            }
            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
        #endregion
    }
}
