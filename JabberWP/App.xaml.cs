using System;
using JabberWP.Pages;
using JabberWP.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

#if WINDOWS_PHONE_APP
using Windows.Phone.UI.Input;
#endif

namespace JabberWP
{
    public sealed partial class App : Application
    {
        private TransitionCollection _transitions;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.CacheSize = 1;
                Window.Current.Content = rootFrame;

#if WINDOWS_PHONE_APP
                HardwareButtons.BackPressed += OnBackPressed;
#endif
            }

            if (rootFrame.Content == null)
            {
                // Straight to the contacts list when an account is already stored;
                // the page connects on load. Otherwise ask for credentials first.
                Type start = AccountStore.HasAccount
                    ? typeof(ContactsPage)
                    : typeof(LoginPage);

                if (rootFrame.ContentTransitions != null)
                {
                    _transitions = new TransitionCollection();
                    foreach (Transition transition in rootFrame.ContentTransitions)
                    {
                        _transitions.Add(transition);
                    }
                }
                rootFrame.ContentTransitions = null;
                rootFrame.Navigated += OnFirstNavigated;

                if (!rootFrame.Navigate(start, e.Arguments))
                {
                    throw new Exception("Failed to open the first page.");
                }
            }

            Window.Current.Activate();
        }

#if WINDOWS_PHONE_APP
        /// <summary>
        /// The phone has no PickSingleFileAsync: the app is suspended while the file
        /// picker runs and reactivated with the result here. The frame and its pages
        /// survive suspension, so the continuation is handed to whichever page asked
        /// for it.
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            base.OnActivated(args);

            if (args.Kind != ActivationKind.PickFileContinuation)
            {
                return;
            }

            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                // The app was terminated rather than suspended, so the page that
                // started the pick no longer exists. Nothing sensible to resume.
                return;
            }

            ChatPage chatPage = rootFrame.Content as ChatPage;
            FileOpenPickerContinuationEventArgs pickerArgs =
                args as FileOpenPickerContinuationEventArgs;
            if (chatPage != null && pickerArgs != null)
            {
                chatPage.ContinueFileOpenPicker(pickerArgs);
            }

            Window.Current.Activate();
        }
#endif

        /// <summary>Restores the default page transitions after the first navigation.</summary>
        private void OnFirstNavigated(object sender, NavigationEventArgs e)
        {
            Frame rootFrame = sender as Frame;
            rootFrame.ContentTransitions = _transitions ?? new TransitionCollection
            {
                new NavigationThemeTransition()
            };
            rootFrame.Navigated -= OnFirstNavigated;
        }

#if WINDOWS_PHONE_APP
        /// <summary>
        /// The phone's hardware back button. Unhandled means "leave the app", which
        /// on this platform suspends it - the connection is dropped by suspension,
        /// not by us, until background support exists.
        /// </summary>
        private void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            Frame frame = Window.Current.Content as Frame;
            if (frame != null && frame.CanGoBack)
            {
                e.Handled = true;
                frame.GoBack();
            }
        }
#endif

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            SuspendingDeferral deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                // The file picker suspends the app on this platform, so this fires
                // in the middle of attaching a picture. Disconnecting there would
                // drop the session the user is trying to send into - and did.
                if (AppState.Instance.IsPickingFile)
                {
                    return;
                }

                // Close the stream cleanly. Without this the server keeps the old
                // session bound to our resource until it times out, which then
                // collides with the next connection attempt.
                await AppState.Instance.DisconnectAsync();
            }
            catch (Exception)
            {
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
