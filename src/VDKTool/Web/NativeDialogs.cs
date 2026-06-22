using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace VDKTool.Web
{
    /// <summary>
    /// Native file/folder dialogs. HttpListener handler threads are MTA, but the
    /// WinForms common dialogs require an STA thread, so every dialog call is
    /// marshaled onto a dedicated short-lived STA thread.
    /// </summary>
    internal static class NativeDialogs
    {
        private static T RunSta<T>(Func<T> func)
        {
            T result = default;
            var t = new Thread(() => { result = func(); });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            return result;
        }

        // Only honored when it points at an existing directory; otherwise the
        // common dialogs misbehave (revert to last-used or fail silently).
        private static bool ValidDir(string dir) =>
            !string.IsNullOrEmpty(dir) && Directory.Exists(dir);

        public static string PickFile(string title, string filter, string initialDir = null)
        {
            return RunSta(() =>
            {
                using var dlg = new OpenFileDialog
                {
                    Title = string.IsNullOrEmpty(title) ? "Select a file" : title,
                    Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (ValidDir(initialDir)) dlg.InitialDirectory = initialDir;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
            });
        }

        public static string PickFolder(string title, string initialDir = null)
        {
            return RunSta(() =>
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = string.IsNullOrEmpty(title) ? "Select a folder" : title,
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true
                };
                // SelectedPath opens the dialog expanded to that folder.
                if (ValidDir(initialDir)) dlg.SelectedPath = initialDir;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
            });
        }

        public static string PickSave(string defaultName, string filter, string initialDir = null)
        {
            return RunSta(() =>
            {
                using var dlg = new SaveFileDialog
                {
                    Title = "Save as",
                    FileName = defaultName ?? "",
                    Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
                    OverwritePrompt = true
                };
                if (ValidDir(initialDir)) dlg.InitialDirectory = initialDir;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
            });
        }
    }
}
