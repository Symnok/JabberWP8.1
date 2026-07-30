using System.Collections.Generic;

namespace JabberWP.Core
{
    /// <summary>
    /// One PUT/GET pair handed out by an XEP-0363 upload component.
    ///
    /// In its own file rather than beside the HTTP code that consumes it: this is what
    /// the XMPP slot response parses into, so XmppConnection needs it even where the
    /// uploading half is not compiled in - as in the background agent, which never
    /// uploads anything.
    /// </summary>
    public class UploadSlot
    {
        /// <summary>Where the bytes go (HTTP PUT).</summary>
        public string PutUrl { get; set; }

        /// <summary>The URL to send in the message - what the recipient fetches.</summary>
        public string GetUrl { get; set; }

        /// <summary>
        /// Headers the component requires on the PUT (often Authorization).
        /// Only Authorization, Cookie and Expires are permitted by the XEP.
        /// </summary>
        public Dictionary<string, string> Headers { get; private set; }

        public UploadSlot()
        {
            Headers = new Dictionary<string, string>();
        }

        public bool IsUsable
        {
            get { return !string.IsNullOrEmpty(PutUrl) && !string.IsNullOrEmpty(GetUrl); }
        }
    }
}
