using System;
using Microsoft.Phone.Shell;

namespace JabberWP.Services
{
    /// <summary>
    /// New-message toasts.
    ///
    /// ShellToast, not the WinRT ToastNotificationManager: this is the Silverlight
    /// app model, and ShellToast is the API that works here. Same platform rule as
    /// before though - a toast raised while the app is in the foreground is not
    /// shown. It is only visible once the app is in the background, which is now
    /// possible thanks to continuous background execution.
    /// </summary>
    public static class ToastHelper
    {
        /// <summary>
        /// Query string on the toast's navigation URI, read back by ContactsPage to
        /// work out which chat to open.
        /// </summary>
        public const string CHAT_PARAMETER = "chat";

        public static void showMessage(string fromBareJid, string body)
        {
            if (string.IsNullOrEmpty(fromBareJid))
            {
                return;
            }

            try
            {
                ShellToast toast = new ShellToast();
                toast.Title = fromBareJid;
                toast.Content = body ?? "";
                // Tapping it launches the app straight into that conversation.
                toast.NavigationUri = new Uri(
                    "/Pages/ChatPage.xaml?" + CHAT_PARAMETER + "=" + Uri.EscapeDataString(fromBareJid),
                    UriKind.Relative);
                toast.Show();
            }
            catch (Exception)
            {
                // A toast is never worth taking the caller down for, and this runs
                // from the socket's message handler.
            }
        }
    }
}
