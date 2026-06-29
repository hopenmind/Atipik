using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Atypik.Overlay;

public partial class BoundingBoxTool : Window
{
    // -- Win32 -----------------------------------------------------------------
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint GA_ROOT = 2;  // get root ancestor (top-level window)

    // -- State -----------------------------------------------------------------
    private Point  _origin;
    private bool   _dragging;
    private IntPtr _selfHWnd;

    /// <summary>Raised when the user confirms a selection. Null = cancelled.</summary>
    public event Action<FrameResult?>? FrameSelected;

    // -- Init ------------------------------------------------------------------

    public BoundingBoxTool()
    {
        InitializeComponent();

        // Span all monitors
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Pin hint bar width to screen width
        HintBorder.Width = Width;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancel();
        };

        Loaded += (_, _) =>
        {
            _selfHWnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        };
    }

    // -- Mouse drawing ---------------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _origin   = e.GetPosition(DrawCanvas);
        _dragging = true;
        DrawCanvas.CaptureMouse();

        SelectionRect.Visibility = Visibility.Visible;
        UpdateRect(_origin, _origin);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        Point current = e.GetPosition(DrawCanvas);
        UpdateRect(_origin, current);

        // Dimensions label
        double w = Math.Abs(current.X - _origin.X);
        double h = Math.Abs(current.Y - _origin.Y);
            DimText.Text = $"{(int)w} x {(int)h}";

        double lx = Math.Min(_origin.X, current.X);
        double ly = Math.Max(_origin.Y, current.Y) + 6;
        Canvas.SetLeft(DimLabel, lx);
        Canvas.SetTop(DimLabel,  ly);
        DimLabel.Visibility = w > 20 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DrawCanvas.ReleaseMouseCapture();

        Point end = e.GetPosition(DrawCanvas);
        Rect  selection = BuildRect(_origin, end);

        // Reject tiny accidental clicks
        if (selection.Width < 20 || selection.Height < 20)
        {
            Cancel();
            return;
        }

        Confirm(selection);
    }

    // -- Result ----------------------------------------------------------------

    private void Confirm(Rect canvasRect)
    {
        // Convert canvas coords -> screen coords
        Point screenOrigin = PointToScreen(
            DrawCanvas.TranslatePoint(canvasRect.TopLeft, this));

        Rect screenRect = new(
            screenOrigin.X, screenOrigin.Y,
            canvasRect.Width, canvasRect.Height);

        // Find target window at center of selection (excluding ourselves)
        IntPtr hwnd = GetTargetHWnd(screenRect);
        string title = GetWindowTitle(hwnd);

        Hide();
        FrameSelected?.Invoke(new FrameResult(screenRect, hwnd, title));
        Close();
    }

    private void Cancel()
    {
        Hide();
        FrameSelected?.Invoke(null);
        Close();
    }

    // -- Drawing helpers -------------------------------------------------------

    private void UpdateRect(Point a, Point b)
    {
        Rect r = BuildRect(a, b);

        Canvas.SetLeft(SelectionRect, r.Left);
        Canvas.SetTop(SelectionRect,  r.Top);
        SelectionRect.Width  = r.Width;
        SelectionRect.Height = r.Height;

        // Corner anchors
        PositionCorner(CornerTL, r.Left - 4, r.Top - 4);
        PositionCorner(CornerTR, r.Right - 4, r.Top - 4);
        PositionCorner(CornerBL, r.Left - 4, r.Bottom - 4);
        PositionCorner(CornerBR, r.Right - 4, r.Bottom - 4);

        CornerTL.Visibility = CornerTR.Visibility =
        CornerBL.Visibility = CornerBR.Visibility =
            r.Width > 10 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void PositionCorner(System.Windows.Shapes.Ellipse e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e,  y);
    }

    private static Rect BuildRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    // -- Win32 helpers ---------------------------------------------------------

    private IntPtr GetTargetHWnd(Rect screenRect)
    {
        // Sample center of selection
        var pt = new POINT
        {
            X = (int)(screenRect.Left + screenRect.Width  / 2),
            Y = (int)(screenRect.Top  + screenRect.Height / 2)
        };

        // Temporarily hide overlay so WindowFromPoint sees the target
        Hide();
        IntPtr child = WindowFromPoint(pt);
        Show();

        // Walk up to root window
        IntPtr root = GetAncestor(child, GA_ROOT);

        // Exclude ourselves
        return root == _selfHWnd ? child : root;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, 256);
        return sb.Length > 0 ? sb.ToString() : "(untitled window)";
    }
}
