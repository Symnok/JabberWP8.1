using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using JabberWP.Core;
using JabberWP.Models;
using JabberWP.Services;
using Microsoft.Phone.Controls;

namespace JabberWP.Pages
{
    public partial class ContactsPage : PhoneApplicationPage
    {
        private bool _connectAttempted;
        private Chat _renameTarget;

        public ContactsPage()
        {
            InitializeComponent();
            contacts_lstb.ItemsSource = AppState.Instance.Chats;
            requests_ic.ItemsSource = AppState.Instance.SubscriptionRequests;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // The contact list is the app's home screen, so nothing belongs behind it:
            // BACK from here should leave the app. This also stops two loops - going
            // "back" into the chat a toast deep-linked to, and returning to the login
            // page while signed in.
            while (NavigationService.RemoveBackEntry() != null)
            {
            }

            AppState.Instance.ActiveChatJid = null;
            AppState.Instance.StateChanged += OnStateChanged;
            AppState.Instance.Closed += OnClosed;
            AppState.Instance.Chats.CollectionChanged += OnCollectionsChanged;
            AppState.Instance.SubscriptionRequests.CollectionChanged += OnCollectionsChanged;

            UpdateState();

            if (!_connectAttempted && !AppState.Instance.IsConnected)
            {
                _connectAttempted = true;
                await ConnectFromStoreAsync();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            AppState.Instance.StateChanged -= OnStateChanged;
            AppState.Instance.Closed -= OnClosed;
            AppState.Instance.Chats.CollectionChanged -= OnCollectionsChanged;
            AppState.Instance.SubscriptionRequests.CollectionChanged -= OnCollectionsChanged;

            HideRename();
            HideAddContact();
        }

        private async System.Threading.Tasks.Task ConnectFromStoreAsync()
        {
            XmppAccount account = AccountStore.Load();
            if (account == null || !account.IsUsable)
            {
                NavigationService.Navigate(new Uri("/Pages/LoginPage.xaml", UriKind.Relative));
                return;
            }

            state_tblck.Text = "connecting...";
            string error = await AppState.Instance.ConnectAsync(account);
            if (error != null)
            {
                state_tblck.Text = error;
                return;
            }
            UpdateState();
        }

        private void OnStateChanged(object sender, XmppState state)
        {
            UpdateState();
        }

        private void OnClosed(object sender, string reason)
        {
            state_tblck.Text = reason;
        }

        private void OnCollectionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateState();
        }

        private void UpdateState()
        {
            switch (AppState.Instance.State)
            {
                case XmppState.Connected:
                    XmppAccount account = AppState.Instance.Account;
                    string who = account == null ? "connected" : "connected as " + account.Jid;
                    // The background half is worth showing: if this never gets past
                    // "starting", the app is not really tracking and the system will
                    // suspend it the moment it leaves the foreground.
                    state_tblck.Text = who + " - background: " +
                        (LocationKeepAlive.Instance.Status ?? "off");
                    break;
                case XmppState.Connecting: state_tblck.Text = "connecting..."; break;
                case XmppState.Securing: state_tblck.Text = "starting TLS..."; break;
                case XmppState.Authenticating: state_tblck.Text = "signing in..."; break;
                case XmppState.Binding: state_tblck.Text = "binding resource..."; break;
                case XmppState.Failed: state_tblck.Text = "not connected"; break;
                default: state_tblck.Text = "disconnected"; break;
            }

            empty_tblck.Visibility = AppState.Instance.Chats.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            requests_ic.Visibility = AppState.Instance.SubscriptionRequests.Count == 0
                ? Visibility.Collapsed : Visibility.Visible;
        }

        #region --Contacts--
        private void Contact_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Chat chat = contacts_lstb.SelectedItem as Chat;
            // Cleared straight away, or coming back to this page would refuse to
            // reopen the same conversation - selecting it again is not a change.
            contacts_lstb.SelectedIndex = -1;
            if (chat == null)
            {
                return;
            }

            chat.Unread = 0;
            NavigationService.Navigate(new Uri(
                "/Pages/ChatPage.xaml?" + ToastHelper.CHAT_PARAMETER + "=" +
                Uri.EscapeDataString(chat.Jid), UriKind.Relative));
        }

        private void Contact_Hold(object sender, GestureEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return;
            }

            _renameTarget = element.DataContext as Chat;
            if (_renameTarget == null)
            {
                return;
            }

            renameTarget_tblck.Text = _renameTarget.Jid;
            rename_tbx.Text = _renameTarget.Name ?? "";
            rename_grid.Visibility = Visibility.Visible;
            rename_tbx.Focus();
        }
        #endregion

        #region --Rename--
        private async void RenameSave_Click(object sender, RoutedEventArgs e)
        {
            Chat target = _renameTarget;
            string name = rename_tbx.Text;
            HideRename();
            if (target != null)
            {
                await AppState.Instance.RenameAsync(target, name);
            }
        }

        private void RenameCancel_Click(object sender, RoutedEventArgs e)
        {
            HideRename();
        }

        private void HideRename()
        {
            rename_grid.Visibility = Visibility.Collapsed;
            _renameTarget = null;
        }
        #endregion

        #region --Subscription requests--
        private async void AcceptSubscription_Click(object sender, RoutedEventArgs e)
        {
            SubscriptionRequest request = RequestOf(sender);
            if (request != null)
            {
                await AppState.Instance.AcceptSubscriptionAsync(request);
            }
        }

        private async void DeclineSubscription_Click(object sender, RoutedEventArgs e)
        {
            SubscriptionRequest request = RequestOf(sender);
            if (request != null)
            {
                await AppState.Instance.DeclineSubscriptionAsync(request);
            }
        }

        private static SubscriptionRequest RequestOf(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as SubscriptionRequest;
        }
        #endregion

        #region --Add contact--
        private void AddContact_Click(object sender, EventArgs e)
        {
            addContact_tbx.Text = "";
            addContactError_tblck.Visibility = Visibility.Collapsed;
            addContact_grid.Visibility = Visibility.Visible;
            addContact_tbx.Focus();
        }

        private async void AddContactSave_Click(object sender, RoutedEventArgs e)
        {
            string error = await AppState.Instance.AddContactAsync(addContact_tbx.Text.Trim());
            if (error != null)
            {
                addContactError_tblck.Text = error;
                addContactError_tblck.Visibility = Visibility.Visible;
                return;
            }
            HideAddContact();
        }

        private void AddContactCancel_Click(object sender, RoutedEventArgs e)
        {
            HideAddContact();
        }

        private void HideAddContact()
        {
            addContact_grid.Visibility = Visibility.Collapsed;
        }
        #endregion

        #region --Application bar--
        private async void Reconnect_Click(object sender, EventArgs e)
        {
            await ConnectFromStoreAsync();
        }

        private void Account_Click(object sender, EventArgs e)
        {
            NavigationService.Navigate(new Uri("/Pages/AccountPage.xaml", UriKind.Relative));
        }

        private async void SignOut_Click(object sender, EventArgs e)
        {
            LocationKeepAlive.Instance.Stop();
            await AppState.Instance.DisconnectAsync();
            AccountStore.Clear();
            AppState.Instance.Chats.Clear();
            NavigationService.Navigate(new Uri("/Pages/LoginPage.xaml", UriKind.Relative));
        }
        #endregion
    }
}
