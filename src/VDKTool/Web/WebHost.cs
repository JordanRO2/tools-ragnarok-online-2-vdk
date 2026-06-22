using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VDKTool.Core;

namespace VDKTool.Web
{
    /// <summary>
    /// Local web UI server built on HttpListener. Serves the embedded wwwroot and a
    /// JSON API for VDK/CT operations + native file dialogs. Binds to 127.0.0.1 on a
    /// free port. The server itself is transport-agnostic about the front end: it can
    /// be hosted inside a native Photino window (default) or opened in a browser
    /// (<c>--browser</c>); see <see cref="Program"/>.
    /// </summary>
    internal sealed class WebHost
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Report the InformationalVersion ("1.0.0") rather than the 4-part
        // AssemblyVersion ("1.0.0.0"). IncludeSourceRevisionInInformationalVersion
        // is disabled in the csproj so there is no "+<sha>" suffix to strip; we
        // still trim any build metadata defensively in case that changes.
        private static string Version
        {
            get
            {
                var info = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (!string.IsNullOrEmpty(info))
                {
                    int plus = info.IndexOf('+');
                    return plus >= 0 ? info.Substring(0, plus) : info;
                }
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            }
        }

        private readonly HttpListener _listener;
        private readonly Thread _acceptThread;
        private volatile bool _stopping;

        /// <summary>The URL the server is listening on, e.g. http://127.0.0.1:NNNN/.</summary>
        public string Url { get; }

