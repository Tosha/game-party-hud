using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GamePartyHud.Hud;

public partial class HudWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<HudMember> MemberList { get; } = new();

    private int _columnCount = 1;

    /// <summary>
    /// Number of columns the HUD should render (1 for parties of ≤10, 2 for 11–20).
    /// Bound by <c>HudWindow.xaml</c>'s <c>ColumnMajorUniformGrid.Columns</c>.
    /// </summary>
    public int ColumnCount
    {
        get => _columnCount;
        private set
        {
            if (_columnCount == value) return;
            _columnCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColumnCount)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RecomputeColumnCount()
    {
        ColumnCount = MemberList.Count > 10 ? 2 : 1;
    }

    private bool _isLocked = true;
    public bool IsLocked => _isLocked;

    private HudMember? _dragSource;
    private Point _dragStart;
    private const double DragThreshold = 4.0;

    /// <summary>Raised when the user picks "Kick from party" on a card's context menu.</summary>
    public event Action<string>? KickRequested;

    public HudWindow()
    {
        InitializeComponent();
        Members.ItemsSource = MemberList;
        MemberList.CollectionChanged += OnMemberListChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => UpdateLockVisual();
    }

    private void OnMemberListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RecomputeColumnCount();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HitTestInterop.ApplyExtendedStyles(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != HitTestInterop.WM_NCHITTEST) return IntPtr.Zero;

        handled = true;
        if (!_isLocked)
        {
            return new IntPtr(HitTestInterop.HTCLIENT);
        }

        int sx = HitTestInterop.LoWord(lParam);
        int sy = HitTestInterop.HiWord(lParam);

        try
        {
            var clientPt = PointFromScreen(new Point(sx, sy));
            if (IsOverLockButton(clientPt))
            {
                return new IntPtr(HitTestInterop.HTCLIENT);
            }
        }
        catch
        {
            // PointFromScreen can throw if the window isn't fully set up — fall through.
        }
        return new IntPtr(HitTestInterop.HTTRANSPARENT);
    }

    private bool IsOverLockButton(Point clientPt)
    {
        if (LockButton.ActualWidth <= 0 || LockButton.ActualHeight <= 0) return false;
        var origin = LockButton.TranslatePoint(new Point(0, 0), this);
        var rect = new Rect(origin, new Size(LockButton.ActualWidth, LockButton.ActualHeight));
        return rect.Contains(clientPt);
    }

    private void OnLockButtonClick(object sender, RoutedEventArgs e)
    {
        _isLocked = !_isLocked;
        UpdateLockVisual();
    }

    private void UpdateLockVisual()
    {
        LockGlyph.Text = _isLocked ? "🔒" : "🔓";
        RootBorder.BorderThickness = _isLocked ? new Thickness(0) : new Thickness(1);
    }

    /// <summary>Block-drag on empty area, begin-swap-drag on a card, in unlocked mode.</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_isLocked) return;
        if (IsWithinLockButton(e.OriginalSource)) return;

        _dragStart = e.GetPosition(this);
        _dragSource = MemberCardUnder(_dragStart);

        if (_dragSource is null)
        {
            DragMove();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isLocked || _dragSource is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(this);
        if ((pos - _dragStart).Length < DragThreshold) return;

        var target = MemberCardUnder(pos);
        if (target is null || ReferenceEquals(target, _dragSource)) return;

        int si = MemberList.IndexOf(_dragSource);
        int ti = MemberList.IndexOf(target);
        if (si >= 0 && ti >= 0 && si != ti)
        {
            MemberList.Move(si, ti);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragSource = null;
    }

    private HudMember? MemberCardUnder(Point clientPt)
    {
        var result = VisualTreeHelper.HitTest(this, clientPt);
        if (result?.VisualHit is null) return null;
        DependencyObject? d = result.VisualHit;
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.DataContext is HudMember m) return m;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private bool IsWithinLockButton(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (ReferenceEquals(d, LockButton)) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private static HudMember? MemberFromContextMenuSender(object sender)
    {
        // ContextMenu is a popup tree outside the main visual tree, so DataContext
        // doesn't auto-flow. Walk up from the MenuItem to the ContextMenu, then
        // use PlacementTarget (the element that owns the menu) to find the HudMember.
        if (sender is not MenuItem mi) return null;
        DependencyObject? current = mi;
        while (current is not null && current is not System.Windows.Controls.ContextMenu)
            current = LogicalTreeHelper.GetParent(current);
        if (current is not System.Windows.Controls.ContextMenu cm) return null;
        if (cm.PlacementTarget is not FrameworkElement target) return null;
        return target.DataContext as HudMember;
    }

    private void OnKickClick(object sender, RoutedEventArgs e)
    {
        if (MemberFromContextMenuSender(sender) is { } m)
        {
            KickRequested?.Invoke(m.PeerId);
        }
    }
}
