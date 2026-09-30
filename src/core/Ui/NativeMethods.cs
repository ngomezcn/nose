using System.Runtime.InteropServices;

namespace AICopilotCore.Ui;

// P/Invoke compartido por XPlaneWindowWatcher y XPlaneDocker.
//
// Todo lo de aqui se usa contra la ventana de OTRO proceso (X-Plane), y
// eso condiciona que variantes se eligen: SetWindowPos manda mensajes que
// la ventana destino tiene que procesar, asi que una llamada sincrona se
// queda bloqueada hasta que el simulador atienda su cola -- hasta un frame
// entero, o para siempre si el sim esta colgado. De ahi que el docker use
// SWP_ASYNCWINDOWPOS y ShowWindowAsync en el camino normal: encolan la
// peticion y vuelven al instante.
internal static class NativeMethods {
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // WS_CAPTION es WS_BORDER|WS_DLGFRAME: quitarlo se lleva por delante la
    // barra de titulo entera.
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_THICKFRAME = 0x00040000;

    public const long WS_EX_DLGMODALFRAME = 0x00000001;
    public const long WS_EX_WINDOWEDGE = 0x00000100;
    public const long WS_EX_CLIENTEDGE = 0x00000200;
    public const long WS_EX_STATICEDGE = 0x00020000;
    // Popups WPF (tooltips): sin activar el shell, o Windows lo sube y el
    // hueco de X-Plane se ve negro (EnsureZOrder no puede poner XP encima
    // de la ventana activa — solo puede bajar el shell).
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    // Para tooltips / dropdowns WPF (HWND aparte): con el shell debajo de
    // X-Plane sus Popups quedan tapados o negros; TOPMOST mientras duran.
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public const int WM_GETMINMAXINFO = 0x0024;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // Las variantes ...Ptr son las correctas en x64 (el core se publica
    // siempre win-x64): los estilos extendidos no caben en un int.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
