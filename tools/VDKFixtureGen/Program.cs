using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VDKFixtureGen
{
    // =====================================================================
    // RO2 fixture generator — INDEPENDENT writer.
    //
    // Emits sample.vdk (VDISK1.1) and sample.ct (RO2SEC!) directly as bytes.
    // The byte layout was derived by reading VDKTool.Core's PARSERS
    // (VDKArchive.Load and CTProcessor.Read) and writing the inverse. This
    // project does NOT reference VDKTool.Core and never calls VDKPacker or
    // CTProcessor.Write. zlib here is System.IO.Compression.ZLibStream
    // (header 78 9C), distinct from the Ionic/ProDotNetZip stream the Core
    // packer would produce. CRC-16 XMODEM is our own.
    // =====================================================================
    internal static class Program
    {
        private const int HEADER_SIZE = 28;   // VDK header: 8(ver)+u32*5 = 28
        private const int ENTRY_SIZE = 145;   // 1 flag + 128 name + u32*4
        private const int NAME_SIZE = 128;

        private static Encoding _eucKr;

        private static int Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _eucKr = Encoding.GetEncoding(51949); // euc-kr

            string outDir = args.Length > 0
                ? args[0]
                : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures");
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            string vdkPath = Path.Combine(outDir, "sample.vdk");
            string ctPath = Path.Combine(outDir, "sample.ct");

            BuildVdk(vdkPath);
            BuildCt(ctPath);

            Console.WriteLine("sample.vdk -> " + vdkPath + "  (" + new FileInfo(vdkPath).Length + " bytes)");
            Console.WriteLine("  sha256 = " + Sha256Hex(File.ReadAllBytes(vdkPath)));
            Console.WriteLine("sample.ct  -> " + ctPath + "  (" + new FileInfo(ctPath).Length + " bytes)");
            Console.WriteLine("  sha256 = " + Sha256Hex(File.ReadAllBytes(ctPath)));
            return 0;
        }

        // -----------------------------------------------------------------
        // VDK builder
        // -----------------------------------------------------------------
        //
        // The hierarchical table is a depth-first INLINE tree exactly as
        // VDKArchive.ParseEntries walks it:
        //   - entries are read sequentially, 145 bytes each;
        //   - a directory entry (flag!=0, name != "." / "..") is recursed
        //     into IMMEDIATELY: its children are the bytes that follow;
        //   - a file entry is followed inline by its compressed payload;
        //   - a sibling chain ends when next_offset (u32@141) == 0;
        //     otherwise next_offset = absolute file offset of the next sibling.
        //
        // For 1:1 repack compatibility with VDKPacker (which lays entries out
        // sequentially from HEADER_SIZE and remaps offsets by absolute entry
        // position), we ALSO lay out sequentially from offset 28 with no gaps,
        // and every next/data offset we write is the true absolute position of
        // a real entry. data_offset replicates the OBJECT_STRUCTURE "pointer"
        // rarity: directories point to their first child entry; files use 0.

        private sealed class Node
        {
            public bool IsDir;
            public byte[] NameBytes;     // raw EUC-KR, no padding, no null
            public byte[] Payload;       // file: compressed bytes; dir: null
            public uint UncompressedSize;
            public List<Node> Children = new List<Node>();

            // resolved during layout
            public long EntryPos;
            public uint DataOffset;
            public uint NextOffset;
        }

        private static void BuildVdk(string path)
        {
            // Known file contents (assertable). One filename is non-ASCII EUC-KR.
            // Compressed with ZLibStream SmallestSize -> 78 9C header (level ~9),
            // which differs byte-for-byte from the Core/Ionic packer output.
            byte[] cModelA = MakePattern(0xA1, 600);          // textures/wood.dds-ish
            byte[] cModelB = Encoding.ASCII.GetBytes("MODEL-MESH-DATA-VERTICES-0123456789-" + new string('Z', 200));
            byte[] cReadme = Encoding.ASCII.GetBytes("RO2 VDK fixture. Known content for assertions.\nLine2.\nLine3-END\n");
            byte[] cTexA = MakePattern(0x55, 1024);
            byte[] cKorean = Encoding.UTF8.GetBytes("한글 파일 내용 (korean file body) 12345"); // 한글 파일 내용

            // Build the tree. ROOT siblings in deliberately NON-alphabetical order:
            //   "textures" (dir), then "models" (dir), then "zz_readme.txt" (file).
            // Inside dirs, files are also NOT sorted.
            var nTexA   = File_("wood.dds", cTexA);
            var nTexKor = FileRaw(_eucKr.GetBytes("한글텍스처.dds"), cKorean); // 한글텍스처.dds
            var textures = Dir("textures", nTexA, nTexKor);

            var nModelB = File_("mesh.nif", cModelB);
            var nModelA = File_("anim.dds", cModelA);
            var models = Dir("models", nModelB, nModelA);

            var readme = File_("zz_readme.txt", cReadme);

            var roots = new List<Node> { textures, models, readme };

            // ---- Layout pass: assign sequential positions, next/data offsets ----
            long cursor = HEADER_SIZE;
            int fileCount = 0, dirCount = 0;
            // preorder traversal mirroring ParseEntries recursion
            void Layout(List<Node> siblings)
            {
                for (int i = 0; i < siblings.Count; i++)
                {
                    var node = siblings[i];
                    node.EntryPos = cursor;
                    cursor += ENTRY_SIZE;
                    if (node.IsDir)
                    {
                        dirCount++;
                        // data_offset rarity: point to first child entry (the
                        // position right after this entry = cursor now), if any.
                        node.DataOffset = node.Children.Count > 0 ? (uint)cursor : 0u;
                        Layout(node.Children); // children inline, depth-first
                    }
                    else
                    {
                        fileCount++;
                        node.DataOffset = 0u; // ASSET-style files: 0
                        cursor += node.Payload.Length; // payload follows entry
                    }
                }
                // next_offset chain: last sibling = 0, others -> next sibling pos
                for (int i = 0; i < siblings.Count; i++)
                    siblings[i].NextOffset =
                        (i + 1 < siblings.Count) ? (uint)siblings[i + 1].EntryPos : 0u;
            }
            Layout(roots);

            long flatTableStart = cursor;

            // ---- Write pass ----
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                // Header (28 bytes)
                w.Write(Encoding.ASCII.GetBytes("VDISK1.1")); // 8 bytes, no null needed (exact 8)
                w.Write((uint)0xF00);          // u32@8  NON-zero, OBJECT_STRUCTURE-like
                w.Write((uint)fileCount);      // u32@12 file count
                w.Write((uint)dirCount);       // u32@16 folder count
                w.Write((uint)flatTableStart); // u32@20 total size (preserved on repack)
                w.Write((uint)(fileCount * 264 + 4)); // u32@24 validation (VDISK1.1)

                // Hierarchical entries, depth-first inline (same order as Layout)
                void WriteTree(List<Node> siblings)
                {
                    foreach (var node in siblings)
                    {
                        WriteEntry(w, node);
                        if (node.IsDir)
                            WriteTree(node.Children);
                        else
                            w.Write(node.Payload);
                    }
                }
                WriteTree(roots);

                // Flat lookup table. Records in NON-alphabetical (insertion/hash)
                // order, uppercased path bytes, 260-byte padded, each followed by
                // the absolute offset of the file entry it points to.
                var flatFiles = new List<Node>();
                CollectFiles(roots, "", flatFiles, out var flatPaths);

                // deliberately scramble order (NOT alphabetical): reverse it.
                var idx = new List<int>();
                for (int i = 0; i < flatFiles.Count; i++) idx.Add(i);
                idx.Reverse();

                w.Write((uint)idx.Count);
                foreach (int i in idx)
                {
                    byte[] rec = new byte[260];
                    // uppercased path, backslash separators (RO2 flat table style)
                    byte[] pb = _eucKr.GetBytes(flatPaths[i].ToUpperInvariant().Replace('/', '\\'));
                    Array.Copy(pb, 0, rec, 0, Math.Min(pb.Length, 260));
                    w.Write(rec);
                    w.Write((uint)flatFiles[i].EntryPos);
                }
            }
        }

        private static void CollectFiles(List<Node> siblings, string prefix,
            List<Node> outFiles, out List<string> outPaths)
        {
            outPaths = new List<string>();
            var paths = outPaths;
            void Walk(List<Node> sibs, string pre)
            {
                foreach (var n in sibs)
                {
                    string name = _eucKr.GetString(n.NameBytes);
                    string full = string.IsNullOrEmpty(pre) ? name : pre + "/" + name;
                    if (n.IsDir) Walk(n.Children, full);
                    else { outFiles.Add(n); paths.Add(full); }
                }
            }
            Walk(siblings, prefix);
        }

        private static void WriteEntry(BinaryWriter w, Node node)
        {
            byte[] entry = new byte[ENTRY_SIZE];
            entry[0] = (byte)(node.IsDir ? 1 : 0);
            int copy = Math.Min(node.NameBytes.Length, NAME_SIZE - 1);
            Array.Copy(node.NameBytes, 0, entry, 1, copy); // rest stays null (terminator)

            uint unc = node.IsDir ? 0u : node.UncompressedSize;
            uint comp = node.IsDir ? 0u : (uint)node.Payload.Length;
            BitConverter.GetBytes(unc).CopyTo(entry, 129);
            BitConverter.GetBytes(comp).CopyTo(entry, 133);
            BitConverter.GetBytes(node.DataOffset).CopyTo(entry, 137);
            BitConverter.GetBytes(node.NextOffset).CopyTo(entry, 141);
            w.Write(entry);
        }

        private static Node Dir(string name, params Node[] children)
        {
            var d = new Node { IsDir = true, NameBytes = _eucKr.GetBytes(name) };
            d.Children.AddRange(children);
            return d;
        }

        private static Node File_(string name, byte[] rawContent) =>
            FileRaw(_eucKr.GetBytes(name), rawContent);

        private static Node FileRaw(byte[] nameBytes, byte[] rawContent)
        {
            byte[] comp = ZlibCompress(rawContent);
            return new Node
            {
                IsDir = false,
                NameBytes = nameBytes,
                Payload = comp,
                UncompressedSize = (uint)rawContent.Length
            };
        }

        private static byte[] MakePattern(byte seed, int len)
        {
            byte[] b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = (byte)((seed + i * 7) & 0xFF);
            return b;
        }

        // zlib via System.IO.Compression. CompressionLevel.Optimal maps to the
        // default deflate level, whose 2-byte zlib header is 78 9C (FLEVEL=2),
        // matching real RO2 ASSET streams and explicitly distinct from
        // SmallestSize (78 DA) and from the Ionic/ProDotNetZip output the Core
        // packer would produce. This is our OWN writer, never VDKPacker.
        private static byte[] ZlibCompress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var z = new ZLibStream(output, CompressionLevel.Optimal, true))
                    z.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        // -----------------------------------------------------------------
        // CT builder (RO2SEC!)
        // -----------------------------------------------------------------
        //
        // Layout per CTProcessor.Read:
        //   64-byte header: "RO2SEC!" UTF-16LE + 2 null bytes + UTF-16LE
        //     timestamp (NUL-terminated within the 64 bytes), zero-padded.
        //   i32 numColumns; then each header as: i32 charLen + UTF-16LE chars.
        //   i32 numTypes;   then each type as i32 type code.
        //   i32 numRows;    then each row: per column a packed value.
        //   u16 CRC-16/XMODEM over the concatenated ROW-VALUE bytes only.

        private static readonly (string Type, int Code)[] Columns = new[]
        {
            ("BYTE", 2), ("SHORT", 3), ("WORD", 4), ("INT", 5), ("DWORD", 6),
            ("DWORD_HEX", 7), ("STRING", 8), ("FLOAT", 9), ("INT64", 11), ("BOOL", 12)
        };

        private static void BuildCt(string path)
        {
            const string MAGIC = "RO2SEC!";
            const string TIMESTAMP = "2026-06-21 12:34:56"; // FIXED, known

            string[] headers =
            {
                "col_byte", "col_short", "col_word", "col_int", "col_dword",
                "col_dwordhex", "col_string", "col_float", "col_int64", "col_bool"
            };

            // Rows. Columns in the order of Columns[] above.
            // Row 1: ordinary values.
            // Row 2: STRING literal "0" (the historical bug), edge numerics.
            // Row 3: empty STRING (distinct from "0"), FLOAT with many decimals.
            var rows = new List<string[]>
            {
                new[] { "200", "-12345", "65000", "-2000000000", "4000000000",
                        "0xDEADBEEF", "hello_world", "1.5", "9223372036854775807", "1" },
                new[] { "0", "32767", "0", "2147483647", "0",
                        "0x0", "0", "0.0", "-9223372036854775808", "0" },
                new[] { "255", "-32768", "1", "1", "1",
                        "0xFFFFFFFF", "", "3.14159265358979", "-1", "1" },
            };

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                // --- 64-byte header ---
                byte[] header = new byte[64];
                byte[] magicB = Encoding.Unicode.GetBytes(MAGIC); // UTF-16LE
                Array.Copy(magicB, header, magicB.Length);
                int pos = magicB.Length;
                header[pos++] = 0; header[pos++] = 0; // 2-byte separator
                byte[] tsB = Encoding.Unicode.GetBytes(TIMESTAMP);
                Array.Copy(tsB, 0, header, pos, Math.Min(tsB.Length, 64 - pos - 2));
                w.Write(header);

                // --- columns (headers) ---
                w.Write(headers.Length);
                foreach (var h in headers) WriteCtString(w, h);

                // --- types ---
                w.Write(Columns.Length);
                foreach (var c in Columns) w.Write(c.Code);

                // --- rows + CRC over packed row bytes ---
                using (var rowMs = new MemoryStream())
                using (var rowW = new BinaryWriter(rowMs))
                {
                    w.Write(rows.Count);
                    foreach (var row in rows)
                    {
                        for (int j = 0; j < Columns.Length; j++)
                        {
                            byte[] packed = PackCtValue(Columns[j].Type, row[j]);
                            w.Write(packed);
                            rowW.Write(packed);
                        }
                    }
                    ushort crc = Crc16Xmodem(rowMs.ToArray());
                    w.Write(crc);
                }

                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        private static void WriteCtString(BinaryWriter w, string value)
        {
            if (string.IsNullOrEmpty(value)) { w.Write(0); return; }
            w.Write(value.Length);                       // char count
            w.Write(Encoding.Unicode.GetBytes(value));   // UTF-16LE bytes
        }

        private static byte[] PackCtValue(string type, string value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                switch (type)
                {
                    case "BYTE":
                    case "BOOL":
                        w.Write(byte.Parse(value)); break;
                    case "SHORT":
                        w.Write(short.Parse(value)); break;
                    case "WORD":
                        w.Write(ushort.Parse(value)); break;
                    case "INT":
                        w.Write(int.Parse(value)); break;
                    case "DWORD":
                        w.Write(uint.Parse(value)); break;
                    case "DWORD_HEX":
                        w.Write(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? Convert.ToUInt32(value, 16) : uint.Parse(value));
                        break;
                    case "FLOAT":
                        w.Write(float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "INT64":
                        w.Write(long.Parse(value)); break;
                    case "STRING":
                        // Critical: literal "0" must write len=1 + UTF16 "0";
                        // only a truly empty string writes len=0.
                        if (string.IsNullOrEmpty(value)) w.Write(0);
                        else { w.Write(value.Length); w.Write(Encoding.Unicode.GetBytes(value)); }
                        break;
                    default:
                        w.Write(int.Parse(value)); break;
                }
                return ms.ToArray();
            }
        }

        // CRC-16/XMODEM: poly 0x1021, init 0x0000, no reflect, no xorout.
        private static ushort Crc16Xmodem(byte[] data)
        {
            ushort crc = 0x0000;
            const ushort poly = 0x1021;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                    crc = (ushort)(((crc & 0x8000) != 0) ? (crc << 1) ^ poly : (crc << 1));
            }
            return crc;
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
