using System;
using JabberWP.Core;
using JabberWP.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace JabberWP.Pages
{
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            AppState.Instance.AttachDispatcher(Dispatcher);

            XmppAccount saved = AccountStore.Load();
            if (saved != null)
            {
                jid_tbx.Text = saved.Jid ?? "";
                password_pbx.Password = saved.Password ?? "";
                host_tbx.Text = saved.Host ?? "";
                port_tbx.Text = saved.Port.ToString();
            }
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
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
                ShowError("Enter a full Jabber ID (you@example.com) and a password.");
                return;
            }

            SetBusy(true);
            string error = await AppState.Instance.ConnectAsync(account);
            SetBusy(false);

            if (error != null)
            {
                ShowError(error);
                return;
            }

            // Only save once the server has actually accepted the credentials, so a
            // typo is never persisted.
            AccountStore.Save(account);
            Frame.Navigate(typeof(ContactsPage));

            // Drop the login page: going "back" to it from the contact list would be
            // confusing while a session is live.
            if (Frame.CanGoBack)
            {
                Frame.BackStack.Clear();
            }
        }

        #region --Restore from backup--
        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".txt");

            // The picker suspends the app; the flag keeps OnSuspending from doing
            // its usual teardown while we are mid-flow.
            AppState.Instance.IsPickingFile = true;
            picker.PickSingleFileAndContinue();
        }

        /// <summary>Called by App.OnActivated once the picker returns.</summary>
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
                    ShowError("That is not a JabberWP account backup.");
                    return;
                }

                // Filled in, not connected: the user presses connect, which is also
                // what saves the account once the server has accepted it.
                jid_tbx.Text = account.Jid ?? "";
                password_pbx.Password = account.Password ?? "";
                host_tbx.Text = account.Host ?? "";
                port_tbx.Text = account.Port.ToString();

                ShowError("Restored from " + args.Files[0].Name + ". Press connect.");
            }
            catch (Exception ex)
            {
                ShowError("Restore failed: " + ex.Message);
            }
        }
        #endregion

        private void ShowError(string message)
        {
            error_tblck.Text = message;
            error_tblck.Visibility = Visibility.Visible;
        }

        private void SetBusy(bool busy)
        {
            busy_pgb.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            connect_btn.IsEnabled = !busy;
            if (busy)
            {
                error_tblck.Visibility = Visibility.Collapsed;
            }
        }
    }
}
