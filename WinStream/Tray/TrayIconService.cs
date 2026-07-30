#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinStream.Tray;

internal sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const int GwlWndProc = -4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;
    private const uint NifGuid = 0x0020;
    private const uint NotifyIconVersion4 = 4;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint MfString = 0;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint OpenCommand = 1;
    private const uint ExitCommand = 2;

    private static readonly Guid IconGuid = new("8B0CF0AE-9DC7-4E2E-AEC1-E69BFF8C3BE7");

    private readonly IntPtr _windowHandle;
    private readonly WindowProcedure _windowProcedure;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _oldWindowProcedure;
    private IntPtr _iconHandle;
    private bool _disposed;

    public TrayIconService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _windowProcedure = WindowProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WinStreamTray.ico");
        _iconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 32, 32, LrLoadFromFile);
        if (_iconHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not load tray icon at {iconPath}.");
        }

        _oldWindowProcedure = SetWindowProcedure(
            _windowHandle,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));

        AddIcon();
    }

    private void AddIcon()
    {
        var data = CreateIconData();
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            throw new InvalidOperationException("Windows could not add the WinStream tray icon.");
        }

        data.TimeoutOrVersion = NotifyIconVersion4;
        ShellNotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Flags = NifMessage | NifIcon | NifTip | NifGuid,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        ToolTip = "WinStream — AirPlay audio sender",
        IconGuid = IconGuid
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            AddIcon();
            return IntPtr.Zero;
        }

        if (message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xffff);
            if (notification == WmLButtonUp)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }

            if (notification == WmRButtonUp)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_oldWindowProcedure, hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, OpenCommand, "Open WinStream");
            AppendMenu(menu, MfString, ExitCommand, "Quit");
            GetCursorPos(out var cursor);
            SetForegroundWindow(_windowHandle);

            var selected = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCommand,
                cursor.X,
                cursor.Y,
                _windowHandle,
                IntPtr.Zero);

            if (selected == OpenCommand)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (selected == ExitCommand)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var data = CreateIconData();
        ShellNotifyIcon(NimDelete, ref data);

        if (_oldWindowProcedure != IntPtr.Zero)
        {
            SetWindowProcedure(_windowHandle, _oldWindowProcedure);
            _oldWindowProcedure = IntPtr.Zero;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        _disposed = true;
    }

    private static IntPtr SetWindowProcedure(IntPtr hwnd, IntPtr procedure) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr(hwnd, GwlWndProc, procedure)
            : new IntPtr(SetWindowLong(hwnd, GwlWndProc, procedure.ToInt32()));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ToolTip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid IconGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousProcedure,
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
