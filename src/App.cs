using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace FrameDock
{
    internal static class App
    {
        [STAThread]
        public static void Main()
        {
            bool isFirstInstance;
            using (var singleInstance = new Mutex(
                true,
                @"Local\PicShot-SingleInstance",
                out isFirstInstance))
            {
                if (!isFirstInstance)
                    return;

                AppDiagnostics.Initialize(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "FrameDock", "data", "logs"));
                AppDiagnostics.Log("application-start; version=" + AppUpdates.CurrentVersion);
                try { RunApplication(); }
                catch (Exception error)
                {
                    AppDiagnostics.Log("application-failed", error);
                    MessageBox.Show("PicShot 运行失败：" + error.Message +
                        "\n诊断信息位于 %LocalAppData%\\FrameDock\\data\\logs\\runtime.log。",
                        "PicShot", MessageBoxButton.OK, MessageBoxImage.Error);
                    Environment.ExitCode = 1;
                }
            }
        }

        static void RunApplication()
        {
            StateStore store;
            try { store = new StateStore(); }
            catch (IOException error)
            {
                MessageBox.Show(error.Message, "PicShot", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string screenshots = string.IsNullOrWhiteSpace(store.State.ScreenshotsFolder)
                ? Path.Combine(pictures, "Screenshots")
                : store.State.ScreenshotsFolder;
            store.State.ScreenshotsFolder = screenshots;

            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            AppDiagnostics.Attach(application);
            var library = new ImageLibrary(store, application.Dispatcher);
            var placement = new ImagePlacement(screenshots);
            if (Directory.Exists(screenshots))
            {
                library.TryAddWatcher(screenshots, true, true, false);
                library.ScanFolderInBackground(screenshots, true);
            }
            if (Directory.Exists(placement.DownloadsFolder))
            {
                library.TryAddWatcher(placement.DownloadsFolder, false, false, false);
                library.ScanFolderInBackground(placement.DownloadsFolder, false);
            }
            library.StartWatchers();
            StartSpecialWatcher(library, store.State.WeChatFolder, store.State.WeChatPathDecided);
            StartSpecialWatcher(library, store.State.QqFolder, store.State.QqPathDecided);
            var window = new MainWindow(store, library, placement);
            application.MainWindow = window;
            application.Run(window);
        }

        static void StartSpecialWatcher(ImageLibrary library, string folder, bool decided)
        {
            if (!decided || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            library.TryAddWatcher(folder, true, false, true);
        }
    }
}
