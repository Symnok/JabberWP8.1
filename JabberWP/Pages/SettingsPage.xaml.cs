using System;
using System.Windows;
using System.Windows.Navigation;
using JabberWP.Services;
using Microsoft.Phone.Controls;

namespace JabberWP.Pages
{
    public partial class SettingsPage : PhoneApplicationPage
    {
        /// <summary>
        /// True while the checkbox is being set from stored state, so loading the page
        /// does not look like the user toggling it.
        /// </summary>
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _loading = true;
            background_chk.IsChecked = AppSettings.BackgroundEnabled;
            _loading = false;

            ShowState();
        }

        private void Background_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            bool enabled = background_chk.IsChecked == true;
            AppSettings.BackgroundEnabled = enabled;

            // Applied immediately, not on next launch: leaving location running after
            // the user switched it off would be the wrong way round.
            if (enabled)
            {
                BackgroundAgentHelper.Register();
                if (AppState.Instance.IsConnected)
                {
                    LocationKeepAlive.Instance.Start();
                }
            }
            else
            {
                LocationKeepAlive.Instance.Stop();
                BackgroundAgentHelper.Unregister();
            }

            ShowState();
        }

        private void ShowState()
        {
            if (background_chk.IsChecked == true)
            {
                state_tblck.Text = "agent: " + (BackgroundAgentHelper.Status ?? "not registered") +
                                   ", location: " + (LocationKeepAlive.Instance.Status ?? "off");
            }
            else
            {
                state_tblck.Text = "Background mode is off.";
            }
        }
    }
}
