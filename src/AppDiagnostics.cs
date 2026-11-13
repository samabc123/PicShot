using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FrameDock
{
    internal static class AppDiagnostics
    {
        const long MaxLogBytes = 512 * 1024;
        const int MaxEntryBytes = 16 * 1024;
        static readonly object gate = new object();
        static string logFile;
        static int installed;

        internal static void Initialize(string directory)
        {
            lock (gate) logFile = Path.Combine(directory, "runtime.log");
            if (Interlocked.Exchange(ref installed, 1) != 0) return;
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                Log("fatal; terminating=" + args.IsTerminating, args.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs args)
            {
                Log("background-task-unobserved", args.Exception);
            };
        }

        internal static void Attach(Application application)
        {
            application.DispatcherUnhandledException += delegate(object sender,
                System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
            {
                // Do not continue after an unknown UI failure with potentially invalid state.
                Log("dispatcher-unhandled", args.Exception);
            };
            application.SessionEnding += delegate { Log("windows-session-ending"); };
            application.Exit += delegate(object sender, ExitEventArgs args)
            {
                Log("application-exit; code=" + args.ApplicationExitCode);
            };
        }

        internal static void Log(string message, Exception error = null)
        {
            try
            {
                lock (gate)
                {
                    if (logFile == null) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(logFile));
                    string text = DateTimeOffset.Now.ToString("o") + " pid=" +
                        Process.GetCurrentProcess().Id + " " + message;
                    if (error != null)
                    {
                        string detail = error.ToString();
                        text += Environment.NewLine + detail.Substring(0, Math.Min(detail.Length, MaxEntryBytes));
                    }
                    var encoding = new UTF8Encoding(false);
                    byte[] entry = new byte[MaxEntryBytes];
                    int charsUsed, bytesUsed;
                    bool completed;
                    encoding.GetEncoder().Convert(text.ToCharArray(), 0, text.Length,
                        entry, 0, MaxEntryBytes - encoding.GetByteCount(Environment.NewLine), true,
                        out charsUsed, out bytesUsed, out completed);
                    bytesUsed += encoding.GetBytes(Environment.NewLine, 0, Environment.NewLine.Length,
                        entry, bytesUsed);
                    var info = new FileInfo(logFile);
                    if (info.Exists && info.Length + bytesUsed > MaxLogBytes)
                    {
                        string previous = logFile + ".previous";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(logFile, previous);
                    }
                    using (var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                        stream.Write(entry, 0, bytesUsed);
                }
            }
            catch
            {
                // Diagnostic failures must never terminate the application.
            }
        }
    }
}