        private WebHost(HttpListener listener, string url)
        {
            _listener = listener;
            Url = url;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "vdktool-webhost"
            };
        }

        /// <summary>
        /// Starts the HttpListener on a free loopback port and a background accept
        /// thread. Returns once the listener is actually listening, so callers may
        /// safely point a window/browser at <see cref="Url"/> immediately.
        /// Returns null if the listener could not be started.
        /// </summary>
        public static WebHost Start()
        {
            int port = FindFreePort();
            string prefix = $"http://127.0.0.1:{port}/";

            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: could not start web server on {prefix}: {ex.Message}");
                return null;
            }

            var host = new WebHost(listener, prefix);
            host._acceptThread.Start();
            return host;
        }

        /// <summary>Stops the listener and the accept thread (idempotent).</summary>
        public void Stop()
        {
            if (_stopping) return;
            _stopping = true;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch (Exception ex)
                    {
                        try { WriteJson(ctx, 500, new { error = ex.Message }); } catch { }
                    }
                });
            }
        }

        /// <summary>
        /// Legacy/browser mode: start the server and open it in a browser
        /// (Firefox app window if available, otherwise the default browser), then
        /// block until Ctrl+C. Kept so <c>--browser</c> reproduces the old behavior.
        /// </summary>
        public static int RunBrowserMode()
        {
            var host = Start();
            if (host == null) return 1;

            Console.WriteLine("VDK Tool - web UI");
            Console.WriteLine($"Listening on {host.Url}");
            Console.WriteLine("Press Ctrl+C to stop.");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                host.Stop();
            };

            FirefoxLauncher.Launch(host.Url);

            // Block until cancelled; the accept loop runs on the background thread.
            cts.Token.WaitHandle.WaitOne();
            return 0;
        }

        /// <summary>
        /// Headless mode (<c>--server-only</c> / <c>--headless</c>): start the web
        /// host, print the listening URL, and block until Ctrl+C. Opens NO native
        /// window and NO browser. Intended for smoke-tests / CI where the JSON API
        /// must be reachable without any UI surface.
        /// </summary>
        public static int RunServerOnlyMode()
        {
            var host = Start();
            if (host == null) return 1;

            Console.WriteLine($"Listening on {host.Url}");
            Console.WriteLine("Server-only mode (no window, no browser). Press Ctrl+C to stop.");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                host.Stop();
            };

            cts.Token.WaitHandle.WaitOne();
            return 0;
        }

        private static int FindFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // --------------------------------------------------------------------
        // Routing
        // --------------------------------------------------------------------

        private static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;

            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                ServeStatic(ctx, "index.html");
                return;
            }

            if (path == "/api/health")
            {
                WriteJson(ctx, 200, new { ok = true, version = Version });
                return;
            }

            // App settings (default output folder): GET reads, POST persists.
            if (path == "/api/settings")
            {
                HandleSettings(ctx, method);
                return;
            }

            // Jobs: SSE stream (GET) and cancel (POST). Path = /api/jobs/{id}/events|cancel
            if (path.StartsWith("/api/jobs/"))
            {
                HandleJobs(ctx, path, method);
                return;
            }

            if (path.StartsWith("/api/"))
            {
                HandleApi(ctx, path);
                return;
            }

            if (method == "GET")
            {
                ServeStatic(ctx, path.TrimStart('/'));
                return;
            }

            WriteJson(ctx, 404, new { error = "not found" });
        }

        private static void HandleApi(HttpListenerContext ctx, string path)
        {
            JsonElement body = ReadBody(ctx);
            try
            {
                switch (path)
                {
                    case "/api/pick-file":
                    {
                        string p = NativeDialogs.PickFile(Str(body, "title"), Str(body, "filter"), Str(body, "initialDir"));
                        WriteJson(ctx, 200, new { path = p });
                        return;
                    }
                    case "/api/pick-folder":
                    {
                        string p = NativeDialogs.PickFolder(Str(body, "title"), Str(body, "initialDir"));
                        WriteJson(ctx, 200, new { path = p });
                        return;
                    }
                    case "/api/pick-save":
                    {
                        string p = NativeDialogs.PickSave(Str(body, "defaultName"), Str(body, "filter"), Str(body, "initialDir"));
                        WriteJson(ctx, 200, new { path = p });
                        return;
                    }
                    case "/api/vdk/list":
                    {
                        string vdkPath = Str(body, "vdkPath");
                        if (!File.Exists(vdkPath)) { WriteJson(ctx, 400, new { error = "vdkPath not found" }); return; }
                        var archive = VDKArchive.Load(vdkPath);
                        var files = archive.GetFileEntries();
                        var folders = archive.GetDirectoryEntries();
                        WriteJson(ctx, 200, new
                        {
                            version = archive.Version,
                            fileCount = files.Count,
                            folderCount = folders.Count,
                            files = files.Select(f => new { path = f.Path, size = f.UncompressedSize }).ToArray()
                        });
                        return;
                    }
                    case "/api/vdk/preview":
                    {
                        string vdkPath = Str(body, "vdkPath");
                        string entryPath = Str(body, "entryPath");
                        if (!File.Exists(vdkPath)) { WriteJson(ctx, 400, new { error = "vdkPath not found" }); return; }
                        if (string.IsNullOrEmpty(entryPath)) { WriteJson(ctx, 400, new { error = "entryPath required" }); return; }
                        int maxBytes = Int(body, "maxBytes") ?? 4096;
                        if (maxBytes < 0) maxBytes = 0;

                        var archive = VDKArchive.Load(vdkPath);
                        if (!archive.TryExtractEntryBytes(entryPath, out var bytes))
                        {
                            WriteJson(ctx, 400, new { error = "entryPath not found in archive" });
                            return;
                        }
                        bytes ??= Array.Empty<byte>();

                        int size = bytes.Length;
                        bool truncated = size > maxBytes;
                        int take = Math.Min(size, maxBytes);
                        var slice = new byte[take];
                        Array.Copy(bytes, slice, take);

                        WriteJson(ctx, 200, new
                        {
                            size,
                            truncated,
                            hex = ToHex(slice),
                            text = ToPrintableText(slice)
                        });
                        return;
                    }
                    case "/api/vdk/extract":
                    {
                        string vdkPath = Str(body, "vdkPath");
                        string outDir = Str(body, "outDir");
                        if (!File.Exists(vdkPath)) { WriteJson(ctx, 400, new { error = "vdkPath not found" }); return; }
                        if (string.IsNullOrEmpty(outDir)) { WriteJson(ctx, 400, new { error = "outDir required" }); return; }

                        var job = JobRegistry.Create();
                        job.Run((progress, token) =>
                        {
                            var archive = VDKArchive.Load(vdkPath);
                            int extracted = archive.ExtractAll(outDir, progress, token);
                            return new { extracted };
                        });
                        WriteJson(ctx, 202, new { jobId = job.Id });
                        return;
                    }
                    case "/api/vdk/pack":
                    {
                        string srcDir = Str(body, "srcDir");
                        string outPath = Str(body, "outPath");
                        if (!Directory.Exists(srcDir)) { WriteJson(ctx, 400, new { error = "srcDir not found" }); return; }
                        if (string.IsNullOrEmpty(outPath)) { WriteJson(ctx, 400, new { error = "outPath required" }); return; }

                        var job = JobRegistry.Create();
                        job.Run((progress, token) =>
                        {
                            // Sidecar-free reconstruction (convention A, level 1,
                            // empty dirs preserved) -> byte-exact 1:1.
                            var result = VDKBuilder.BuildFromDirectory(srcDir, outPath);
                            return new
                            {
                                files = result.FileCount,
                                folders = result.FolderCount,
                                bytes = result.TotalBytes
                            };
                        });
                        WriteJson(ctx, 202, new { jobId = job.Id });
                        return;
                    }
                    case "/api/ct/to":
                    {
                        string ctPath = Str(body, "ctPath");
                        string format = (Str(body, "format") ?? "xlsx").ToLowerInvariant();
                        if (!File.Exists(ctPath)) { WriteJson(ctx, 400, new { error = "ctPath not found" }); return; }
                        var proc = new CTProcessor();
                        proc.Read(ctPath);
                        string outPath = Path.ChangeExtension(ctPath, format == "csv" ? ".csv" : ".xlsx");
                        if (format == "csv") proc.ExportToCSV(outPath);
                        else proc.ExportToXLSX(outPath);
                        WriteJson(ctx, 200, new { outPath, columns = proc.Headers.Count, rows = proc.Rows.Count });
                        return;
                    }
                    case "/api/ct/from":
                    {
                        string inPath = Str(body, "inPath");
                        if (!File.Exists(inPath)) { WriteJson(ctx, 400, new { error = "inPath not found" }); return; }
                        string outPath = Str(body, "outPath");
                        if (string.IsNullOrEmpty(outPath)) outPath = Path.ChangeExtension(inPath, ".ct");
                        var proc = new CTProcessor();
                        string ext = Path.GetExtension(inPath).ToLowerInvariant();
                        if (ext == ".csv") proc.ImportFromCSV(inPath);
                        else proc.ImportFromXLSX(inPath);
                        proc.Write(outPath, proc.Headers, proc.Types, proc.Rows);
                        WriteJson(ctx, 200, new { outPath, columns = proc.Headers.Count, rows = proc.Rows.Count });
                        return;
                    }
                    case "/api/ct/batch":
                    {
                        string dir = Str(body, "dir");
                        string format = (Str(body, "format") ?? "xlsx").ToLowerInvariant();
                        if (!Directory.Exists(dir)) { WriteJson(ctx, 400, new { error = "dir not found" }); return; }

                        var job = JobRegistry.Create();
                        job.Run((progress, token) =>
                        {
                            var ctFiles = Directory.GetFiles(dir, "*.ct", SearchOption.AllDirectories);
                            int total = ctFiles.Length;
                            int ok = 0, failed = 0, current = 0;
                            var results = new List<object>();
                            foreach (var f in ctFiles)
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    var proc = new CTProcessor();
                                    proc.Read(f);
                                    string outPath = Path.ChangeExtension(f, format == "csv" ? ".csv" : ".xlsx");
                                    if (format == "csv") proc.ExportToCSV(outPath);
                                    else proc.ExportToXLSX(outPath);
                                    ok++;
                                    results.Add(new { file = f, ok = true });
                                }
                                catch (Exception ex)
                                {
                                    failed++;
                                    results.Add(new { file = f, ok = false, error = ex.Message });
                                }
                                current++;
                                progress.Report((current, total, f));
                            }
                            return new { total, ok, failed, results };
                        });
                        WriteJson(ctx, 202, new { jobId = job.Id });
                        return;
                    }
                    default:
                        WriteJson(ctx, 404, new { error = "unknown endpoint" });
                        return;
                }
            }
            catch (Exception ex)
            {
                WriteJson(ctx, 500, new { error = ex.Message });
            }
        }

        // --------------------------------------------------------------------
        // App settings (default output folder)
        //   GET  /api/settings -> { defaultOutputFolder }
        //   POST /api/settings { defaultOutputFolder } -> { defaultOutputFolder }
        // --------------------------------------------------------------------

        private static void HandleSettings(HttpListenerContext ctx, string method)
        {
            if (method == "GET")
            {
                var s = AppSettings.Load();
                WriteJson(ctx, 200, new { defaultOutputFolder = s.DefaultOutputFolder });
                return;
            }
            if (method == "POST")
            {
                JsonElement body = ReadBody(ctx);
                var saved = AppSettings.Save(new Settings { DefaultOutputFolder = Str(body, "defaultOutputFolder") ?? "" });
                WriteJson(ctx, 200, new { defaultOutputFolder = saved.DefaultOutputFolder });
                return;
            }
            WriteJson(ctx, 405, new { error = "method not allowed" });
        }

        // --------------------------------------------------------------------
        // Jobs: SSE event stream + cancel
        //   GET  /api/jobs/{id}/events  -> text/event-stream
        //   POST /api/jobs/{id}/cancel  -> cancels the job's CancellationToken
        // --------------------------------------------------------------------

        private static void HandleJobs(HttpListenerContext ctx, string path, string method)
        {
            // path = /api/jobs/{id}/{action}
            var rest = path.Substring("/api/jobs/".Length).Trim('/');
            int slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                WriteJson(ctx, 404, new { error = "unknown endpoint" });
                return;
            }
            string id = rest.Substring(0, slash);
            string action = rest.Substring(slash + 1);

            if (!JobRegistry.TryGet(id, out var job))
            {
                WriteJson(ctx, 404, new { error = "job not found" });
                return;
            }

            if (action == "cancel" && method == "POST")
            {
                job.Cancel();
                WriteJson(ctx, 200, new { ok = true, jobId = id });
                return;
            }

            if (action == "events" && method == "GET")
            {
                StreamJobEvents(ctx, job);
                return;
            }

            WriteJson(ctx, 404, new { error = "unknown endpoint" });
        }

        // Server-Sent Events stream over HttpListener. Replays any events already
        // buffered (so a client that connects after a few progress reports does not
        // miss them), then streams new ones until the job reaches a terminal event.
        private static void StreamJobEvents(HttpListenerContext ctx, Job job)
        {
            var resp = ctx.Response;
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream; charset=utf-8";
            resp.SendChunked = true;
            resp.Headers["Cache-Control"] = "no-cache";
            resp.Headers["Connection"] = "keep-alive";
            resp.Headers["X-Accel-Buffering"] = "no";

            var os = resp.OutputStream;
            int next = 0;
            try
            {
                // Initial keep-alive comment so the stream opens immediately.
                WriteSse(os, ": open\n\n");

                while (true)
                {
                    bool terminal = false;
                    foreach (var ev in job.DrainFrom(ref next))
                    {
                        WriteSse(os, "data: " + ev.Json + "\n\n");
                        if (ev.IsTerminal) terminal = true;
                    }
                    if (terminal) break;

                    // Wait for new events (or completion); periodic keep-alive on timeout.
                    if (!job.WaitForEvent(next, 15000))
                        WriteSse(os, ": keep-alive\n\n");
                }
            }
            catch
            {
                // Client disconnected mid-stream; nothing to do.
            }
            finally
            {
                try { os.Flush(); } catch { }
                try { os.Close(); } catch { }
            }
        }

        private static void WriteSse(Stream os, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            os.Write(b, 0, b.Length);
            os.Flush();
        }

        // --------------------------------------------------------------------
        // Static assets (embedded with disk fallback)
        // --------------------------------------------------------------------

        private static void ServeStatic(HttpListenerContext ctx, string rel)
        {
            if (string.IsNullOrEmpty(rel)) rel = "index.html";
            rel = rel.Replace('\\', '/').TrimStart('/');
            if (rel.Contains("..")) { WriteJson(ctx, 400, new { error = "bad path" }); return; }

            byte[] data = LoadAsset(rel);
            if (data == null)
            {
                WriteJson(ctx, 404, new { error = "asset not found: " + rel });
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = MimeFor(rel);
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
        }

        private static byte[] LoadAsset(string rel)
        {
            // Disk fallback first (dev convenience): wwwroot next to exe or in source.
            foreach (var baseDir in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            })
            {
                string p = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }

            // Embedded resource: LogicalName "wwwroot/<rel>". MSBuild's
            // %(RecursiveDir) bakes the build-host directory separator into the
            // logical name, so nested assets (e.g. vendor/pretext.mjs) embed as
            // "wwwroot/vendor\pretext.mjs" on Windows. Try the forward-slash name
            // first, then a backslash variant so subfolder assets resolve on any
            // build host. The URL contract is unchanged ("/vendor/pretext.mjs").
            var asm = Assembly.GetExecutingAssembly();
            foreach (var resName in new[] { "wwwroot/" + rel, "wwwroot/" + rel.Replace('/', '\\') })
            {
                using var s = asm.GetManifestResourceStream(resName);
                if (s == null) continue;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            return null;
        }

        private static string MimeFor(string rel)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            return ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".mjs" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream"
            };
        }

        // --------------------------------------------------------------------
        // JSON helpers
        // --------------------------------------------------------------------

        private static JsonElement ReadBody(HttpListenerContext ctx)
        {
            try
            {
                using var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
                string raw = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(raw)) return default;
                return JsonSerializer.Deserialize<JsonElement>(raw, JsonOpts);
            }
            catch { return default; }
        }

        private static string Str(JsonElement body, string name)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;
            if (body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        private static int? Int(JsonElement body, string name)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;
            if (body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
                return n;
            return null;
        }

        // Lowercase hex, no separators, of the given bytes.
        private static string ToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Best-effort printable rendering: ASCII printable chars (0x20-0x7E) kept,
        // everything else (control chars, high bytes) shown as '.'. Newline/tab also
        // become '.' so the result is a single inspectable line per the preview spec.
        private static string ToPrintableText(byte[] data)
        {
            var sb = new StringBuilder(data.Length);
            foreach (byte b in data)
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            return sb.ToString();
        }

        private static void WriteJson(HttpListenerContext ctx, int status, object payload)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
        }
    }

    // ========================================================================
    // Background job + SSE event infrastructure (in-memory).
    //
    // A Job runs one long Core operation on a thread-pool task with an
    // IProgress<(int,int,string)> bridged to SSE "progress" events and a
    // CancellationToken driven by /api/jobs/{id}/cancel. Every event is appended
    // to an in-memory list so a late-connecting SSE client can replay the ones it
    // missed. Jobs live for a short grace period after completion, then are
    // reaped so the dictionary does not grow unbounded.
    // ========================================================================

    internal sealed class JobEvent
    {
        public string Json;        // pre-serialized SSE payload (data: <Json>)
        public bool IsTerminal;    // done | error | cancelled
    }

    internal sealed class Job
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public string Id { get; } = Guid.NewGuid().ToString("N");

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<JobEvent> _events = new List<JobEvent>();
        private readonly object _lock = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        /// <summary>UTC time the job reached a terminal state (for reaping); null while running.</summary>
        public DateTime? CompletedUtc { get; private set; }

        public void Cancel()
        {
            try { _cts.Cancel(); } catch { }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the thread pool. The function receives an
        /// IProgress that emits "progress" SSE events and the job's CancellationToken,
        /// and returns the result object that becomes the "done" event's result.
        /// </summary>
        public void Run(Func<IProgress<(int current, int total, string item)>, CancellationToken, object> work)
        {
            var progress = new Progress<(int current, int total, string item)>(p =>
                Emit(new { type = "progress", current = p.current, total = p.total, item = p.item }, terminal: false));

            Task.Run(() =>
            {
                try
                {
                    object result = work(progress, _cts.Token);
                    Emit(new { type = "done", result }, terminal: true);
                }
                catch (OperationCanceledException)
                {
                    Emit(new { type = "cancelled" }, terminal: true);
                }
                catch (Exception ex)
                {
                    Emit(new { type = "error", error = ex.Message }, terminal: true);
                }
                finally
                {
                    CompletedUtc = DateTime.UtcNow;
                    JobRegistry.Reap();
                }
            });
        }

        private void Emit(object payload, bool terminal)
        {
            var ev = new JobEvent
            {
                Json = JsonSerializer.Serialize(payload, JsonOpts),
                IsTerminal = terminal
            };
            lock (_lock) _events.Add(ev);
            _signal.Set();
        }

        /// <summary>
        /// Returns every event from index <paramref name="from"/> onward, advancing
        /// <paramref name="from"/> past them. Safe to call repeatedly.
        /// </summary>
        public IEnumerable<JobEvent> DrainFrom(ref int from)
        {
            List<JobEvent> outList;
            lock (_lock)
            {
                if (from >= _events.Count) return Array.Empty<JobEvent>();
                outList = _events.GetRange(from, _events.Count - from);
                from = _events.Count;
            }
            return outList;
        }

        /// <summary>
        /// Blocks until at least <paramref name="expectedAtLeast"/>+1 events exist or
        /// the timeout elapses. Returns true if new events became available.
        /// </summary>
        public bool WaitForEvent(int have, int timeoutMs)
        {
            lock (_lock)
            {
                if (_events.Count > have) return true;
            }
            bool got = _signal.WaitOne(timeoutMs);
            lock (_lock)
            {
                return _events.Count > have;
            }
        }
    }

    internal static class JobRegistry
    {
        private static readonly ConcurrentDictionary<string, Job> Jobs = new ConcurrentDictionary<string, Job>();

        // Completed jobs are kept this long so SSE clients can still fetch the final
        // event (and result) before the job is reaped.
        private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

        public static Job Create()
        {
            var job = new Job();
            Jobs[job.Id] = job;
            return job;
        }

        public static bool TryGet(string id, out Job job) => Jobs.TryGetValue(id, out job);

        /// <summary>Removes completed jobs older than the retention window.</summary>
        public static void Reap()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in Jobs)
            {
                var c = kv.Value.CompletedUtc;
                if (c.HasValue && now - c.Value > Retention)
                    Jobs.TryRemove(kv.Key, out _);
            }
        }
    }
}
