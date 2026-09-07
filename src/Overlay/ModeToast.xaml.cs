using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Atypik.Core;
using Atypik.i18n;

namespace Atypik.Overlay;

public partial class ModeToast : Window
{
    private static ModeToast? _open;
    private DispatcherTimer? _timer;

    public ModeToast()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionTopCenter();
    }

    /// <summary>Show the mode name for 3 seconds, then auto-close. Thread-safe.</summary>
    public static void ShowFor(OutputMode mode)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => ShowFor(mode));
            return;
        }

        // Replace any toast already on screen.
        if (_open is not null)
        {
            _open._timer?.Stop();
            _open.Close();
            _open = null;
        }

        var toast = new ModeToast();
        toast.ModeLabel.Text = Loc.T(mode switch
        {
            OutputMode.Off        => "toast.mode_off",
            OutputMode.Poetry     => "toast.mode_poetry",
            _                     => "toast.mode_correction",
        });
        toast.Card.BorderBrush = mode switch
        {
            OutputMode.Off        => new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x80)),
            OutputMode.Poetry     => new SolidColorBrush(Color.FromRgb(0x45, 0xD6, 0xC5)),
            _                     => new SolidColorBrush(Color.FromRgb(0x7C, 0x36, 0xCC)),
        };
        toast.Show();
        _open = toast;

        toast._timer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(3) };
        toast._timer.Tick += (_, _) =>
        {
            toast._timer.Stop();
            toast.Close();
            if (ReferenceEquals(_open, toast)) _open = null;
        };
        toast._timer.Start();
    }

    private void PositionTopCenter()
    {
        // Centre horizontally near the top of the primary screen.
        double sw = SystemParameters.PrimaryScreenWidth;
        Left = (sw - ActualWidth) / 2.0;
        Top = SystemParameters.PrimaryScreenHeight * 0.08;
    }
}
