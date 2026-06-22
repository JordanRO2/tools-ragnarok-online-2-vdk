using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VDKTool.Core
{
    /// <summary>
    /// Sidecar-free 1:1 VDK builder. Reconstructs the entire .vdk (header +
    /// hierarchical entry table + payloads + flat hash table) PURELY from a directory
    /// of extracted files and folders, using the reverse-engineered CVDisk algorithm.
    /// This is an EXACT port of _rebuild.py's convention-A path (validated
    /// byte-identical on the clean-built client VDKs) — the algorithm is ported
    /// as-is, not reinvented.
    ///
    /// CERO SIDECAR. No .vdkmanifest, no .vdkorder. The reconstruction is fixed to:
    ///   - convention A (modern packer): dir dataOffset = position of its "."; children
    ///     (dirs + files interleaved) sorted by ASCII-uppercased EUC-KR name bytes.
    ///   - compression level 1 (zlib BestSpeed, the level the modern client packer
    ///     uses); RAW store when compressed >= uncompressed.
    ///   - empty directories are preserved (the real folder tree is walked, not just
    ///     the file set).
    ///   - VDISK1.1 header; u32@8 = align_down(256, entry position of the last file in
    ///     flat-table order) — reverse-engineered to match Gravity's packer byte-for-byte.
    ///
    /// See docs/VDK_FORMAT_RE.md for the format spec.
    /// </summary>
    public static class VDKBuilder
    {
        // Fixed reconstruction parameters (convention A, level 1, VDISK1.1).
        private const string VERSION = "VDISK1.1";
        private const uint U32_AT_8 = 0u;
        private const int LEVEL = 1;

        private const int ENTRY = 145;
        private const int NAME = 128;

        // MSVC stdext::hash_map (Dinkumware) prime table used by CVDisk's hash_map.
        private static readonly int[] PRIMES =
        {
            17,23,29,37,41,53,67,83,103,131,163,211,257,331,409,521,647,821,1031,1291,1627,
            2053,2591,3251,4099,5167,6521,8209,10331,13007,16411,20663,26017,32771,41299,52021,65537,
            82571,104033,131101,165161,209987,263171,330287,413857,519277,651407,816521,1023631,1284215,
            1605269,2006597
        };

        private static Encoding _euckr;
        private static Encoding EucKr
        {
            get
            {
                if (_euckr == null)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    _euckr = Encoding.GetEncoding(51949);
                }
                return _euckr;
            }
        }

        public sealed class BuildResult
        {
            public string OutputPath;
            public uint FileCount;
            public uint FolderCount;   // real dirs only
            public long FlatTableOffset;
            public long TotalBytes;
        }

        // ------------------------------------------------------------------
        // Tree node (mirrors _rebuild.py's Node)
        // ------------------------------------------------------------------
        private sealed class Node
        {
            public string Name;
            public bool IsDir;
            public Dictionary<string, Node> Children = new Dictionary<string, Node>(StringComparer.Ordinal);
            public byte[] Data;       // file payload (uncompressed)
            public byte[] Comp;       // compressed (or raw) bytes
            public uint Unc;
            public uint CSize;
        }

        // Linear entry record (mirrors _rebuild.py's elist tuples)
        private sealed class Ent
        {
            public bool IsDir;
            public string Name;       // "." / ".." / real name
            public Node Node;
            public string Role;       // rootdot / dot / dotdot / dir / file
            public long Pos;          // absolute position of the 145-byte entry
            public uint DataOff;
            public uint NextOff;
        }

        // ------------------------------------------------------------------
        // EUC-KR byte helpers
        // ------------------------------------------------------------------
        private static byte[] EucKrBytes(string s) => EucKr.GetBytes(s);

        // ASCII-uppercased EUC-KR name bytes — the packer's SafeStrToUpper compare key.
        private static byte[] UpperKey(string name)
        {
            byte[] b = EucKr.GetBytes(name);
            byte[] outb = new byte[b.Length];
            for (int i = 0; i < b.Length; i++)
            {
                byte c = b[i];
                outb[i] = (c >= 0x61 && c <= 0x7A) ? (byte)(c - 0x20) : c;
            }
            return outb;
        }

        private static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }

        // Children sorted ascending by ASCII-uppercased EUC-KR name bytes.
        private static List<Node> SortedChildren(Node node)
        {
            var list = node.Children.Values.ToList();
            list.Sort((x, y) => CompareBytes(UpperKey(x.Name), UpperKey(y.Name)));
            return list;
        }

        // ------------------------------------------------------------------
        // Path hash (CVDisk sub_DEAFB0): on uppercased full path, "/" separators,
        // EUC-KR bytes. Only full 4-byte LE words; trailing len%4 bytes ignored.
        // ------------------------------------------------------------------
        private static uint Ro2Hash(byte[] upperPathBytes)
        {
            uint h = 0;
            int words = upperPathBytes.Length >> 2;
            for (int i = 0; i < words; i++)
            {
                uint w = (uint)(upperPathBytes[4 * i]
                                | (upperPathBytes[4 * i + 1] << 8)
                                | (upperPathBytes[4 * i + 2] << 16)
                                | (upperPathBytes[4 * i + 3] << 24));
                h = unchecked(h + w * (uint)(i + 1));
            }
            return h;
        }

        private static int NewBuckets(int count)
        {
            int t = (int)(count / 0.75);
            foreach (int p in PRIMES)
                if (t <= p) return p;
            return t;
        }

        // Reproduces _rebuild.py's flat_order: emulates the MSVC hash_map insertion +
        // bucket-order serialization. Input = file relpaths ("/" separators) in
        // HIERARCHICAL order. Output = relpaths in on-disk flat-table order.
        private static List<string> FlatOrder(List<string> fileHierOrder)
        {
            int B = 17;
            var bk = new List<List<(uint h, string p)>>();
            for (int i = 0; i < B; i++) bk.Add(new List<(uint, string)>());
            int count = 0;

            foreach (string p in fileHierOrder)
            {
                byte[] key = UpperKey(p);   // p.upper() then EUC-KR; ASCII-upper of bytes is equivalent for "/"-joined names
                uint h = Ro2Hash(key);
                bk[(int)(h % (uint)B)].Insert(0, (h, p));
                count++;
                if (count > B * 2.25)
                {
                    int nb = NewBuckets(count);
                    if (nb != B)
                    {
                        var ng = new List<List<(uint, string)>>();
                        for (int i = 0; i < nb; i++) ng.Add(new List<(uint, string)>());
                        for (int i = 0; i < B; i++)
                            foreach (var nd in bk[i])
                                ng[(int)(nd.Item1 % (uint)nb)].Insert(0, nd);
                        bk = ng; B = nb;
                    }
                }
            }

            var outp = new List<string>();
            for (int i = 0; i < B; i++)
                foreach (var nd in bk[i])
                    outp.Add(nd.Item2);
            return outp;
        }

        // ------------------------------------------------------------------
        // Build the directory tree from relpaths. Empty-directory relpaths
        // (dirRels) are materialized as dir nodes so the on-disk tree includes
        // folders that hold no files — mirrors _rebuild.py's build_tree which
        // ensure()s every dir before adding files.
        // ------------------------------------------------------------------
        private static Node BuildTree(List<(string rel, byte[] data)> files, List<string> dirRels = null)
        {
            var root = new Node { Name = "", IsDir = true };

            Node Ensure(string path)
            {
                var cur = root;
                if (!string.IsNullOrEmpty(path))
                {
                    foreach (string p in path.Split('/'))
                    {
                        if (p.Length == 0) continue;
                        if (!cur.Children.TryGetValue(p, out var nx))
                        {
                            nx = new Node { Name = p, IsDir = true };
                            cur.Children[p] = nx;
                        }
                        cur = nx;
                    }
                }
                return cur;
            }

            if (dirRels != null)
                foreach (var d in dirRels) Ensure(d);   // include empty directories

            foreach (var (rel, data) in files)
            {
                string[] parts = rel.Split('/');
                var cur = Ensure(string.Join("/", parts, 0, parts.Length - 1));
                string leafName = parts[parts.Length - 1];
                cur.Children[leafName] = new Node { Name = leafName, IsDir = false, Data = data };
            }
            return root;
        }

        // ------------------------------------------------------------------
        // Public API: build a VDK PURELY from a directory of extracted files
        // and folders (convention A, level 1, empty dirs preserved). No sidecar.
        // ------------------------------------------------------------------
        public static BuildResult BuildFromDirectory(string dir, string outPath)
        {
            CollectFromDirectory(dir, out var files, out var dirs);
            return Build(files, dirs, outPath);
        }

        // Enumerate the real folder tree under dir.
        //   files: (relpath with '/', uncompressed bytes) for every regular file.
        //   dirs : every subdirectory relpath (so empty dirs are preserved).
        private static void CollectFromDirectory(string dir,
            out List<(string rel, byte[] data)> files, out List<string> dirs)
        {
            files = new List<(string, byte[])>();
            dirs = new List<string>();
            string root = Path.GetFullPath(dir);

            foreach (string dp in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                string rel = dp.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (rel.Length > 0) dirs.Add(rel);
            }
            foreach (string fp in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = fp.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                files.Add((rel, File.ReadAllBytes(fp)));
            }
        }

        // ------------------------------------------------------------------
        // Core build (EXACT port of _rebuild.py's convention-A rebuild()).
        // ------------------------------------------------------------------
        public static BuildResult Build(List<(string rel, byte[] data)> files,
            List<string> dirs, string outPath)
        {
            const string version = VERSION;
            const uint u32at8 = U32_AT_8;
            const int level = LEVEL;

            var root = BuildTree(files, dirs);

            int hdr = version == "VDISK1.1" ? 28 : 24;

            var elist = new List<Ent>();
            var fileHierOrder = new List<string>();

            Ent Emit(bool isDir, string name, Node node, string role)
            {
                var e = new Ent { IsDir = isDir, Name = name, Node = node, Role = role };
                elist.Add(e);
                return e;
            }

            // root: single "." then root children subtrees
            Emit(true, ".", root, "rootdot");

            // recursive walk (children of a real dir already emitted as dir entry by caller)
            void Walk(Node node, string cp)
            {
                Emit(true, ".", node, "dot");
                Emit(true, "..", node, "dotdot");
                foreach (var ch in SortedChildren(node))
                {
                    if (ch.IsDir)
                    {
                        Emit(true, ch.Name, ch, "dir");
                        Walk(ch, cp + ch.Name + "/");
                    }
                    else
                    {
                        Emit(false, ch.Name, ch, "file");
                        fileHierOrder.Add(cp + ch.Name);
                    }
                }
            }

            foreach (var ch in SortedChildren(root))
            {
                if (ch.IsDir)
                {
                    Emit(true, ch.Name, ch, "dir");
                    Walk(ch, ch.Name + "/");
                }
                else
                {
                    Emit(false, ch.Name, ch, "file");
                    fileHierOrder.Add(ch.Name);
                }
            }

            // compress files & compute sizes; assign positions
            long pos = hdr;
            foreach (var e in elist)
            {
                e.Pos = pos;
                pos += ENTRY;
                if (e.Role == "file")
                {
                    CompressLevel(e.Node.Data, level, out byte[] cb, out uint unc, out uint cs);
                    e.Node.Comp = cb; e.Node.Unc = unc; e.Node.CSize = cs;
                    pos += cs;
                }
            }
            long flatOff = pos;

            // Build lookups: node -> dot/dotdot/entry index
            var dotIdx = new Dictionary<Node, int>();
            var dotdotIdx = new Dictionary<Node, int>();
            var entryIdx = new Dictionary<Node, int>();
            for (int i = 0; i < elist.Count; i++)
            {
                var e = elist[i];
                switch (e.Role)
                {
                    case "rootdot": break;
                    case "dot": dotIdx[e.Node] = i; break;
                    case "dotdot": dotdotIdx[e.Node] = i; break;
                    case "dir":
                    case "file": entryIdx[e.Node] = i; break;
                }
            }

            // root chain: [rootdot] + each root child's entry
            var rootChildren = SortedChildren(root);
            var rootChain = new List<int> { 0 };
            foreach (var c in rootChildren) rootChain.Add(entryIdx[c]);
            for (int k = 0; k < rootChain.Count; k++)
            {
                int ci = rootChain[k];
                uint nx = (k + 1 < rootChain.Count) ? (uint)elist[rootChain[k + 1]].Pos : 0u;
                if (ci == 0)
                {
                    // rootdot: dataoff = self pos, nextoff
                    elist[ci].DataOff = (uint)elist[0].Pos;
                    elist[ci].NextOff = nx;
                }
                else
                {
                    var e = elist[ci];
                    if (e.IsDir)
                    {
                        e.DataOff = (uint)elist[dotIdx[e.Node]].Pos; // dir -> its "." pos
                        e.NextOff = nx;
                    }
                    else
                    {
                        e.DataOff = 0;
                        e.NextOff = nx;
                    }
                }
            }

            // each real dir
            void LinkDir(Node node)
            {
                int d = dotIdx[node];
                int dd = dotdotIdx[node];
                var kids = SortedChildren(node);
                var chain = new List<int> { d, dd };
                foreach (var c in kids) chain.Add(entryIdx[c]);
                for (int k = 0; k < chain.Count; k++)
                {
                    int ci = chain[k];
                    uint nx = (k + 1 < chain.Count) ? (uint)elist[chain[k + 1]].Pos : 0u;
                    var e = elist[ci];
                    if (e.Role == "dot")
                    {
                        e.DataOff = (uint)e.Pos;        // self
                        e.NextOff = nx;
                    }
                    else if (e.Role == "dotdot")
                    {
                        // parent's "." pos. The parent of `node` is the dir whose dot chain
                        // contains `node`; we recover it from node's recorded parent dot.
                        e.DataOff = (uint)elist[_parentDot[node]].Pos;
                        e.NextOff = nx;
                    }
                    else if (e.IsDir)
                    {
                        e.DataOff = (uint)elist[dotIdx[e.Node]].Pos;
                        e.NextOff = nx;
                    }
                    else
                    {
                        e.DataOff = 0;
                        e.NextOff = nx;
                    }
                }
                foreach (var c in kids)
                    if (c.IsDir) LinkDir(c);
            }

            // We need the parent-dot index per dir node. Compute it by a structural walk
            // mirroring the emit order: each real dir D records its parent dir's dot index.
            _parentDot = new Dictionary<Node, int>();
            void RecordParents(Node node, int parentDotIndex)
            {
                // node is a real dir already emitted; its own dot is dotIdx[node]
                foreach (var c in SortedChildren(node))
                {
                    if (c.IsDir)
                    {
                        _parentDot[c] = dotIdx[node];
                        RecordParents(c, dotIdx[node]);
                    }
                }
            }
            // root's dot is elist[0] (rootdot). Root children's parent dot = rootdot index 0.
            foreach (var c in rootChildren)
            {
                if (c.IsDir)
                {
                    _parentDot[c] = 0;
                    RecordParents(c, 0);
                }
            }

            foreach (var c in rootChildren)
                if (c.IsDir) LinkDir(c);

            // counts
            uint fileCount = (uint)elist.Count(e => e.Role == "file");
            uint folderCount = (uint)elist.Count(e => e.Role == "dir"); // real dirs only

            // ---- write the file
            var outBuf = new List<byte>();

            // header
            byte[] verb = Encoding.ASCII.GetBytes(version);
            byte[] verPad = new byte[8];
            Array.Copy(verb, verPad, Math.Min(verb.Length, 8));
            outBuf.AddRange(verPad);
            outBuf.AddRange(BitConverter.GetBytes(u32at8));
            outBuf.AddRange(BitConverter.GetBytes(fileCount));
            outBuf.AddRange(BitConverter.GetBytes(folderCount));
            int tsPos = outBuf.Count;
            outBuf.AddRange(BitConverter.GetBytes(0u)); // totalSize placeholder
            if (version == "VDISK1.1")
                outBuf.AddRange(BitConverter.GetBytes(fileCount * 264 + 4));

            // entries
            foreach (var e in elist)
            {
                byte[] rec = new byte[ENTRY];
                rec[0] = (byte)(e.IsDir ? 1 : 0);
                byte[] nb = EucKrBytes(e.Name);
                int nlen = Math.Min(nb.Length, NAME - 1);
                Array.Copy(nb, 0, rec, 1, nlen);
                if (e.Role == "file")
                {
                    Array.Copy(BitConverter.GetBytes(e.Node.Unc), 0, rec, 129, 4);
                    Array.Copy(BitConverter.GetBytes(e.Node.CSize), 0, rec, 133, 4);
                }
                Array.Copy(BitConverter.GetBytes(e.DataOff), 0, rec, 137, 4);
                Array.Copy(BitConverter.GetBytes(e.NextOff), 0, rec, 141, 4);
                outBuf.AddRange(rec);
                if (e.Role == "file")
                    outBuf.AddRange(e.Node.Comp);
            }

            if (outBuf.Count != flatOff)
                throw new InvalidOperationException($"layout mismatch: wrote {outBuf.Count}, expected {flatOff}");

            // flat table: relpath -> its entry position
            var relPos = new Dictionary<string, long>(StringComparer.Ordinal);
            void Collect(Node node, string cp)
            {
                foreach (var ch in SortedChildren(node))
                {
                    if (ch.IsDir) Collect(ch, cp + ch.Name + "/");
                    else relPos[cp + ch.Name] = elist[entryIdx[ch]].Pos;
                }
            }
            Collect(root, "");

            // Flat serialization order is reconstructed purely from the file set
            // (emulating the packer's MSVC hash_map insertion + bucket order).
            List<string> forder = FlatOrder(fileHierOrder);

            outBuf.AddRange(BitConverter.GetBytes(fileCount));
            foreach (string rel in forder)
            {
                byte[] up = UpperKey(rel);
                byte[] rec = new byte[260];
                Array.Copy(up, 0, rec, 0, Math.Min(up.Length, 260));
                outBuf.AddRange(rec);
                outBuf.AddRange(BitConverter.GetBytes((uint)relPos[rel]));
            }

            // totalSize (header@20) = flatOff - 145 (root "." entry not counted)
            byte[] ts = BitConverter.GetBytes((uint)(flatOff - 145));
            for (int i = 0; i < 4; i++) outBuf[tsPos + i] = ts[i];

            // u32@8 (header offset 8) = align_down(256, entry position of the LAST file
            // in flat-table order) — i.e. the offset field of the last flat record, masked
            // to 256. Reverse-engineered from Gravity's packer (matches every canonical
            // client VDK); fully reconstructible since we know the flat order + entry
            // positions. The client ignores this field, but reproducing it makes the
            // round-trip byte-identical.
            uint u8 = 0u;
            if (forder.Count > 0)
            {
                long lastEntryPos = relPos[forder[forder.Count - 1]];
                u8 = (uint)((lastEntryPos / 256) * 256);
            }
            byte[] u8b = BitConverter.GetBytes(u8);
            for (int i = 0; i < 4; i++) outBuf[8 + i] = u8b[i];

            byte[] outBytes = outBuf.ToArray();
            File.WriteAllBytes(outPath, outBytes);

            return new BuildResult
            {
                OutputPath = outPath,
                FileCount = fileCount,
                FolderCount = folderCount,
                FlatTableOffset = flatOff,
                TotalBytes = outBytes.Length
            };
        }

        // Per-call parent-dot map (set inside Build). Not thread-safe across concurrent
        // builds; Build is invoked sequentially by callers.
        [ThreadStatic] private static Dictionary<Node, int> _parentDot;

        // ------------------------------------------------------------------
        // Compression: zlib level N; store RAW if compressed >= original.
        // Mirrors _rebuild.py's comp_level1 (generalized to any level).
        // ------------------------------------------------------------------
        private static void CompressLevel(byte[] data, int level, out byte[] comp, out uint unc, out uint csize)
        {
            byte[] z = VDKTool.Core.Zlib.ZlibManaged.Deflate(data, level);
            if (z.Length >= data.Length)
            {
                comp = data; unc = (uint)data.Length; csize = (uint)data.Length; // store raw
            }
            else
            {
                comp = z; unc = (uint)data.Length; csize = (uint)z.Length;
            }
        }

    }
}
