namespace TaoDown.App;

using System.Runtime.InteropServices;

/// <summary>
/// 窗口摆放助手（v2.62）：置顶 + 主屏工作区居中 + 定尺寸。
/// 用 Win32 系统调用实现，不依赖 Windows App SDK 1.3+ 的窗口接口——
/// 真机教训：包版本拆分后部分窗口接口缺失，系统调用全版本可用。
/// </summary>
internal static class WindowPlacement
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SPI_GETWORKAREA = 0x0048;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private static readonly nint HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>把窗口定为指定外框尺寸、置于主屏工作区正中并置顶，然后激活显示。
    /// dx/dy 为可选级联偏移：并发多窗时依次错开，避免完全叠在同一点互相盖住。</summary>
    public static void MakeTopmostCentered(Microsoft.UI.Xaml.Window window, int width, int height, int dx = 0, int dy = 0)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == 0)
            {
                return;
            }

            // 先按目标外框尺寸摆放（尺寸含边框，DPI 缩放下系统自动换算）
            SetWindowPos(hwnd, 0, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            if (!GetWindowRect(hwnd, out var sized))
            {
                return;
            }

            // 主屏工作区（去掉任务栏）；取不到退回整屏
            var wa = new RECT();
            if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref wa, 0) || wa.Right <= wa.Left)
            {
                wa = new RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = GetSystemMetrics(SM_CXSCREEN),
                    Bottom = GetSystemMetrics(SM_CYSCREEN),
                };
            }

            var left = wa.Left + Math.Max(0, (wa.Right - wa.Left - (sized.Right - sized.Left)) / 2) + dx;
            var top = wa.Top + Math.Max(0, (wa.Bottom - wa.Top - (sized.Bottom - sized.Top)) / 2) + dy;
            SetWindowPos(hwnd, HWND_TOPMOST, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch
        {
            // 置顶居中失败不阻断：窗口仍按默认位置显示
        }
    }
}
