using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using JabberWP.Core;
using JabberWP.Models;
using JabberWP.Services;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Tasks;

namespace JabberWP.Pages
{
    public partial class ChatPage : PhoneApplicationPage
    {
        private Chat _chat;
        private bool _sending;
        private readonly PhotoChooserTask PHOTO_CHOOSER;

        public ChatPage()
        {
            InitializeComponent();

            // Choosers must be constructed and wired up in the page's constructor.
            // The chooser takes the app away and the page may be recreated before the
            // result comes back, so a handler attached later would never be called.
            PHOTO_CHOOSER = new PhotoChooserTask();
            PHOTO_CHOOSER.ShowCamera = true;
            PHOTO_CHOOSER.Completed += OnPhotoChosen;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Arrives either from the contact list or from a toast's navigation URI -
            // both use the same query parameter.
            string jid = null;
            if (NavigationContext.QueryString.ContainsKey(ToastHelper.CHAT_PARAMETER))
            {
                jid = NavigationContext.QueryString[ToastHelper.CHAT_PARAMETER];
            }

            _chat = AppState.Instance.GetOrCreateChat(jid, null);
            if (_chat == null)
            {
                if (NavigationService.CanGoBack)
                {
                    NavigationService.GoBack();
                }
                return;
            }

            AppState.Instance.ActiveChatJid = _chat.Jid;
            _chat.Unread = 0;

            name_tblck.Text = _chat.Jid;
            presence_tblck.Text = _chat.PresenceText;
            messages_lstb.ItemsSource = _chat.Messages;

            _chat.PropertyChanged += OnChatPropertyChanged;
            _chat.Messages.CollectionChanged += OnMessagesChanged;

            ScrollToEnd();
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

        /// <summary>
        /// Sends BACK to the contact list when this page was opened straight from a
        /// toast.
        ///
        /// A toast navigates directly here, so this page is the bottom of the back
        /// stack and the default BACK behaviour leaves the app entirely. Going to the
        /// contact list matches what BACK does when the chat was opened from there.
        /// ContactsPage clears the back stack on arrival, so this cannot bounce
        /// between the two pages.
        /// </summary>
        protected override void OnBackKeyPress(CancelEventArgs e)
        {
            base.OnBackKeyPress(e);

            if (!NavigationService.CanGoBack)
            {
                e.Cancel = true;
                NavigationService.Navigate(
                    new Uri("/Pages/ContactsPage.xaml", UriKind.Relative));
            }
        }

        private void OnChatPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "PresenceText")
            {
                presence_tblck.Text = _chat.PresenceText;
            }
        }

        private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _chat.Unread = 0;
            ScrollToEnd();
        }

        #region --Sending--
        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            await SendAsync();
        }

        private async void Message_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
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
        }
        #endregion

        #region --Sending a picture--
        private void SendPicture_Click(object sender, EventArgs e)
        {
            if (!AppState.Instance.IsConnected)
            {
                ShowStatus("Not connected.");
                return;
            }

            try
            {
                // ShowCamera lets the user take a new photo from inside the chooser,
                // so one task covers both the library and the camera.
                PHOTO_CHOOSER.Show();
            }
            catch (Exception ex)
            {
                ShowStatus("Could not open the picture chooser: " + ex.Message);
            }
        }

        private async void OnPhotoChosen(object sender, PhotoResult e)
        {
            if (e == null || e.TaskResult != TaskResult.OK || e.ChosenPhoto == null)
            {
                return;                            // cancelled
            }
            if (_chat == null)
            {
                return;
            }

            string fileName = System.IO.Path.GetFileName(e.OriginalFileName ?? "");
            if (string.IsNullOrEmpty(fileName))
            {
                // The chooser does not always give a usable name, and the server wants
                // one for the slot request.
                fileName = "image.jpg";
            }

            ShowStatus("Uploading " + fileName + "...");
            SetBusy(true);
            try
            {
                // The picture chooser returns JPEG regardless of the source.
                string error = await AppState.Instance.SendImageAsync(
                    _chat, e.ChosenPhoto, fileName, "image/jpeg");

                if (error != null)
                {
                    ShowStatus(error);
                    return;
                }

                HideStatus();
                ScrollToEnd();
            }
            finally
            {
                SetBusy(false);
                try
                {
                    e.ChosenPhoto.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
        #endregion

        /// <summary>Opens the link in a tapped message, if it has one.</summary>
        private void Bubble_Tap(object sender, GestureEventArgs e)
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

            // WebBrowserTask rather than Launcher: this is the Silverlight way, and
            // it does not need the app to be a registered URI handler.
            WebBrowserTask browser = new WebBrowserTask();
            browser.Uri = uri;
            browser.Show();
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

        private void SetBusy(bool busy)
        {
            send_btn.IsEnabled = !busy;
            message_tbx.IsEnabled = !busy;
        }

        private void ScrollToEnd()
        {
            if (_chat == null || _chat.Messages.Count == 0)
            {
                return;
            }
            messages_lstb.ScrollIntoView(_chat.Messages[_chat.Messages.Count - 1]);
        }
    }
}
