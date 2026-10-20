using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FolderGitStatus
{
    /// <summary>
    /// "Folder Git Status" pane in the Output window and %LOCALAPPDATA%\FolderGitStatus\log.txt
    /// (useful for debugging without access to the VS window). Debug is only logged with "Diagnostic log" on.
    /// </summary>
    internal static class Log
    {
        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderGitStatus", "log.txt");

        private static readonly object FileGate = new object();
        private static Guid PaneGuid = new Guid("4E1C2B7A-0F7D-4C8E-9C55-2B1D6A3F9E10");
        private static IVsOutputWindowPane _pane;
        private static Func<bool> _diagnosticsEnabled = () => false;

        public static void Initialize(IVsOutputWindow output, Func<bool> diagnosticsEnabled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _diagnosticsEnabled = diagnosticsEnabled;
            if (output != null && output.CreatePane(ref PaneGuid, "Folder Git Status", 1, 1) == 0)
            {
                output.GetPane(ref PaneGuid, out _pane);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, string.Empty);
            }
            catch (IOException)
            {
            }
        }

        public static void Write(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            WriteToFile(line);
#pragma warning disable VSTHRD010 // OutputStringThreadSafe can be called from any thread
            _pane?.OutputStringThreadSafe(line);
#pragma warning restore VSTHRD010
        }

        public static void Debug(string message)
        {
            if (_diagnosticsEnabled())
            {
                Write(message);
            }
        }

        private static void WriteToFile(string line)
        {
            try
            {
                lock (FileGate)
                {
                    File.AppendAllText(FilePath, line);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
