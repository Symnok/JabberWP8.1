using System;
using System.IO.IsolatedStorage;

namespace JabberWP.Services
{
    /// <summary>
    /// App preferences, persisted in the same isolated storage settings as the account.
    /// </summary>
    public static class AppSettings
    {
        private const string KEY_BACKGROUND_ENABLED = "background_enabled";

        private static IsolatedStorageSettings Settings
        {
            get { return IsolatedStorageSettings.ApplicationSettings; }
        }

        /// <summary>
        /// Whether the app may keep running in the background: the location-tracking
        /// keep-alive and the periodic agent.
        ///
        /// Defaults to ON, which is what the app did before this switch existed.
        /// Turning it off means no location access at all and no scheduled agent, so
        /// messages only arrive while the app is open - the trade being battery life
        /// and the location indicator.
        /// </summary>
        public static bool BackgroundEnabled
        {
            get
            {
                try
                {
                    if (Settings.Contains(KEY_BACKGROUND_ENABLED))
                    {
                        return Convert.ToBoolean(Settings[KEY_BACKGROUND_ENABLED]);
                    }
                }
                catch (Exception)
                {
                }
                return true;
            }
            set
            {
                try
                {
                    Settings[KEY_BACKGROUND_ENABLED] = value;
                    Settings.Save();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
