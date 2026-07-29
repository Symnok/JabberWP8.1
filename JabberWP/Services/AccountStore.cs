using System;
using System.Text;
using JabberWP.Core;
using Windows.Security.Credentials;
using Windows.Storage;

namespace JabberWP.Services
{
    /// <summary>
    /// Persists the account. The password goes into the PasswordVault (encrypted by
    /// the OS, per app); everything else into LocalSettings.
    ///
    /// Keep the storage keys stable - a background task added later will read the
    /// same values, the way GoogleContactSyncWP shares IsolatedStorageSettings
    /// between its app and its agent.
    /// </summary>
    public static class AccountStore
    {
        private const string VAULT_RESOURCE = "JabberWP";

        private const string KEY_JID = "account_jid";
        private const string KEY_HOST = "account_host";
        private const string KEY_PORT = "account_port";
        private const string KEY_RESOURCE = "account_resource";

        public static bool HasAccount
        {
            get
            {
                object jid = Settings.Values[KEY_JID];
                return jid != null && !string.IsNullOrEmpty(jid.ToString());
            }
        }

        /// <summary>Loads the saved account, or null if none / password missing.</summary>
        public static XmppAccount Load()
        {
            string jid = ReadString(KEY_JID);
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            XmppAccount account = new XmppAccount();
            account.Jid = jid;
            account.Host = ReadString(KEY_HOST);
            account.Resource = ReadString(KEY_RESOURCE);
            if (string.IsNullOrEmpty(account.Resource))
            {
                account.Resource = Xmpp.DEFAULT_RESOURCE;
            }

            object port = Settings.Values[KEY_PORT];
            account.Port = port == null ? Xmpp.DEFAULT_PORT : Convert.ToInt32(port);

            account.Password = LoadPassword(jid);
            if (string.IsNullOrEmpty(account.Password))
            {
                // Settings without a password are useless for connecting, but the
                // login page can still prefill from them, so return the account and
                // let the caller decide.
                account.Password = "";
            }
            return account;
        }

        public static void Save(XmppAccount account)
        {
            if (account == null)
            {
                return;
            }

            Settings.Values[KEY_JID] = account.Jid ?? "";
            Settings.Values[KEY_HOST] = account.Host ?? "";
            Settings.Values[KEY_PORT] = account.Port;
            Settings.Values[KEY_RESOURCE] = account.Resource ?? Xmpp.DEFAULT_RESOURCE;

            SavePassword(account.Jid, account.Password);
        }

        public static void Clear()
        {
            string jid = ReadString(KEY_JID);
            if (!string.IsNullOrEmpty(jid))
            {
                RemovePassword(jid);
            }

            Settings.Values.Remove(KEY_JID);
            Settings.Values.Remove(KEY_HOST);
            Settings.Values.Remove(KEY_PORT);
            Settings.Values.Remove(KEY_RESOURCE);
        }

        #region --Backup and restore--
        private const string BACKUP_HEADER = "jabberwp-account-backup 1";

        /// <summary>
        /// Serialises an account to the plain-text backup format.
        ///
        /// The password is written IN CLEAR. That is what makes the file usable after
        /// a reinstall without any key material to carry over, and it is why the file
        /// is only as safe as wherever the user puts it.
        /// </summary>
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
        /// Parses a backup file. Returns null when the text is not one - a wrong file
        /// picked by mistake must not half-overwrite the stored account.
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
                // Not trimmed: a password may legitimately start or end with a space.
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

        #region --Password vault--
        private static string LoadPassword(string jid)
        {
            try
            {
                PasswordVault vault = new PasswordVault();
                PasswordCredential credential = vault.Retrieve(VAULT_RESOURCE, jid);
                if (credential == null)
                {
                    return "";
                }
                // WP8.1 returns the credential with the password not yet populated.
                credential.RetrievePassword();
                return credential.Password ?? "";
            }
            catch (Exception)
            {
                // Retrieve throws rather than returning null when nothing is stored.
                return "";
            }
        }

        private static void SavePassword(string jid, string password)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            RemovePassword(jid);
            if (string.IsNullOrEmpty(password))
            {
                return;
            }

            try
            {
                PasswordVault vault = new PasswordVault();
                vault.Add(new PasswordCredential(VAULT_RESOURCE, jid, password));
            }
            catch (Exception)
            {
            }
        }

        private static void RemovePassword(string jid)
        {
            try
            {
                PasswordVault vault = new PasswordVault();
                PasswordCredential existing = vault.Retrieve(VAULT_RESOURCE, jid);
                if (existing != null)
                {
                    vault.Remove(existing);
                }
            }
            catch (Exception)
            {
            }
        }
        #endregion

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        private static string ReadString(string key)
        {
            object value = Settings.Values[key];
            return value == null ? "" : value.ToString();
        }
    }
}
