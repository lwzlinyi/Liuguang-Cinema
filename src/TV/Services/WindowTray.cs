using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TV.Services;

internal sealed class WindowTray : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private const uint WmSize = 0x0005;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmSystemCommand = 0x0112;
    private const uint MinimizeCommand = 0xF020;
    private const uint SizeMinimized = 1;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint OpenItem = 1;
    private const uint ExitItem = 2;
    private readonly nint _window;
    private nint _host;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly SubclassProc _callback;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _restore;
    private readonly Action _exit;
    private NotifyIconData _data;
    private bool _registered;
    private bool _disposed;
    private bool _subclassed;
    private bool _hidden;
    private bool _wasMaximized;

    public WindowTray(nint window, DispatcherQueue dispatcher, string title, Action restore, Action exit)
    {
        _window = window;
        _dispatcher = dispatcher;
        _restore = restore;
        _exit = exit;
        _callback = OnMessage;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (Environment.ProcessPath is { } executable)
            ExtractIconEx(executable, 0, nint.Zero, out _icon, 1);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = IconId,
            Flags = 0x01 | 0x02 | 0x04 | 0x80, Message = CallbackMessage,
            Icon = _icon, ToolTip = title, Info = string.Empty, InfoTitle = string.Empty
        };
        _subclassed = SetWindowSubclass(window, _callback, IconId, 0);
    }

    /// <summary>备用托盘宿主窗口：某些窗口（如 WinUI 顶层窗）在某些会话下不被 Shell 接受。</summary>
    private nint EnsureHost()
    {
        if (_host != 0) return _host;
        var host = CreateWindowEx(0, "STATIC", "TVTrayHost", 0, 0, 0, 0, 0, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        if (host == 0 || !SetWindowSubclass(host, _callback, IconId + 1, 0))
        {
            if (host != 0) DestroyWindow(host);
            return 0;
        }
        _host = host;
        return _host;
    }

    public bool Enabled { get; set; } = true;
    public bool IsHidden => _hidden;
    public bool IsEndingSession { get; private set; }
    public event EventHandler? VisibilityChanged;

    public void Restore()
    {
        if (_disposed) return;
        if (_hidden)
        {
            _hidden = false;
            RemoveIcon();
            ShowWindow(_window, _wasMaximized ? 3 : 9);
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (IsIconic(_window)) ShowWindow(_window, 9);
        SetForegroundWindow(_window);
        _restore();
    }

    public bool TryMinimizeFromClose() => Hide(requireMinimizeSetting: false);

    private bool Hide(bool requireMinimizeSetting = true)
    {
        if (_disposed || _hidden || requireMinimizeSetting && !Enabled || !AddIcon())
        {
            if (!_disposed && !_hidden)
                Diagnostics.Log($"窗口未收起到托盘: enabled={Enabled} subclassed={_subclassed} icon={_icon != 0}");
            return false;
        }
        _wasMaximized = IsZoomed(_window);
        _hidden = true;
        ShowWindow(_window, 0);
        Diagnostics.Log("Window moved to tray");
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool AddIcon()
    {
        if (!_subclassed || _icon == 0) return false;
        if (_registered) return true;
        if (!ShellNotifyIcon(NimAdd, ref _data))
        {
            Diagnostics.Log($"托盘图标首试失败: size={_data.Size} window={_window} flags=0x{_data.Flags:x}");
            // 某些会话下 Shell 不接受 WinUI 顶层窗作为托盘宿主，改用自建宿主窗口重试。
            if (EnsureHost() is var host && host != 0)
            {
                _data.Window = host;
                if (ShellNotifyIcon(NimAdd, ref _data)) return RegisterVersion();
            }
            _data.Window = _window;
            _data.Flags = 0x01 | 0x02 | 0x04;
            if (!ShellNotifyIcon(NimAdd, ref _data))
            {
                Diagnostics.Log("托盘图标添加失败: 系统托盘不可用");
                return false;
            }
        }
        return RegisterVersion();
    }

    private bool RegisterVersion()
    {
        _data.Version = 4;
        if (ShellNotifyIcon(NimSetVersion, ref _data)) return _registered = true;
        Diagnostics.Log($"托盘图标版本设置失败: err={Marshal.GetLastWin32Error()}");
        ShellNotifyIcon(NimDelete, ref _data);
        return false;
    }

    private void RemoveIcon()
    {
        if (!_registered) return;
        ShellNotifyIcon(NimDelete, ref _data);
        _registered = false;
    }

    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmQueryEndSession) return 1;
        if (message == WmEndSession)
        {
            IsEndingSession = wParam != 0;
            if (IsEndingSession) _dispatcher.TryEnqueue(() => _exit());
            return 0;
        }
        if (message == WmSystemCommand && (wParam & 0xfff0) == MinimizeCommand && Hide())
            return 0;
        if (message == WmSize && wParam == SizeMinimized && Enabled)
            _dispatcher.TryEnqueue(() => { if (IsIconic(_window)) Hide(); });
        else if (message == _taskbarCreated && _taskbarCreated != 0 && _hidden)
        {
            _registered = false;
            if (!AddIcon()) _dispatcher.TryEnqueue(Restore);
        }
        else if (message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            if (notification is NinSelect or NinKeySelect) _dispatcher.TryEnqueue(Restore);
            if (notification == WmContextMenu)
            {
                var point = new Point { X = unchecked((short)(wParam & 0xFFFF)), Y = unchecked((short)((wParam >> 16) & 0xFFFF)) };
                _dispatcher.TryEnqueue(() => ShowMenu(point));
            }
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu(Point position)
    {
        if (_disposed || !_hidden) return;
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        uint command;
        try
        {
            AppendMenu(menu, 0, OpenItem, "打开PCbox");
            AppendMenu(menu, 0, ExitItem, "退出");
            SetMenuDefaultItem(menu, OpenItem, false);
            SetForegroundWindow(_window);
            command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, position.X, position.Y, _window, 0);
            PostMessage(_window, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
        if (command == OpenItem) Restore();
        else if (command == ExitItem) _exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        if (_subclassed) RemoveWindowSubclass(_window, _callback, IconId);
        if (_host != 0)
        {
            RemoveWindowSubclass(_host, _callback, IconId + 1);
            DestroyWindow(_host);
            _host = 0;
        }
        if (_icon != 0) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Message;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ToolTip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint msg, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string path, int index, nint large, out nint small, uint count);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint item, string title);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, bool byPosition);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
