using System.Xml.Linq;

namespace JabberWP.Core
{
    /// <summary>
    /// XML namespaces and small shared constants from the XMPP RFCs and XEPs.
    /// </summary>
    public static class Xmpp
    {
        public static readonly XNamespace CLIENT_NS = "jabber:client";
        public static readonly XNamespace STREAM_NS = "http://etherx.jabber.org/streams";
        public static readonly XNamespace TLS_NS = "urn:ietf:params:xml:ns:xmpp-tls";
        public static readonly XNamespace SASL_NS = "urn:ietf:params:xml:ns:xmpp-sasl";
        public static readonly XNamespace BIND_NS = "urn:ietf:params:xml:ns:xmpp-bind";
        public static readonly XNamespace SESSION_NS = "urn:ietf:params:xml:ns:xmpp-session";
        public static readonly XNamespace ROSTER_NS = "jabber:iq:roster";
        public static readonly XNamespace DISCO_ITEMS_NS = "http://jabber.org/protocol/disco#items";
        public static readonly XNamespace DISCO_INFO_NS = "http://jabber.org/protocol/disco#info";

        /// <summary>XEP-0363 HTTP File Upload. v0 is current; the other is legacy.</summary>
        public static readonly XNamespace HTTP_UPLOAD_NS = "urn:xmpp:http:upload:0";
        public static readonly XNamespace HTTP_UPLOAD_LEGACY_NS = "urn:xmpp:http:upload";

        /// <summary>XEP-0066: marks a message body that is really a shared file.</summary>
        public static readonly XNamespace OOB_NS = "jabber:x:oob";

        public const int DEFAULT_PORT = 5222;
        public const string DEFAULT_RESOURCE = "JabberWP";
    }

    public enum XmppState
    {
        Disconnected,
        Connecting,
        Securing,
        Authenticating,
        Binding,
        Connected,
        Failed
    }

    /// <summary>
    /// RFC 6121 presence availability. Ordered so that "more available" sorts first.
    /// </summary>
    public enum Presence
    {
        Chat,
        Online,
        Away,
        ExtendedAway,
        DoNotDisturb,
        Offline
    }
}
