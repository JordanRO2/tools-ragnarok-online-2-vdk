# VDK (VDISK) archive format

The container format used by Ragnarok Online 2 (`Rag2.exe`, class `CVDisk`) for
its `.vdk` / `.VDK` data archives. A file consists of a header, a hierarchical
entry table with each file's payload stored inline, and a flat path→offset
lookup table. Entry names are EUC-KR (codepage 51949).

## File layout

```
[header]       28 bytes (VDISK1.1) / 24 bytes (VDISK1.0)
[entry table]  145-byte entries; a file entry is immediately followed by its payload
[flat table]   u32 count, then count records of (260-byte path + u32 offset)
```

## Header

| off | size | field | notes |
|-----|------|-------|-------|
| 0   | 8 | magic       | `"VDISK1.1"` or `"VDISK1.0"` (NUL-terminated) |
| 8   | 4 | u32@8       | `align_down(256, offset of the last file's flat-table record)`. VDISK1.0 holds `0xFFFFFF00` in this slot instead. Read but unused by the client. |
| 12  | 4 | fileCount   | number of file entries |
| 16  | 4 | folderCount | number of real directory entries (excludes `.` / `..`) |
| 20  | 4 | totalSize   | `flatTableOffset - 145` |
| 24  | 4 | validation  | `fileCount * 264 + 4` (VDISK1.1 only) |

## Entry (145 bytes)

| off | size | field |
|-----|------|-------|
| 0   | 1   | isDir (1 = directory, 0 = file) |
| 1   | 128 | name, EUC-KR, NUL-padded |
| 129 | 4   | uncompressedSize |
| 133 | 4   | compressedSize |
| 137 | 4   | dataOffset |
| 141 | 4   | nextOffset (absolute position of the next sibling; 0 = last) |

## Entry table order

Pre-order DFS:

```
emit root "." entry
for each root child (sorted): emit_subtree(child)

emit_subtree(dir D):
    emit D entry
    emit D "." ; emit D ".."
    for each child of D (sorted):
        emit_subtree(child) if directory, else emit(file entry + payload)
```

- A file entry is immediately followed by its `compressedSize` payload bytes.
- Sibling sort: ascending by the ASCII-uppercased name bytes (`SafeStrToUpper`),
  directories and files mixed. Because it is uppercased, `A`–`Z` (0x41–0x5A) sort
  before `_` (0x5F), e.g. `String_GuildSkill_*` < `string_guild_*`.
- dataOffset / nextOffset:
  - root `.`: dataOffset = its own position (28); nextOffset = first root child (or 0). Root has no `..`.
  - directory `D`: dataOffset = position of D's `.` entry.
  - any `.`: dataOffset = its own position.
  - `..` in D: dataOffset = position of D's parent's `.` entry.
  - file: dataOffset = 0.
  - nextOffset = next sibling's absolute position (0 if last in its chain).

## Payload compression

zlib stream — level 1, memLevel 8, default strategy, windowBits 15 (2-byte
header + deflate + adler32). If the compressed result is not smaller than the
input, the payload is stored raw (`compressedSize == uncompressedSize`).

## Flat lookup table

A serialization of an MSVC `stdext::hash_map` (Dinkumware), written in bucket order.

Path hash (`CVDisk` `sub_DEAFB0`), over the uppercased full path with `/` separators:

```
uint h = 0;
for (i = 0; i < strlen(path)/4; i++)   // full 4-byte LE words only; trailing len%4 bytes ignored
    h += le_uint32(path + 4*i) * (i + 1);
```

Table: 17 initial buckets; bucket = `h % bucketCount`; prepend to the chain.
Grow when `count > bucketCount * 2.25` → `bucketCount = smallest prime ≥ count / 0.75`
from the prime table, then rehash (walk old buckets 0..N-1, each chain head→tail,
prepend into the new buckets). Prime table:

```
17,23,29,37,41,53,67,83,103,131,163,211,257,331,409,521,647,821,1031,1291,1627,
2053,2591,3251,4099,5167,6521,8209,10331,13007,16411,20663,26017,32771,41299,52021,
65537,82571,104033,131101,165161,209987,263171,330287,413857,519277,651407,816521,...
```

Insertion order = the hierarchical file order (the sorted DFS above).
Serialization = walk buckets 0..N-1, each chain head→tail, emitting
(260-byte uppercased path, u32 entry position) per file; `count` is written first as a u32.

## Empty directories

A directory with no files still has an entry; it is part of the tree and the
counts/offsets. Dropping it shifts `folderCount` and every subsequent offset.

## Addresses (`Rag2.exe`, b303 PacketsCapture `.i64`)

- `CVDisk::OpenVDK` 0xde6920, `CreateVDK` 0xde6790, `Close` 0xde6ad0
- `CVDisk::FindFileOffset` 0xde70d0 → flat lookup `sub_DEA5B0` → `sub_DEAE10`
- path hash `sub_DEAFB0` 0xdeafb0
- hash table: ctor `sub_DEA4C0(17, 0.75, 0.25, 2.25, 10)`, insert `sub_DEB230`,
  resize-target `sub_DEAC40`, rehash `sub_DEB060`, prime table `dword_1437398`
- `CVDisk::BuildVDKFromDirectory` 0xde6c60, `WriteFileToVDK` 0xde9170,
  `LoadResourceCache` 0xdea0c0, `AddToCache` 0xdea600
