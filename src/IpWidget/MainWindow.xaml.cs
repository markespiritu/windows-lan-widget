using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace IpWidget;

public partial class MainWindow : Window
{
    private static readonly Brush IdleBorder = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromArgb(0x99, 0x6C, 0xB8, 0xFF));
    private static readonly Brush LightCardBackground = new SolidColorBrush(Color.FromArgb(0xB8, 0xF2, 0xF2, 0xF2));
    private static readonly Brush CardBackground = new SolidColorBrush(Color.FromArgb(0xA6, 0x14, 0x14, 0x14));
    // Text-only mode while Shift+Alt is held: a faint panel so you can see what you're grabbing.
    private static readonly Brush GhostBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x14, 0x14, 0x14));
    private static readonly System.Windows.Media.Effects.DropShadowEffect TextShadow = new()
    {
        Color = Colors.Black, BlurRadius = 3, ShadowDepth = 0, Opacity = 1,
        RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
    };
    private static readonly System.Windows.Media.Effects.DropShadowEffect HaloShadow = new()
    {
        Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
    };

    private readonly int _slot;
    private readonly WidgetSettings _settings;
    private readonly DispatcherTimer _keyTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _debounceTimer;

    private IntPtr _hwnd;
    private bool _interactive;
    private bool _menuOpen;
    private bool _dragging;

    // Resizing: Shift+Alt + mouse wheel, or Shift+Alt + drag the bottom-right corner.
    private const double MinScale = 0.6, MaxScale = 3.0, GripSize = 18;
    private bool _resizing;
    private double _resizeStartScale, _resizeStartWidth, _resizeStartX;

    internal MainWindow(int slot)
    {
        _slot = slot;
        _settings = WidgetSettings.Load(slot);
        InitializeComponent();

        Topmost = _settings.KeepOnTop;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _settings.Left ?? -10000;   // final placement happens in OnLoaded, once the size is known
        Top = _settings.Top ?? -10000;
        Title = slot == 1 ? "IP Widget" : $"IP Widget (slot {slot})";

        // Poll Shift+Alt: holding them makes the widget clickable/draggable, otherwise clicks pass through.
        _keyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => UpdateInteractive(), Dispatcher);
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _debounceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { _debounceTimer!.Stop(); Refresh(); }, Dispatcher);
        _debounceTimer.Stop();

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        MouseLeftButtonDown += OnLeftDown;
        MouseRightButtonUp += OnRightUp;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseWheel += OnWheel;
        Loaded += OnLoaded;

        ApplyScale(_settings.Scale);
        ApplyLook();

        StartupShortcut.RefreshIfPresent(slot);
        Refresh();
    }

    // ---------- window plumbing ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetExStyle(_hwnd,
            add: NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED,
            remove: 0);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        SendToBottom();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Pin to the bottom of the z-order so it sits on the desktop under every other window.
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && !_settings.KeepOnTop && !_forceOnTop)
        {
            var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            if ((wp.flags & NativeMethods.SWP_NOZORDER) == 0)
            {
                wp.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private void SendToBottom()
    {
        if (_hwnd == IntPtr.Zero || _settings.KeepOnTop || _forceOnTop) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow();
        _keyTimer.Start();
        _refreshTimer.Start();
    }

    private void PlaceWindow()
    {
        if (_settings.Left is double l && _settings.Top is double t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
            return;
        }
        PlaceDefault();
    }

    private void PlaceDefault()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Top + 24 + (_slot - 1) * (ActualHeight + 12);
        if (Top + ActualHeight > area.Bottom) Top = area.Top + 24;
    }

    private bool IsOnScreen(double left, double top)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        double w = Math.Max(ActualWidth, 100), h = Math.Max(ActualHeight, 60);
        // At least 40px of the widget must be visible.
        return left + w > vl + 40 && left < vr - 40 && top + h > vt + 40 && top < vb - 40;
    }

    // ---------- click-through / Shift+Alt interaction ----------

    private void UpdateInteractive()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool want = NativeMethods.IsModifierDown() || _menuOpen || _dragging || _resizing;
        if (want == _interactive) return;
        _interactive = want;

        if (want)
        {
            NativeMethods.SetExStyle(_hwnd, 0, NativeMethods.WS_EX_TRANSPARENT);
            InstallWheelHook();
        }
        else
        {
            NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT, 0);
            RemoveWheelHook();
        }

        ApplyLook();
    }

    // ---------- look ----------

    internal static Color ParseColor(string? hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? "#FFFFFF"); }
        catch { return Colors.White; }
    }

    private void ApplyLook()
    {
        var text = ParseColor(_settings.TextColor);
        // Dark text gets a light panel / light halo; light text gets dark ones.
        bool darkText = (0.2126 * text.R + 0.7152 * text.G + 0.0722 * text.B) / 255.0 < 0.5;
        var primary = new SolidColorBrush(text);
        var secondary = new SolidColorBrush(Color.FromArgb(0xB3, text.R, text.G, text.B));
        HostText.Foreground = IpText.Foreground = primary;
        TextShadow.Color = HaloShadow.Color = darkText ? Colors.White : Colors.Black;

        if (_settings.ShowBackground)
        {
            Card.Background = darkText ? LightCardBackground : CardBackground;
            Card.BorderBrush = _interactive ? ActiveBorder : IdleBorder;
            Body.Effect = null;
            Halo.Effect = null;
            LinkText.Foreground = MacText.Foreground = secondary;
        }
        else
        {
            // No panel behind the text: full strength everywhere so the halo can carry it on any wallpaper.
            LinkText.Foreground = MacText.Foreground = primary;
            Card.Background = _interactive ? GhostBackground : Brushes.Transparent;
            Card.BorderBrush = _interactive ? ActiveBorder : Brushes.Transparent;
            Body.Effect = _settings.TextShadow ? TextShadow : null;
            Halo.Effect = _settings.TextShadow ? HaloShadow : null;
        }
    }

    // ---------- resizing ----------

    private void ApplyScale(double scale)
    {
        scale = Math.Round(Math.Clamp(scale, MinScale, MaxScale), 2);
        _settings.Scale = scale;
        Card.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        // Pixel-snapped text looks best at 100%; scaled text needs ideal layout to stay smooth.
        TextOptions.SetTextFormattingMode(this, Math.Abs(scale - 1.0) < 0.001 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
    }

    private bool InGrip(Point p) => p.X >= ActualWidth - GripSize && p.Y >= ActualHeight - GripSize;

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_interactive) return;
        e.Handled = true;
        StepScale(e.Delta);
    }

    private void StepScale(int delta)
    {
        NativeMethods.MaskModifierRelease();
        ApplyScale(_settings.Scale + (delta > 0 ? 0.1 : -0.1));
        _settings.Save(_slot);
    }

    // Only hooked while Shift+Alt is held, and only wheel events over the widget are taken.
    private IntPtr _wheelHook;
    private NativeMethods.LowLevelMouseProc? _wheelProc;

    private void InstallWheelHook()
    {
        if (_wheelHook != IntPtr.Zero) return;
        _wheelProc ??= WheelHookProc;
        _wheelHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _wheelProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveWheelHook()
    {
        if (_wheelHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_wheelHook);
        _wheelHook = IntPtr.Zero;
    }

    private IntPtr WheelHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == NativeMethods.WM_MOUSEWHEEL && _interactive
            && NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            // Must be over the widget AND the widget must be the visible window there (not covered by another app).
            if (info.pt.X >= r.Left && info.pt.X < r.Right && info.pt.Y >= r.Top && info.pt.Y < r.Bottom
                && NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(info.pt), NativeMethods.GA_ROOT) == _hwnd)
            {
                int delta = (short)(info.mouseData >> 16);
                Dispatcher.BeginInvoke(() => StepScale(delta));
                return new IntPtr(1); // swallow it so the window underneath doesn't scroll/zoom
            }
        }
        return NativeMethods.CallNextHookEx(_wheelHook, nCode, wParam, lParam);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_resizing)
        {
            // The top-left corner stays put, so the new width is simply where the mouse is plus the grab offset.
            double newWidth = Math.Max(40, p.X + (_resizeStartWidth - _resizeStartX));
            ApplyScale(_resizeStartScale * newWidth / _resizeStartWidth);
            return;
        }
        Cursor = _interactive && InGrip(p) ? Cursors.SizeNWSE : null;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        ReleaseMouseCapture();
        _settings.Save(_slot);
        SendToBottom();
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive) return;

        var p = e.GetPosition(this);
        if (InGrip(p))
        {
            NativeMethods.MaskModifierRelease();
            _resizing = true;
            _resizeStartScale = _settings.Scale;
            _resizeStartWidth = ActualWidth;
            _resizeStartX = p.X;
            CaptureMouse();
            return;
        }

        NativeMethods.MaskModifierRelease();
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _dragging = false; }

        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save(_slot);
        SendToBottom();
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive) return;
        e.Handled = true;
        NativeMethods.MaskModifierRelease();
        var menu = BuildMenu();
        menu.PlacementTarget = Card;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => { _menuOpen = false; RemoveEscHook(); _openMenu = null; SendToBottom(); };
        _menuOpen = true;
        _openMenu = menu;
        menu.IsOpen = true;
        InstallEscHook();
    }

    // The widget never takes keyboard focus, so Esc would go to whatever app is in front.
    // While the menu is open, a low-level keyboard hook catches Esc and closes it.
    private ContextMenu? _openMenu;
    private IntPtr _escHook;
    private NativeMethods.LowLevelMouseProc? _escProc;

    private void InstallEscHook()
    {
        if (_escHook != IntPtr.Zero) return;
        _escProc ??= EscHookProc;
        _escHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _escProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveEscHook()
    {
        if (_escHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_escHook);
        _escHook = IntPtr.Zero;
    }

    private IntPtr EscHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == NativeMethods.WM_KEYDOWN && _openMenu != null
            && Marshal.ReadInt32(lParam) == NativeMethods.VK_ESCAPE)
        {
            var menu = _openMenu;
            Dispatcher.BeginInvoke(() => menu.IsOpen = false);
            return new IntPtr(1); // swallow it
        }
        return NativeMethods.CallNextHookEx(_escHook, nCode, wParam, lParam);
    }

    // ---------- menu ----------

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = $"IP Widget  (slot {_slot})", IsEnabled = false });
        menu.Items.Add(new Separator());

        var adapterMenu = new MenuItem { Header = "Adapter" };
        var auto = new MenuItem { Header = "Automatic (primary adapter)", IsCheckable = true, IsChecked = string.IsNullOrEmpty(_settings.AdapterId) };
        auto.Click += (_, _) => SelectAdapter(null);
        adapterMenu.Items.Add(auto);
        adapterMenu.Items.Add(new Separator());
        foreach (var n in NetworkInfo.Candidates())
        {
            var info = NetworkInfo.Describe(n);
            var item = new MenuItem
            {
                Header = $"{info.Name}  ({info.Kind}, {(info.IsUp ? "connected" : "disconnected")})",
                ToolTip = info.Description,
                IsCheckable = true,
                IsChecked = _settings.AdapterId == info.Id,
            };
            string id = info.Id;
            item.Click += (_, _) => SelectAdapter(id);
            adapterMenu.Items.Add(item);
        }
        menu.Items.Add(adapterMenu);

        var onTop = new MenuItem { Header = "Keep on top of other windows", IsCheckable = true, IsChecked = _settings.KeepOnTop };
        onTop.Click += (_, _) =>
        {
            _settings.KeepOnTop = !_settings.KeepOnTop;
            _settings.Save(_slot);
            Topmost = _settings.KeepOnTop;
            SendToBottom();
        };
        menu.Items.Add(onTop);

        menu.Items.Add(FieldsMenu());
        menu.Items.Add(IpFormatMenu());
        var text = new MenuItem { Header = "Font, style, color and spacing…", FontWeight = FontWeights.SemiBold };
        text.Click += (_, _) => Dispatcher.BeginInvoke(OpenTextSettings);
        menu.Items.Add(text);

        var background = new MenuItem { Header = "Show background", IsCheckable = true, IsChecked = _settings.ShowBackground };
        background.Click += (_, _) =>
        {
            _settings.ShowBackground = !_settings.ShowBackground;
            _settings.Save(_slot);
            ApplyLook();
        };
        menu.Items.Add(background);

        var shadow = new MenuItem
        {
            Header = "Text shadow (when background is off)",
            IsCheckable = true,
            IsChecked = _settings.TextShadow,
            IsEnabled = !_settings.ShowBackground,
        };
        shadow.Click += (_, _) =>
        {
            _settings.TextShadow = !_settings.TextShadow;
            _settings.Save(_slot);
            ApplyLook();
        };
        menu.Items.Add(shadow);

        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = StartupShortcut.Exists(_slot) };
        startup.Click += (_, _) =>
        {
            try
            {
                if (StartupShortcut.Exists(_slot)) StartupShortcut.Remove(_slot);
                else StartupShortcut.Create(_slot);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't update the startup shortcut:\n" + ex.Message, "IP Widget");
            }
        };
        menu.Items.Add(startup);

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += (_, _) => Refresh();
        menu.Items.Add(refresh);

        var resetSize = new MenuItem { Header = "Reset size" };
        resetSize.Click += (_, _) =>
        {
            ApplyScale(1.0);
            _settings.Save(_slot);
        };
        menu.Items.Add(resetSize);

        var reset = new MenuItem { Header = "Reset position" };
        reset.Click += (_, _) =>
        {
            _settings.Left = _settings.Top = null;
            _settings.Save(_slot);
            PlaceDefault();
        };
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);

        return menu;
    }

    private void SelectAdapter(string? id)
    {
        _settings.AdapterId = id;
        _settings.Save(_slot);
        Refresh();
    }

    // ---------- data ----------

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _debounceTimer.Stop(); _debounceTimer.Start(); });

    private void Refresh()
    {
        var candidates = NetworkInfo.Candidates();
        NetworkInterface? nic = string.IsNullOrEmpty(_settings.AdapterId)
            ? NetworkInfo.Primary(candidates)
            : candidates.FirstOrDefault(n => n.Id == _settings.AdapterId);

        _info = nic == null ? null : NetworkInfo.Describe(nic);
        ApplyFields();
    }

    private AdapterInfo? _info;

    /// <summary>Fills the text and hides whatever the user switched off; empty rows collapse on their own.</summary>
    private void ApplyFields()
    {
        var s = _settings;
        HostText.Visibility = s.HideHostname ? Visibility.Collapsed : Visibility.Visible;
        MacText.Visibility = s.HideMac ? Visibility.Collapsed : Visibility.Visible;

        ApplyFont(s.FontFamily);
        HostText.Text = FormatHostname(s.HostnameCase);
        HostText.FontSize = s.HostnameSize;
        IpText.FontSize = s.IpSize;
        MacText.FontSize = s.MacSize;
        StyleText(HostText, s.HostnameStyle ?? new FieldStyle { Weight = "SemiBold" });
        StyleText(IpText, s.IpStyle ?? new FieldStyle());
        StyleText(MacText, s.MacStyle ?? new FieldStyle());

        if (_info == null)
        {
            // Always show why there's nothing, even if the IP field is hidden.
            IpText.Text = string.IsNullOrEmpty(s.AdapterId) ? "No network adapter" : "Adapter not found";
            IpText.Visibility = Visibility.Visible;
            MacText.Text = "";
            LinkText.Text = "";
            LinkText.Visibility = Visibility.Collapsed;
            return;
        }

        IpText.Text = FormatIp(_info.Ip, s.IpFormat);
        IpText.Visibility = s.HideIp ? Visibility.Collapsed : Visibility.Visible;
        MacText.Text = _info.Mac;

        // Type and speed share a line but can have different sizes, so they're separate runs.
        LinkText.Inlines.Clear();
        if (!s.HideType)
        {
            var run = new System.Windows.Documents.Run(_info.Kind) { FontSize = s.TypeSize };
            StyleText(run, s.TypeStyle ?? new FieldStyle());
            LinkText.Inlines.Add(run);
        }
        if (!s.HideType && !s.HideSpeed)
            LinkText.Inlines.Add(new System.Windows.Documents.Run(" · ") { FontSize = Math.Min(s.TypeSize, s.SpeedSize) });
        if (!s.HideSpeed)
        {
            var run = new System.Windows.Documents.Run(_info.Speed) { FontSize = s.SpeedSize };
            StyleText(run, s.SpeedStyle ?? new FieldStyle());
            LinkText.Inlines.Add(run);
        }
        LinkText.Visibility = s.HideType && s.HideSpeed ? Visibility.Collapsed : Visibility.Visible;

        // No hostname: let the IP span both rows (vertically centred) instead of sitting under an empty gap.
        if (s.HideHostname)
        {
            Grid.SetRow(IpText, 0);
            Grid.SetRowSpan(IpText, 2);
        }
        else
        {
            Grid.SetRow(IpText, 1);
            Grid.SetRowSpan(IpText, 1);
        }

        // Keep the two columns apart only when both sides of a row are showing.
        // Gaps are user-adjustable (Spacing menu): rows only get a gap when there's a row above them.
        double col = s.ColumnGap, row = s.RowGap;
        bool topRowShown = HostText.Visibility == Visibility.Visible || LinkText.Visibility == Visibility.Visible;
        HostText.Margin = new Thickness(0, 0, LinkText.Visibility == Visibility.Visible ? col : 0, 0);
        IpText.Margin = new Thickness(0, s.HideHostname || !topRowShown ? 0 : row, MacText.Visibility == Visibility.Visible ? col : 0, 0);
        MacText.Margin = new Thickness(0, topRowShown ? row : 0, 0, 0);
    }

    // ---------- font & hostname case ----------

    private static readonly FontFamily DefaultFont = new("Segoe UI");
    private static readonly FontFamily DefaultMacFont = new("Consolas");

    /// <summary>Applies a font to every field (null = defaults). Also used for live preview while browsing.</summary>
    private void ApplyFont(string? family)
    {
        var font = string.IsNullOrEmpty(family) ? null : new FontFamily(family);
        HostText.FontFamily = IpText.FontFamily = LinkText.FontFamily = font ?? DefaultFont;
        MacText.FontFamily = font ?? DefaultMacFont;
    }

    internal static string FormatHostname(string mode)
    {
        // MachineName is the NetBIOS name (always upper case); the DNS host name keeps the case it was typed in.
        string asTyped;
        try { asTyped = System.Net.Dns.GetHostName(); } catch { asTyped = Environment.MachineName; }
        if (!asTyped.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !asTyped.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            asTyped = Environment.MachineName;

        return mode switch
        {
            "title" => asTyped.Length == 0 ? asTyped : char.ToUpperInvariant(asTyped[0]) + asTyped[1..].ToLowerInvariant(),
            "lower" => asTyped.ToLowerInvariant(),
            "asis" => asTyped,
            _ => asTyped.ToUpperInvariant(),
        };
    }

    internal static readonly double[] SizePresets = { 9, 10, 11, 12, 13, 14, 16, 17, 18, 20, 22, 24, 28, 32, 36, 42, 48, 56, 64, 72 };

    private bool _forceOnTop;

    /// <summary>
    /// Opens the text dialog. It edits the live settings so the widget previews every change;
    /// Cancel restores the snapshot taken here.
    /// </summary>
    private void OpenTextSettings()
    {
        var snapshot = _settings.Clone();
        var dialog = new TextSettingsWindow(_settings, InstalledFonts.Value, () => { ApplyLook(); ApplyFields(); });

        // Lift the widget above other windows while the dialog is open, so the preview is visible.
        _forceOnTop = true;
        Topmost = true;
        bool ok;
        try { ok = dialog.ShowDialog() == true; }
        finally
        {
            _forceOnTop = false;
            Topmost = _settings.KeepOnTop;
            SendToBottom();
        }

        if (ok)
            _settings.Save(_slot);
        else
            _settings.CopyTextSettingsFrom(snapshot);
        ApplyLook();
        ApplyFields();
    }

    // ---------- bold / italic / underline ----------

    internal static readonly string[] Weights = { "Light", "Normal", "Medium", "SemiBold", "Bold", "Black" };

    internal static FontWeight ToWeight(string? name) => name switch
    {
        "Light" => FontWeights.Light,
        "Medium" => FontWeights.Medium,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        "Black" => FontWeights.Black,
        _ => FontWeights.Normal,
    };

    private static void StyleText(System.Windows.Documents.TextElement el, FieldStyle st)
    {
        el.FontWeight = ToWeight(st.Weight);
        el.FontStyle = st.Italic ? FontStyles.Italic : FontStyles.Normal;
        if (el is System.Windows.Documents.Inline inline)
            inline.TextDecorations = st.Underline ? TextDecorations.Underline : null;
    }

    private static void StyleText(TextBlock tb, FieldStyle st)
    {
        tb.FontWeight = ToWeight(st.Weight);
        tb.FontStyle = st.Italic ? FontStyles.Italic : FontStyles.Normal;
        tb.TextDecorations = st.Underline ? TextDecorations.Underline : null;
    }

    private static readonly Lazy<List<string>> InstalledFonts = new(() =>
        Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(n => !n.StartsWith("@") && !n.Contains('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList());

    private static string FormatIp(string ip, string format)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return ip; // "No IPv4 address", "-", etc.
        return format switch
        {
            "last2" => $"{parts[2]}.{parts[3]}",
            "last1" => $".{parts[3]}",
            _ => ip,
        };
    }

    internal static readonly (string Name, string Hex)[] ColorPresets =
    {
        ("White", "#FFFFFF"), ("Light gray", "#C8C8C8"), ("Black", "#000000"), ("Yellow", "#FFE066"),
        ("Orange", "#FFA94D"), ("Green", "#69DB7C"), ("Cyan", "#66D9E8"), ("Blue", "#74C0FC"),
        ("Pink", "#F783AC"), ("Red", "#FF6B6B"),
    };

    private MenuItem IpFormatMenu()
    {
        string sample = _info?.Ip is string ip && ip.Split('.').Length == 4 ? ip : "192.168.26.218";
        var parent = new MenuItem { Header = "IP address format" };
        foreach (var (key, label) in new[] { ("full", "Full"), ("last2", "Last two parts"), ("last1", "Last part only") })
        {
            var item = new MenuItem
            {
                Header = $"{label}   {FormatIp(sample, key)}",
                IsCheckable = true,
                IsChecked = _settings.IpFormat == key || (key == "full" && _settings.IpFormat is not ("last2" or "last1")),
            };
            item.Click += (_, _) =>
            {
                _settings.IpFormat = key;
                _settings.Save(_slot);
                ApplyFields();
            };
            parent.Items.Add(item);
        }
        return parent;
    }

    private MenuItem FieldsMenu()
    {
        var fields = new (string Label, Func<bool> Hidden, Action<bool> SetHidden)[]
        {
            ("Hostname",    () => _settings.HideHostname, v => _settings.HideHostname = v),
            ("IP address",  () => _settings.HideIp,       v => _settings.HideIp = v),
            ("MAC address", () => _settings.HideMac,      v => _settings.HideMac = v),
            ("Type (LAN/WLAN)", () => _settings.HideType, v => _settings.HideType = v),
            ("Speed",       () => _settings.HideSpeed,    v => _settings.HideSpeed = v),
        };

        var parent = new MenuItem { Header = "Show fields" };
        var items = new List<MenuItem>();
        void UpdateEnabled()
        {
            // Don't allow hiding the last visible field, or the widget would vanish.
            int shown = fields.Count(f => !f.Hidden());
            for (int i = 0; i < fields.Length; i++)
                items[i].IsEnabled = !(shown == 1 && !fields[i].Hidden());
        }

        foreach (var f in fields)
        {
            var item = new MenuItem { Header = f.Label, IsCheckable = true, IsChecked = !f.Hidden(), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                f.SetHidden(!item.IsChecked);
                _settings.Save(_slot);
                ApplyFields();
                UpdateEnabled();
            };
            items.Add(item);
            parent.Items.Add(item);
        }
        UpdateEnabled();
        return parent;
    }

    protected override void OnClosed(EventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _keyTimer.Stop();
        _refreshTimer.Stop();
        RemoveWheelHook();
        base.OnClosed(e);
    }
}
