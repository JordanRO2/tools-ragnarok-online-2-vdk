using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VDKTool.Core;

namespace VDKTool
{
    /// <summary>
    /// Entry point. This is now a WinExe (GUI subsystem) so a double-click does NOT
    /// pop a console window — the GUI (Photino) mode shows only the native window.
    ///
    /// CLI mode (a command / args beyond the window flag) still prints to the console:
    /// at startup we AttachConsole(ATTACH_PARENT_PROCESS) so Console.WriteLine reaches
    /// the terminal that launched us. (Caveat: because we are a GUI-subsystem process,
    /// cmd/PowerShell do not wait for us to exit; capture with redirection to a file
    /// or Start-Process -Wait. See VERIFY notes / README.)
    ///
    /// GUI mode (no command): starts the local HttpListener server and shows the UI in
    /// a native Photino (WebView2) window. Passing --browser (or VDKTOOL_BROWSER=1)
    /// keeps the legacy behavior: serve + open the UI in Firefox / the default browser.
    /// Passing --debug in GUI mode allocates a console (AllocConsole) so the web host
    /// log ("Listening on ...") is visible.
    ///
    /// Main stays [STAThread] because Photino requires an STA thread on Windows.
    ///
    /// Exit codes:
    ///   0  success
    ///   1  usage / argument / fatal error
    ///   2  partial failure (some items in a batch failed)
    /// </summary>
    internal static class Program
    {
        // kernel32 console interop. Because the PE is a GUI-subsystem (WinExe) binary,
        // Windows does not give us a console on double-click. For CLI use we re-attach
        // to the parent terminal's console (best-effort); for GUI --debug we allocate
        // a fresh console window for the log.
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            args ??= Array.Empty<string>();

            // Pull --debug / -debug out of the args before anything else parses them.
            bool debug = HasDebugFlag(args, out args);

            // Headless server mode: --server-only / --headless. Starts the web host
            // and blocks until Ctrl+C, opening NO window and NO browser. Used for
            // smoke-tests / CI. Detected (and stripped) before web-mode detection.
            bool serverOnly = HasServerOnlyFlag(args, out args);
            if (serverOnly)
            {
                // A console is required to print the listening URL and catch Ctrl+C.
                // Attach to the launching terminal if there is one; otherwise alloc.
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                    AllocConsole();
                return Web.WebHost.RunServerOnlyMode();
            }

            // Web UI (GUI) mode is selected by no args, or only the window flag
            // (--browser/--window). Anything else is CLI mode.
            bool guiMode = IsWebMode(args, out bool useBrowser);

            if (guiMode)
            {
                // GUI: no console at all unless --debug, which gives the web host log
                // a window to print to (AllocConsole). Without --debug we run silent.
                if (debug)
                {
                    AllocConsole();
                }
                else
                {
                    // No console: send any stray Console.Write to a no-op sink so a
                    // bare GUI launch never throws on a missing stdout handle.
                    Console.SetOut(TextWriter.Null);
                    Console.SetError(TextWriter.Null);
                }

                return useBrowser ? Web.WebHost.RunBrowserMode() : RunWindowMode();
            }

            // CLI mode: best-effort attach to the launching terminal's console so
            // Console.WriteLine is visible. If there is no parent console (e.g. the
            // exe was double-clicked with a command, or launched detached), this
            // simply fails and we continue silently — redirection still works.
            AttachConsole(ATTACH_PARENT_PROCESS);

            int exit = Cli.Run(args);

            // Flush + a trailing newline so the shell prompt returns on its own line
            // after our (attached) output.
            try
            {
                Console.Out.Flush();
                Console.WriteLine();
            }
            catch { /* no console attached / redirected handle closed: ignore */ }

            return exit;
        }

