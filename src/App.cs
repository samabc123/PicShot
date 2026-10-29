using System;
using System.IO;
using System.Windows;

namespace FrameDock
{
    internal static class App
    {
        [STAThread]
        public static void Main()
        {
            var store = new StateStore();
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string screenshots = string.IsNullOrWhiteSpace(store.State.ScreenshotsFolder)
                ? Path.Combine(pictures, "Screenshots")
                : store.State.ScreenshotsFolder;
            store.State.ScreenshotsFolder = screenshots;

            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var library = new ImageLibrary(store, application.Dispatcher);
            if (Directory.Exists(screenshots))
                library.AddWatcher(screenshots, true, true, false);
            library.StartWatchers();
            var window = new MainWindow(store, library);
            application.MainWindow = window;
            application.Run(window);
        }
    }
}
