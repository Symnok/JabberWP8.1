using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Navigation;
using JabberWP.Services;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;

namespace JabberWP
{
    public partial class App : Application
    {
        /// <summary>The root frame every page is hosted in.</summary>
        public static PhoneApplicationFrame RootFrame { get; private set; }

        private bool phoneApplicationInitialized = false;

        public App()
        {
            UnhandledException += Application_UnhandledException;

            InitializeComponent();
            InitializePhoneApplication();

            if (Debugger.IsAttached)
            {
                Application.Current.Host.Settings.EnableFrameRateCounter = false;
            }
        }

        #region --Lifetime--
        /// <summary>Cold start.</summary>
        private void Application_Launching(object sender, LaunchingEventArgs e)
        {
            // Re-registered on every launch on purpose: a PeriodicTask expires after
            // 14 days at most and the OS then stops running it silently.
            BackgroundAgentHelper.Register();
        }

        /// <summary>
        /// Coming back from being tombstoned or deactivated.
        ///
        /// e.IsApplicationInstancePreserved false means the process was torn down and
        /// everything in memory is gone, so the connection has to be rebuilt from the
        /// stored account.
        /// </summary>
        private async void Application_Activated(object sender, ActivatedEventArgs e)
        {
            try
            {
                // Pushes the agent's expiry out again, same as on a cold start.
                BackgroundAgentHelper.Register();

                await AppState.Instance.EnsureConnectedAsync();
                LocationKeepAlive.Instance.Start();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Leaving the foreground. NOT a disconnect: with continuous background
        /// execution the app keeps running here, which is the entire point of being
        /// a Silverlight app. Closing the stream would defeat it.
        /// </summary>
        private void Application_Deactivated(object sender, DeactivatedEventArgs e)
        {
        }

        /// <summary>The user backed out of the app - it really is ending.</summary>
        private void Application_Closing(object sender, ClosingEventArgs e)
        {
            LocationKeepAlive.Instance.Stop();
        }
        #endregion

        #region --Errors--
        private void RootFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
        }

        private void Application_UnhandledException(object sender, ApplicationUnhandledExceptionEventArgs e)
        {
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
        }
        #endregion

        #region --Phone application initialisation--
        private void InitializePhoneApplication()
        {
            if (phoneApplicationInitialized)
            {
                return;
            }

            RootFrame = new PhoneApplicationFrame();
            RootFrame.Navigated += CompleteInitializePhoneApplication;
            RootFrame.NavigationFailed += RootFrame_NavigationFailed;
            RootFrame.Navigated += CheckForResetNavigation;

            phoneApplicationInitialized = true;
        }

        private void CompleteInitializePhoneApplication(object sender, NavigationEventArgs e)
        {
            if (RootVisual != RootFrame)
            {
                RootVisual = RootFrame;
            }
            RootFrame.Navigated -= CompleteInitializePhoneApplication;
        }

        private void CheckForResetNavigation(object sender, NavigationEventArgs e)
        {
            if (e.NavigationMode == NavigationMode.Reset)
            {
                RootFrame.Navigated += ClearBackStackAfterReset;
            }
        }

        private void ClearBackStackAfterReset(object sender, NavigationEventArgs e)
        {
            RootFrame.Navigated -= ClearBackStackAfterReset;

            // Only reset-then-new/refresh should clear the stack.
            if (e.NavigationMode != NavigationMode.New && e.NavigationMode != NavigationMode.Refresh)
            {
                return;
            }

            while (RootFrame.RemoveBackEntry() != null)
            {
            }
        }
        #endregion
    }
}