        /// <summary>
        /// Detects and strips a debug flag (--debug / -debug, case-insensitive) from
        /// the argument list. Returns true if it was present; <paramref name="rest"/>
        /// is the args with every debug flag removed so the rest of the parsing /
        /// window-mode detection is unaffected.
        /// </summary>
        private static bool HasDebugFlag(string[] args, out string[] rest)
        {
            bool found = false;
            var kept = new List<string>(args.Length);
            foreach (var a in args)
            {
                if (string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-debug", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    continue;
                }
                kept.Add(a);
            }
            rest = kept.ToArray();
            return found;
        }

        /// <summary>
        /// Detects and strips the headless server flag (--server-only / --headless,
        /// case-insensitive). Returns true if present; <paramref name="rest"/> is the
        /// args with every such flag removed.
        /// </summary>
        private static bool HasServerOnlyFlag(string[] args, out string[] rest)
        {
            bool found = false;
            var kept = new List<string>(args.Length);
            foreach (var a in args)
            {
                if (string.Equals(a, "--server-only", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    continue;
                }
                kept.Add(a);
            }
            rest = kept.ToArray();
            return found;
        }

        /// <summary>
        /// True when the program should launch the web UI. That is the case with no
        /// args, or when the only argument is the web-mode flag (--browser/--window).
        /// <paramref name="useBrowser"/> is set when the legacy browser path is wanted,
        /// either via --browser or the VDKTOOL_BROWSER environment variable.
        /// </summary>
        private static bool IsWebMode(string[] args, out bool useBrowser)
        {
            bool envBrowser =
                string.Equals(Environment.GetEnvironmentVariable("VDKTOOL_BROWSER"), "1", StringComparison.Ordinal) ||
                string.Equals(Environment.GetEnvironmentVariable("VDKTOOL_BROWSER"), "true", StringComparison.OrdinalIgnoreCase);

            if (args == null || args.Length == 0)
            {
                useBrowser = envBrowser;
                return true;
            }

            // Only a single web-mode flag and nothing else -> still web mode.
            if (args.Length == 1)
            {
                string a = args[0].ToLowerInvariant();
                if (a == "--browser" || a == "-b")
                {
                    useBrowser = true;
                    return true;
                }
                if (a == "--window")
                {
                    useBrowser = false;
                    return true;
                }
            }

            useBrowser = false;
            return false;
        }

        /// <summary>
        /// Default web-UI mode: start the HttpListener on a background thread, then
        /// host the served URL inside a native Photino (WebView2) window. When the
        /// window closes, stop the server and exit cleanly.
        /// </summary>
        private static int RunWindowMode()
        {
            var host = Web.WebHost.Start();
            if (host == null) return 1; // Start() already reported the error.

            string url = host.Url;
            Console.WriteLine("VDK Tool - web UI (native window)");
            Console.WriteLine($"Listening on {url}");

            try
            {
                // Server is already listening (Start returned), so loading the URL
                // immediately is safe.
                // Chromeless windows on Windows REQUIRE an explicit size AND location
                // (no Center()/UseOsDefault*). Center it manually from the screen size.
                const int winW = 1180, winH = 860;
                int scrW = GetSystemMetrics(0), scrH = GetSystemMetrics(1); // SM_CXSCREEN/SM_CYSCREEN
                int winX = scrW > winW ? (scrW - winW) / 2 : 0;
                int winY = scrH > winH ? (scrH - winH) / 2 : 0;

                var window = new Photino.NET.PhotinoWindow()
                    .SetTitle("VDK Tool")
                    .SetUseOsDefaultSize(false)
                    .SetUseOsDefaultLocation(false)
                    .SetSize(winW, winH)
                    .SetLocation(new System.Drawing.Point(winX, winY))
                    .SetMinSize(980, 660)
                    .SetResizable(true)
                    .SetChromeless(true)   // no OS title bar/border; the web UI draws its own
                    .SetContextMenuEnabled(false);

                // Custom title bar bridge: the web UI's window controls post messages
                // here (it cannot move/close a chromeless window on its own).
                window.RegisterWebMessageReceivedHandler((object sender, string message) =>
                {
                    var w = (Photino.NET.PhotinoWindow)sender;
                    switch (message)
                    {
                        case "win:minimize": w.SetMinimized(true); break;
                        case "win:maximize": w.SetMaximized(!w.Maximized); break;
                        case "win:close":    w.Close(); break;
                        default:
                            // drag:<screenX>,<screenY> — move the window so the grab point
                            // stays under the cursor (chromeless has no OS title-bar drag).
                            if (message.StartsWith("drag:", StringComparison.Ordinal))
                                HandleDrag(w, message);
                            // resize:<edge>:<screenX>:<screenY> — resize from an edge/corner
                            // (chromeless has no OS resize border either).
                            else if (message.StartsWith("resize:", StringComparison.Ordinal))
                                HandleResize(w, message);
                            break;
                    }
                });

                // Tell the UI when the OS maximize state changes so the max/restore
                // button icon stays in sync (also covers Win+Up / double-click).
                window.WindowMaximized += (s, e) => window.SendWebMessage("win:state:maximized");
                window.WindowRestored  += (s, e) => window.SendWebMessage("win:state:restored");

                // Round the chromeless window's corners + add a thin border (Windows 11).
                window.WindowCreated += (s, e) => ApplyWindowStyling();

                // Window/taskbar icon. The exe already carries app.ico via
                // <ApplicationIcon>; this also sets it on the Photino window so the
                // title bar matches. SetIconFile needs a real path, so for single-file
                // we extract the embedded app.ico to a temp file first.
                string iconPath = ResolveIconFile();
                if (iconPath != null)
                {
                    try { window.SetIconFile(iconPath); }
                    catch { /* non-fatal: window inherits the exe icon */ }
                }

                window.Load(url);

                window.WaitForClose();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: could not open the native window: {ex.Message}");
                Console.Error.WriteLine("Tip: run with --browser to use a web browser instead.");
                return 1;
            }
            finally
            {
                host.Stop();
            }
        }

        // Chromeless-window drag. The UI posts the cursor's SCREEN position when the
        // title bar is grabbed ("drag:start:X:Y") and on each move ("drag:move:X:Y");
        // we move the window by the same delta so the grab point stays under the cursor.
        private static int _dragMx, _dragMy, _dragWx, _dragWy;
        private static void HandleDrag(Photino.NET.PhotinoWindow w, string message)
        {
            var p = message.Split(':');
            if (p.Length < 4 || !int.TryParse(p[2], out int sx) || !int.TryParse(p[3], out int sy)) return;
            if (p[1] == "start")
            {
                _dragMx = sx; _dragMy = sy;
                var loc = w.Location;
                _dragWx = loc.X; _dragWy = loc.Y;
            }
            else if (p[1] == "move")
            {
                w.MoveTo(_dragWx + (sx - _dragMx), _dragWy + (sy - _dragMy));
            }
        }

        // Chromeless-window edge/corner resize. The UI posts the grabbed edge + cursor
        // SCREEN position on grab ("resize:start:<edge>:X:Y") and on each move
        // ("resize:move:X:Y"). We resize/reposition the window with an atomic
        // SetWindowPos (no flicker) so the dragged edge tracks the cursor — a
        // chromeless window has no OS resize border. Edge codes: t b l r tl tr bl br.
        private const int MinWinW = 980, MinWinH = 660;
        private static IntPtr _rsHwnd;
        private static int _rsMx, _rsMy, _rsX, _rsY, _rsW, _rsH;
        private static string _rsEdge = "";
        private static void HandleResize(Photino.NET.PhotinoWindow w, string message)
        {
            var p = message.Split(':');
            if (p.Length < 2) return;
            if (p[1] == "start")
            {
                if (p.Length < 5) return;
                _rsEdge = p[2];
                if (!int.TryParse(p[3], out _rsMx) || !int.TryParse(p[4], out _rsMy)) return;
                _rsHwnd = FindOwnWindow();
                if (_rsHwnd == IntPtr.Zero) return;
                GetWindowRect(_rsHwnd, out WinRect r);
                _rsX = r.Left; _rsY = r.Top; _rsW = r.Right - r.Left; _rsH = r.Bottom - r.Top;
            }
            else if (p[1] == "move")
            {
                if (p.Length < 4 || _rsHwnd == IntPtr.Zero) return;
                if (!int.TryParse(p[2], out int sx) || !int.TryParse(p[3], out int sy)) return;
                int dx = sx - _rsMx, dy = sy - _rsMy;
                int nx = _rsX, ny = _rsY, nw = _rsW, nh = _rsH;
                bool left = _rsEdge.Contains('l'), right = _rsEdge.Contains('r'),
                     top = _rsEdge.Contains('t'), bottom = _rsEdge.Contains('b');
                if (right)  nw = _rsW + dx;
                if (bottom) nh = _rsH + dy;
                if (left)  { nw = _rsW - dx; nx = _rsX + dx; }
                if (top)   { nh = _rsH - dy; ny = _rsY + dy; }
                // Clamp to the min size, pinning the opposite edge for left/top grabs.
                if (nw < MinWinW) { if (left) nx = _rsX + (_rsW - MinWinW); nw = MinWinW; }
                if (nh < MinWinH) { if (top)  ny = _rsY + (_rsH - MinWinH); nh = MinWinH; }
                SetWindowPos(_rsHwnd, IntPtr.Zero, nx, ny, nw, nh, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out WinRect r);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        private struct WinRect { public int Left, Top, Right, Bottom; }

        private static IntPtr FindOwnWindow()
        {
            uint me = GetCurrentProcessId();
            IntPtr best = IntPtr.Zero; int bestArea = -1;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint p);
                if (p == me && IsWindowVisible(h))
                {
                    GetWindowRect(h, out WinRect r);
                    int area = (r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea) { bestArea = area; best = h; }
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // Windows 11: round the chromeless window's corners and give it a thin border
        // so it reads as a modern floating window (the OS won't round a borderless
        // popup on its own). Harmless no-op on older Windows.
        private static void ApplyWindowStyling()
        {
            try
            {
                IntPtr h = FindOwnWindow();
                if (h == IntPtr.Zero) return;
                int round = 2;                                          // DWMWCP_ROUND
                DwmSetWindowAttribute(h, 33, ref round, sizeof(int));    // DWMWA_WINDOW_CORNER_PREFERENCE
                int border = 0x00554233;                                // COLORREF 0x00BBGGRR ~ subtle #334255
                DwmSetWindowAttribute(h, 34, ref border, sizeof(int));   // DWMWA_BORDER_COLOR
            }
            catch { /* pre-Win11 / DWM unavailable: window just stays square */ }
        }

        /// <summary>
        /// Resolves a filesystem path to app.ico for Photino's SetIconFile.
        /// Prefers app.ico next to the exe (framework-dependent / Content copy);
        /// falls back to extracting the embedded app.ico to a temp file (single-file
        /// publish, where there is no loose file on disk). Returns null if neither is
        /// available, in which case the window simply inherits the exe's icon.
        /// </summary>
        private static string ResolveIconFile()
        {
            try
            {
                string onDisk = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(onDisk)) return onDisk;

                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream("app.ico");
                if (s == null) return null;

                string temp = Path.Combine(Path.GetTempPath(), "VDK_Tool.app.ico");
                using (var fs = File.Create(temp)) s.CopyTo(fs);
                return temp;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Robust manual CLI parser + command dispatch built on VDKTool.Core.
    /// Paths with spaces are already tokenized correctly by the .NET runtime into
    /// the string[] args, so no manual re-splitting is done (this is the fix for the
    /// old WinExe space-handling bug).
    /// </summary>
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            // Global help / flags.
            var positionals = new List<string>();
            var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool quiet = false;
            bool help = false;
            string output = null;
            string formatFlag = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "-h":
                    case "--help":
                        help = true;
                        break;
                    case "-q":
                    case "--quiet":
                        quiet = true;
                        break;
                    case "-o":
                    case "--output":
                        if (i + 1 < args.Length) output = args[++i];
                        else { Console.Error.WriteLine("Error: --output requires a value"); return 1; }
                        break;
                    case "--csv":
                        formatFlag = "csv";
                        break;
                    case "--xlsx":
                        formatFlag = "xlsx";
                        break;
                    default:
                        positionals.Add(a);
                        break;
                }
            }

            if (positionals.Count == 0)
            {
                PrintUsage();
                // help with no command is success; otherwise usage error.
                return help ? 0 : 1;
            }

            string command = positionals[0].ToLowerInvariant();
            var rest = positionals.Skip(1).ToList();

            if (help)
            {
                PrintUsage(command);
                return 0;
            }

            try
            {
                switch (command)
                {
                    case "help":
                        PrintUsage();
                        return 0;

                    case "extract":
                    case "x":
                        return ExtractVdk(rest, output, quiet);

                    case "extractall":
                    case "xa":
                        return ExtractAll(rest, quiet);

                    case "list":
                    case "l":
                        return ListVdk(rest, false);

                    case "listall":
                    case "la":
                        return ListVdk(rest, true);

                    case "pack":
                    case "p":
                        return Pack(rest, output, quiet);

                    case "ct2xlsx":
                    case "ct":
                        return ConvertCt(rest, formatFlag ?? "xlsx", output, quiet);

                    case "ct2csv":
                        return ConvertCt(rest, "csv", output, quiet);

                    case "xlsx2ct":
                        return ConvertToCt(rest, output, quiet);

                    case "csv2ct":
                        return ConvertToCt(rest, output, quiet);

                    case "ctall":
                    case "cta":
                        return ConvertAllCt(rest, formatFlag ?? "xlsx", quiet);

                    default:
                        // Fix #2: unknown command -> error + usage + exit 1 (NEVER assume extract).
                        Console.Error.WriteLine($"Error: unknown command '{positionals[0]}'");
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        // --------------------------------------------------------------------
        // VDK commands
        // --------------------------------------------------------------------

        private static int ExtractVdk(List<string> rest, string output, bool quiet)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("Error: VDK file path required");
                PrintUsage("extract");
                return 1;
            }
            string vdkPath = rest[0];
            if (!File.Exists(vdkPath))
            {
                Console.Error.WriteLine($"Error: File not found: {vdkPath}");
                return 1;
            }

            string outputDir = output ?? (rest.Count >= 2 ? rest[1] : null);
            if (string.IsNullOrEmpty(outputDir))
            {
                string baseName = Path.GetFileNameWithoutExtension(vdkPath);
                string parentDir = Path.GetDirectoryName(Path.GetFullPath(vdkPath));
                outputDir = Path.Combine(parentDir, baseName + "_UNPACKED");
            }

            if (!quiet)
            {
                Console.WriteLine($"Extracting: {vdkPath}");
                Console.WriteLine($"Output: {outputDir}");
            }

            var archive = VDKArchive.Load(vdkPath);
            int extracted = archive.ExtractAll(outputDir);

            if (!quiet)
            {
                Console.WriteLine($"Version: {archive.Version}");
                Console.WriteLine($"Done! Extracted {extracted} files (empty dirs preserved; repack reconstructs 1:1)");
            }
            return 0;
        }

        private static int ExtractAll(List<string> rest, bool quiet)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("Error: Directory path required");
                PrintUsage("extractall");
                return 1;
            }
            string directory = rest[0];
            string suffix = rest.Count >= 2 ? rest[1] : "_UNPACKED";

            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"Error: Directory not found: {directory}");
                return 1;
            }

