using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Web.Http;
using Windows.Web.Http.Headers;

namespace JabberWP.Core
{
    /// <summary>
    /// The HTTP half of XEP-0363: upload the file the server just gave us a slot for.
    /// Separate from XmppConnection because it speaks HTTP, not XMPP - the only thing
    /// tying them together is the slot.
    /// </summary>
    public static class HttpUploadService
    {
        /// <summary>
        /// PUTs the content to the slot. Returns null on success, or an error to show.
        ///
        /// Takes a Stream rather than a file: on this platform a picture comes from
        /// PhotoChooserTask, which hands back an open stream and no path worth using.
        /// </summary>
        public static async Task<string> PutAsync(UploadSlot slot, Stream content,
            string contentType)
        {
            if (slot == null || !slot.IsUsable || content == null)
            {
                return "No upload slot.";
            }

            Uri target;
            if (!Uri.TryCreate(slot.PutUrl, UriKind.Absolute, out target))
            {
                return "The server returned an unusable upload address.";
            }

            try
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    // Rewind: the chooser may already have read from it (for the
                    // thumbnail), and a partially consumed stream uploads a truncated
                    // file that the recipient cannot open.
                    if (content.CanSeek)
                    {
                        content.Position = 0;
                    }

                    foreach (KeyValuePair<string, string> header in slot.Headers)
                    {
                        try
                        {
                            httpClient.DefaultRequestHeaders.Append(header.Key, header.Value);
                        }
                        catch (Exception)
                        {
                            // An unusable header is not worth failing the upload over.
                        }
                    }

                    HttpStreamContent httpContent =
                        new HttpStreamContent(content.AsInputStream());
                    if (!string.IsNullOrEmpty(contentType))
                    {
                        try
                        {
                            httpContent.Headers.ContentType =
                                new HttpMediaTypeHeaderValue(contentType);
                        }
                        catch (Exception)
                        {
                        }
                    }

                    HttpResponseMessage response = await httpClient.PutAsync(target, httpContent);
                    if (!response.IsSuccessStatusCode)
                    {
                        return "Upload rejected by the server: " +
                               (int)response.StatusCode + ' ' + response.ReasonPhrase;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return "Upload failed: " + ex.Message;
            }
        }
    }
}
