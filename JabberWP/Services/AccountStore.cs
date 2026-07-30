using System;
using System.IO.IsolatedStorage;
using System.Text;
using JabberWP.Core;

namespace JabberWP.Services
{
    /// <summary>
    /// Persists the single account.
    ///
    /// IsolatedStorageSettings rather than the WinRT PasswordVault: it is the storage
    /// a Silverlight background agent can also read, which matters as soon as the
    /// ScheduledAgent needs the credentials to connect. Your GoogleContactSyncWP
    /// shares state with its agent exactly this way.
    ///
    /// The password is stored unencrypted, as it was on the WinRT build - the backup
    /// file already carries it in clear, so nothing is gained by protecting only one
    /// of the two copies.
    /// </summary>
    public static class AccountStore
    {
        private const string KEY_JID = "account_jid";
        private const string KEY_PASSWORD = "account_password";
        private const string KEY_HOST = "account_host";
        private const string KEY_PORT = "account_port";
        private const string KEY_RESOURCE = "account_resource";

        private static IsolatedStorageSettings Settings
        {
            get { return IsolatedStorageSettings.ApplicationSettings; }
        }

        public static bool HasAccount
        {
            get { return !string.IsNullOrEmpty(read(KEY_JID)); }
        }

        public static XmppAccount Load()
        {
            string jid = read(KEY_JID);
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            XmppAccount account = new XmppAccount();
            account.Jid = jid;
            account.Password = read(KEY_PASSWORD);
            account.Host = read(KEY_HOST);
            account.Resource = read(KEY_RESOURCE);
            if (string.IsNullOrEmpty(account.Resource))
            {
                account.Resource = Xmpp.DEFAULT_RESOURCE;
            }

            int port;
            account.Port = int.TryParse(read(KEY_PORT), out port) && port > 0
                ? port
                : Xmpp.DEFAULT_PORT;
            return account;
        }

        public static void Save(XmppAccount account)
        {
            if (account == null)
            {
                return;
            }

            write(KEY_JID, account.Jid ?? "");
            write(KEY_PASSWORD, account.Password ?? "");
            write(KEY_HOST, account.Host ?? "");
            write(KEY_PORT, account.Port.ToString());
            write(KEY_RESOURCE, account.Resource ?? Xmpp.DEFAULT_RESOURCE);
            save();
        }

        public static void Clear()
        {
            remove(KEY_JID);
            remove(KEY_PASSWORD);
            remove(KEY_HOST);
            remove(KEY_PORT);
            remove(KEY_RESOURCE);
            save();
        }

        #region --Backup and restore--
        private const string BACKUP_HEADER = "jabberwp-account-backup 1";

        /// <summary>Serialises an account to the plain-text backup format.</summary>
        public static string exportToText(XmppAccount account)
        {
            if (account == null)
            {
                return "";
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine(BACKUP_HEADER);
            builder.AppendLine("jid=" + (account.Jid ?? ""));
            builder.AppendLine("password=" + (account.Password ?? ""));
            builder.AppendLine("host=" + (account.Host ?? ""));
            builder.AppendLine("port=" + account.Port);
            builder.AppendLine("resource=" + (account.Resource ?? Xmpp.DEFAULT_RESOURCE));
            return builder.ToString();
        }

        /// <summary>
        /// Parses a backup file. Null when the text is not one, so a wrong file cannot
        /// half-overwrite the stored account.
        /// </summary>
        public static XmppAccount parseBackup(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string[] lines = text.Split(new char[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || lines[0].Trim() != BACKUP_HEADER)
            {
                return null;
            }

            XmppAccount account = new XmppAccount();
            for (int i = 1; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = lines[i].Substring(0, separator).Trim();
                // Not trimmed: a password may start or end with a space.
                string value = lines[i].Substring(separator + 1);

                switch (key)
                {
                    case "jid": account.Jid = value.Trim(); break;
                    case "password": account.Password = value; break;
                    case "host": account.Host = value.Trim(); break;
                    case "resource": account.Resource = value.Trim(); break;
                    case "port":
                        int port;
                        if (int.TryParse(value.Trim(), out port) && port > 0)
                        {
                            account.Port = port;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(account.Resource))
            {
                account.Resource = Xmpp.DEFAULT_RESOURCE;
            }
            return string.IsNullOrEmpty(account.Jid) ? null : account;
        }
        #endregion

        #region --Isolated storage helpers--
        private static string read(string key)
        {
            try
            {
                return Settings.Contains(key) ? Settings[key] as string : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static void write(string key, string value)
        {
            try
            {
                Settings[key] = value;
            }
            catch (Exception)
            {
            }
        }

        private static void remove(string key)
        {
            try
            {
                if (Settings.Contains(key))
                {
                    Settings.Remove(key);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void save()
        {
            try
            {
                Settings.Save();
            }
            catch (Exception)
            {
            }
        }
        #endregion
    }
}
