using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace VDKTool.Core
{
    public class FileEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDirectory { get; set; }
        public uint UncompressedSize { get; set; }
        public uint CompressedSize { get; set; }
        public uint Offset { get; set; }       // next_offset (u32@141)
        public uint DataOffset { get; set; }    // u32@137
        public long DataPosition { get; set; }  // absolute position of payload (files) / entry start area
        public long EntryPosition { get; set; } // absolute position of the 145-byte entry
        public byte[] RawNameBytes { get; set; }
    }

    public class VDKArchive
    {
        private const int ENTRY_SIZE = 145;
        private const int NAME_SIZE = 128;
        private static readonly Encoding KoreanEncoding;

        public string FilePath { get; private set; }
        public string Version { get; private set; }
        public uint HeaderU32At8 { get; private set; }
        public uint FileCount { get; private set; }
        public uint FolderCount { get; private set; }
        public uint TotalSize { get; private set; }
        public uint Validation { get; private set; }
        public List<FileEntry> Entries { get; private set; }

        // Linearized list of ALL raw entries in the exact order they appear in the
        // hierarchical table (dirs, ".", "..", files). Used for manifest capture.
        public List<FileEntry> RawEntriesInOrder { get; private set; }

        // Flat table records captured verbatim, in original order.
        public List<(byte[] pathBytes, uint offset)> FlatRecords { get; private set; }

        static VDKArchive()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            KoreanEncoding = Encoding.GetEncoding(51949); // euc-kr
        }

        public VDKArchive()
        {
            Entries = new List<FileEntry>();
            RawEntriesInOrder = new List<FileEntry>();
            FlatRecords = new List<(byte[], uint)>();
        }

        public static VDKArchive Load(string filePath)
        {
            var archive = new VDKArchive { FilePath = filePath };

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                byte[] versionBytes = reader.ReadBytes(8);
                int nullPos = Array.IndexOf(versionBytes, (byte)0);
                if (nullPos < 0) nullPos = 8;
                archive.Version = Encoding.ASCII.GetString(versionBytes, 0, nullPos);

                archive.HeaderU32At8 = reader.ReadUInt32();
                archive.FileCount = reader.ReadUInt32();
                archive.FolderCount = reader.ReadUInt32();
                archive.TotalSize = reader.ReadUInt32();

                if (archive.Version == "VDISK1.0")
                {
                    if (archive.HeaderU32At8 != 4294967040)
                        throw new InvalidDataException("Invalid VDISK1.0 magic");
                    archive.Validation = 0;
                }
                else if (archive.Version == "VDISK1.1")
                {
                    archive.Validation = reader.ReadUInt32();
                    uint expected = archive.FileCount * 264 + 4;
                    if (archive.Validation != expected)
                        throw new InvalidDataException("Invalid VDISK1.1 validation");
                }
                else
                {
                    throw new InvalidDataException($"Unknown VDK format: {archive.Version}");
                }

                ParseEntries(reader, "", archive.Entries, archive.RawEntriesInOrder);

                // After parsing the hierarchical table, the stream is positioned at the
                // flat lookup table. Read it verbatim.
                ReadFlatTable(reader, archive);
            }

            return archive;
        }

        private static void ReadFlatTable(BinaryReader reader, VDKArchive archive)
        {
            var s = reader.BaseStream;
            if (s.Position + 4 > s.Length) return;
            uint count = reader.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                if (s.Position + 264 > s.Length) break;
                byte[] path = reader.ReadBytes(260);
                uint off = reader.ReadUInt32();
                archive.FlatRecords.Add((path, off));
            }
        }

        private static void ParseEntries(BinaryReader reader, string currentPath,
            List<FileEntry> entries, List<FileEntry> raw)
        {
            while (true)
            {
                if (reader.BaseStream.Position + ENTRY_SIZE > reader.BaseStream.Length)
                    break;

                long entryPos = reader.BaseStream.Position;
                byte[] entryData = reader.ReadBytes(ENTRY_SIZE);

                bool isDir = entryData[0] != 0;

                byte[] nameBytes = new byte[NAME_SIZE];
                Array.Copy(entryData, 1, nameBytes, 0, NAME_SIZE);
                int nameEnd = Array.IndexOf(nameBytes, (byte)0);
                if (nameEnd < 0) nameEnd = NAME_SIZE;
                string name = KoreanEncoding.GetString(nameBytes, 0, nameEnd);
                byte[] rawName = new byte[nameEnd];
                Array.Copy(nameBytes, 0, rawName, 0, nameEnd);

                uint uncompSize = BitConverter.ToUInt32(entryData, 129);
                uint compSize = BitConverter.ToUInt32(entryData, 133);
                uint dataOffset = BitConverter.ToUInt32(entryData, 137);
                uint nextOffset = BitConverter.ToUInt32(entryData, 141);

                string fullPath = string.IsNullOrEmpty(currentPath) ? name : Path.Combine(currentPath, name);
                long dataPosition = reader.BaseStream.Position;

                var entry = new FileEntry
                {
                    Name = name,
                    Path = fullPath,
                    IsDirectory = isDir,
                    UncompressedSize = uncompSize,
                    CompressedSize = compSize,
                    Offset = nextOffset,
                    DataOffset = dataOffset,
                    DataPosition = dataPosition,
                    EntryPosition = entryPos,
                    RawNameBytes = rawName
                };
                entries.Add(entry);
                raw.Add(entry);

                if (isDir)
                {
                    if (name != "." && name != "..")
                        ParseEntries(reader, fullPath, entries, raw);
                }
                else
                {
                    reader.BaseStream.Seek(dataPosition + compSize, SeekOrigin.Begin);
                }

                if (nextOffset == 0)
                    break;
            }
        }

        public byte[] ExtractFile(FileEntry entry)
        {
            byte[] raw = ReadRawCompressed(entry);
            if (entry.UncompressedSize == entry.CompressedSize)
                return raw;
            try { return DecompressZlib(raw); }
            catch
            {
                try { return DecompressDeflate(raw); }
                catch { return raw; }
            }
        }

        public byte[] ReadRawCompressed(FileEntry entry)
        {
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
            {
                stream.Seek(entry.DataPosition, SeekOrigin.Begin);
                byte[] data = new byte[entry.CompressedSize];
                int read = 0;
                while (read < data.Length)
                {
                    int n = stream.Read(data, read, data.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return data;
            }
        }

        private static byte[] DecompressZlib(byte[] data)
        {
            using (var input = new MemoryStream(data, 2, data.Length - 2))
            using (var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        private static byte[] DecompressDeflate(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        public List<FileEntry> GetFileEntries()
        {
            return Entries.FindAll(e => !e.IsDirectory && e.Name != "." && e.Name != "..");
        }

        public List<FileEntry> GetDirectoryEntries()
        {
            return Entries.FindAll(e => e.IsDirectory && e.Name != "." && e.Name != "..");
        }

        // ----------------------------------------------------------------------
        // Locate a file entry by its (relative) archive path. Matching is tolerant
        // of '/' vs '\' separators and is case-insensitive. Returns null if no file
        // entry matches (directories and ".", ".." are never returned).
        // ----------------------------------------------------------------------
        public FileEntry FindEntry(string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath)) return null;
            string Norm(string p) => p.Replace('\\', '/').Trim('/');
            string want = Norm(entryPath);
            foreach (var e in Entries)
            {
                if (e.IsDirectory || e.Name == "." || e.Name == "..") continue;
                if (string.Equals(Norm(e.Path), want, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        // ----------------------------------------------------------------------
        // PREVIEW: extract a single entry to MEMORY by its archive path, without
        // writing anything to disk. Returns the decompressed bytes, or null if the
        // path does not resolve to a file entry. Reuses FindEntry + ExtractFile so
        // the bytes are identical to a full extract of that file.
        // ----------------------------------------------------------------------
        public byte[] ExtractEntryBytes(string entryPath)
        {
            var entry = FindEntry(entryPath);
            return entry == null ? null : ExtractFile(entry);
        }

        // Try-variant: returns false (and null bytes) instead of relying on a null
        // return, for callers that prefer the Try pattern.
        public bool TryExtractEntryBytes(string entryPath, out byte[] bytes)
        {
            var entry = FindEntry(entryPath);
            if (entry == null) { bytes = null; return false; }
            bytes = ExtractFile(entry);
            return true;
        }

        // ----------------------------------------------------------------------
        // Sidecar-free extract: write every file (decompressed) to disk AND create
        // every directory on disk — INCLUDING empty directories — so the extracted
        // tree is a faithful, complete image of the archive's folder structure. A
        // subsequent VDKBuilder.BuildFromDirectory reconstructs the VDK 1:1 purely
        // from this tree; NO .vdkmanifest / .vdkorder is written.
        //
        // progress (optional) reports (current, total, item) once per file written.
        // ct (optional) is honored at each file boundary; on cancellation the
        // partially written output directory is removed best-effort and the
        // OperationCanceledException propagates.
        //
        // Returns the number of file entries written.
        // ----------------------------------------------------------------------
        public int ExtractAll(string outputDir,
            IProgress<(int current, int total, string item)> progress = null,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDir);

            // Total = number of file entries actually written (for progress only).
            int total = 0;
            foreach (var fe in RawEntriesInOrder)
                if (!fe.IsDirectory) total++;
            int current = 0;

            try
            {
                foreach (var fe in RawEntriesInOrder)
                {
                    ct.ThrowIfCancellationRequested();

                    if (fe.IsDirectory)
                    {
                        // Skip the "." and ".." pseudo-entries; create real dirs
                        // (this also materializes empty directories on disk).
                        if (fe.Name == "." || fe.Name == "..") continue;
                        string dirPath = Path.Combine(outputDir, fe.Path);
                        Directory.CreateDirectory(dirPath);
                        continue;
                    }

                    byte[] uncompressed = ExtractFile(fe);

                    string outPath = Path.Combine(outputDir, fe.Path);
                    string od = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(od)) Directory.CreateDirectory(od);
                    File.WriteAllBytes(outPath, uncompressed);

                    current++;
                    progress?.Report((current, total, fe.Path));
                }

                return total;
            }
            catch (OperationCanceledException)
            {
                // Best-effort cleanup of the partially written output directory.
                try { Directory.Delete(outputDir, true); } catch { }
                throw;
            }
        }
    }
}
