using System;

namespace JabberWP.Core
{
    /// <summary>
    /// Everything needed to open one XMPP session. Plain data - no behaviour, so it
    /// can be handed to a background task later without dragging anything with it.
    /// </summary>
    public class XmppAccount
    {
        /// <summary>Bare JID the user logs in as, e.g. "someone@example.com".</summary>
        public string Jid { get; set; }

        public string Password { get; set; }

        /// <summary>
        /// Server to connect to. Normally the JID's domain; set explicitly when the
        /// XMPP host differs from the domain (no SRV lookup is done - see below).
        /// </summary>
        public string Host { get; set; }

        public int Port { get; set; }

        /// <summary>
        /// Resource identifying this client, appended to the JID after binding.
        /// The server may replace it; the bound value is what counts.
        /// </summary>
        public string Resource { get; set; }

        /// <summary>
        /// Whether TLS is mandatory. Left on: SASL PLAIN sends the password in
        /// clear, so refusing to authenticate without TLS is the only safe default.
        /// </summary>
        public bool RequireTls { get; set; }

        public XmppAccount()
        {
            Port = Xmpp.DEFAULT_PORT;
            Resource = Xmpp.DEFAULT_RESOURCE;
            RequireTls = true;
        }

        /// <summary>
        /// Host to actually dial: the explicit one if given, otherwise the domain
        /// from the JID.
        ///
        /// There is no _xmpp-client._tcp SRV lookup here. WinRT exposes no DNS SRV
        /// API, so servers whose XMPP host differs from their domain (a common
        /// setup) need Host filled in by hand on the login screen.
        /// </summary>
        public string EffectiveHost
        {
            get
            {
                if (!string.IsNullOrEmpty(Host))
                {
                    return Host;
                }
                // Fully qualified on both sides: this class has a string property
                // called Jid, which shadows the type name inside the class body.
                Core.Jid parsed = Core.Jid.Parse(Jid);
                return parsed == null ? "" : parsed.Domain;
            }
        }

        public bool IsUsable
        {
            get
            {
                // Fully qualified on both sides: this class has a string property
                // called Jid, which shadows the type name inside the class body.
                Core.Jid parsed = Core.Jid.Parse(Jid);
                return parsed != null &&
                       !string.IsNullOrEmpty(parsed.Local) &&
                       !string.IsNullOrEmpty(Password) &&
                       !string.IsNullOrEmpty(EffectiveHost) &&
                       Port > 0;
            }
        }
    }
}
