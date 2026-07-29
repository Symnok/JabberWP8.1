using System;
using System.Collections.Generic;
using JabberWP.Core;
using JabberWP.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace JabberWP.Pages
{
    /// <summary>
    /// Edits the single stored account. Saving reconnects, because a changed JID or
    /// password is meaningless until the server has accepted it.
    /// </summary>
    public sealed partial class AccountPage : Page
    {
        private string _originalJid = "";

        public AccountPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            XmppAccount account = AccountStore.Load();
            if (account == null)
            {
                port_tbx.Text = Xmpp.DEFAULT_PORT.ToString();
                return;
            }

            _originalJid = account.Jid ?? "";
            jid_tbx.Text = _originalJid;
            password_pbx.Password = account.Password ?? "";
            host_tbx.Text = account.Host ?? "";
            port_tbx.Text = account.Port.ToString();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            XmppAccount account = new XmppAccount();
            account.Jid = jid_tbx.Text.Trim();
            account.Password = password_pbx.Password;
            account.Host = host_tbx.Text.Trim();
            account.Resource = Xmpp.DEFAULT_RESOURCE;

            int port;
            if (!int.TryParse(port_tbx.Text.Trim(), out port) || port <= 0)
            {
                port = Xmpp.DEFAULT_PORT;
            }
            account.Port = port;

            if (!account.IsUsable)
            {
                ShowStatus("Enter a full Jabber ID (you@example.com) and a password.");
                return;
            }

            // A different account means a different roster and different
            // conversations, so nothing from the old session may carry over.
            bool accountChanged = !string.Equals(account.Jid, _originalJid,
                StringComparison.OrdinalIgnoreCase);

            SetBusy(true);
            string error = await AppState.Instance.ConnectAsync(account);
            SetBusy(false);

            if (error != null)
            {
                // Deliberately not saved: a rejected change must not replace working
                // credentials, or the next launch cannot connect either.
                ShowStatus(error);
                return;
            }

            if (accountChanged)
            {
                AppState.Instance.Chats.Clear();
            }
            AccountStore.Save(account);
            _originalJid = account.Jid;

            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        #region --Backup and restore--
        /// <summary>
        /// Writes the account to a file the user chooses. A picker rather than a
        /// fixed location, for two reasons: app storage is deleted on uninstall,
        /// which is the very thing this guards against, and the picker needs no
        /// extra capability in the manifest.
        /// </summary>
        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            FileSavePicker picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.SuggestedFileName = "jabberwp-account";
            picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });

            // The picker suspends the app, and OnSuspending would otherwise close
            // the XMPP stream while we are still in our own flow.
            AppState.Instance.IsPickingFile = true;
            picker.PickSaveFileAndContinue();
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".txt");

            AppState.Instance.IsPickingFile = true;
            picker.PickSingleFileAndContinue();
        }

        /// <summary>Called by App.OnActivated once the save picker returns.</summary>
        public async void ContinueFileSavePicker(FileSavePickerContinuationEventArgs args)
        {
            AppState.Instance.IsPickingFile = false;

            if (args == null || args.File == null)
            {
                return;                            // cancelled
            }

            XmppAccount account = BuildAccountFromFields();
            try
            {
                // Tells a provider like OneDrive that we are mid-write, so it does
                // not sync a half-written file.
                CachedFileManager.DeferUpdates(args.File);
                await FileIO.WriteTextAsync(args.File, AccountStore.exportToText(account));
                FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(args.File);

                ShowStatus(status == FileUpdateStatus.Complete
                    ? "Account backed up to " + args.File.Name + "."
                    : "Could not finish writing " + args.File.Name + ".");
            }
            catch (Exception ex)
            {
                ShowStatus("Backup failed: " + ex.Message);
            }
        }

        /// <summary>Called by App.OnActivated once the open picker returns.</summary>
        public async void ContinueFileOpenPicker(FileOpenPickerContinuationEventArgs args)
        {
            AppState.Instance.IsPickingFile = false;

            if (args == null || args.Files == null || args.Files.Count == 0)
            {
                return;                            // cancelled
            }

            try
            {
                string text = await FileIO.ReadTextAsync(args.Files[0]);
                XmppAccount account = AccountStore.parseBackup(text);
                if (account == null)
                {
                    ShowStatus("That is not a JabberWP account backup.");
                    return;
                }

                // Filled in but NOT saved or connected: the user gets to see what
                // was restored and press "save and reconnect" themselves.
                jid_tbx.Text = account.Jid ?? "";
                password_pbx.Password = account.Password ?? "";
                host_tbx.Text = account.Host ?? "";
                port_tbx.Text = account.Port.ToString();

                ShowStatus("Restored from " + args.Files[0].Name +
                           ". Press save and reconnect to use it.");
            }
            catch (Exception ex)
            {
                ShowStatus("Restore failed: " + ex.Message);
            }
        }

        private XmppAccount BuildAccountFromFields()
        {
            XmppAccount account = new XmppAccount();
            account.Jid = jid_tbx.Text.Trim();
            account.Password = password_pbx.Password;
            account.Host = host_tbx.Text.Trim();
            account.Resource = Xmpp.DEFAULT_RESOURCE;

            int port;
            if (!int.TryParse(port_tbx.Text.Trim(), out port) || port <= 0)
            {
                port = Xmpp.DEFAULT_PORT;
            }
            account.Port = port;
            return account;
        }
        #endregion

        private void ShowStatus(string message)
        {
            status_tblck.Text = message;
            status_tblck.Visibility = Visibility.Visible;
        }

        private void SetBusy(bool busy)
        {
            busy_pgb.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            save_btn.IsEnabled = !busy;
            if (busy)
            {
                status_tblck.Visibility = Visibility.Collapsed;
            }
        }
    }
}
