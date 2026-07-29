using System;
using System.ComponentModel;
using System.Threading.Tasks;
using JabberWP.Core;
using JabberWP.Models;
using JabberWP.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace JabberWP.Pages
{
    public sealed partial class ChatPage : Page
    {
        private Chat _chat;
        private bool _sending;
        private bool _picking;

        public ChatPage()
        {
            InitializeComponent();

            // ButtonBase marks PointerPressed handled in its class handler, so a
            // handler attached in XAML is never called. handledEventsToo: true is
            // the only way to see the press - and the press is what matters here,
            // because Click arrives after the keyboard has hidden and moved the
            // button out from under the finger.
            send_btn.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(Send_PointerPressed), true);
            attach_btn.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(Attach_PointerPressed), true);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Coming back from the file picker the app was suspended, and the
            // parameter is the continuation rather than a JID. Keep the chat we
            // already had in that case.
            string jid = e.Parameter as string;
            if (!string.IsNullOrEmpty(jid))
            {
                _chat = AppState.Instance.GetOrCreateChat(jid, null);
            }
            if (_chat == null)
            {
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                return;
            }

            AppState.Instance.ActiveChatJid = _chat.Jid;
            _chat.Unread = 0;

            name_tblck.Text = _chat.Jid;
            presence_tblck.Text = _chat.PresenceText;
            messages_lstv.ItemsSource = _chat.Messages;

            _chat.PropertyChanged += OnChatPropertyChanged;
            _chat.Messages.CollectionChanged += OnMessagesChanged;

            ScrollToEnd();
            UpdateSendState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            AppState.Instance.ActiveChatJid = null;
            if (_chat != null)
            {
                _chat.PropertyChanged -= OnChatPropertyChanged;
                _chat.Messages.CollectionChanged -= OnMessagesChanged;
            }
        }

        private void OnChatPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "PresenceText")
            {
                presence_tblck.Text = _chat.PresenceText;
            }
        }

        private void OnMessagesChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // AppState already marshalled this onto the UI thread.
            _chat.Unread = 0;
            ScrollToEnd();
        }

        #region --Sending text--
        /// <summary>
        /// The path that normally fires: sends on the press, before the keyboard
        /// hides and the layout reflows. Deliberately does NOT set e.Handled - the
        /// button has already handled the event, and suppressing it further would
        /// only break the pressed visual state.
        /// </summary>
        private async void Send_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            await SendAsync();
        }

        /// <summary>
        /// Fallback for anything that raises Click without a pointer press (keyboard,
        /// accessibility). A no-op after the press path already sent, because the
        /// text box is cleared first.
        /// </summary>
        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            await SendAsync();
        }

        private async void Message_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await SendAsync();
            }
        }

        private async Task SendAsync()
        {
            if (_sending || _chat == null)
            {
                return;
            }

            string body = message_tbx.Text.Trim();
            if (string.IsNullOrEmpty(body))
            {
                return;
            }
            if (!AppState.Instance.IsConnected)
            {
                ShowStatus("Not connected - message not sent.");
                return;
            }

            _sending = true;
            try
            {
                message_tbx.Text = "";
                await AppState.Instance.SendAsync(_chat, body);
                ScrollToEnd();
            }
            finally
            {
                _sending = false;
            }

            // Keep the keyboard up and the caret where the user expects it.
            message_tbx.Focus(FocusState.Programmatic);
        }
        #endregion

        #region --Sending a picture--
        private void Attach_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            StartPick();
        }

        private void Attach_Click(object sender, RoutedEventArgs e)
        {
            StartPick();
        }

        /// <summary>
        /// Opens the picker. Guarded because the press and Click paths can both
        /// arrive, and launching the picker twice would suspend the app twice.
        /// </summary>
        private void StartPick()
        {
            if (_picking)
            {
                return;
            }
            if (!AppState.Instance.IsConnected)
            {
                ShowStatus("Not connected.");
                return;
            }

            _picking = true;

            // Tells the suspend handler to leave the connection alone: launching the
            // picker suspends the app, and closing the stream there would disconnect
            // us mid-attach.
            AppState.Instance.IsPickingFile = true;

            FileOpenPicker picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".gif");

            // On the phone there is no PickSingleFileAsync: the app is suspended
            // while the picker runs and the result comes back through
            // App.OnActivated, which routes it to ContinueFileOpenPicker below.
            picker.PickSingleFileAndContinue();
        }

        /// <summary>
        /// Called by App.OnActivated once the picker returns.
        /// </summary>
        public async void ContinueFileOpenPicker(FileOpenPickerContinuationEventArgs args)
        {
            // Released here whatever the outcome, including cancellation, or the
            // attach button would stay dead and the next real suspend would leave
            // the stream open.
            _picking = false;
            AppState.Instance.IsPickingFile = false;

            if (args == null || args.Files == null || args.Files.Count == 0)
            {
                return;                            // user cancelled
            }

            StorageFile file = args.Files[0];
            if (_chat == null)
            {
                return;
            }

            SetBusy(true);

            // Suspension may have killed the socket even though we did not close it
            // ourselves, so make sure there is a session before asking for a slot.
            if (!AppState.Instance.IsConnected)
            {
                ShowStatus("Reconnecting...");
                string reconnectError = await AppState.Instance.EnsureConnectedAsync();
                if (reconnectError != null)
                {
                    SetBusy(false);
                    ShowStatus(reconnectError);
                    return;
                }
            }

            ShowStatus("Uploading " + file.Name + "...");

            string error = await AppState.Instance.SendImageAsync(_chat, file);

            SetBusy(false);
            if (error != null)
            {
                ShowStatus(error);
                return;
            }

            HideStatus();
            ScrollToEnd();
        }
        #endregion

        /// <summary>Opens the link in a tapped message, if it has one.</summary>
        private async void Bubble_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return;
            }

            XmppMessage message = element.DataContext as XmppMessage;
            if (message == null || !message.HasUrl)
            {
                return;
            }

            Uri uri;
            if (!Uri.TryCreate(message.FirstUrl, UriKind.Absolute, out uri))
            {
                return;
            }

            e.Handled = true;
            await Launcher.LaunchUriAsync(uri);
        }

        #region --Small helpers--
        private void UpdateSendState()
        {
            bool connected = AppState.Instance.IsConnected;
            send_btn.IsEnabled = connected;
            attach_btn.IsEnabled = connected;
        }

        private void SetBusy(bool busy)
        {
            send_btn.IsEnabled = !busy && AppState.Instance.IsConnected;
            attach_btn.IsEnabled = !busy && AppState.Instance.IsConnected;
        }

        private void ShowStatus(string message)
        {
            status_tblck.Text = message;
            status_tblck.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            status_tblck.Visibility = Visibility.Collapsed;
        }

        private void ScrollToEnd()
        {
            if (_chat == null || _chat.Messages.Count == 0)
            {
                return;
            }
            messages_lstv.ScrollIntoView(_chat.Messages[_chat.Messages.Count - 1]);
        }
        #endregion
    }
}
