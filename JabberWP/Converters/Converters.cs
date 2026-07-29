using System;
using JabberWP.Core;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace JabberWP.Converters
{
    /// <summary>
    /// Right-aligns our own messages, left-aligns the other party's.
    /// WP8.1 has no x:Bind and no function bindings, so shaping a bound value for
    /// the view means a converter.
    /// </summary>
    public sealed class OutgoingToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool outgoing = value is bool && (bool)value;
            return outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Bubble colour: accent for our messages, neutral grey for theirs.</summary>
    public sealed class OutgoingToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
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

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Availability dot colour. Green means reachable right now; amber means the
    /// contact is connected but idle or busy; grey means offline.
    /// </summary>
    public sealed class PresenceToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
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

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool visible = value is bool && (bool)value;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }
}
