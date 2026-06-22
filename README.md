# RO2 VDK Toolkit

A C# / .NET 10 toolkit for working with **Ragnarok Online 2** client data:
extract and repack **VDK** archives (VDISK format) and convert **CT** binary
tables to and from Excel / CSV. Ships as a single standalone Windows executable
with both a graphical UI and a full command-line interface.

## Features

- **VDK extract / pack — byte-for-byte 1:1.** Extracting a `.VDK` writes the
  decompressed files and preserves empty directories. Packing reconstructs the
  archive **purely from the folder tree**, so an unmodified round-trip is
  **byte-identical** (verified by SHA256 in the regression suite). See
  [`docs/VDK_FORMAT_RE.md`](docs/VDK_FORMAT_RE.md) for the reversed format.
- **CT ↔ XLSX / CSV — 1:1 round-trip.** Convert `.ct` tables to spreadsheets for
  editing and back to `.ct`. The rebuilt table is byte-identical to the original.
- **Standalone GUI + CLI** in one self-contained executable. No .NET install
  required on the target machine.

## Download

Grab `VDK_Tool.exe` from the [latest release](../../releases/latest) — a single
self-contained Windows x64 binary. Drop it anywhere and run it: double-click for
the GUI, or call it from a terminal for the CLI (see below). To build it
yourself, see [Building](#building).

## Standalone GUI

Double-click `VDK_Tool.exe` (or run it with no arguments). It opens the UI in a
**native application window** (Photino / WebView2) — not a browser tab. The window
is frameless with its own title bar and is freely resizable.

> **Windows 11:** the WebView2 runtime is preinstalled, so the window works out of
> the box. On older/stripped Windows where the *Microsoft Edge WebView2 Runtime* is
> missing, install it once (a free Microsoft component) or use the browser fallback.

Browser fallback (serve the same UI in your default browser instead of the native
window):

```
VDK_Tool.exe --browser
```

The environment variable `VDKTOOL_BROWSER=1` has the same effect as `--browser`.

### Default output folder

The gear icon in the title bar opens **Settings**, where you can set a default
output folder. It is remembered between runs (saved to
`%APPDATA%\VDKTool\settings.json`), pre-fills the **Extract** field, and is the
default starting directory for the Browse dialogs. Any per-operation output field
still overrides it; leave it blank to disable.

## Command-line interface

Passing any command runs in CLI mode and prints its output to the launching
terminal. The CLI process does not block the shell, so to capture output or wait
for completion inside a script, use `Start-Process -Wait` or redirect to a file.

### Commands

| Command      | Alias | Arguments                       | Description                                   |
|--------------|-------|---------------------------------|-----------------------------------------------|
| `extract`    | `x`   | `<file.vdk> [outdir]`           | Extract a VDK (files + empty dirs)            |
| `extractall` | `xa`  | `<dir> [suffix]`                | Extract every `*.VDK` in a dir (suffix `_UNPACKED`) |
| `list`       | `l`   | `<file.vdk>`                    | List file entries                             |
| `listall`    | `la`  | `<file.vdk>`                    | List all entries (dirs + `.` + `..`)          |
| `pack`       | `p`   | `<dir> [out.vdk]`               | Reconstruct a VDK from a folder (1:1)         |
| `ct2xlsx`    | `ct`  | `<file.ct>`                     | CT → XLSX                                      |
| `ct2csv`     | —     | `<file.ct>`                     | CT → CSV                                       |
| `xlsx2ct`    | —     | `<file.xlsx> [out.ct]`          | XLSX → CT                                      |
| `csv2ct`     | —     | `<file.csv> [out.ct]`           | CSV → CT                                       |
| `ctall`      | `cta` | `<dir>`                         | Convert all `*.ct` in a dir tree (recursive)  |
| `help`       | —     | —                               | Show usage                                    |

`pack` takes a directory produced by `extract` (the file tree, including empty
directories) and reconstructs the archive byte-for-byte.

### Flags

| Flag             | Description                                              |
|------------------|---------------------------------------------------------|
| `--output`, `-o` | Override the output path (file or directory)            |
| `--csv`          | Select CSV output for `ct2xlsx` / `ctall`               |
| `--xlsx`         | Force XLSX output for `ct2xlsx` / `ctall` (the default)  |
| `--quiet`, `-q`  | Suppress progress output                                |
| `--help`, `-h`   | Show help (global, or per-command when used with a command) |
| `--browser`, `-b`| Launch the web UI in a browser instead of the native window |
| `--window`       | Force the native Photino window (the default UI mode)   |
| `--debug`        | GUI mode only: open a console with the web-host log     |

### Exit codes

| Code | Meaning                                          |
|------|--------------------------------------------------|
| `0`  | Success                                          |
| `1`  | Usage / argument error, or a fatal error         |
| `2`  | Partial failure (some items in a batch operation failed) |

### Examples

```bat
REM Extract, then repack 1:1
VDK_Tool.exe extract "DATA.VDK" "DATA_UNPACKED"
VDK_Tool.exe pack    "DATA_UNPACKED" "DATA.repacked.VDK"

REM List contents
VDK_Tool.exe list "ASSET.VDK"

REM CT round-trip
VDK_Tool.exe ct2csv "Skill.ct" -o "Skill.csv"
VDK_Tool.exe csv2ct "Skill.csv" "Skill.rebuilt.ct"

REM Batch-extract every VDK in a folder, and batch-convert every CT
VDK_Tool.exe extractall "C:\RO2\Data"
VDK_Tool.exe ctall "C:\RO2\Tables" --csv
```

## Building

The repository is a .NET 10 solution (`VDKTool.sln`):

- `src/VDKTool.Core` — library: VDK and CT read/write logic
- `src/VDKTool` — exe (`VDK_Tool`): CLI dispatcher + web UI host
- `tests/VDKTool.Tests` — byte-exact 1:1 regression harness
- `tools/VDKFixtureGen` — helper that generates the synthetic test fixtures

Run [`build.ps1`](build.ps1) to restore, build, run the 1:1 regression tests, and
produce the single-file self-contained executable; it prints the final path and
its SHA256:

```powershell
./build.ps1
```

Output: `publish\win-x64\VDK_Tool.exe` (runs without a .NET install). For a plain
framework-dependent build and the test suite:

```powershell
dotnet build -c Release
dotnet run --project tests/VDKTool.Tests -c Release
```

## Dependencies

| Package                          | Used for                                            |
|----------------------------------|-----------------------------------------------------|
| `ClosedXML`                      | XLSX read/write (CT ↔ Excel)                         |
| `System.Text.Encoding.CodePages` | EUC-KR (codepage 51949) decoding of VDK filenames   |
| `Photino.NET`                    | Native WebView2 window for the standalone GUI        |

## Supported formats

### VDK archive (VDISK 1.0 / 1.1)
- Magic: `VDISK1.0` or `VDISK1.1`
- Hierarchical entries (145-byte records), EUC-KR filenames
- Zlib/Deflate compression, reproduced byte-exact on repack
- 1:1 repack by **pure reconstruction** from the folder (see `docs/VDK_FORMAT_RE.md`)

### CT binary table
- Header: `RO2SEC!` (new) or `RO2!` (2013 and earlier), UTF-16LE, + timestamp
- 10 data types: BYTE, SHORT, WORD, INT, DWORD, DWORD_HEX, STRING, FLOAT, INT64, BOOL
- UTF-16LE strings, CRC-16 XMODEM footer

## License

Released under the [MIT License](LICENSE).

## Legal Disclaimer

This project is an unofficial, open-source personal tool developed solely for
educational and research purposes. It is completely non-profit and is not intended
to infringe upon any copyrights. Ragnarok Online 2, along with all related assets,
data formats, and intellectual property, are the sole property of Gravity Co., Ltd.
and WarpPortal. This tool is provided "as is" without any warranty, and the author
assumes no liability for its use.
