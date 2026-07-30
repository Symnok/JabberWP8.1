using System;
using System.Windows;
using System.Windows.Navigation;
using JabberWP.Core;
using JabberWP.Services;
using Microsoft.Phone.Controls;

namespace JabberWP.Pages
{
    /// <summary>
    /// Edits the single stored account. Saving reconnects, because a changed JID or
    /// password means nothing until the server has accepted it.
    /// </summary>
    public partial class AccountPage : PhoneApplicationPage
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

            if (NavigationService.CanGoBack)
            {
                NavigationService.GoBack();
            }
            else
            {
                NavigationService.Navigate(
                    new Uri("/Pages/ContactsPage.xaml", UriKind.Relative));
            }
        }

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
