using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Headers;

namespace JabberWP.Core
{
    /// <summary>
    /// One PUT/GET pair handed out by an XEP-0363 upload component.
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

    /// <summary>
    /// The HTTP half of XEP-0363: upload the file the server just gave us a slot for.
    /// Separate from XmppConnection because it speaks HTTP, not XMPP - the only thing
    /// tying them together is the slot.
    /// </summary>
    public static class HttpUploadService
    {
        /// <summary>
        /// PUTs the file to the slot. Returns null on success, or an error to show.
        /// </summary>
        public static async Task<string> PutAsync(UploadSlot slot, StorageFile file,
            string contentType)
        {
            if (slot == null || !slot.IsUsable || file == null)
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
                using (HttpClient client = new HttpClient())
                using (IRandomAccessStreamWithContentType stream = await file.OpenReadAsync())
                {
                    HttpStreamContent content = new HttpStreamContent(stream);
                    if (!string.IsNullOrEmpty(contentType))
                    {
                        try
                        {
                            content.Headers.ContentType =
                                new HttpMediaTypeHeaderValue(contentType);
                        }
                        catch (Exception)
                        {
                            // An odd content type is not worth failing the upload.
                        }
                    }

                    foreach (KeyValuePair<string, string> header in slot.Headers)
                    {
                        try
                        {
                            client.DefaultRequestHeaders.Append(header.Key, header.Value);
                        }
                        catch (Exception)
                        {
                        }
                    }

                    HttpResponseMessage response = await client.PutAsync(target, content);
                    if (!response.IsSuccessStatusCode)
                    {
                        return "Upload failed: " + (int)response.StatusCode + " " +
                               response.ReasonPhrase;
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
