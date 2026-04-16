using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GamePartyHud.Capture;

namespace GamePartyHud.Calibration;

public partial class RegionSelectorWindow : Window
{
    public HpRegion? Result { get; private set; }
    private Point _start;
    private bool _dragging;

    public RegionSelectorWindow(string prompt)
    {
        InitializeComponent();
        Prompt.Text = prompt + "\n(Press Esc to cancel.)";
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void OnDown(object? s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(SelectionCanvas);
        Canvas.SetLeft(Selection, _start.X);
        Canvas.SetTop(Selection, _start.Y);
        Selection.Width = 0;
        Selection.Height = 0;
        Selection.Visibility = Visibility.Visible;
        _dragging = true;
        CaptureMouse();
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(SelectionCanvas);
        double x = Math.Min(_start.X, cur.X);
        double y = Math.Min(_start.Y, cur.Y);
        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = Math.Abs(cur.X - _start.X);
        Selection.Height = Math.Abs(cur.Y - _start.Y);
    }

    private void OnUp(object? s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        if (Selection.Width < 2 || Selection.Height < 2)
        {
            // Accidental click: ignore and let user try again.
            Selection.Visibility = Visibility.Collapsed;
            return;
        }

        // Convert selection (in WPF DIPs relative to the canvas) to physical screen pixels.
        var topLeftScreen = PointToScreen(new Point(Canvas.GetLeft(Selection), Canvas.GetTop(Selection)));
        var bottomRightScreen = PointToScreen(new Point(
            Canvas.GetLeft(Selection) + Selection.Width,
            Canvas.GetTop(Selection) + Selection.Height));

        Result = new HpRegion(
            Monitor: 0, // Single-monitor assumption for v0.1.0; the capture layer uses absolute coords.
            X: (int)Math.Round(topLeftScreen.X),
            Y: (int)Math.Round(topLeftScreen.Y),
            W: (int)Math.Round(bottomRightScreen.X - topLeftScreen.X),
            H: (int)Math.Round(bottomRightScreen.Y - topLeftScreen.Y));
        Close();
    }
}
