using System;
using JabberWP.Core;
using JabberWP.Models;
using JabberWP.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace JabberWP.Pages
{
    public sealed partial class ContactsPage : Page
    {
        private bool _connectAttempted;

        /// <summary>Contact the rename flyout/overlay is acting on.</summary>
        private Chat _renameTarget;

        public ContactsPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            contacts_lstv.ItemsSource = AppState.Instance.Chats;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            AppState.Instance.AttachDispatcher(Dispatcher);
            AppState.Instance.ActiveChatJid = null;

            AppState.Instance.StateChanged += OnStateChanged;
            AppState.Instance.Closed += OnClosed;

            // The roster arrives after this page is already up, so the empty-state
            // text has to react to the collection filling rather than being decided
            // once on navigation.
            AppState.Instance.Chats.CollectionChanged += OnChatsChanged;

            UpdateState();

            // Launching straight into this page (account already saved) means
            // nothing has connected yet. Do it once, not on every back navigation.
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
            AppState.Instance.Chats.CollectionChanged -= OnChatsChanged;

            // This page is cached (NavigationCacheMode.Enabled), so a half-finished
            // rename would still be on screen when it is shown again.
            HideRename();
        }

        private void OnChatsChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // AppState has already marshalled this onto the UI thread.
            UpdateState();
        }

        private async System.Threading.Tasks.Task ConnectFromStoreAsync()
        {
            XmppAccount account = AccountStore.Load();
            if (account == null || !account.IsUsable)
            {
                Frame.Navigate(typeof(LoginPage));
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

        private void UpdateState()
        {
            switch (AppState.Instance.State)
            {
                case XmppState.Connected:
                    XmppAccount account = AppState.Instance.Account;
                    state_tblck.Text = account == null
                        ? "connected"
                        : "connected as " + account.Jid;
                    break;
                case XmppState.Connecting:
                    state_tblck.Text = "connecting...";
                    break;
                case XmppState.Securing:
                    state_tblck.Text = "starting TLS...";
                    break;
                case XmppState.Authenticating:
                    state_tblck.Text = "signing in...";
                    break;
                case XmppState.Binding:
                    state_tblck.Text = "binding resource...";
                    break;
                case XmppState.Failed:
                    state_tblck.Text = "not connected";
                    break;
                default:
                    state_tblck.Text = "disconnected";
                    break;
            }

            empty_tblck.Visibility = AppState.Instance.Chats.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            reconnect_abb.IsEnabled = !AppState.Instance.IsConnected;
        }

        private void Contact_Click(object sender, ItemClickEventArgs e)
        {
            Chat chat = e.ClickedItem as Chat;
            if (chat == null)
            {
                return;
            }
            chat.Unread = 0;
            Frame.Navigate(typeof(ChatPage), chat.Jid);
        }

        #region --Rename--
        private void Contact_Holding(object sender, HoldingRoutedEventArgs e)
        {
            // Started fires as soon as the press passes the hold threshold;
            // Completed only arrives on release, which feels late.
            // Qualified: HoldingRoutedEventArgs is a XAML type but HoldingState
            // comes from Windows.UI.Input, and importing both input namespaces
            // invites ambiguity.
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started)
            {
                return;
            }

            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return;
            }

            // Remember the target here rather than reading DataContext off the
            // MenuFlyoutItem later - a flyout is not part of the item's visual tree,
            // so inherited DataContext cannot be relied on.
            _renameTarget = element.DataContext as Chat;
            if (_renameTarget == null)
            {
                return;
            }
            FlyoutBase.ShowAttachedFlyout(element);
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_renameTarget == null)
            {
                return;
            }

            renameTarget_tblck.Text = _renameTarget.Jid;
            rename_tbx.Text = _renameTarget.Name ?? "";
            rename_grid.Visibility = Visibility.Visible;
            rename_tbx.Focus(FocusState.Programmatic);
            rename_tbx.SelectAll();
        }

        private async void RenameSave_Click(object sender, RoutedEventArgs e)
        {
            Chat target = _renameTarget;
            HideRename();
            if (target == null)
            {
                return;
            }
            await AppState.Instance.RenameAsync(target, rename_tbx.Text);
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

        private async void Reconnect_Click(object sender, RoutedEventArgs e)
        {
            await ConnectFromStoreAsync();
        }

        private void Account_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(AccountPage));
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            await AppState.Instance.DisconnectAsync();
            AccountStore.Clear();
            AppState.Instance.Chats.Clear();
            Frame.Navigate(typeof(LoginPage));
        }
    }
}
