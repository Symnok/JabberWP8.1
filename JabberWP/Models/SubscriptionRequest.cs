using System;

namespace JabberWP.Models
{
    /// <summary>
    /// Somebody asking to see our presence, waiting for an answer.
    ///
    /// Plain and immutable: the list it lives in is rebuilt from the server on every
    /// connect, so there is no state here worth notifying about.
    /// </summary>
    public class SubscriptionRequest
    {
        public string Jid { get; private set; }

        public DateTime Received { get; private set; }

        public SubscriptionRequest(string jid)
        {
            Jid = jid;
            Received = DateTime.Now;
        }

        /// <summary>Line shown above the accept/decline buttons.</summary>
        public string Text
        {
            get { return Jid + " would like to add you."; }
        }
    }
}
