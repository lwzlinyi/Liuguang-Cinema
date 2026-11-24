using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TV.Services;

/// <summary>
/// 监听鼠标侧键（XButton1，通常是"后退"键）触发导航返回。
/// 使用独立的 subclass id（与 WindowTray 的 id=1 不同），两者可叠加在同一顶层窗口。
/// 消息回调发生在 UI 线程，直接把 GoBack 派发到 DispatcherQueue。
/// </summary>
internal sealed class MouseBackNavigation : IDisposable
{
    private const uint WmXButtonDown = 0x020B;
    private const uint XButton1 = 0x0001;
    private const nuint SubclassId = 2;

    private readonly nint _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _goBack;
    private readonly SubclassProc _callback;
    private bool _subclassed;
    private bool _disposed;

    public MouseBackNavigation(nint window, DispatcherQueue dispatcher, Action goBack)
    {
        _window = window;
        _dispatcher = dispatcher;
        _goBack = goBack;
        _callback = OnMessage;
        try
        {
            _subclassed = SetWindowSubclass(window, _callback, SubclassId, 0);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"鼠标侧键返回注册失败: {ex.GetType().Name}");
        }
    }

    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmXButtonDown)
        {
            // LOWORD(wParam) 表示按下的侧键：XBUTTON1 = 后退，XBUTTON2 = 前进。
            var button = wParam & 0xFFFFu;
            if (button == XButton1)
            {
                _dispatcher.TryEnqueue(() => _goBack());
                return 0; // 已处理，不再向下传递。
            }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subclassed)
        {
            try { RemoveWindowSubclass(_window, _callback, SubclassId); }
            catch (Exception ex) { Diagnostics.Log($"鼠标侧键返回注销失败: {ex.GetType().Name}"); }
        }
    }

    private delegate nint SubclassProc(nint window, uint msg, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
