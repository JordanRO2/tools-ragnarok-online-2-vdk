using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VDKTool.Core;

namespace VDKTool.Tests
{
    static class Program
    {
        static string Sha(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        // Robustly resolve tests/fixtures/ relative to the repo, regardless of
        // the working directory the harness is launched from. We walk up from
        // the executable location (and from the current directory as a fallback)
        // looking for a directory that contains tests/fixtures/sample.vdk.
        static string FindFixturesDir()
        {
            var candidates = new List<string>();
            candidates.Add(AppContext.BaseDirectory);
            candidates.Add(Directory.GetCurrentDirectory());

            foreach (var start in candidates)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string fixtures = Path.Combine(dir.FullName, "tests", "fixtures");
                    if (File.Exists(Path.Combine(fixtures, "sample.vdk")) &&
                        File.Exists(Path.Combine(fixtures, "sample.ct")))
                        return fixtures;
                    dir = dir.Parent;
                }
            }
            return null;
        }

        static int Main(string[] args)
        {
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            string temp = Path.Combine(Path.GetTempPath(), "vdktest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            Console.WriteLine("Temp: " + temp);

            int failures = 0;

            try
            {
                // --- DEFAULT: self-contained regression over committed fixtures. ---
                string fixtures = FindFixturesDir();
                if (fixtures == null)
                {
                    Console.WriteLine("FATAL: could not locate tests/fixtures (sample.vdk + sample.ct).");
                    failures++;
                }
                else
                {
                    Console.WriteLine("Fixtures: " + fixtures);
                    string sampleVdk = Path.Combine(fixtures, "sample.vdk");
                    string sampleCt = Path.Combine(fixtures, "sample.ct");

                    failures += FixtureVdkReconstruct(temp, sampleVdk); // (a) + (b)
                    failures += FixtureCtRoundTrip(temp, sampleCt);     // (d) + (e)
                }

                // --- OPTIONAL: legacy "against the client" mode, behind an env var. ---
                // Enable with VDK_CLIENT_TEST=1 (uses the hardcoded client paths
                // below) or VDK_CLIENT_TEST=<path-to-a-client-root>. Never runs in
                // the default / CI path, so the suite stays self-contained.
                string clientMode = Environment.GetEnvironmentVariable("VDK_CLIENT_TEST");
                if (!string.IsNullOrEmpty(clientMode))
                {
                    Console.WriteLine("\n### VDK_CLIENT_TEST set -> running legacy client round-trips ###");
                    failures += RunClientTests(temp);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                failures++;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"FAILURES: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // =====================================================================
        // Self-contained fixture tests
        // =====================================================================

        // (a) Sidecar-free reconstruction round-trip: extract the canonical fixture
        //     (files + empty dirs, NO manifest) then VDKBuilder.BuildFromDirectory
        //     it back and assert SHA256 == the original fixture.
        // (b) extract decodes the expected file content (known assertable bytes),
        //     including the preserved empty directory.
        //
        // The fixture sample.vdk is itself produced by VDKBuilder (canonical format:
        // convention A, level 1, empty dirs preserved); see tests/fixtures/README.md.
        static int FixtureVdkReconstruct(string temp, string sampleVdk)
        {
            Console.WriteLine("\n=== [a/b] VDK fixture: extract -> reconstruct 1:1 + content + empty dirs ===");
            string work = Path.Combine(temp, "vdk_rt");
            Directory.CreateDirectory(work);
            string localVdk = Path.Combine(work, "sample.VDK");
            File.Copy(sampleVdk, localVdk, true);

            string extractDir = Path.Combine(work, "extracted");
            string repacked = Path.Combine(work, "sample.repacked.VDK");

            int fail = 0;
            try
            {
                var archive = VDKArchive.Load(localVdk);
                int extracted = archive.ExtractAll(extractDir);
                Console.WriteLine($"  extracted {extracted} files");

                // (b) Decode + verify a couple of known files from the fixture.
                fail += VerifyExtractedContent(extractDir);

                // (b) The empty directory must have been materialized on disk.
                string emptyDir = Path.Combine(extractDir, "empty_dir");
                if (!Directory.Exists(emptyDir))
                {
                    Console.WriteLine("  EMPTY-DIR FAIL: empty_dir/ was not created on extract");
                    fail++;
                }
                else if (Directory.EnumerateFileSystemEntries(emptyDir).GetEnumerator().MoveNext())
                {
                    Console.WriteLine("  EMPTY-DIR FAIL: empty_dir/ is not empty");
                    fail++;
                }
                else
                {
                    Console.WriteLine("  empty directory empty_dir/ preserved OK");
                }

                // (a) Reconstruct purely from the folder tree and compare SHA256.
                var res = VDKBuilder.BuildFromDirectory(extractDir, repacked);
                Console.WriteLine($"  reconstructed files={res.FileCount} folders={res.FolderCount} bytes={res.TotalBytes}");

                string a = Sha(localVdk), b = Sha(repacked);
                if (a == b)
                {
                    Console.WriteLine("  VDK reconstruct: SHA256 BYTE-IDENTICAL 1:1 OK");
                }
                else
                {
                    Console.WriteLine("  VDK reconstruct: MISMATCH");
                    Console.WriteLine("    orig sha=" + a);
                    Console.WriteLine("    pack sha=" + b);
                    Console.WriteLine("    " + DescribeFirstDiff(localVdk, repacked));
                    fail++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  EXC: " + ex);
                fail++;
            }
            return fail;
        }

        // (b) Verify known fixture content decompresses to the expected bytes.
        static int VerifyExtractedContent(string extractDir)
        {
            int fail = 0;

            // textures/wood.dds : 1024 bytes, b[i] = (0x55 + i*7) & 0xFF
            fail += CheckGenerated(extractDir, Path.Combine("textures", "wood.dds"), 1024, 0x55, 7);
            // models/anim.dds : 600 bytes, b[i] = (0xA1 + i*7) & 0xFF
            fail += CheckGenerated(extractDir, Path.Combine("models", "anim.dds"), 600, 0xA1, 7);

            // models/mesh.nif : ASCII, starts "MODEL-MESH-DATA-VERTICES-"
            fail += CheckPrefix(extractDir, Path.Combine("models", "mesh.nif"), "MODEL-MESH-DATA-VERTICES-");
            // zz_readme.txt : ASCII, starts "RO2 VDK fixture."
            fail += CheckPrefix(extractDir, "zz_readme.txt", "RO2 VDK fixture.");

            // EUC-KR (non-ASCII) filename with UTF-8 body content.
            string koreanName = "한글텍스처.dds"; // 한글텍스처.dds
            string koreanBody = "한글 파일 내용 (korean file body) 12345";
            string kpath = Path.Combine(extractDir, "textures", koreanName);
            if (!File.Exists(kpath))
            {
                Console.WriteLine("  CONTENT FAIL: missing EUC-KR named file textures/" + koreanName);
                fail++;
            }
            else
            {
                string got = Encoding.UTF8.GetString(File.ReadAllBytes(kpath));
                if (got != koreanBody)
                {
                    Console.WriteLine("  CONTENT FAIL: korean body mismatch");
                    fail++;
                }
            }

            if (fail == 0) Console.WriteLine("  extracted content: all known files decode OK");
            return fail;
        }

        static int CheckGenerated(string root, string rel, int len, int seed, int step)
        {
            string p = Path.Combine(root, rel);
            if (!File.Exists(p)) { Console.WriteLine("  CONTENT FAIL: missing " + rel); return 1; }
            byte[] b = File.ReadAllBytes(p);
            if (b.Length != len) { Console.WriteLine($"  CONTENT FAIL: {rel} len {b.Length} != {len}"); return 1; }
            for (int i = 0; i < len; i++)
            {
                byte expect = (byte)((seed + i * step) & 0xFF);
                if (b[i] != expect) { Console.WriteLine($"  CONTENT FAIL: {rel} byte@{i} {b[i]:X2}!={expect:X2}"); return 1; }
            }
            return 0;
        }

        static int CheckPrefix(string root, string rel, string prefix)
        {
            string p = Path.Combine(root, rel);
            if (!File.Exists(p)) { Console.WriteLine("  CONTENT FAIL: missing " + rel); return 1; }
            string txt = Encoding.ASCII.GetString(File.ReadAllBytes(p));
            if (!txt.StartsWith(prefix, StringComparison.Ordinal))
            {
                Console.WriteLine($"  CONTENT FAIL: {rel} does not start with \"{prefix}\"");
                return 1;
            }
            return 0;
        }

        // (d) CT ct -> xlsx -> ct is SHA256-identical to sample.ct.
        // (e) STRING cell "0" survives as "0" (not empty).
        static int FixtureCtRoundTrip(string temp, string sampleCt)
        {
            Console.WriteLine("\n=== [d/e] CT fixture: ct -> xlsx -> ct 1:1 + STRING \"0\" ===");
            string work = Path.Combine(temp, "ct_rt");
            Directory.CreateDirectory(work);
            string localCt = Path.Combine(work, "sample.ct");
            File.Copy(sampleCt, localCt, true);
            string xlsx = Path.Combine(work, "sample.xlsx");
            string rebuilt = Path.Combine(work, "sample.rebuilt.ct");

            int fail = 0;
            try
            {
                var p = new CTProcessor();
                p.Read(localCt);

                // (e) Confirm a STRING cell holding "0" was read as "0", not "".
                fail += VerifyStringZero(p);

                p.ExportToXLSX(xlsx);

                var q = new CTProcessor();
                q.ImportFromXLSX(xlsx);

                // (e) Confirm the "0" STRING also survives the XLSX round-trip in memory.
                fail += VerifyStringZero(q);

                q.Write(rebuilt, q.Headers, q.Types, q.Rows);

                string a = Sha(localCt), b = Sha(rebuilt);
                if (a == b)
                {
                    Console.WriteLine("  CT round-trip: SHA256 BYTE-IDENTICAL 1:1 OK");
                }
                else
                {
                    Console.WriteLine("  CT round-trip: MISMATCH");
                    Console.WriteLine("    orig sha=" + a);
                    Console.WriteLine("    pack sha=" + b);
                    Console.WriteLine("    " + DescribeFirstDiff(localCt, rebuilt));
                    fail++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  EXC: " + ex);
                fail++;
            }
            return fail;
        }

        // (e) Find the STRING column and assert that a literal "0" exists and is
        //     preserved exactly (the historical corruption turned it into "").
        static int VerifyStringZero(CTProcessor ct)
        {
            int stringCol = -1;
            for (int c = 0; c < ct.Types.Count; c++)
            {
                if (string.Equals(ct.Types[c], "STRING", StringComparison.OrdinalIgnoreCase))
                {
                    stringCol = c;
                    break;
                }
            }
            if (stringCol < 0)
            {
                Console.WriteLine("  STRING\"0\" FAIL: no STRING column found");
                return 1;
            }

            bool foundZero = false;
            foreach (var row in ct.Rows)
            {
                if (stringCol < row.Count && row[stringCol] == "0")
                {
                    foundZero = true;
                    break;
                }
            }
            if (!foundZero)
            {
                Console.WriteLine("  STRING\"0\" FAIL: no STRING cell equal to \"0\" (corruption to empty?)");
                return 1;
            }
            Console.WriteLine("  STRING cell \"0\" preserved as \"0\" OK");
            return 0;
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // =====================================================================
        // Legacy "against the client" tests (only via VDK_CLIENT_TEST env var)
        // =====================================================================

        static int RunClientTests(string temp)
        {
            int failures = 0;
            failures += RunCtRoundTrip(temp);
            failures += RunVdkRoundTrip(temp, "ASSET",
                @"C:\Users\Darkr\Proyectos\ReverseEngineering\ragnarok-online-2\client-patches\RO2-Patches\ASSET.VDK");
            failures += RunVdkRoundTrip(temp, "OBJECT_STRUCTURE",
                @"C:\Users\Darkr\Proyectos\ReverseEngineering\ragnarok-online-2\clients\ClientFiles\Ragnarok Online 2\Data\OBJECT_STRUCTURE.VDK");
            return failures;
        }

        static int RunCtRoundTrip(string temp)
        {
            Console.WriteLine("\n=== [client] CT round-trip (ct2xlsx -> xlsx2ct) ===");
            string srcDir = @"C:\Users\Darkr\Proyectos\ReverseEngineering\ragnarok-online-2\client-patches\RO2-Patches\ASSET_VDK\ASSET";
            if (!Directory.Exists(srcDir)) { Console.WriteLine("  SOURCE MISSING: " + srcDir); return 1; }
            var ctFiles = new List<string>(Directory.GetFiles(srcDir, "*.ct"));
            ctFiles.Sort(StringComparer.OrdinalIgnoreCase);
            int sample = 40;
            var envAll = Environment.GetEnvironmentVariable("CT_ALL");
            if (envAll == "1") sample = ctFiles.Count;
            if (ctFiles.Count > sample) ctFiles = ctFiles.GetRange(0, sample);

            string work = Path.Combine(temp, "ct");
            Directory.CreateDirectory(work);

            int identical = 0, total = 0, fail = 0;
            var diffs = new List<string>();

            foreach (var ct in ctFiles)
            {
                total++;
                string name = Path.GetFileName(ct);
                string origCopy = Path.Combine(work, name);
                File.Copy(ct, origCopy, true);
                string xlsx = Path.Combine(work, Path.GetFileNameWithoutExtension(name) + ".xlsx");
                string rebuilt = Path.Combine(work, Path.GetFileNameWithoutExtension(name) + ".rebuilt.ct");

                try
                {
                    var p = new CTProcessor();
                    p.Read(origCopy);
                    p.ExportToXLSX(xlsx);

                    var q = new CTProcessor();
                    q.ImportFromXLSX(xlsx);
                    q.Write(rebuilt, q.Headers, q.Types, q.Rows);

                    if (Sha(origCopy) == Sha(rebuilt)) identical++;
                    else
                    {
                        fail++;
                        if (diffs.Count < 10) diffs.Add(name + " (" + DescribeFirstDiff(origCopy, rebuilt) + ")");
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    if (diffs.Count < 10) diffs.Add(name + " EXC: " + ex.Message);
                }
            }

            Console.WriteLine($"CT identical: {identical}/{total}");
            foreach (var d in diffs) Console.WriteLine("  DIFF " + d);
            return fail;
        }

        static int RunVdkRoundTrip(string temp, string label, string srcVdk)
        {
            Console.WriteLine($"\n=== [client] VDK round-trip ({label}): extract -> reconstruct ===");
            if (!File.Exists(srcVdk))
            {
                Console.WriteLine("  SOURCE MISSING: " + srcVdk);
                return 1;
            }

            string work = Path.Combine(temp, label);
            Directory.CreateDirectory(work);
            string localVdk = Path.Combine(work, label + ".VDK");
            File.Copy(srcVdk, localVdk, true);

            string extractDir = Path.Combine(work, "extracted");
            string repacked = Path.Combine(work, label + ".repacked.VDK");

            try
            {
                var archive = VDKArchive.Load(localVdk);
                archive.ExtractAll(extractDir);

                var res = VDKBuilder.BuildFromDirectory(extractDir, repacked);
                Console.WriteLine($"  reconstructed files={res.FileCount} folders={res.FolderCount}");

                string a = Sha(localVdk), b = Sha(repacked);
                if (a == b)
                {
                    Console.WriteLine($"  {label}: BYTE-IDENTICAL 1:1 OK");
                    return 0;
                }
                else
                {
                    Console.WriteLine($"  {label}: MISMATCH");
                    Console.WriteLine("    orig sha=" + a);
                    Console.WriteLine("    pack sha=" + b);
                    Console.WriteLine("    " + DescribeFirstDiff(localVdk, repacked));
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  EXC: " + ex);
                return 1;
            }
        }

        static string DescribeFirstDiff(string a, string b)
        {
            byte[] da = File.ReadAllBytes(a), db = File.ReadAllBytes(b);
            if (da.Length != db.Length)
            {
                int max = Math.Min(da.Length, db.Length);
                int fd = -1;
                for (int i = 0; i < max; i++) if (da[i] != db[i]) { fd = i; break; }
                return $"len {da.Length} vs {db.Length}, firstdiff@" + (fd < 0 ? "EOF" : "0x" + fd.ToString("X"));
            }
            for (int i = 0; i < da.Length; i++)
                if (da[i] != db[i])
                    return $"len equal {da.Length}, firstdiff@0x{i:X} {da[i]:X2} vs {db[i]:X2}";
            return "equal";
        }
    }
}
