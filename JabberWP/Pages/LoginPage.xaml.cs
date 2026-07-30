using System;
using System.Windows;
using System.Windows.Navigation;
using JabberWP.Core;
using JabberWP.Services;
using Microsoft.Phone.Controls;

namespace JabberWP.Pages
{
    public partial class LoginPage : PhoneApplicationPage
    {
        public LoginPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Already signed in - this page is the app's start page, so a launch with
            // a stored account lands here and moves straight on.
            if (AccountStore.HasAccount && e.NavigationMode == NavigationMode.New)
            {
                GoToContacts();
                return;
            }

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

            // Saved only once the server has accepted the credentials, so a typo is
            // never persisted.
            AccountStore.Save(account);
            GoToContacts();
        }

        private void GoToContacts()
        {
            NavigationService.Navigate(new Uri("/Pages/ContactsPage.xaml", UriKind.Relative));
        }

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