            var vdkFiles = Directory.GetFiles(directory, "*.VDK");
            if (vdkFiles.Length == 0)
            {
                Console.Error.WriteLine($"No VDK files found in: {directory}");
                return 1;
            }

            if (!quiet) Console.WriteLine($"Found {vdkFiles.Length} VDK files in: {directory}");

            int success = 0, failed = 0;
            foreach (var vdkPath in vdkFiles)
            {
                string baseName = Path.GetFileNameWithoutExtension(vdkPath);
                string outputDir = Path.Combine(directory, baseName + suffix);
                if (!quiet) Console.WriteLine($"[{success + failed + 1}/{vdkFiles.Length}] {baseName}.VDK -> {baseName}{suffix}/");
                try
                {
                    var archive = VDKArchive.Load(vdkPath);
                    int extracted = archive.ExtractAll(outputDir);
                    if (!quiet) Console.WriteLine($"    Extracted {extracted} files");
                    success++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    ERROR: {ex.Message}");
                    failed++;
                }
            }

            if (!quiet) Console.WriteLine($"Complete! {success} succeeded, {failed} failed");
            return failed > 0 ? 2 : 0;
        }

        private static int ListVdk(List<string> rest, bool showAll)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("Error: VDK file path required");
                return 1;
            }
            string vdkPath = rest[0];
            if (!File.Exists(vdkPath))
            {
                Console.Error.WriteLine($"Error: File not found: {vdkPath}");
                return 1;
            }

            var archive = VDKArchive.Load(vdkPath);
            var files = archive.GetFileEntries();
            var dirs = archive.GetDirectoryEntries();

            Console.WriteLine($"File: {vdkPath}");
            Console.WriteLine($"Version: {archive.Version}");
            Console.WriteLine($"Directories: {dirs.Count}");
            Console.WriteLine($"Files: {files.Count}");
            Console.WriteLine($"Total entries (incl . and ..): {archive.Entries.Count}");
            Console.WriteLine();

            if (showAll)
            {
                foreach (var entry in archive.Entries)
                {
                    string type = entry.IsDirectory ? "[DIR]" : "[FILE]";
                    Console.WriteLine($"  {type} {entry.Path} (pos: 0x{entry.DataPosition:X}, size: {entry.UncompressedSize:N0})");
                }
            }
            else
            {
                foreach (var entry in files)
                    Console.WriteLine($"  {entry.Path} ({entry.UncompressedSize:N0} bytes)");
            }
            return 0;
        }

        private static int Pack(List<string> rest, string output, bool quiet)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("Error: Source directory required");
                PrintUsage("pack");
                return 1;
            }
            string sourceDir = rest[0];
            if (!Directory.Exists(sourceDir))
            {
                Console.Error.WriteLine($"Error: Directory not found: {sourceDir}");
                return 1;
            }

            string outputFile = output ?? (rest.Count >= 2 ? rest[1] : null);
            if (string.IsNullOrEmpty(outputFile))
            {
                string dirName = Path.GetFileName(sourceDir.TrimEnd('\\', '/'));
                string parentDir = Path.GetDirectoryName(Path.GetFullPath(sourceDir));
                outputFile = Path.Combine(parentDir, dirName + ".VDK");
            }

            if (!quiet)
            {
                Console.WriteLine($"Packing: {sourceDir}");
                Console.WriteLine($"Output: {outputFile}");
            }

            // Sidecar-free: reconstruct the VDK purely from the directory tree
            // (convention A, level 1, empty dirs preserved) -> byte-exact 1:1.
            var result = VDKBuilder.BuildFromDirectory(sourceDir, outputFile);

            if (!quiet)
                Console.WriteLine($"Done! Packed {result.FileCount} files, {result.FolderCount} folders ({result.TotalBytes:N0} bytes)");
            return 0;
        }

        // --------------------------------------------------------------------
        // CT commands
        // --------------------------------------------------------------------

        private static int ConvertCt(List<string> rest, string format, string output, bool quiet)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("Error: CT file path required");
                PrintUsage("ct2xlsx");
                return 1;
            }
            string ctPath = rest[0];
            if (!File.Exists(ctPath))
            {
                Console.Error.WriteLine($"Error: File not found: {ctPath}");
                return 1;
            }

            string ext = format == "csv" ? ".csv" : ".xlsx";
            string outputPath = output ?? Path.ChangeExtension(ctPath, ext);

            if (!quiet)
            {
                Console.WriteLine($"Converting: {ctPath}");
                Console.WriteLine($"Output: {outputPath}");
            }

            var processor = new CTProcessor();
            processor.Read(ctPath);
            if (format == "csv") processor.ExportToCSV(outputPath);
            else processor.ExportToXLSX(outputPath);

            if (!quiet)
                Console.WriteLine($"Done! {processor.Headers.Count} columns, {processor.Rows.Count} rows");
            return 0;
        }

        private static int ConvertToCt(List<string> rest, string output, bool quiet)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("Error: input file path required (.xlsx or .csv)");
                return 1;
            }
            string inPath = rest[0];
            if (!File.Exists(inPath))
            {
                Console.Error.WriteLine($"Error: File not found: {inPath}");
                return 1;
            }

            string outputPath = output ?? (rest.Count >= 2 ? rest[1] : Path.ChangeExtension(inPath, ".ct"));

            if (!quiet)
            {
                Console.WriteLine($"Converting: {inPath}");
                Console.WriteLine($"Output: {outputPath}");
            }

            var processor = new CTProcessor();
            string ext = Path.GetExtension(inPath).ToLowerInvariant();
            if (ext == ".csv") processor.ImportFromCSV(inPath);
            else processor.ImportFromXLSX(inPath);

            processor.Write(outputPath, processor.Headers, processor.Types, processor.Rows);

            if (!quiet)
                Console.WriteLine($"Done! {processor.Headers.Count} columns, {processor.Rows.Count} rows");
            return 0;
        }

        private static int ConvertAllCt(List<string> rest, string format, bool quiet)
        {
            if (rest.Count < 1)
            {
                Console.Error.WriteLine("Error: Directory path required");
                PrintUsage("ctall");
                return 1;
            }
            string directory = rest[0];
            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"Error: Directory not found: {directory}");
                return 1;
            }

            var ctFiles = Directory.GetFiles(directory, "*.ct", SearchOption.AllDirectories);
            if (ctFiles.Length == 0)
            {
                Console.Error.WriteLine($"No CT files found in: {directory}");
                return 1;
            }

            string ext = format == "csv" ? ".csv" : ".xlsx";
            if (!quiet) Console.WriteLine($"Found {ctFiles.Length} CT files in: {directory}");

            int success = 0, failed = 0;
            foreach (var ctPath in ctFiles)
            {
                string relativePath = ctPath.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar);
                string outputPath = Path.ChangeExtension(ctPath, ext);
                if (!quiet) Console.WriteLine($"[{success + failed + 1}/{ctFiles.Length}] {relativePath}");
                try
                {
                    var processor = new CTProcessor();
                    processor.Read(ctPath);
                    if (format == "csv") processor.ExportToCSV(outputPath);
                    else processor.ExportToXLSX(outputPath);
                    if (!quiet) Console.WriteLine($"    -> {processor.Headers.Count} columns, {processor.Rows.Count} rows");
                    success++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    ERROR: {ex.Message}");
                    failed++;
                }
            }

            if (!quiet) Console.WriteLine($"Complete! {success} succeeded, {failed} failed");
            return failed > 0 ? 2 : 0;
        }

        // --------------------------------------------------------------------
        // Usage
        // --------------------------------------------------------------------

        public static void PrintUsage(string command = null)
        {
            switch (command)
            {
                case "extract":
                case "x":
                    Console.WriteLine("Usage: VDK_Tool extract <file.vdk> [output_dir] [--output DIR] [--quiet]");
                    Console.WriteLine("  Extract a VDK archive (files + empty dirs; no sidecar). 'pack' rebuilds 1:1.");
                    return;
                case "extractall":
                case "xa":
                    Console.WriteLine("Usage: VDK_Tool extractall <dir> [suffix] [--quiet]");
                    Console.WriteLine("  Extract every *.VDK in <dir>. suffix defaults to _UNPACKED.");
                    return;
                case "pack":
                case "p":
                    Console.WriteLine("Usage: VDK_Tool pack <dir> [output.vdk] [--output FILE] [--quiet]");
                    Console.WriteLine("  Reconstruct a VDK purely from a directory tree (no sidecar; byte-exact 1:1).");
                    return;
                case "ct2xlsx":
                case "ct":
                case "ct2csv":
                    Console.WriteLine("Usage: VDK_Tool ct2xlsx|ct2csv <file.ct> [--output FILE] [--csv] [--quiet]");
                    return;
                case "xlsx2ct":
                case "csv2ct":
                    Console.WriteLine("Usage: VDK_Tool xlsx2ct|csv2ct <file.xlsx|.csv> [output.ct] [--output FILE]");
                    return;
                case "ctall":
                case "cta":
                    Console.WriteLine("Usage: VDK_Tool ctall <dir> [--csv] [--quiet]");
                    return;
            }

            Console.WriteLine("VDK Tool - Ragnarok Online 2 VDK/CT toolkit");
            Console.WriteLine();
            Console.WriteLine("Usage: VDK_Tool <command> [args] [flags]");
            Console.WriteLine("       VDK_Tool                (no args -> web UI in a native window)");
            Console.WriteLine("       VDK_Tool --browser      (web UI in Firefox / default browser)");
            Console.WriteLine();
            Console.WriteLine("Web UI mode:");
            Console.WriteLine("  --window              Force the native Photino window (the default)");
            Console.WriteLine("  --browser, -b         Serve the UI and open it in a browser instead");
            Console.WriteLine("                        (env: VDKTOOL_BROWSER=1 has the same effect)");
            Console.WriteLine("  --debug               Show a console window with the server log");
            Console.WriteLine("                        (GUI mode only; combinable with --browser/--window)");
            Console.WriteLine("  --server-only         Headless: start the web host, print the listening");
            Console.WriteLine("    (--headless)        URL, and run until Ctrl+C. No window, no browser.");
            Console.WriteLine("                        For smoke-tests / CI.");
            Console.WriteLine();
            Console.WriteLine("VDK commands:");
            Console.WriteLine("  extract, x   <file.vdk> [outdir]   Extract a VDK (files + empty dirs)");
            Console.WriteLine("  extractall, xa <dir> [suffix]      Extract all *.VDK in a dir");
            Console.WriteLine("  list, l      <file.vdk>            List file entries");
            Console.WriteLine("  listall, la  <file.vdk>            List all entries (dirs + . + ..)");
            Console.WriteLine("  pack, p      <dir> [out.vdk]       Reconstruct a VDK from a dir (1:1)");
            Console.WriteLine();
            Console.WriteLine("CT commands:");
            Console.WriteLine("  ct2xlsx, ct  <file.ct>            CT -> XLSX");
            Console.WriteLine("  ct2csv       <file.ct>            CT -> CSV");
            Console.WriteLine("  xlsx2ct      <file.xlsx> [out.ct] XLSX -> CT");
            Console.WriteLine("  csv2ct       <file.csv>  [out.ct] CSV -> CT");
            Console.WriteLine("  ctall, cta   <dir>                Convert all *.ct in a dir tree");
            Console.WriteLine();
            Console.WriteLine("Global flags:");
            Console.WriteLine("  --output, -o <path>   Override output path");
            Console.WriteLine("  --csv                 Select CSV output (ct/ctall)");
            Console.WriteLine("  --quiet, -q           Suppress progress output");
            Console.WriteLine("  --help, -h            Show help (global or per-command)");
            Console.WriteLine();
            Console.WriteLine("Exit codes: 0 success | 1 usage/error | 2 partial failure");
        }
    }
}
