using System;

namespace JabberWP.Core
{
    /// <summary>One chat message, in or out.</summary>
    public class XmppMessage
    {
        /// <summary>Bare JID of the other party (never our own).</summary>
        public string ContactJid { get; set; }

        public string Body { get; set; }

        /// <summary>True when we sent it.</summary>
        public bool IsOutgoing { get; set; }

        public DateTime Timestamp { get; set; }

        /// <summary>Stanza id, used to match delivery receipts later.</summary>
        public string Id { get; set; }

        /// <summary>
        /// Time only. Bound by the chat bubble - the raw DateTime renders as a full
        /// date and time, which is unreadable at the size the stamp is drawn.
        /// </summary>
        public string TimeText
        {
            get { return Timestamp.ToString("HH:mm"); }
        }

        #region --Links and inline images--
        private static readonly string[] IMAGE_EXTENSIONS =
            { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

        /// <summary>
        /// First http(s) link in the body, or null. Files shared over XMPP arrive as
        /// a plain URL in the message text (XEP-0363 upload, or any client pasting a
        /// link), so this is what makes a message actionable.
        /// </summary>
        public string FirstUrl
        {
            get
            {
                if (string.IsNullOrEmpty(Body))
                {
                    return null;
                }

                string[] tokens = Body.Split(new char[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i].Trim();
                    if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        // Trailing punctuation is almost never part of the link.
                        return token.TrimEnd('.', ',', ')', ']', '>', '"', '\'');
                    }
                }
                return null;
            }
        }

        public bool HasUrl
        {
            get { return FirstUrl != null; }
        }

        /// <summary>
        /// True when the whole message is one image link, which is exactly the shape
        /// of a shared picture. Anything else stays text, so a sentence that happens
        /// to mention a .png is not turned into a picture.
        /// </summary>
        public bool IsImage
        {
            get
            {
                string url = FirstUrl;
                if (url == null || Body == null || Body.Trim() != url)
                {
                    return false;
                }

                string path = url;
                int query = path.IndexOf('?');
                if (query >= 0)
                {
                    path = path.Substring(0, query);
                }

                for (int i = 0; i < IMAGE_EXTENSIONS.Length; i++)
                {
                    if (path.EndsWith(IMAGE_EXTENSIONS[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>Inverse of IsImage, so the template needs no negating converter.</summary>
        public bool IsText
        {
            get { return !IsImage; }
        }

        /// <summary>
        /// Bound straight to Image.Source: WinRT downloads and caches the bitmap
        /// itself, so no separate prefetch step is needed.
        /// </summary>
        public Uri ImageUri
        {
            get
            {
                if (!IsImage)
                {
                    return null;
                }
                Uri uri;
                if (Uri.TryCreate(FirstUrl, UriKind.Absolute, out uri))
                {
                    return uri;
                }
                return null;
            }
        }
        #endregion

        public XmppMessage()
        {
            Timestamp = DateTime.Now;
        }

        public XmppMessage(string contactJid, string body, bool isOutgoing)
        {
            ContactJid = contactJid;
            Body = body;
            IsOutgoing = isOutgoing;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>One entry from the server-side roster (RFC 6121 contact list).</summary>
    public class RosterItem
    {
        public string Jid { get; set; }

        /// <summary>Server-side nickname, may be empty.</summary>
        public string Name { get; set; }

        /// <summary>none / to / from / both / remove.</summary>
        public string Subscription { get; set; }

        public Presence Presence { get; set; }

        /// <summary>Free-text status ("In a meeting"), may be empty.</summary>
        public string Status { get; set; }

        public RosterItem()
        {
            Presence = Presence.Offline;
        }

        /// <summary>Name if the server gave one, otherwise the JID's local part.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name))
                {
                    return Name;
                }
                // Qualified: the Jid property shadows the Jid type in this class.
                Core.Jid parsed = Core.Jid.Parse(Jid);
                if (parsed != null && !string.IsNullOrEmpty(parsed.Local))
                {
                    return parsed.Local;
                }
                return Jid ?? "";
            }
        }
    }
}
