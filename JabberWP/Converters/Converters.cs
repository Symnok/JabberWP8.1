using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using JabberWP.Core;

namespace JabberWP.Converters
{
    // Silverlight's IValueConverter takes a CultureInfo where the WinRT one takes a
    // language string - the only difference from the WinRT versions of these.

    /// <summary>Right-aligns our own messages, left-aligns the other party's.</summary>
    public class OutgoingToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool outgoing = value is bool && (bool)value;
            return outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Bubble colour: accent for our messages, neutral grey for theirs.</summary>
    public class OutgoingToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool outgoing = value is bool && (bool)value;
            if (outgoing)
            {
                object accent = Application.Current.Resources["PhoneAccentBrush"];
                if (accent != null)
                {
                    return accent;
                }
                return new SolidColorBrush(Color.FromArgb(255, 0, 114, 198));
            }
            return new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Availability dot: green means reachable now, amber idle or busy, grey offline.
    /// </summary>
    public class PresenceToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Presence presence = value is Presence ? (Presence)value : Presence.Offline;
            switch (presence)
            {
                case Presence.Online:
                case Presence.Chat:
                    return new SolidColorBrush(Color.FromArgb(255, 76, 175, 80));
                case Presence.Away:
                case Presence.ExtendedAway:
                    return new SolidColorBrush(Color.FromArgb(255, 255, 170, 0));
                case Presence.DoNotDisturb:
                    return new SolidColorBrush(Color.FromArgb(255, 214, 69, 65));
                default:
                    return new SolidColorBrush(Color.FromArgb(255, 90, 90, 90));
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool visible = value is bool && (bool)value;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }
}
