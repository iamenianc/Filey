using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Filey
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // A machine-global mutex name detects a second instance across user sessions.
        private const string MutexName = @"Global\FileyApp";
        private const string UniqueWindowTitle = "Filey — Dual Pane File Manager";

        private Mutex _singleInstanceMutex;
        private bool _ownsMutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            var window = new MainWindow();
            window.Show();
        }

        private static void ActivateExistingInstance()
        {
            IntPtr hwnd = FindWindow(null, UniqueWindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_ownsMutex)
                _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
