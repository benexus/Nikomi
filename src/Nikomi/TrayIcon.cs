using System.Runtime.InteropServices;
using Nikomi.Core;

namespace Nikomi;

/// <summary>
/// The notification-area icon, done with Shell_NotifyIcon on a hidden window (WinUI 3 has no tray API).
/// Left click opens the window, right click shows the menu, a click on a notification raises BalloonClicked.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public sealed record MenuItem(int Id, string Text, bool Checked = false, bool IsDefault = false, bool Separator = false);

    private const int CallbackMessage = 0x8001;   // WM_APP + 1
    private const uint IconId = 1;

    private readonly WndProcDelegate _wndProc;   // kept alive for the window's lifetime
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _iconNormal, _iconAlert;
    private bool _alert;
    private string _tip = "Nikomi";
    private bool _added;

    public event Action? LeftClicked;
    public event Action? BalloonClicked;
    public event Action<int>? MenuCommand;
    public Func<IReadOnlyList<MenuItem>>? MenuProvider { get; set; }

    public TrayIcon(string normalIconPath, string alertIconPath)
    {
        _wndProc = WndProc;
        IntPtr hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = "NikomiTrayWindow",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, "NikomiTrayWindow", "Nikomi tray", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        _iconNormal = LoadImage(IntPtr.Zero, normalIconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        _iconAlert = LoadImage(IntPtr.Zero, alertIconPath, 1, size, size, 0x10);
        TryEnableDarkMenus();
        Add();
    }

    public void SetAlert(bool alert)
    {
        if (alert == _alert) return;
        _alert = alert;
        var d = NewData(0x02 /* NIF_ICON */);
        Shell_NotifyIcon(1 /* NIM_MODIFY */, ref d);
    }

    public void SetTooltip(string text)
    {
        text = text.Length > 127 ? text[..127] : text;
        if (text == _tip) return;
        _tip = text;
        var d = NewData(0x04 | 0x80 /* NIF_TIP | NIF_SHOWTIP */);
        Shell_NotifyIcon(1, ref d);
    }

    /// <summary>Shows a notification (a toast on Windows 11).</summary>
    public void ShowNotification(string title, string text, bool warning = true)
    {
        var d = NewData(0x10 /* NIF_INFO */);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..252] + "..." : text;
        d.dwInfoFlags = warning ? 0x02u /* NIIF_WARNING */ : 0x01u /* NIIF_INFO */;
        Shell_NotifyIcon(1, ref d);
    }

    private void Add()
    {
        var d = NewData(0x01 | 0x02 | 0x04 | 0x80 /* MESSAGE | ICON | TIP | SHOWTIP */);
        _added = Shell_NotifyIcon(0 /* NIM_ADD */, ref d);
        d.uVersionOrTimeout = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIcon(4 /* NIM_SETVERSION */, ref d);
    }

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _alert ? _iconAlert : _iconNormal,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Windows calls this directly. An exception that escaped from here would end the whole app, and
        // the app-wide handler never sees it, so it is caught and logged here.
        try
        {
            return HandleMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Write("Tray icon error: " + ex);
            return IntPtr.Zero;
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            int evt = (int)((long)lParam & 0xFFFF);
            switch (evt)
            {
                case 0x0400: // NIN_SELECT (left click)
                case 0x0401: // NIN_KEYSELECT
                    LeftClicked?.Invoke();
                    break;
                case 0x007B: // WM_CONTEXTMENU (right click)
                    ShowMenu((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF));
                    break;
                case 0x0405: // NIN_BALLOONUSERCLICK
                    BalloonClicked?.Invoke();
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Add(); // Explorer restarted: put the icon back.
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = MenuProvider?.Invoke();
        if (items == null || items.Count == 0) return;
        IntPtr menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.Separator) { AppendMenu(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null); continue; }
                AppendMenu(menu, item.Checked ? 0x8u /* MF_CHECKED */ : 0u, (UIntPtr)item.Id, item.Text);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)item.Id, false);
            }
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(menu, 0x0100 | 0x0002 | 0x0080 /* RETURNCMD | RIGHTBUTTON | NONOTIFY */, x, y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0) MenuCommand?.Invoke(cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Lets the Win32 popup menu follow dark mode. Windows has no documented way to do this: these are
    /// uxtheme exports 135 and 136, known by number only, which Explorer uses too. They have had this
    /// meaning since Windows 10 1903 (build 18362), so older builds are left with the light menu. To use
    /// documented calls only, remove this method: the only effect is a light menu in dark mode.
    /// </summary>
    private static void TryEnableDarkMenus()
    {
        if (Environment.OSVersion.Version.Build < DarkMenuMinBuild) return;
        try
        {
            SetPreferredAppMode(1 /* AllowDark */);
            FlushMenuThemes();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = NewData(0);
            Shell_NotifyIcon(2 /* NIM_DELETE */, ref d);
            _added = false;
        }
        DestroyWindow(_hwnd);
        if (_iconNormal != IntPtr.Zero) DestroyIcon(_iconNormal);
        if (_iconAlert != IntPtr.Zero) DestroyIcon(_iconAlert);
    }

    // ---------------------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, bool byPos);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    private const int DarkMenuMinBuild = 18362;
    // Loaded from System32 only, never from the app folder or the search path.
    [DllImport("uxtheme.dll", EntryPoint = "#135"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void FlushMenuThemes();
}
