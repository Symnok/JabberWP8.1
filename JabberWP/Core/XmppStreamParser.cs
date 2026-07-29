using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace JabberWP.Core
{
    /// <summary>
    /// Splits the never-ending XMPP XML stream into individual stanzas.
    ///
    /// An XMPP session is ONE XML document that only closes when the session ends,
    /// so XDocument.Load() cannot be used - it would block forever waiting for the
    /// closing tag. Instead we buffer incoming text and hand out complete top-level
    /// elements as they arrive.
    ///
    /// Not a general XML parser. It only needs to find element boundaries, which
    /// means it has to respect exactly three things: quoted attribute values (a '&gt;'
    /// inside one must not end the tag), self-closing tags, and the opening
    /// &lt;stream:stream&gt; header that never closes.
    /// </summary>
    public class XmppStreamParser
    {
        // Namespaces every stanza is parsed against. A stanza pulled out of the
        // stream is a fragment: <stream:features> or <iq> with no declarations of
        // its own, so parsing it standalone fails ("undeclared prefix" / wrong
        // namespace). Wrapping it in a root that declares them fixes both.
        private const string WRAPPER_OPEN =
            "<wrap xmlns='jabber:client'" +
            " xmlns:stream='http://etherx.jabber.org/streams'" +
            " xmlns:xml='http://www.w3.org/XML/1998/namespace'>";
        private const string WRAPPER_CLOSE = "</wrap>";

        private readonly StringBuilder BUFFER = new StringBuilder();

        /// <summary>
        /// Feeds newly received text into the parser and returns every complete
        /// stanza it now contains. Incomplete trailing data stays buffered.
        /// </summary>
        public IList<XElement> Push(string text)
        {
            List<XElement> stanzas = new List<XElement>();
            if (string.IsNullOrEmpty(text))
            {
                return stanzas;
            }

            BUFFER.Append(text);

            while (true)
            {
                string raw = TakeNextElement();
                if (raw == null)
                {
                    break;
                }

                XElement parsed = TryParse(raw);
                if (parsed != null)
                {
                    stanzas.Add(parsed);
                }
            }
            return stanzas;
        }

        /// <summary>
        /// Drops everything buffered. Called on every stream restart (after TLS and
        /// after SASL) - the old stream is gone and any half-read stanza with it.
        /// </summary>
        public void Reset()
        {
            BUFFER.Clear();
        }

        /// <summary>
        /// Pulls one complete top-level element off the front of the buffer, or
        /// returns null if there is not a complete one yet.
        /// </summary>
        private string TakeNextElement()
        {
            string buffer = BUFFER.ToString();
            int i = 0;
            int depth = 0;
            int elementStart = -1;

            while (i < buffer.Length)
            {
                char c = buffer[i];

                if (c != '<')
                {
                    // Character data between elements. Whitespace between stanzas is
                    // legal (and used as a keep-alive), so skip it silently.
                    i++;
                    continue;
                }

                // Comments, CDATA and the XML declaration: skip wholesale. None of
                // them nest, so find the matching terminator.
                if (StartsWith(buffer, i, "<!--"))
                {
                    int end = buffer.IndexOf("-->", i, StringComparison.Ordinal);
                    if (end < 0) return null;                 // incomplete
                    i = end + 3;
                    continue;
                }
                if (StartsWith(buffer, i, "<![CDATA["))
                {
                    int end = buffer.IndexOf("]]>", i, StringComparison.Ordinal);
                    if (end < 0) return null;
                    i = end + 3;
                    continue;
                }
                if (StartsWith(buffer, i, "<?"))
                {
                    int end = buffer.IndexOf("?>", i, StringComparison.Ordinal);
                    if (end < 0) return null;
                    i = end + 2;
                    if (depth == 0) TrimTo(i, ref buffer, ref i, ref elementStart);
                    continue;
                }

                bool isClosingTag = StartsWith(buffer, i, "</");
                int tagStart = i;
                int tagEnd = FindTagEnd(buffer, i);
                if (tagEnd < 0)
                {
                    return null;                              // tag not fully received
                }

                bool isSelfClosing = buffer[tagEnd - 1] == '/';

                if (depth == 0 && !isClosingTag)
                {
                    elementStart = tagStart;

                    // <stream:stream ...> opens the session document and is never
                    // closed until the session ends. Treat it as a standalone unit
                    // instead of letting it push the depth to 1 forever.
                    if (IsStreamHeader(buffer, tagStart))
                    {
                        string header = buffer.Substring(tagStart, tagEnd - tagStart + 1);
                        Consume(tagEnd + 1);
                        return header;
                    }
                }

                if (isClosingTag)
                {
                    depth--;
                }
                else if (!isSelfClosing)
                {
                    depth++;
                }

                i = tagEnd + 1;

                bool completed = depth == 0 && elementStart >= 0 &&
                                 (isClosingTag || isSelfClosing);
                if (completed)
                {
                    string element = buffer.Substring(elementStart, i - elementStart);
                    Consume(i);
                    return element;
                }
            }

            return null;
        }

        /// <summary>
        /// Index of the '&gt;' that ends the tag starting at <paramref name="start"/>,
        /// ignoring any inside quoted attribute values. -1 if not yet received.
        /// </summary>
        private static int FindTagEnd(string buffer, int start)
        {
            char quote = '\0';
            for (int i = start; i < buffer.Length; i++)
            {
                char c = buffer[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsStreamHeader(string buffer, int tagStart)
        {
            return StartsWith(buffer, tagStart, "<stream:stream") ||
                   StartsWith(buffer, tagStart, "<stream ");
        }

        private static bool StartsWith(string buffer, int index, string value)
        {
            if (index + value.Length > buffer.Length)
            {
                return false;
            }
            return string.CompareOrdinal(buffer, index, value, 0, value.Length) == 0;
        }

        private void Consume(int count)
        {
            BUFFER.Remove(0, count);
        }

        private void TrimTo(int upTo, ref string buffer, ref int i, ref int elementStart)
        {
            Consume(upTo);
            buffer = BUFFER.ToString();
            i = 0;
            elementStart = -1;
        }

        /// <summary>
        /// Parses one raw stanza, in a namespace context that makes stream: and
        /// jabber:client resolve. Returns null for anything unparseable rather than
        /// throwing - a malformed stanza must not kill the read loop.
        /// </summary>
        private static XElement TryParse(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            // The stream header is not a stanza; report it as an empty element so
            // callers can see the stream (re)started without special-casing text.
            if (raw.StartsWith("<stream:stream", StringComparison.Ordinal) ||
                raw.StartsWith("<stream ", StringComparison.Ordinal))
            {
                return new XElement(Xmpp.STREAM_NS + "stream");
            }

            // </stream:stream> - the peer closed the session.
            if (raw.StartsWith("</", StringComparison.Ordinal))
            {
                return new XElement(Xmpp.STREAM_NS + "closed");
            }

            try
            {
                XElement wrapper = XElement.Parse(WRAPPER_OPEN + raw + WRAPPER_CLOSE);
                foreach (XElement child in wrapper.Elements())
                {
                    return child;
                }
            }
            catch (Exception)
            {
                // Unparseable fragment. Dropping it is the only safe option; the
                // stream itself is still positioned correctly because the split was
                // done on tag boundaries, not by the XML parser.
            }
            return null;
        }
    }
}
