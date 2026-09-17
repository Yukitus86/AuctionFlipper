using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AuctionFlipper.Core;

namespace AuctionFlipper.Ui;

/// <summary>
/// The alert popup.
///
/// This is a plain always-on-top window rather than a Windows toast on purpose. A real toast needs
/// the Windows SDK projections, which would drag a package reference into an otherwise dependency-
/// free project, and it would look nothing like the rest of the tool. A small window costs nothing,
/// matches the board, and can be clicked to jump straight to the flip.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;

    public ToastWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _dismissTimer.Tick += (_, _) => FadeOut();

        MouseLeftButtonDown += (_, _) => Clicked?.Invoke();
        MouseEnter += (_, _) => _dismissTimer.Stop();
        MouseLeave += (_, _) => _dismissTimer.Start();
    }

    public event Action? Clicked;

    public void ShowFlip(FlipOpportunity flip, bool copied)
    {
        Chip.Hue = flip.Info.Hue;
        Chip.Monogram = Format.Monogram(flip.Info.DisplayName);

        TitleText.Text = flip.Count > 1
            ? $"{flip.Info.DisplayName} x{flip.Count}"
            : flip.Info.DisplayName;

        DetailText.Text =
            $"buy {Format.Coins(flip.BuyTotal)} - relist {Format.Coins(flip.ResellTotal)} - "
            + $"ROI {Format.Percent(flip.Roi)} - sells {Format.Absorb(flip.AbsorbHours)}";

        HintText.Text = copied
            ? $"\"{flip.SearchText}\" copied - paste it into /ah"
            : $"search /ah for {flip.SearchText}";

        GradeText.Text = Format.Grade(flip.Grade);
        GradeText.Foreground = flip.Grade switch
        {
            FlipGrade.S => new SolidColorBrush(Color.FromRgb(0xC7, 0x7D, 0xFF)),
            FlipGrade.A => new SolidColorBrush(Color.FromRgb(0x2E, 0xD4, 0x7A)),
            FlipGrade.B => new SolidColorBrush(Color.FromRgb(0x35, 0xC2, 0xE4)),
            _ => new SolidColorBrush(Color.FromRgb(0x7A, 0x87, 0x98)),
        };

        NetText.Text = Format.Signed(flip.NetProfit);

        PositionBottomRight();

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    private void PositionBottomRight()
    {
        // Working area, so the popup never sits under the taskbar.
        Rect area = SystemParameters.WorkArea;
        Left = area.Right - Width - 8;
        Top = area.Bottom - ActualHeight - 8;

        if (ActualHeight <= 0)
        {
            // Height is unknown until the first layout pass; correct it once measured.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => Top = SystemParameters.WorkArea.Bottom - ActualHeight - 8));
        }
    }

    public void FadeOut()
    {
        _dismissTimer.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fade);
    }
}
