using System;
using Windows.Devices.Geolocation;

namespace JabberWP.Services
{
    /// <summary>
    /// Keeps the app running after HOME is pressed, so the XMPP connection survives
    /// and messages keep arriving.
    ///
    /// This is the reason the app moved to Silverlight. WMAppManifest declares
    /// &lt;BackgroundExecution&gt;&lt;ExecutionType Name="LocationTracking"/&gt;, and
    /// the system honours it only while the app holds a live Geolocator subscription
    /// that is actually reporting. Both halves are required: the declaration alone
    /// does nothing, and so does a subscription without the declaration - which is
    /// exactly why the WinRT build could never be made to work.
    ///
    /// The position is never read, stored or transmitted. The cost is the location
    /// indicator staying on and worse battery life.
    /// </summary>
    public class LocationKeepAlive
    {
        private static LocationKeepAlive _instance;
        public static LocationKeepAlive Instance
        {
            get { return _instance ?? (_instance = new LocationKeepAlive()); }
        }

        private LocationKeepAlive()
        {
        }

        private Geolocator _geolocator;

        public bool IsRunning
        {
            get { return _geolocator != null; }
        }

        /// <summary>
        /// Last thing the geolocator reported about itself, plus how many position
        /// reports have arrived. If the count never moves, tracking is not really
        /// running and the system will suspend the app regardless of the manifest.
        /// </summary>
        public string Status { get; private set; }

        public int Reports { get; private set; }

        /// <summary>Starts the subscription. Idempotent, never throws.</summary>
        public bool Start()
        {
            // Checked here rather than at the call sites: this is the only place a
            // Geolocator is created, so with background mode off the app cannot touch
            // location at all, however it was reached.
            if (!AppSettings.BackgroundEnabled)
            {
                Status = "disabled";
                return false;
            }

            if (_geolocator != null)
            {
                return true;
            }

            try
            {
                _geolocator = new Geolocator();
                // Frequent enough to count as active tracking. A long report
                // interval or a large movement threshold makes the system stop
                // treating the app as tracking, and it gets suspended.
                _geolocator.DesiredAccuracy = PositionAccuracy.Default;
                _geolocator.MovementThreshold = 0;
                _geolocator.ReportInterval = 5000;

                _geolocator.PositionChanged += OnPositionChanged;
                _geolocator.StatusChanged += OnStatusChanged;

                Status = "starting";
                return true;
            }
            catch (Exception ex)
            {
                Status = "unavailable: " + ex.Message;
                _geolocator = null;
                return false;
            }
        }

        public void Stop()
        {
            if (_geolocator == null)
            {
                return;
            }

            try
            {
                _geolocator.PositionChanged -= OnPositionChanged;
                _geolocator.StatusChanged -= OnStatusChanged;
            }
            catch (Exception)
            {
            }
            _geolocator = null;
            Status = "stopped";
        }

        // The position is of no interest; the handler exists because an unsubscribed
        // Geolocator does not keep the app alive.
        private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args)
        {
            Reports++;
            Status = "tracking (" + Reports + ")";
        }

        private void OnStatusChanged(Geolocator sender, StatusChangedEventArgs args)
        {
            Status = args.Status.ToString();
        }
    }
}
