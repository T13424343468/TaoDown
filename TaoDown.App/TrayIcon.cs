using System.Runtime.InteropServices;

namespace TaoDown.App;

/// <summary>
/// 系统托盘图标（Win32 Shell_NotifyIcon 自实现，无第三方依赖）：
/// 左键单击/双击恢复主窗口；右键弹出原生菜单（打开/全部暂停/全部继续/退出）。
/// v1.02 根治（照抄 TaoDBM v1.0.7 实锤方案）：不再自建隐藏消息窗口——真机上
/// CreateWindowExW 可能返回空（TaoDBM 真机日志实锤），自建窗一断托盘整链静默失效。
/// 改为子类化主窗句柄：托盘回调消息直接托管到主窗 WndProc，退出前恢复原过程并删图标。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_APP_TRAY = 0x8001;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;
    private const uint NIM_MODIFY = 1;
    private const int NIF_INFO = 0x10;
    private const int NIIF_INFO = 1;
    private const uint NIF_MESSAGE = 1;
    private const uint NIF_ICON = 2;
    private const uint NIF_TIP = 4;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int GWLP_WNDPROC = -4;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 静态持有回调、实例引用与原窗口过程指针，防止被垃圾回收导致原生回调悬空
    private static WndProcDelegate? _proc;
    private static TrayIcon? _instance;
    private static IntPtr _oldProc;

    private readonly IntPtr _mainHwnd;
    private readonly Action _onLeftClick;
    private readonly Action _onPauseAll;
    private readonly Action _onResumeAll;
    private readonly Action _onExit;
    private readonly NOTIFYICONDATAW _data;
    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;
    private bool _subclassed;

    public TrayIcon(string iconPath, string tip, IntPtr mainHwnd, Action onLeftClick,
        Action onPauseAll, Action onResumeAll, Action onExit)
    {
        _mainHwnd = mainHwnd;
        _onLeftClick = onLeftClick;
        _onPauseAll = onPauseAll;
        _onResumeAll = onResumeAll;
        _onExit = onExit;
        _instance = this;
        _proc = WndProc;

        // 子类化主窗：托盘回调消息由主窗 WndProc 接管，不再依赖自建消息窗口
        if (_mainHwnd != IntPtr.Zero)
        {
            _oldProc = GetWindowLongPtrW(_mainHwnd, GWLP_WNDPROC);
            if (_oldProc != IntPtr.Zero)
            {
                SetWindowLongPtrW(_mainHwnd, GWLP_WNDPROC,
                    Marshal.GetFunctionPointerForDelegate(_proc));
                _subclassed = true;
                StartupLog.Write($"托盘：消息托管到主窗 hwnd=0x{_mainHwnd.ToInt64():X}");
            }
            else
            {
                StartupLog.Write("托盘：取主窗原窗口过程失败，仍尝试注册（消息链可能不可用）");
            }
        }
        else
        {
            StartupLog.Write("托盘：主窗句柄为空，仍尝试注册（托盘可能收不到消息）");
        }

        _hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
        _data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _mainHwnd,
            uID = 1,
            uFlags = (int)(NIF_MESSAGE | NIF_ICON | NIF_TIP),
            uCallbackMessage = (int)WM_APP_TRAY,
            hIcon = _hIcon,
            szTip = tip,
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref _data);
        _data.uVersion = (int)NOTIFYICON_VERSION_4;
        if (_added)
        {
            Shell_NotifyIconW(NIM_SETVERSION, ref _data);
            StartupLog.Write("托盘：向系统注册成功");
        }
        else
        {
            StartupLog.Write("托盘：向系统注册失败（Shell_NotifyIcon NIM_ADD 返回 false）");
        }
    }

    public bool IsAvailable => _added;

    /// <summary>更新悬停提示（任务状态变化时调用）；失败静默。</summary>
    public void UpdateTip(string tip)
    {
        if (!_added || _disposed || tip.Length == 0)
        {
            return;
        }

        try
        {
            // data 是结构体拷贝，szTip 每次随修改请求整体重传，无需回写到 _data
            var data = _data;
            data.szTip = tip.Length > 127 ? tip[..127] : tip;
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch
        {
            // 提示更新失败不影响主程序
        }
    }

    /// <summary>托盘气泡通知（下载完成/失败）：系统标准提示，带声音；失败静默。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added || _disposed || text.Length == 0)
        {
            return;
        }

        try
        {
            var data = _data;
            data.szInfoTitle = title.Length > 63 ? title[..63] : title;
            data.szInfo = text.Length > 255 ? text[..255] : text;
            data.dwInfoFlags = NIIF_INFO;
            data.uFlags |= NIF_INFO;
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch
        {
            // 通知失败不影响主程序
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY && _instance is { } tray)
        {
            // NOTIFYICON_VERSION_4: lParam 低 16 位为事件；右键可能是 RBUTTONUP 或 CONTEXTMENU
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONUP || evt == WM_LBUTTONDBLCLK)
            {
                tray._onLeftClick();
            }
            else if (evt == WM_RBUTTONUP || evt == WM_CONTEXTMENU)
            {
                tray.ShowMenu();
            }

            return IntPtr.Zero;
        }

        // 其余消息原样交还原窗口过程，主窗自身消息链不受子类化影响
        return _oldProc != IntPtr.Zero
            ? CallWindowProcW(_oldProc, hwnd, msg, wParam, lParam)
            : IntPtr.Zero;
    }

    /// <summary>托盘右键菜单：原生弹出菜单，选中命令直接执行。</summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenuW(menu, 0, 1, "显示主窗口");
        AppendMenuW(menu, 0, 2, "全部暂停");
        AppendMenuW(menu, 0, 3, "全部继续");
        AppendMenuW(menu, 0x800, 0, "-"); // 分隔线
        AppendMenuW(menu, 0, 9, "退出");

        GetCursorPos(out var pt);
        // 必须置前，否则点击菜单外部时菜单不会消失（Win32 托盘菜单经典要求）
        SetForegroundWindow(_mainHwnd);
        var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
            pt.X, pt.Y, _mainHwnd, IntPtr.Zero);
        DestroyMenu(menu);

        switch (cmd)
        {
            case 1:
                _onLeftClick();
                break;
            case 2:
                _onPauseAll();
                break;
            case 3:
                _onResumeAll();
                break;
            case 9:
                _onExit();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subclassed && _oldProc != IntPtr.Zero)
        {
            // 先恢复原窗口过程，再删图标，保证消息链还原
            SetWindowLongPtrW(_mainHwnd, GWLP_WNDPROC, _oldProc);
            _subclassed = false;
        }

        if (_added)
        {
            var data = _data;
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        if (_instance == this)
        {
            _instance = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProcW(IntPtr prevProc, IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y,
        IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
