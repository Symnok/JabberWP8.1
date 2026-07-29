using System;

namespace JabberWP.Core
{
    /// <summary>
    /// A Jabber ID: [local@]domain[/resource].
    ///
    /// Comparisons are on the BARE jid (local@domain) because that is what a
    /// conversation is addressed to - the resource identifies one connected client
    /// of that account and changes constantly.
    /// </summary>
    public class Jid
    {
        public string Local { get; private set; }
        public string Domain { get; private set; }
        public string Resource { get; private set; }

        public string Bare
        {
            get
            {
                return string.IsNullOrEmpty(Local) ? Domain : Local + "@" + Domain;
            }
        }

        public string Full
        {
            get
            {
                return string.IsNullOrEmpty(Resource) ? Bare : Bare + "/" + Resource;
            }
        }

        public Jid(string local, string domain, string resource)
        {
            Local = local ?? "";
            Domain = domain ?? "";
            Resource = resource ?? "";
        }

        /// <summary>
        /// Parses a JID. Returns null for anything without a domain part, which is
        /// the only requirement the format actually has.
        /// </summary>
        public static Jid Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            string rest = value.Trim();
            string local = "";
            string resource = "";

            // Resource is everything after the FIRST '/', and may itself contain '/'.
            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                resource = rest.Substring(slash + 1);
                rest = rest.Substring(0, slash);
            }

            // Local part is everything before the LAST '@' - '@' is legal inside a
            // local part when escaped, and the domain never contains one.
            int at = rest.LastIndexOf('@');
            if (at >= 0)
            {
                local = rest.Substring(0, at);
                rest = rest.Substring(at + 1);
            }

            if (string.IsNullOrEmpty(rest))
            {
                return null;
            }
            return new Jid(local, rest, resource);
        }

        /// <summary>Bare JID of <paramref name="value"/>, or "" if unparseable.</summary>
        public static string BareOf(string value)
        {
            Jid jid = Parse(value);
            return jid == null ? "" : jid.Bare;
        }

        public bool BareEquals(Jid other)
        {
            if (other == null)
            {
                return false;
            }
            return string.Equals(Bare, other.Bare, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            return Full;
        }
    }
}
