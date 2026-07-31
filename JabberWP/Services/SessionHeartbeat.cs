using System;
using System.Globalization;
using System.IO;
using System.IO.IsolatedStorage;

namespace JabberWP.Services
{
    /// <summary>
    /// Tells the background agent whether the app already holds a live XMPP
    /// session, so it can skip its own.
    ///
    /// The agent connects as a SECOND resource ("-bg"), which makes the account
    /// go available then unavailable on every run. Clients that collapse presence
    /// to the bare JID and keep the last stanza then show the account as offline
    /// even though the app's session is still connected - which is what makes the
    /// account look "not registered" until you reconnect by hand.
    ///
    /// Nothing can stop that flap once the second session exists: presence is
    /// broadcast by the server whatever priority the resource claims. So the
    /// answer is not to open it while the app is connected.
    ///
    /// A timestamp rather than a boolean on purpose. A flag set to "connected"
    /// would stay set if the app were killed, and the agent would then skip for
    /// ever - trading a cosmetic presence bug for total loss of background
    /// notifications. A stale timestamp simply lets the agent run again.
    ///
    /// Its own file rather than IsolatedStorageSettings: that is one blob holding
    /// the account too, and rewriting it every minute would risk the credentials
    /// for the sake of a heartbeat.
    /// </summary>
    public static class SessionHeartbeat
    {
        private const string FILE = "session.heartbeat";

        /// <summary>
        /// How long a heartbeat counts as current. Comfortably more than the
        /// app's beat interval, so an ordinary scheduling delay is not mistaken
        /// for a dead app.
        /// </summary>
        public static readonly TimeSpan MAX_AGE = TimeSpan.FromMinutes(3);

        /// <summary>Called by the app while its session is up.</summary>
        public static void Beat()
        {
            try
            {
                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                using (IsolatedStorageFileStream stream =
                    store.OpenFile(FILE, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream))
                {
                    writer.Write(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
                // A missed beat only costs one skipped agent run.
            }
        }

        /// <summary>Called by the app when its session ends, so the agent resumes at once.</summary>
        public static void Clear()
        {
            try
            {
                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (store.FileExists(FILE))
                    {
                        store.DeleteFile(FILE);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// True when the app reported a live session recently enough to trust.
        /// Read by the agent; false on any doubt, because failing to notify is
        /// worse than a presence flicker.
        /// </summary>
        public static bool IsAppSessionLive()
        {
            try
            {
                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.FileExists(FILE))
                    {
                        return false;
                    }

                    string text;
                    using (IsolatedStorageFileStream stream =
                        store.OpenFile(FILE, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        text = reader.ReadToEnd();
                    }

                    DateTime beat;
                    if (!DateTime.TryParse(text, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out beat))
                    {
                        return false;
                    }
                    return DateTime.UtcNow - beat.ToUniversalTime() < MAX_AGE;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
