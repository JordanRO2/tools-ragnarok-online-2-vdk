using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VDKTool.Web
{
    /// <summary>
    /// Locates and launches Firefox in a dedicated app-style window.
    ///
    /// Resolution order:
    ///   1. Portable Firefox bundled next to the exe: .\firefox\firefox.exe
    ///   2. Installed Firefox via registry (HKLM/HKCU) or Program Files paths.
    ///   3. Fallback: the system default browser (Process.Start + UseShellExecute).
    ///
    /// When Firefox is found, it is launched against a temporary, throwaway profile
    /// with --no-remote --new-window so the window is dedicated to this app and does
    /// not fold into an existing Firefox session. The temp profile is created under
    /// %TEMP%\vdktool-ffprofile-&lt;pid&gt; and removed on best effort at exit.
    /// </summary>
    internal static class FirefoxLauncher
    {
        public static void Launch(string url)
        {
            string firefox = FindFirefox();
            if (firefox != null)
            {
                try
                {
                    LaunchFirefoxApp(firefox, url);
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[firefox] failed to launch Firefox ({ex.Message}); falling back to default browser.");
                }
            }

            // Fallback: default browser.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[browser] could not open a browser automatically: {ex.Message}");
                Console.WriteLine($"Open this URL manually: {url}");
            }
        }

        private static void LaunchFirefoxApp(string firefoxExe, string url)
        {
            string profileDir = Path.Combine(Path.GetTempPath(),
                "vdktool-ffprofile-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(profileDir);

            // -P with an explicit profile path is done via --profile (path form).
            // --no-remote => do not connect to an existing instance (dedicated window).
            // --new-window <url> => open the URL in its own window.
            var psi = new ProcessStartInfo
            {
                FileName = firefoxExe,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add(profileDir);
            psi.ArgumentList.Add("--no-remote");
            psi.ArgumentList.Add("--new-window");
            psi.ArgumentList.Add(url);

            Process.Start(psi);

            // Best-effort cleanup of the temp profile when this process exits.
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true); }
                catch { /* best effort */ }
            };
        }

        private static string FindFirefox()
        {
            // 1. Portable Firefox bundled next to the exe.
            try
            {
                string bundled = Path.Combine(AppContext.BaseDirectory, "firefox", "firefox.exe");
                if (File.Exists(bundled)) return bundled;
            }
            catch { }

            // 2. Registry: App Paths + Mozilla keys (HKLM then HKCU).
            string fromReg = FromRegistry();
            if (fromReg != null && File.Exists(fromReg)) return fromReg;

            // 3. Common install locations.
            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
            })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static string FromRegistry()
        {
            // App Paths\firefox.exe
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var k = root.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
                    var val = k?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(val) && File.Exists(val)) return val;
                }
                catch { }
            }

            // Mozilla\Mozilla Firefox\<ver>\Main\PathToExe
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var mz = root.OpenSubKey(@"SOFTWARE\Mozilla\Mozilla Firefox");
                    var current = mz?.GetValue("CurrentVersion") as string;
                    if (!string.IsNullOrEmpty(current))
                    {
                        using var main = root.OpenSubKey(
                            $@"SOFTWARE\Mozilla\Mozilla Firefox\{current}\Main");
                        var path = main?.GetValue("PathToExe") as string;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                    }
                }
                catch { }
            }

            return null;
        }
    }
}
