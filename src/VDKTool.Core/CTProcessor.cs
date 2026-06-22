using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace VDKTool.Core
{
    /// <summary>
    /// CT (Custom Table) file processor for Ragnarok Online 2.
    /// Supports reading and writing CT binary format with byte-identical round-trip.
    /// </summary>
    public class CTProcessor
    {
        private const int CT_HEADER_SIZE = 64;
        private const string CT_MAGIC_NEW = "RO2SEC!";
        private const string CT_MAGIC_OLD = "RO2!";
        private string _detectedMagic = CT_MAGIC_NEW;

        // Name of the hidden worksheet used to persist round-trip metadata.
        private const string META_SHEET = "__vdk_meta";

        // Sentinels distinguishing an empty STRING cell from a literal "0".
        // Stored in cells as text; never collide with real data because the
        // import path only checks IsEmpty() vs content.
        public enum CTDataType
        {
            BYTE = 2,
            SHORT = 3,
            WORD = 4,
            INT = 5,
            DWORD = 6,
            DWORD_HEX = 7,
            STRING = 8,
            FLOAT = 9,
            INT64 = 11,
            BOOL = 12
        }

        public static readonly Dictionary<int, string> TypeNames = new Dictionary<int, string>
        {
            { 2, "BYTE" }, { 3, "SHORT" }, { 4, "WORD" }, { 5, "INT" },
            { 6, "DWORD" }, { 7, "DWORD_HEX" }, { 8, "STRING" }, { 9, "FLOAT" },
            { 11, "INT64" }, { 12, "BOOL" }
        };

        public static readonly Dictionary<string, int> TypeCodes = new Dictionary<string, int>
        {
            { "BYTE", 2 }, { "SHORT", 3 }, { "WORD", 4 }, { "INT", 5 },
            { "DWORD", 6 }, { "DWORD_HEX", 7 }, { "STRING", 8 }, { "FLOAT", 9 },
            { "INT64", 11 }, { "BOOL", 12 }
        };

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string FilePath { get; private set; }
        public List<string> Headers { get; private set; }
        public List<string> Types { get; private set; }
        public List<List<string>> Rows { get; private set; }

        // Original header timestamp (UTF-16LE, header bytes 16..). Persisted across
        // the XLSX round-trip so the rebuilt CT header is byte-identical.
        public string Timestamp { get; set; }

        public CTProcessor()
        {
            Headers = new List<string>();
            Types = new List<string>();
            Rows = new List<List<string>>();
        }

        public void Read(string filePath)
        {
            FilePath = filePath;
            Headers.Clear();
            Types.Clear();
            Rows.Clear();

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                ReadHeader(reader);

                int numColumns = reader.ReadInt32();
                for (int i = 0; i < numColumns; i++)
                    Headers.Add(ReadString(reader));

                int numTypes = reader.ReadInt32();
                for (int i = 0; i < numTypes; i++)
                {
                    int typeCode = reader.ReadInt32();
                    Types.Add(TypeNames.ContainsKey(typeCode) ? TypeNames[typeCode] : $"UNKNOWN_{typeCode}");
                }

                int numRows = reader.ReadInt32();
                for (int i = 0; i < numRows; i++)
                {
                    var row = new List<string>();
                    for (int j = 0; j < Types.Count; j++)
                        row.Add(ReadValue(reader, Types[j]));
                    Rows.Add(row);
                }
            }
        }

        public void Write(string filePath, List<string> headers, List<string> types,
            List<List<string>> rows, string timestamp = null, string magic = null)
        {
            if (magic != null) _detectedMagic = magic;
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                // Fix #1: use the persisted original timestamp; only fall back to "now"
                // when none is available (genuinely new file).
                WriteHeader(writer, timestamp ?? Timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                writer.Write(headers.Count);
                foreach (var header in headers)
                    WriteString(writer, header);

                writer.Write(types.Count);
                foreach (var typeName in types)
                    writer.Write(TypeCodes.ContainsKey(typeName) ? TypeCodes[typeName] : 5);

                using (var rowDataStream = new MemoryStream())
                using (var rowDataWriter = new BinaryWriter(rowDataStream))
                {
                    writer.Write(rows.Count);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        for (int j = 0; j < types.Count; j++)
                        {
                            string value = j < row.Count ? row[j] : "";
                            byte[] valueBytes = PackValue(types[j], value);
                            writer.Write(valueBytes);
                            rowDataWriter.Write(valueBytes);
                        }
                    }

                    byte[] rowData = rowDataStream.ToArray();
                    ushort crc = CalculateCRC16(rowData);
                    writer.Write(crc);
                }
            }
        }

        private void ReadHeader(BinaryReader reader)
        {
            byte[] headerData = reader.ReadBytes(CT_HEADER_SIZE);

            byte[] newMagicBytes = Encoding.Unicode.GetBytes(CT_MAGIC_NEW);
            byte[] oldMagicBytes = Encoding.Unicode.GetBytes(CT_MAGIC_OLD);
            byte[] magicBytes;

            bool isNewMagic = StartsWith(headerData, newMagicBytes);
            if (isNewMagic)
            {
                magicBytes = newMagicBytes;
                _detectedMagic = CT_MAGIC_NEW;
            }
            else if (StartsWith(headerData, oldMagicBytes))
            {
                magicBytes = oldMagicBytes;
                _detectedMagic = CT_MAGIC_OLD;
            }
            else
            {
                throw new InvalidDataException("Invalid CT file magic (expected RO2SEC! or RO2!)");
            }

            int timestampStart = magicBytes.Length + 2;
            int timestampEnd = timestampStart;
            while (timestampEnd < headerData.Length - 1)
            {
                if (headerData[timestampEnd] == 0 && headerData[timestampEnd + 1] == 0)
                    break;
                timestampEnd += 2;
            }

            if (timestampEnd > timestampStart)
            {
                byte[] timestampBytes = new byte[timestampEnd - timestampStart];
                Array.Copy(headerData, timestampStart, timestampBytes, 0, timestampBytes.Length);
                Timestamp = Encoding.Unicode.GetString(timestampBytes);
            }
            else
            {
                Timestamp = "";
            }
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (data[i] != prefix[i]) return false;
            return true;
        }

        private void WriteHeader(BinaryWriter writer, string timestamp)
        {
            byte[] header = new byte[CT_HEADER_SIZE];

            byte[] magicBytes = Encoding.Unicode.GetBytes(_detectedMagic);
            Array.Copy(magicBytes, header, magicBytes.Length);

            int pos = magicBytes.Length;
            header[pos++] = 0;
            header[pos++] = 0;

            byte[] timestampBytes = Encoding.Unicode.GetBytes(timestamp ?? "");
            Array.Copy(timestampBytes, 0, header, pos, Math.Min(timestampBytes.Length, CT_HEADER_SIZE - pos - 2));

            writer.Write(header);
        }

        private string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length == 0) return "";
            byte[] bytes = reader.ReadBytes(length * 2);
            return Encoding.Unicode.GetString(bytes);
        }

        private void WriteString(BinaryWriter writer, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                writer.Write(0);
                return;
            }
            writer.Write(value.Length);
            writer.Write(Encoding.Unicode.GetBytes(value));
        }

        private string ReadValue(BinaryReader reader, string typeName)
        {
            switch (typeName)
            {
                case "BYTE":
                case "BOOL":
                    return reader.ReadByte().ToString(Inv);
                case "SHORT":
                    return reader.ReadInt16().ToString(Inv);
                case "WORD":
                    return reader.ReadUInt16().ToString(Inv);
                case "INT":
                    return reader.ReadInt32().ToString(Inv);
                case "DWORD":
                    return reader.ReadUInt32().ToString(Inv);
                case "DWORD_HEX":
                    return "0x" + reader.ReadUInt32().ToString("X", Inv);
                case "FLOAT":
                    // Fix #3: round-trip "R" format under invariant culture.
                    return reader.ReadSingle().ToString("R", Inv);
                case "INT64":
                    return reader.ReadInt64().ToString(Inv);
                case "STRING":
                    return ReadString(reader);
                default:
                    return reader.ReadInt32().ToString(Inv);
            }
        }

        private byte[] PackValue(string typeName, string value)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // Numeric types default empty to "0"; STRING handled separately below.
                bool isString = typeName == "STRING";
                if (!isString && string.IsNullOrEmpty(value)) value = "0";

                switch (typeName)
                {
                    case "BYTE":
                    case "BOOL":
                        writer.Write(byte.Parse(value, Inv));
                        break;
                    case "SHORT":
                        writer.Write(short.Parse(value, Inv));
                        break;
                    case "WORD":
                        writer.Write(ushort.Parse(value, Inv));
                        break;
                    case "INT":
                        writer.Write(int.Parse(value, Inv));
                        break;
                    case "DWORD":
                        writer.Write(uint.Parse(value, Inv));
                        break;
                    case "DWORD_HEX":
                        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            writer.Write(Convert.ToUInt32(value, 16));
                        else
                            writer.Write(uint.Parse(value, Inv));
                        break;
                    case "FLOAT":
                        writer.Write(float.Parse(value, NumberStyles.Float, Inv));
                        break;
                    case "INT64":
                        writer.Write(long.Parse(value, Inv));
                        break;
                    case "STRING":
                        // Fix #2: only a truly empty/null string writes len=0.
                        // A STRING whose value is "0" must write len=1 + UTF16 "0".
                        if (string.IsNullOrEmpty(value))
                        {
                            writer.Write(0);
                        }
                        else
                        {
                            writer.Write(value.Length);
                            writer.Write(Encoding.Unicode.GetBytes(value));
                        }
                        break;
                    default:
                        writer.Write(int.Parse(value, Inv));
                        break;
                }

                return ms.ToArray();
            }
        }

        private ushort CalculateCRC16(byte[] data)
        {
            ushort crc = 0x0000;
            ushort poly = 0x1021;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ poly);
                    else
                        crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        public void ExportToXLSX(string outputPath)
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("RO2 Table Data");

                // Row 1: Types
                for (int i = 0; i < Types.Count; i++)
                {
                    var cell = worksheet.Cell(1, i + 1);
                    cell.Value = Types[i];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E2F3");
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                // Row 2: Headers
                for (int i = 0; i < Headers.Count; i++)
                {
                    var cell = worksheet.Cell(2, i + 1);
                    cell.Value = Headers[i];
                    cell.Style.Font.Bold = true;
                    cell.Style.Font.FontColor = XLColor.White;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#366092");
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                // Row 3+: Data. To guarantee a lossless round-trip, write EVERY value
                // as text exactly as parsed from the CT. This preserves the empty vs
                // "0" distinction for STRING (Fix #2) and avoids any numeric/locale
                // reformatting (Fix #3). Excel display is secondary to fidelity.
                for (int rowIdx = 0; rowIdx < Rows.Count; rowIdx++)
                {
                    var row = Rows[rowIdx];
                    for (int colIdx = 0; colIdx < row.Count; colIdx++)
                    {
                        var cell = worksheet.Cell(rowIdx + 3, colIdx + 1);
                        cell.SetValue(row[colIdx] ?? "");
                    }
                }

                worksheet.SheetView.FreezeRows(2);

                // Hidden meta sheet: persist magic + timestamp (Fix #1).
                var meta = workbook.Worksheets.Add(META_SHEET);
                meta.Cell(1, 1).Value = "magic";
                meta.Cell(1, 2).SetValue(_detectedMagic ?? CT_MAGIC_NEW);
                meta.Cell(2, 1).Value = "timestamp";
                meta.Cell(2, 2).SetValue(Timestamp ?? "");
                meta.Visibility = XLWorksheetVisibility.Hidden;

                workbook.SaveAs(outputPath);
            }
        }

        public void ExportToCSV(string outputPath)
        {
            using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true)))
            {
                // Unified contract with XLSX: row of Types (commented), then Headers,
                // then data. Persist magic+timestamp on a leading metadata comment line.
                writer.WriteLine("##META\tmagic=" + (_detectedMagic ?? CT_MAGIC_NEW) + "\ttimestamp=" + (Timestamp ?? ""));
                writer.WriteLine("#" + string.Join(",", Types));
                writer.WriteLine(string.Join(",", Headers.ConvertAll(EscapeCSV)));
                foreach (var row in Rows)
                    writer.WriteLine(string.Join(",", row.ConvertAll(EscapeCSV)));
            }
        }

        public void ImportFromXLSX(string inputPath)
        {
            Headers.Clear();
            Types.Clear();
            Rows.Clear();
            _detectedMagic = CT_MAGIC_NEW;
            Timestamp = null;

            using (var workbook = new XLWorkbook(inputPath))
            {
                // Recover meta (Fix #1).
                if (workbook.TryGetWorksheet(META_SHEET, out var meta))
                {
                    string mMagic = meta.Cell(1, 2).GetString();
                    string mTs = meta.Cell(2, 2).GetString();
                    if (!string.IsNullOrEmpty(mMagic)) _detectedMagic = mMagic;
                    Timestamp = mTs;
                }

                var worksheet = workbook.Worksheet(1);
                var lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;

                if (lastColumn == 0 || lastRow < 2)
                    throw new InvalidDataException("XLSX file must have at least 2 rows (types and headers)");

                for (int col = 1; col <= lastColumn; col++)
                {
                    string type = worksheet.Cell(1, col).GetString().Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(type)) type = "INT";
                    Types.Add(type);
                }

                for (int col = 1; col <= lastColumn; col++)
                    Headers.Add(worksheet.Cell(2, col).GetString());

                for (int row = 3; row <= lastRow; row++)
                {
                    var rowData = new List<string>();
                    for (int col = 1; col <= lastColumn; col++)
                    {
                        var cell = worksheet.Cell(row, col);
                        string type = (col - 1) < Types.Count ? Types[col - 1] : "INT";
                        string value;

                        if (cell.IsEmpty())
                        {
                            // Fix #2: an empty STRING cell -> empty string (len=0);
                            // an empty numeric cell -> "0".
                            value = type == "STRING" ? "" : "0";
                        }
                        else if (type == "FLOAT")
                        {
                            // Fix #3: parse under invariant culture, store round-trip "R".
                            if (cell.DataType == XLDataType.Number && cell.TryGetValue(out double d))
                                value = ((float)d).ToString("R", Inv);
                            else
                                value = cell.GetString();
                        }
                        else if (type == "DWORD_HEX")
                        {
                            value = cell.GetString();
                            if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            {
                                if (uint.TryParse(value, NumberStyles.Integer, Inv, out uint uval))
                                    value = "0x" + uval.ToString("X", Inv);
                            }
                        }
                        else
                        {
                            value = cell.GetString();
                        }

                        rowData.Add(value);
                    }
                    Rows.Add(rowData);
                }
            }
        }

        public void ImportFromCSV(string inputPath)
        {
            Headers.Clear();
            Types.Clear();
            Rows.Clear();
            _detectedMagic = CT_MAGIC_NEW;
            Timestamp = null;

            using (var reader = new StreamReader(inputPath, Encoding.UTF8))
            {
                string line;
                // Optional metadata line.
                line = reader.ReadLine();
                if (line != null && line.StartsWith("##META"))
                {
                    foreach (var part in line.Split('\t'))
                    {
                        if (part.StartsWith("magic=")) _detectedMagic = part.Substring(6);
                        else if (part.StartsWith("timestamp=")) Timestamp = part.Substring(10);
                    }
                    line = reader.ReadLine();
                }

                // Types line (starts with '#').
                if (line != null && line.StartsWith("#"))
                {
                    Types.AddRange(ParseCSVLine(line.Substring(1)));
                    line = reader.ReadLine();
                }

                // Headers line.
                if (line != null)
                {
                    Headers.AddRange(ParseCSVLine(line));
                }

                while ((line = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrEmpty(line))
                        Rows.Add(ParseCSVLine(line));
                }
            }
        }

        private string EscapeCSV(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private List<string> ParseCSVLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else inQuotes = false;
                    }
                    else current.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',')
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    else current.Append(c);
                }
            }
            result.Add(current.ToString());
            return result;
        }
    }
}
