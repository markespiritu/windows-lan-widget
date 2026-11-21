using System.Runtime.InteropServices;

namespace IpWidget;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const int VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>Index of the interface Windows would use to reach the given IPv4 address (network byte order).</summary>
    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    // Low-level mouse hook: the widget never takes focus, so Windows doesn't reliably send it wheel messages.
    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GA_ROOT = 2;

    // Standard Windows colour picker (comdlg32), so we don't need to pull in WinForms.
    [StructLayout(LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [DllImport("comdlg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref CHOOSECOLOR cc);

    private const int CC_RGBINIT = 0x1, CC_FULLOPEN = 0x2;
    private static readonly int[] CustomColors = new int[16];

    public static System.Windows.Media.Color? PickColor(IntPtr owner, System.Windows.Media.Color initial)
    {
        var handle = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
        try
        {
            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = owner,
                rgbResult = initial.R | (initial.G << 8) | (initial.B << 16),
                lpCustColors = handle.AddrOfPinnedObject(),
                Flags = CC_RGBINIT | CC_FULLOPEN,
            };
            if (!ChooseColor(ref cc)) return null;
            int c = cc.rgbResult;
            return System.Windows.Media.Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally { handle.Free(); }
    }

    public const int VK_MENU = 0x12, VK_SHIFT = 0x10;

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>
    /// Taps an unassigned key (VK 0xE8). Once Shift+Alt have been used for a click, this stops Windows
    /// from treating the Alt release as "focus the menu bar" in the app in front.
    /// </summary>
    public static void MaskModifierRelease()
    {
        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        keybd_event(0xE8, 0, 2, UIntPtr.Zero);
    }

    /// <summary>The "edit the widget" modifier: Shift + Alt held together (works through Moonlight, unlike Win).</summary>
    public static bool IsModifierDown() => Down(VK_SHIFT) && Down(VK_MENU);

    public static void SetExStyle(IntPtr hwnd, int add, int remove)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long next = (style | (uint)add) & ~(long)(uint)remove;
        if (next != style)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
    }
}
