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

                RunApplication();
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
            var library = new ImageLibrary(store, application.Dispatcher);
            var placement = new ImagePlacement(screenshots);
            if (Directory.Exists(screenshots))
            {
                library.AddWatcher(screenshots, true, true, false);
                library.ScanFolderInBackground(screenshots, true);
            }
            if (Directory.Exists(placement.DownloadsFolder))
            {
                library.AddWatcher(placement.DownloadsFolder, false, false, false);
                library.ScanFolderInBackground(placement.DownloadsFolder, false);
            }
            library.StartWatchers();
            var window = new MainWindow(store, library, placement);
            application.MainWindow = window;
            application.Run(window);
        }
    }
}
