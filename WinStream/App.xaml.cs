using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System;
using Microsoft.UI;

namespace WinStream
{
    public partial class App : Application
    {
        private Window m_window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();

            // Setup the window size
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(m_window);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(640, 400));  // Set to calculated width and height

            m_window.Activate();
        }
    }
}
