/*
 * Vendored from Ionic.Zlib (DotNetZip / ProDotNetZip), licensed under the
 * Microsoft Public License (Ms-PL). Original zlib algorithm by Jean-loup Gailly
 * and Mark Adler. Only the deflate/compression path is included here.
 *
 * This is a pure-managed (C#) deflate compressor that produces byte-identical
 * output to native zlib compress2() (wbits 15, memLevel 8, default strategy).
 * The inflate/decompression path and all stream wrappers have been removed.
 */

using System;
using System.IO;
using System.Text;

namespace VDKTool.Core.Zlib
{
    internal enum BlockState
    {
        NeedMore,
        BlockDone,
        FinishStarted,
        FinishDone
    }

    internal enum DeflateFlavor
    {
        Store,
        Fast,
        Slow
    }

    internal sealed class DeflateManager
    {
        internal delegate BlockState CompressFunc(FlushType flush);

        internal class Config
        {
            internal int GoodLength;

            internal int MaxLazy;

            internal int NiceLength;

            internal int MaxChainLength;

            internal DeflateFlavor Flavor;

            private static readonly Config[] Table;

            private Config(int goodLength, int maxLazy, int niceLength, int maxChainLength, DeflateFlavor flavor)
            {
                GoodLength = goodLength;
                MaxLazy = maxLazy;
                NiceLength = niceLength;
                MaxChainLength = maxChainLength;
                Flavor = flavor;
            }

            public static Config Lookup(CompressionLevel level)
            {
                return Table[(int)level];
            }

            static Config()
            {
                Table = new Config[10]
                {
                    new Config(0, 0, 0, 0, DeflateFlavor.Store),
                    new Config(4, 4, 8, 4, DeflateFlavor.Fast),
                    new Config(4, 5, 16, 8, DeflateFlavor.Fast),
                    new Config(4, 6, 32, 32, DeflateFlavor.Fast),
                    new Config(4, 4, 16, 16, DeflateFlavor.Slow),
                    new Config(8, 16, 32, 32, DeflateFlavor.Slow),
                    new Config(8, 16, 128, 128, DeflateFlavor.Slow),
                    new Config(8, 32, 128, 256, DeflateFlavor.Slow),
                    new Config(32, 128, 258, 1024, DeflateFlavor.Slow),
                    new Config(32, 258, 258, 4096, DeflateFlavor.Slow)
                };
            }
        }

        private static readonly int MEM_LEVEL_MAX = 9;

        private static readonly int MEM_LEVEL_DEFAULT = 8;

        private CompressFunc DeflateFunction;

        private static readonly string[] _ErrorMessage = new string[10] { "need dictionary", "stream end", "", "file error", "stream error", "data error", "insufficient memory", "buffer error", "incompatible version", "" };

        private static readonly int PRESET_DICT = 32;

        private static readonly int INIT_STATE = 42;

        private static readonly int BUSY_STATE = 113;

        private static readonly int FINISH_STATE = 666;

        private static readonly int Z_DEFLATED = 8;

        private static readonly int STORED_BLOCK = 0;

        private static readonly int STATIC_TREES = 1;

        private static readonly int DYN_TREES = 2;

        private static readonly int Z_BINARY = 0;

        private static readonly int Z_ASCII = 1;

        private static readonly int Z_UNKNOWN = 2;

        private static readonly int Buf_size = 16;

        private static readonly int MIN_MATCH = 3;

        private static readonly int MAX_MATCH = 258;

        private static readonly int MIN_LOOKAHEAD = MAX_MATCH + MIN_MATCH + 1;

        private static readonly int HEAP_SIZE = 2 * InternalConstants.L_CODES + 1;

        private static readonly int END_BLOCK = 256;

        internal ZlibCodec _codec;

        internal int status;

        internal byte[] pending;

        internal int nextPending;

        internal int pendingCount;

        internal sbyte data_type;

        internal int last_flush;

        internal int w_size;

        internal int w_bits;

        internal int w_mask;

        internal byte[] window;

        internal int window_size;

        internal short[] prev;

        internal short[] head;

        internal int ins_h;

        internal int hash_size;

        internal int hash_bits;

        internal int hash_mask;

        internal int hash_shift;

        internal int block_start;

        private Config config;

        internal int match_length;

        internal int prev_match;

        internal int match_available;

        internal int strstart;

        internal int match_start;

        internal int lookahead;

        internal int prev_length;

        internal CompressionLevel compressionLevel;

        internal CompressionStrategy compressionStrategy;

        internal short[] dyn_ltree;

        internal short[] dyn_dtree;

        internal short[] bl_tree;

        internal Tree treeLiterals = new Tree();

        internal Tree treeDistances = new Tree();

        internal Tree treeBitLengths = new Tree();

        internal short[] bl_count = new short[InternalConstants.MAX_BITS + 1];

        internal int[] heap = new int[2 * InternalConstants.L_CODES + 1];

        internal int heap_len;

        internal int heap_max;

        internal sbyte[] depth = new sbyte[2 * InternalConstants.L_CODES + 1];

        internal int _lengthOffset;

        internal int lit_bufsize;

        internal int last_lit;

        internal int _distanceOffset;

        internal int opt_len;

        internal int static_len;

        internal int matches;

        internal int last_eob_len;

        internal short bi_buf;

        internal int bi_valid;

        private bool Rfc1950BytesEmitted;

        private bool _WantRfc1950HeaderBytes = true;

        internal bool WantRfc1950HeaderBytes
        {
            get
            {
                return _WantRfc1950HeaderBytes;
            }
            set
            {
                _WantRfc1950HeaderBytes = value;
            }
        }

        internal DeflateManager()
        {
            dyn_ltree = new short[HEAP_SIZE * 2];
            dyn_dtree = new short[(2 * InternalConstants.D_CODES + 1) * 2];
            bl_tree = new short[(2 * InternalConstants.BL_CODES + 1) * 2];
        }

        private void _InitializeLazyMatch()
        {
            window_size = 2 * w_size;
            Array.Clear(head, 0, hash_size);
            config = Config.Lookup(compressionLevel);
            SetDeflater();
            strstart = 0;
            block_start = 0;
            lookahead = 0;
            match_length = (prev_length = MIN_MATCH - 1);
            match_available = 0;
            ins_h = 0;
        }

        private void _InitializeTreeData()
        {
            treeLiterals.dyn_tree = dyn_ltree;
            treeLiterals.staticTree = StaticTree.Literals;
            treeDistances.dyn_tree = dyn_dtree;
            treeDistances.staticTree = StaticTree.Distances;
            treeBitLengths.dyn_tree = bl_tree;
            treeBitLengths.staticTree = StaticTree.BitLengths;
            bi_buf = 0;
            bi_valid = 0;
            last_eob_len = 8;
            _InitializeBlocks();
        }

        internal void _InitializeBlocks()
        {
            for (int i = 0; i < InternalConstants.L_CODES; i++)
            {
                dyn_ltree[i * 2] = 0;
            }
            for (int j = 0; j < InternalConstants.D_CODES; j++)
            {
                dyn_dtree[j * 2] = 0;
            }
            for (int k = 0; k < InternalConstants.BL_CODES; k++)
            {
                bl_tree[k * 2] = 0;
            }
            dyn_ltree[END_BLOCK * 2] = 1;
            opt_len = (static_len = 0);
            last_lit = (matches = 0);
        }

        internal void pqdownheap(short[] tree, int k)
        {
            int num = heap[k];
            for (int num2 = k << 1; num2 <= heap_len; num2 <<= 1)
            {
                if (num2 < heap_len && _IsSmaller(tree, heap[num2 + 1], heap[num2], depth))
                {
                    num2++;
                }
                if (_IsSmaller(tree, num, heap[num2], depth))
                {
                    break;
                }
                heap[k] = heap[num2];
                k = num2;
            }
            heap[k] = num;
        }

        internal static bool _IsSmaller(short[] tree, int n, int m, sbyte[] depth)
        {
            short num = tree[n * 2];
            short num2 = tree[m * 2];
            if (num >= num2)
            {
                if (num == num2)
                {
                    return depth[n] <= depth[m];
                }
                return false;
            }
            return true;
        }

        internal void scan_tree(short[] tree, int max_code)
        {
            int num = -1;
            int num2 = tree[1];
            int num3 = 0;
            int num4 = 7;
            int num5 = 4;
            if (num2 == 0)
            {
                num4 = 138;
                num5 = 3;
            }
            tree[(max_code + 1) * 2 + 1] = short.MaxValue;
            for (int i = 0; i <= max_code; i++)
            {
                int num6 = num2;
                num2 = tree[(i + 1) * 2 + 1];
                if (++num3 < num4 && num6 == num2)
                {
                    continue;
                }
                if (num3 < num5)
                {
                    bl_tree[num6 * 2] = (short)(bl_tree[num6 * 2] + num3);
                }
                else if (num6 != 0)
                {
                    if (num6 != num)
                    {
                        bl_tree[num6 * 2]++;
                    }
                    bl_tree[InternalConstants.REP_3_6 * 2]++;
                }
                else if (num3 <= 10)
                {
                    bl_tree[InternalConstants.REPZ_3_10 * 2]++;
                }
                else
                {
                    bl_tree[InternalConstants.REPZ_11_138 * 2]++;
                }
                num3 = 0;
                num = num6;
                if (num2 == 0)
                {
                    num4 = 138;
                    num5 = 3;
                }
                else if (num6 == num2)
                {
                    num4 = 6;
                    num5 = 3;
                }
                else
                {
                    num4 = 7;
                    num5 = 4;
                }
            }
        }

        internal int build_bl_tree()
        {
            scan_tree(dyn_ltree, treeLiterals.max_code);
            scan_tree(dyn_dtree, treeDistances.max_code);
            treeBitLengths.build_tree(this);
            int num = InternalConstants.BL_CODES - 1;
            while (num >= 3 && bl_tree[Tree.bl_order[num] * 2 + 1] == 0)
            {
                num--;
            }
            opt_len += 3 * (num + 1) + 5 + 5 + 4;
            return num;
        }

        internal void send_all_trees(int lcodes, int dcodes, int blcodes)
        {
            send_bits(lcodes - 257, 5);
            send_bits(dcodes - 1, 5);
            send_bits(blcodes - 4, 4);
            for (int i = 0; i < blcodes; i++)
            {
                send_bits(bl_tree[Tree.bl_order[i] * 2 + 1], 3);
            }
            send_tree(dyn_ltree, lcodes - 1);
            send_tree(dyn_dtree, dcodes - 1);
        }

        internal void send_tree(short[] tree, int max_code)
        {
            int num = -1;
            int num2 = tree[1];
            int num3 = 0;
            int num4 = 7;
            int num5 = 4;
            if (num2 == 0)
            {
                num4 = 138;
                num5 = 3;
            }
            for (int i = 0; i <= max_code; i++)
            {
                int num6 = num2;
                num2 = tree[(i + 1) * 2 + 1];
                if (++num3 < num4 && num6 == num2)
                {
                    continue;
                }
                if (num3 < num5)
                {
                    do
                    {
                        send_code(num6, bl_tree);
                    }
                    while (--num3 != 0);
                }
                else if (num6 != 0)
                {
                    if (num6 != num)
                    {
                        send_code(num6, bl_tree);
                        num3--;
                    }
                    send_code(InternalConstants.REP_3_6, bl_tree);
                    send_bits(num3 - 3, 2);
                }
                else if (num3 <= 10)
                {
                    send_code(InternalConstants.REPZ_3_10, bl_tree);
                    send_bits(num3 - 3, 3);
                }
                else
                {
                    send_code(InternalConstants.REPZ_11_138, bl_tree);
                    send_bits(num3 - 11, 7);
                }
                num3 = 0;
                num = num6;
                if (num2 == 0)
                {
                    num4 = 138;
                    num5 = 3;
                }
                else if (num6 == num2)
                {
                    num4 = 6;
                    num5 = 3;
                }
                else
                {
                    num4 = 7;
                    num5 = 4;
                }
            }
        }

        private void put_bytes(byte[] p, int start, int len)
        {
            Array.Copy(p, start, pending, pendingCount, len);
            pendingCount += len;
        }

        internal void send_code(int c, short[] tree)
        {
            int num = c * 2;
            send_bits(tree[num] & 0xFFFF, tree[num + 1] & 0xFFFF);
        }

        internal void send_bits(int value, int length)
        {
            if (bi_valid > Buf_size - length)
            {
                bi_buf |= (short)((value << bi_valid) & 0xFFFF);
                pending[pendingCount++] = (byte)bi_buf;
                pending[pendingCount++] = (byte)(bi_buf >> 8);
                bi_buf = (short)(value >>> Buf_size - bi_valid);
                bi_valid += length - Buf_size;
            }
            else
            {
                bi_buf |= (short)((value << bi_valid) & 0xFFFF);
                bi_valid += length;
            }
        }

        internal void _tr_align()
        {
            send_bits(STATIC_TREES << 1, 3);
            send_code(END_BLOCK, StaticTree.lengthAndLiteralsTreeCodes);
            bi_flush();
            if (1 + last_eob_len + 10 - bi_valid < 9)
            {
                send_bits(STATIC_TREES << 1, 3);
                send_code(END_BLOCK, StaticTree.lengthAndLiteralsTreeCodes);
                bi_flush();
            }
            last_eob_len = 7;
        }

        internal bool _tr_tally(int dist, int lc)
        {
            pending[_distanceOffset + last_lit * 2] = (byte)((uint)dist >> 8);
            pending[_distanceOffset + last_lit * 2 + 1] = (byte)dist;
            pending[_lengthOffset + last_lit] = (byte)lc;
            last_lit++;
            if (dist == 0)
            {
                dyn_ltree[lc * 2]++;
            }
            else
            {
                matches++;
                dist--;
                dyn_ltree[(Tree.LengthCode[lc] + InternalConstants.LITERALS + 1) * 2]++;
                dyn_dtree[Tree.DistanceCode(dist) * 2]++;
            }
            if ((last_lit & 0x1FFF) == 0 && compressionLevel > CompressionLevel.Level2)
            {
                int num = last_lit << 3;
                int num2 = strstart - block_start;
                for (int i = 0; i < InternalConstants.D_CODES; i++)
                {
                    num = (int)(num + dyn_dtree[i * 2] * (5L + (long)Tree.ExtraDistanceBits[i]));
                }
                num >>= 3;
                if (matches < last_lit / 2 && num < num2 / 2)
                {
                    return true;
                }
            }
            if (last_lit != lit_bufsize - 1)
            {
                return last_lit == lit_bufsize;
            }
            return true;
        }

        internal void send_compressed_block(short[] ltree, short[] dtree)
        {
            int num = 0;
            if (last_lit != 0)
            {
                do
                {
                    int num2 = _distanceOffset + num * 2;
                    int num3 = ((pending[num2] << 8) & 0xFF00) | (pending[num2 + 1] & 0xFF);
                    int num4 = pending[_lengthOffset + num] & 0xFF;
                    num++;
                    if (num3 == 0)
                    {
                        send_code(num4, ltree);
                        continue;
                    }
                    int num5 = Tree.LengthCode[num4];
                    send_code(num5 + InternalConstants.LITERALS + 1, ltree);
                    int num6 = Tree.ExtraLengthBits[num5];
                    if (num6 != 0)
                    {
                        num4 -= Tree.LengthBase[num5];
                        send_bits(num4, num6);
                    }
                    num3--;
                    num5 = Tree.DistanceCode(num3);
                    send_code(num5, dtree);
                    num6 = Tree.ExtraDistanceBits[num5];
                    if (num6 != 0)
                    {
                        num3 -= Tree.DistanceBase[num5];
                        send_bits(num3, num6);
                    }
                }
                while (num < last_lit);
            }
            send_code(END_BLOCK, ltree);
            last_eob_len = ltree[END_BLOCK * 2 + 1];
        }

        internal void set_data_type()
        {
            int i = 0;
            int num = 0;
            int num2 = 0;
            for (; i < 7; i++)
            {
                num2 += dyn_ltree[i * 2];
            }
            for (; i < 128; i++)
            {
                num += dyn_ltree[i * 2];
            }
            for (; i < InternalConstants.LITERALS; i++)
            {
                num2 += dyn_ltree[i * 2];
            }
            data_type = (sbyte)((num2 > num >> 2) ? Z_BINARY : Z_ASCII);
        }

        internal void bi_flush()
        {
            if (bi_valid == 16)
            {
                pending[pendingCount++] = (byte)bi_buf;
                pending[pendingCount++] = (byte)(bi_buf >> 8);
                bi_buf = 0;
                bi_valid = 0;
            }
            else if (bi_valid >= 8)
            {
                pending[pendingCount++] = (byte)bi_buf;
                bi_buf >>= 8;
                bi_valid -= 8;
            }
        }

        internal void bi_windup()
        {
            if (bi_valid > 8)
            {
                pending[pendingCount++] = (byte)bi_buf;
                pending[pendingCount++] = (byte)(bi_buf >> 8);
            }
            else if (bi_valid > 0)
            {
                pending[pendingCount++] = (byte)bi_buf;
            }
            bi_buf = 0;
            bi_valid = 0;
        }

        internal void copy_block(int buf, int len, bool header)
        {
            bi_windup();
            last_eob_len = 8;
            if (header)
            {
                pending[pendingCount++] = (byte)len;
                pending[pendingCount++] = (byte)(len >> 8);
                pending[pendingCount++] = (byte)(~len);
                pending[pendingCount++] = (byte)(~len >> 8);
            }
            put_bytes(window, buf, len);
        }

        internal void flush_block_only(bool eof)
        {
            _tr_flush_block((block_start >= 0) ? block_start : (-1), strstart - block_start, eof);
            block_start = strstart;
            _codec.flush_pending();
        }

        internal BlockState DeflateNone(FlushType flush)
        {
            int num = 65535;
            if (num > pending.Length - 5)
            {
                num = pending.Length - 5;
            }
            while (true)
            {
                if (lookahead <= 1)
                {
                    _fillWindow();
                    if (lookahead == 0 && flush == FlushType.None)
                    {
                        return BlockState.NeedMore;
                    }
                    if (lookahead == 0)
                    {
                        break;
                    }
                }
                strstart += lookahead;
                lookahead = 0;
                int num2 = block_start + num;
                if (strstart == 0 || strstart >= num2)
                {
                    lookahead = strstart - num2;
                    strstart = num2;
                    flush_block_only(eof: false);
                    if (_codec.AvailableBytesOut == 0)
                    {
                        return BlockState.NeedMore;
                    }
                }
                if (strstart - block_start >= w_size - MIN_LOOKAHEAD)
                {
                    flush_block_only(eof: false);
                    if (_codec.AvailableBytesOut == 0)
                    {
                        return BlockState.NeedMore;
                    }
                }
            }
            flush_block_only(flush == FlushType.Finish);
            if (_codec.AvailableBytesOut == 0)
            {
                if (flush != FlushType.Finish)
                {
                    return BlockState.NeedMore;
                }
                return BlockState.FinishStarted;
            }
            if (flush != FlushType.Finish)
            {
                return BlockState.BlockDone;
            }
            return BlockState.FinishDone;
        }

        internal void _tr_stored_block(int buf, int stored_len, bool eof)
        {
            send_bits((STORED_BLOCK << 1) + (eof ? 1 : 0), 3);
            copy_block(buf, stored_len, header: true);
        }

        internal void _tr_flush_block(int buf, int stored_len, bool eof)
        {
            int num = 0;
            int num2;
            int num3;
            if (compressionLevel > CompressionLevel.None)
            {
                if (data_type == Z_UNKNOWN)
                {
                    set_data_type();
                }
                treeLiterals.build_tree(this);
                treeDistances.build_tree(this);
                num = build_bl_tree();
                num2 = opt_len + 3 + 7 >> 3;
                num3 = static_len + 3 + 7 >> 3;
                if (num3 <= num2)
                {
                    num2 = num3;
                }
            }
            else
            {
                num2 = (num3 = stored_len + 5);
            }
            if (stored_len + 4 <= num2 && buf != -1)
            {
                _tr_stored_block(buf, stored_len, eof);
            }
            else if (num3 == num2)
            {
                send_bits((STATIC_TREES << 1) + (eof ? 1 : 0), 3);
                send_compressed_block(StaticTree.lengthAndLiteralsTreeCodes, StaticTree.distTreeCodes);
            }
            else
            {
                send_bits((DYN_TREES << 1) + (eof ? 1 : 0), 3);
                send_all_trees(treeLiterals.max_code + 1, treeDistances.max_code + 1, num + 1);
                send_compressed_block(dyn_ltree, dyn_dtree);
            }
            _InitializeBlocks();
            if (eof)
            {
                bi_windup();
            }
        }

        private void _fillWindow()
        {
            do
            {
                int num = window_size - lookahead - strstart;
                int num2;
                if (num == 0 && strstart == 0 && lookahead == 0)
                {
                    num = w_size;
                }
                else if (num == -1)
                {
                    num--;
                }
                else if (strstart >= w_size + w_size - MIN_LOOKAHEAD)
                {
                    Array.Copy(window, w_size, window, 0, w_size);
                    match_start -= w_size;
                    strstart -= w_size;
                    block_start -= w_size;
                    num2 = hash_size;
                    int num3 = num2;
                    do
                    {
                        int num4 = head[--num3] & 0xFFFF;
                        head[num3] = (short)((num4 >= w_size) ? (num4 - w_size) : 0);
                    }
                    while (--num2 != 0);
                    num2 = w_size;
                    num3 = num2;
                    do
                    {
                        int num4 = prev[--num3] & 0xFFFF;
                        prev[num3] = (short)((num4 >= w_size) ? (num4 - w_size) : 0);
                    }
                    while (--num2 != 0);
                    num += w_size;
                }
                if (_codec.AvailableBytesIn == 0)
                {
                    break;
                }
                num2 = _codec.read_buf(window, strstart + lookahead, num);
                lookahead += num2;
                if (lookahead >= MIN_MATCH)
                {
                    ins_h = window[strstart] & 0xFF;
                    ins_h = ((ins_h << hash_shift) ^ (window[strstart + 1] & 0xFF)) & hash_mask;
                }
            }
            while (lookahead < MIN_LOOKAHEAD && _codec.AvailableBytesIn != 0);
        }

        internal BlockState DeflateFast(FlushType flush)
        {
            int num = 0;
            while (true)
            {
                if (lookahead < MIN_LOOKAHEAD)
                {
                    _fillWindow();
                    if (lookahead < MIN_LOOKAHEAD && flush == FlushType.None)
                    {
                        return BlockState.NeedMore;
                    }
                    if (lookahead == 0)
                    {
                        break;
                    }
                }
                if (lookahead >= MIN_MATCH)
                {
                    ins_h = ((ins_h << hash_shift) ^ (window[strstart + (MIN_MATCH - 1)] & 0xFF)) & hash_mask;
                    num = head[ins_h] & 0xFFFF;
                    prev[strstart & w_mask] = head[ins_h];
                    head[ins_h] = (short)strstart;
                }
                if (num != 0L && ((strstart - num) & 0xFFFF) <= w_size - MIN_LOOKAHEAD && compressionStrategy != CompressionStrategy.HuffmanOnly)
                {
                    match_length = longest_match(num);
                }
                bool flag;
                if (match_length >= MIN_MATCH)
                {
                    flag = _tr_tally(strstart - match_start, match_length - MIN_MATCH);
                    lookahead -= match_length;
                    if (match_length <= config.MaxLazy && lookahead >= MIN_MATCH)
                    {
                        match_length--;
                        do
                        {
                            strstart++;
                            ins_h = ((ins_h << hash_shift) ^ (window[strstart + (MIN_MATCH - 1)] & 0xFF)) & hash_mask;
                            num = head[ins_h] & 0xFFFF;
                            prev[strstart & w_mask] = head[ins_h];
                            head[ins_h] = (short)strstart;
                        }
                        while (--match_length != 0);
                        strstart++;
                    }
                    else
                    {
                        strstart += match_length;
                        match_length = 0;
                        ins_h = window[strstart] & 0xFF;
                        ins_h = ((ins_h << hash_shift) ^ (window[strstart + 1] & 0xFF)) & hash_mask;
                    }
                }
                else
                {
                    flag = _tr_tally(0, window[strstart] & 0xFF);
                    lookahead--;
                    strstart++;
                }
                if (flag)
                {
                    flush_block_only(eof: false);
                    if (_codec.AvailableBytesOut == 0)
                    {
                        return BlockState.NeedMore;
                    }
                }
            }
            flush_block_only(flush == FlushType.Finish);
            if (_codec.AvailableBytesOut == 0)
            {
                if (flush == FlushType.Finish)
                {
                    return BlockState.FinishStarted;
                }
                return BlockState.NeedMore;
            }
            if (flush != FlushType.Finish)
            {
                return BlockState.BlockDone;
            }
            return BlockState.FinishDone;
        }

        internal BlockState DeflateSlow(FlushType flush)
        {
            int num = 0;
            while (true)
            {
                if (lookahead < MIN_LOOKAHEAD)
                {
                    _fillWindow();
                    if (lookahead < MIN_LOOKAHEAD && flush == FlushType.None)
                    {
                        return BlockState.NeedMore;
                    }
                    if (lookahead == 0)
                    {
                        break;
                    }
                }
                if (lookahead >= MIN_MATCH)
                {
                    ins_h = ((ins_h << hash_shift) ^ (window[strstart + (MIN_MATCH - 1)] & 0xFF)) & hash_mask;
                    num = head[ins_h] & 0xFFFF;
                    prev[strstart & w_mask] = head[ins_h];
                    head[ins_h] = (short)strstart;
                }
                prev_length = match_length;
                prev_match = match_start;
                match_length = MIN_MATCH - 1;
                if (num != 0 && prev_length < config.MaxLazy && ((strstart - num) & 0xFFFF) <= w_size - MIN_LOOKAHEAD)
                {
                    if (compressionStrategy != CompressionStrategy.HuffmanOnly)
                    {
                        match_length = longest_match(num);
                    }
                    if (match_length <= 5 && (compressionStrategy == CompressionStrategy.Filtered || (match_length == MIN_MATCH && strstart - match_start > 4096)))
                    {
                        match_length = MIN_MATCH - 1;
                    }
                }
                if (prev_length >= MIN_MATCH && match_length <= prev_length)
                {
                    int num2 = strstart + lookahead - MIN_MATCH;
                    bool flag = _tr_tally(strstart - 1 - prev_match, prev_length - MIN_MATCH);
                    lookahead -= prev_length - 1;
                    prev_length -= 2;
                    do
                    {
                        if (++strstart <= num2)
                        {
                            ins_h = ((ins_h << hash_shift) ^ (window[strstart + (MIN_MATCH - 1)] & 0xFF)) & hash_mask;
                            num = head[ins_h] & 0xFFFF;
                            prev[strstart & w_mask] = head[ins_h];
                            head[ins_h] = (short)strstart;
                        }
                    }
                    while (--prev_length != 0);
                    match_available = 0;
                    match_length = MIN_MATCH - 1;
                    strstart++;
                    if (flag)
                    {
                        flush_block_only(eof: false);
                        if (_codec.AvailableBytesOut == 0)
                        {
                            return BlockState.NeedMore;
                        }
                    }
                }
                else if (match_available != 0)
                {
                    if (_tr_tally(0, window[strstart - 1] & 0xFF))
                    {
                        flush_block_only(eof: false);
                    }
                    strstart++;
                    lookahead--;
                    if (_codec.AvailableBytesOut == 0)
                    {
                        return BlockState.NeedMore;
                    }
                }
                else
                {
                    match_available = 1;
                    strstart++;
                    lookahead--;
                }
            }
            if (match_available != 0)
            {
                bool flag = _tr_tally(0, window[strstart - 1] & 0xFF);
                match_available = 0;
            }
            flush_block_only(flush == FlushType.Finish);
            if (_codec.AvailableBytesOut == 0)
            {
                if (flush == FlushType.Finish)
                {
                    return BlockState.FinishStarted;
                }
                return BlockState.NeedMore;
            }
            if (flush != FlushType.Finish)
            {
                return BlockState.BlockDone;
            }
            return BlockState.FinishDone;
        }

        internal int longest_match(int cur_match)
        {
            int num = config.MaxChainLength;
            int num2 = strstart;
            int num3 = prev_length;
            int num4 = ((strstart > w_size - MIN_LOOKAHEAD) ? (strstart - (w_size - MIN_LOOKAHEAD)) : 0);
            int niceLength = config.NiceLength;
            int num5 = w_mask;
            int num6 = strstart + MAX_MATCH;
            byte b = window[num2 + num3 - 1];
            byte b2 = window[num2 + num3];
            if (prev_length >= config.GoodLength)
            {
                num >>= 2;
            }
            if (niceLength > lookahead)
            {
                niceLength = lookahead;
            }
            do
            {
                int num7 = cur_match;
                if (window[num7 + num3] != b2 || window[num7 + num3 - 1] != b || window[num7] != window[num2] || window[++num7] != window[num2 + 1])
                {
                    continue;
                }
                num2 += 2;
                num7++;
                while (window[++num2] == window[++num7] && window[++num2] == window[++num7] && window[++num2] == window[++num7] && window[++num2] == window[++num7] && window[++num2] == window[++num7] && window[++num2] == window[++num7] && window[++num2] == window[++num7] && window[++num2] == window[++num7] && num2 < num6)
                {
                }
                int num8 = MAX_MATCH - (num6 - num2);
                num2 = num6 - MAX_MATCH;
                if (num8 > num3)
                {
                    match_start = cur_match;
                    num3 = num8;
                    if (num8 >= niceLength)
                    {
                        break;
                    }
                    b = window[num2 + num3 - 1];
                    b2 = window[num2 + num3];
                }
            }
            while ((cur_match = prev[cur_match & num5] & 0xFFFF) > num4 && --num != 0);
            if (num3 <= lookahead)
            {
                return num3;
            }
            return lookahead;
        }

        internal int Initialize(ZlibCodec codec, CompressionLevel level)
        {
            return Initialize(codec, level, 15);
        }

        internal int Initialize(ZlibCodec codec, CompressionLevel level, int bits)
        {
            return Initialize(codec, level, bits, MEM_LEVEL_DEFAULT, CompressionStrategy.Default);
        }

        internal int Initialize(ZlibCodec codec, CompressionLevel level, int bits, CompressionStrategy compressionStrategy)
        {
            return Initialize(codec, level, bits, MEM_LEVEL_DEFAULT, compressionStrategy);
        }

        internal int Initialize(ZlibCodec codec, CompressionLevel level, int windowBits, int memLevel, CompressionStrategy strategy)
        {
            _codec = codec;
            _codec.Message = null;
            if (windowBits < 9 || windowBits > 15)
            {
                throw new ZlibException("windowBits must be in the range 9..15.");
            }
            if (memLevel < 1 || memLevel > MEM_LEVEL_MAX)
            {
                throw new ZlibException($"memLevel must be in the range 1.. {MEM_LEVEL_MAX}");
            }
            _codec.dstate = this;
            w_bits = windowBits;
            w_size = 1 << w_bits;
            w_mask = w_size - 1;
            hash_bits = memLevel + 7;
            hash_size = 1 << hash_bits;
            hash_mask = hash_size - 1;
            hash_shift = (hash_bits + MIN_MATCH - 1) / MIN_MATCH;
            window = new byte[w_size * 2];
            prev = new short[w_size];
            head = new short[hash_size];
            lit_bufsize = 1 << memLevel + 6;
            pending = new byte[lit_bufsize * 4];
            _distanceOffset = lit_bufsize;
            _lengthOffset = 3 * lit_bufsize;
            compressionLevel = level;
            compressionStrategy = strategy;
            Reset();
            return 0;
        }

        internal void Reset()
        {
            _codec.TotalBytesIn = (_codec.TotalBytesOut = 0L);
            _codec.Message = null;
            pendingCount = 0;
            nextPending = 0;
            Rfc1950BytesEmitted = false;
            status = (WantRfc1950HeaderBytes ? INIT_STATE : BUSY_STATE);
            _codec._Adler32 = Adler.Adler32(0u, null, 0, 0);
            last_flush = 0;
            _InitializeTreeData();
            _InitializeLazyMatch();
        }

        internal int End()
        {
            if (status != INIT_STATE && status != BUSY_STATE && status != FINISH_STATE)
            {
                return -2;
            }
            pending = null;
            head = null;
            prev = null;
            window = null;
            if (status != BUSY_STATE)
            {
                return 0;
            }
            return -3;
        }

        private void SetDeflater()
        {
            switch (config.Flavor)
            {
            case DeflateFlavor.Store:
                DeflateFunction = DeflateNone;
                break;
            case DeflateFlavor.Fast:
                DeflateFunction = DeflateFast;
                break;
            case DeflateFlavor.Slow:
                DeflateFunction = DeflateSlow;
                break;
            }
        }

        internal int SetParams(CompressionLevel level, CompressionStrategy strategy)
        {
            int result = 0;
            if (compressionLevel != level)
            {
                Config config = Config.Lookup(level);
                if (config.Flavor != this.config.Flavor && _codec.TotalBytesIn != 0L)
                {
                    result = _codec.Deflate(FlushType.Partial);
                }
                compressionLevel = level;
                this.config = config;
                SetDeflater();
            }
            compressionStrategy = strategy;
            return result;
        }

        internal int SetDictionary(byte[] dictionary)
        {
            if (dictionary == null || status != INIT_STATE)
            {
                throw new ZlibException("Stream error.");
            }
            int num = dictionary.Length;
            int sourceIndex = 0;
            _codec._Adler32 = Adler.Adler32(_codec._Adler32, dictionary, 0, dictionary.Length);
            if (num < MIN_MATCH)
            {
                return 0;
            }
            if (num > w_size - MIN_LOOKAHEAD)
            {
                num = w_size - MIN_LOOKAHEAD;
                sourceIndex = dictionary.Length - num;
            }
            Array.Copy(dictionary, sourceIndex, window, 0, num);
            strstart = num;
            block_start = num;
            ins_h = window[0] & 0xFF;
            ins_h = ((ins_h << hash_shift) ^ (window[1] & 0xFF)) & hash_mask;
            for (int i = 0; i <= num - MIN_MATCH; i++)
            {
                ins_h = ((ins_h << hash_shift) ^ (window[i + (MIN_MATCH - 1)] & 0xFF)) & hash_mask;
                prev[i & w_mask] = head[ins_h];
                head[ins_h] = (short)i;
            }
            return 0;
        }

        internal int Deflate(FlushType flush)
        {
            if (_codec.OutputBuffer == null || (_codec.InputBuffer == null && _codec.AvailableBytesIn != 0) || (status == FINISH_STATE && flush != FlushType.Finish))
            {
                _codec.Message = _ErrorMessage[4];
                throw new ZlibException($"Something is fishy. [{_codec.Message}]");
            }
            if (_codec.AvailableBytesOut == 0)
            {
                _codec.Message = _ErrorMessage[7];
                throw new ZlibException("OutputBuffer is full (AvailableBytesOut == 0)");
            }
            int num = last_flush;
            last_flush = (int)flush;
            if (status == INIT_STATE)
            {
                int num2 = Z_DEFLATED + (w_bits - 8 << 4) << 8;
                int num3 = (int)((compressionLevel - 1) & (CompressionLevel)255) >> 1;
                if (num3 > 3)
                {
                    num3 = 3;
                }
                num2 |= num3 << 6;
                if (strstart != 0)
                {
                    num2 |= PRESET_DICT;
                }
                num2 += 31 - num2 % 31;
                status = BUSY_STATE;
                pending[pendingCount++] = (byte)(num2 >> 8);
                pending[pendingCount++] = (byte)num2;
                if (strstart != 0)
                {
                    pending[pendingCount++] = (byte)((_codec._Adler32 & 0xFF000000u) >> 24);
                    pending[pendingCount++] = (byte)((_codec._Adler32 & 0xFF0000) >> 16);
                    pending[pendingCount++] = (byte)((_codec._Adler32 & 0xFF00) >> 8);
                    pending[pendingCount++] = (byte)(_codec._Adler32 & 0xFF);
                }
                _codec._Adler32 = Adler.Adler32(0u, null, 0, 0);
            }
            if (pendingCount != 0)
            {
                _codec.flush_pending();
                if (_codec.AvailableBytesOut == 0)
                {
                    last_flush = -1;
                    return 0;
                }
            }
            else if (_codec.AvailableBytesIn == 0 && (int)flush <= num && flush != FlushType.Finish)
            {
                return 0;
            }
            if (status == FINISH_STATE && _codec.AvailableBytesIn != 0)
            {
                _codec.Message = _ErrorMessage[7];
                throw new ZlibException("status == FINISH_STATE && _codec.AvailableBytesIn != 0");
            }
            if (_codec.AvailableBytesIn != 0 || lookahead != 0 || (flush != FlushType.None && status != FINISH_STATE))
            {
                BlockState blockState = DeflateFunction(flush);
                if (blockState == BlockState.FinishStarted || blockState == BlockState.FinishDone)
                {
                    status = FINISH_STATE;
                }
                switch (blockState)
                {
                case BlockState.NeedMore:
                case BlockState.FinishStarted:
                    if (_codec.AvailableBytesOut == 0)
                    {
                        last_flush = -1;
                    }
                    return 0;
                case BlockState.BlockDone:
                    if (flush == FlushType.Partial)
                    {
                        _tr_align();
                    }
                    else
                    {
                        _tr_stored_block(0, 0, eof: false);
                        if (flush == FlushType.Full)
                        {
                            for (int i = 0; i < hash_size; i++)
                            {
                                head[i] = 0;
                            }
                        }
                    }
                    _codec.flush_pending();
                    if (_codec.AvailableBytesOut == 0)
                    {
                        last_flush = -1;
                        return 0;
                    }
                    break;
                }
            }
            if (flush != FlushType.Finish)
            {
                return 0;
            }
            if (!WantRfc1950HeaderBytes || Rfc1950BytesEmitted)
            {
                return 1;
            }
            pending[pendingCount++] = (byte)((_codec._Adler32 & 0xFF000000u) >> 24);
            pending[pendingCount++] = (byte)((_codec._Adler32 & 0xFF0000) >> 16);
            pending[pendingCount++] = (byte)((_codec._Adler32 & 0xFF00) >> 8);
            pending[pendingCount++] = (byte)(_codec._Adler32 & 0xFF);
            _codec.flush_pending();
            Rfc1950BytesEmitted = true;
            return (pendingCount == 0) ? 1 : 0;
        }
    }

    internal sealed class Tree
    {
        private static readonly int HEAP_SIZE = 2 * InternalConstants.L_CODES + 1;

        internal static readonly int[] ExtraLengthBits = new int[29]
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
            1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
            4, 4, 4, 4, 5, 5, 5, 5, 0
        };

        internal static readonly int[] ExtraDistanceBits = new int[30]
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3,
            4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
            9, 9, 10, 10, 11, 11, 12, 12, 13, 13
        };

        internal static readonly int[] extra_blbits = new int[19]
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 2, 3, 7
        };

        internal static readonly sbyte[] bl_order = new sbyte[19]
        {
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5,
            11, 4, 12, 3, 13, 2, 14, 1, 15
        };

        internal const int Buf_size = 16;

        private static readonly sbyte[] _dist_code = new sbyte[512]
        {
            0, 1, 2, 3, 4, 4, 5, 5, 6, 6,
            6, 6, 7, 7, 7, 7, 8, 8, 8, 8,
            8, 8, 8, 8, 9, 9, 9, 9, 9, 9,
            9, 9, 10, 10, 10, 10, 10, 10, 10, 10,
            10, 10, 10, 10, 10, 10, 10, 10, 11, 11,
            11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
            11, 11, 11, 11, 12, 12, 12, 12, 12, 12,
            12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
            12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
            12, 12, 12, 12, 12, 12, 13, 13, 13, 13,
            13, 13, 13, 13, 13, 13, 13, 13, 13, 13,
            13, 13, 13, 13, 13, 13, 13, 13, 13, 13,
            13, 13, 13, 13, 13, 13, 13, 13, 14, 14,
            14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
            14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
            14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
            14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
            14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
            14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
            14, 14, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 0, 0, 16, 17,
            18, 18, 19, 19, 20, 20, 20, 20, 21, 21,
            21, 21, 22, 22, 22, 22, 22, 22, 22, 22,
            23, 23, 23, 23, 23, 23, 23, 23, 24, 24,
            24, 24, 24, 24, 24, 24, 24, 24, 24, 24,
            24, 24, 24, 24, 25, 25, 25, 25, 25, 25,
            25, 25, 25, 25, 25, 25, 25, 25, 25, 25,
            26, 26, 26, 26, 26, 26, 26, 26, 26, 26,
            26, 26, 26, 26, 26, 26, 26, 26, 26, 26,
            26, 26, 26, 26, 26, 26, 26, 26, 26, 26,
            26, 26, 27, 27, 27, 27, 27, 27, 27, 27,
            27, 27, 27, 27, 27, 27, 27, 27, 27, 27,
            27, 27, 27, 27, 27, 27, 27, 27, 27, 27,
            27, 27, 27, 27, 28, 28, 28, 28, 28, 28,
            28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
            28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
            28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
            28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
            28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
            28, 28, 28, 28, 28, 28, 28, 28, 29, 29,
            29, 29, 29, 29, 29, 29, 29, 29, 29, 29,
            29, 29, 29, 29, 29, 29, 29, 29, 29, 29,
            29, 29, 29, 29, 29, 29, 29, 29, 29, 29,
            29, 29, 29, 29, 29, 29, 29, 29, 29, 29,
            29, 29, 29, 29, 29, 29, 29, 29, 29, 29,
            29, 29, 29, 29, 29, 29, 29, 29, 29, 29,
            29, 29
        };

        internal static readonly sbyte[] LengthCode = new sbyte[256]
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 8,
            9, 9, 10, 10, 11, 11, 12, 12, 12, 12,
            13, 13, 13, 13, 14, 14, 14, 14, 15, 15,
            15, 15, 16, 16, 16, 16, 16, 16, 16, 16,
            17, 17, 17, 17, 17, 17, 17, 17, 18, 18,
            18, 18, 18, 18, 18, 18, 19, 19, 19, 19,
            19, 19, 19, 19, 20, 20, 20, 20, 20, 20,
            20, 20, 20, 20, 20, 20, 20, 20, 20, 20,
            21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
            21, 21, 21, 21, 21, 21, 22, 22, 22, 22,
            22, 22, 22, 22, 22, 22, 22, 22, 22, 22,
            22, 22, 23, 23, 23, 23, 23, 23, 23, 23,
            23, 23, 23, 23, 23, 23, 23, 23, 24, 24,
            24, 24, 24, 24, 24, 24, 24, 24, 24, 24,
            24, 24, 24, 24, 24, 24, 24, 24, 24, 24,
            24, 24, 24, 24, 24, 24, 24, 24, 24, 24,
            25, 25, 25, 25, 25, 25, 25, 25, 25, 25,
            25, 25, 25, 25, 25, 25, 25, 25, 25, 25,
            25, 25, 25, 25, 25, 25, 25, 25, 25, 25,
            25, 25, 26, 26, 26, 26, 26, 26, 26, 26,
            26, 26, 26, 26, 26, 26, 26, 26, 26, 26,
            26, 26, 26, 26, 26, 26, 26, 26, 26, 26,
            26, 26, 26, 26, 27, 27, 27, 27, 27, 27,
            27, 27, 27, 27, 27, 27, 27, 27, 27, 27,
            27, 27, 27, 27, 27, 27, 27, 27, 27, 27,
            27, 27, 27, 27, 27, 28
        };

        internal static readonly int[] LengthBase = new int[29]
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 10,
            12, 14, 16, 20, 24, 28, 32, 40, 48, 56,
            64, 80, 96, 112, 128, 160, 192, 224, 0
        };

        internal static readonly int[] DistanceBase = new int[30]
        {
            0, 1, 2, 3, 4, 6, 8, 12, 16, 24,
            32, 48, 64, 96, 128, 192, 256, 384, 512, 768,
            1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384, 24576
        };

        internal short[] dyn_tree;

        internal int max_code;

        internal StaticTree staticTree;

        /// <summary>
        /// Map from a distance to a distance code.
        /// </summary>
        /// <remarks>
        /// No side effects. _dist_code[256] and _dist_code[257] are never used.
        /// </remarks>
        internal static int DistanceCode(int dist)
        {
            if (dist >= 256)
            {
                return _dist_code[256 + SharedUtils.URShift(dist, 7)];
            }
            return _dist_code[dist];
        }

        internal void gen_bitlen(DeflateManager s)
        {
            short[] array = dyn_tree;
            short[] treeCodes = staticTree.treeCodes;
            int[] extraBits = staticTree.extraBits;
            int extraBase = staticTree.extraBase;
            int maxLength = staticTree.maxLength;
            int num = 0;
            for (int i = 0; i <= InternalConstants.MAX_BITS; i++)
            {
                s.bl_count[i] = 0;
            }
            array[s.heap[s.heap_max] * 2 + 1] = 0;
            int j;
            for (j = s.heap_max + 1; j < HEAP_SIZE; j++)
            {
                int num2 = s.heap[j];
                int i = array[array[num2 * 2 + 1] * 2 + 1] + 1;
                if (i > maxLength)
                {
                    i = maxLength;
                    num++;
                }
                array[num2 * 2 + 1] = (short)i;
                if (num2 <= max_code)
                {
                    s.bl_count[i]++;
                    int num3 = 0;
                    if (num2 >= extraBase)
                    {
                        num3 = extraBits[num2 - extraBase];
                    }
                    short num4 = array[num2 * 2];
                    s.opt_len += num4 * (i + num3);
                    if (treeCodes != null)
                    {
                        s.static_len += num4 * (treeCodes[num2 * 2 + 1] + num3);
                    }
                }
            }
            if (num == 0)
            {
                return;
            }
            do
            {
                int i = maxLength - 1;
                while (s.bl_count[i] == 0)
                {
                    i--;
                }
                s.bl_count[i]--;
                s.bl_count[i + 1] = (short)(s.bl_count[i + 1] + 2);
                s.bl_count[maxLength]--;
                num -= 2;
            }
            while (num > 0);
            for (int i = maxLength; i != 0; i--)
            {
                int num2 = s.bl_count[i];
                while (num2 != 0)
                {
                    int num5 = s.heap[--j];
                    if (num5 <= max_code)
                    {
                        if (array[num5 * 2 + 1] != i)
                        {
                            s.opt_len = (int)(s.opt_len + ((long)i - (long)array[num5 * 2 + 1]) * array[num5 * 2]);
                            array[num5 * 2 + 1] = (short)i;
                        }
                        num2--;
                    }
                }
            }
        }

        internal void build_tree(DeflateManager s)
        {
            short[] array = dyn_tree;
            short[] treeCodes = staticTree.treeCodes;
            int elems = staticTree.elems;
            int num = -1;
            s.heap_len = 0;
            s.heap_max = HEAP_SIZE;
            for (int i = 0; i < elems; i++)
            {
                if (array[i * 2] != 0)
                {
                    num = (s.heap[++s.heap_len] = i);
                    s.depth[i] = 0;
                }
                else
                {
                    array[i * 2 + 1] = 0;
                }
            }
            int num2;
            while (s.heap_len < 2)
            {
                num2 = (s.heap[++s.heap_len] = ((num < 2) ? (++num) : 0));
                array[num2 * 2] = 1;
                s.depth[num2] = 0;
                s.opt_len--;
                if (treeCodes != null)
                {
                    s.static_len -= treeCodes[num2 * 2 + 1];
                }
            }
            max_code = num;
            for (int i = s.heap_len / 2; i >= 1; i--)
            {
                s.pqdownheap(array, i);
            }
            num2 = elems;
            do
            {
                int i = s.heap[1];
                s.heap[1] = s.heap[s.heap_len--];
                s.pqdownheap(array, 1);
                int num3 = s.heap[1];
                s.heap[--s.heap_max] = i;
                s.heap[--s.heap_max] = num3;
                array[num2 * 2] = (short)(array[i * 2] + array[num3 * 2]);
                s.depth[num2] = (sbyte)(Math.Max((byte)s.depth[i], (byte)s.depth[num3]) + 1);
                array[i * 2 + 1] = (array[num3 * 2 + 1] = (short)num2);
                s.heap[1] = num2++;
                s.pqdownheap(array, 1);
            }
            while (s.heap_len >= 2);
            s.heap[--s.heap_max] = s.heap[1];
            gen_bitlen(s);
            gen_codes(array, num, s.bl_count);
        }

        internal static void gen_codes(short[] tree, int max_code, short[] bl_count)
        {
            short[] array = new short[InternalConstants.MAX_BITS + 1];
            short num = 0;
            for (int i = 1; i <= InternalConstants.MAX_BITS; i++)
            {
                num = (array[i] = (short)(num + bl_count[i - 1] << 1));
            }
            for (int j = 0; j <= max_code; j++)
            {
                int num2 = tree[j * 2 + 1];
                if (num2 != 0)
                {
                    tree[j * 2] = (short)bi_reverse(array[num2]++, num2);
                }
            }
        }

        internal static int bi_reverse(int code, int len)
        {
            int num = 0;
            do
            {
                num |= code & 1;
                code >>= 1;
                num <<= 1;
            }
            while (--len > 0);
            return num >> 1;
        }
    }

    /// <summary>
    /// Describes how to flush the current deflate operation.
    /// </summary>
    public enum FlushType
    {
        /// <summary>No flush at all.</summary>
        None,
        /// <summary>Closes the current block, but doesn't flush it to the output.</summary>
        Partial,
        /// <summary>
        /// Use this during compression to specify that all pending output should be
        /// flushed to the output buffer and the output should be aligned on a byte boundary.
        /// </summary>
        Sync,
        /// <summary>
        /// Use this during compression to specify that all output should be flushed, as
        /// with <c>FlushType.Sync</c>, but also, the compression state should be reset.
        /// </summary>
        Full,
        /// <summary>Signals the end of the compression/decompression stream.</summary>
        Finish
    }

    /// <summary>
    /// The compression level to be used when compressing data.
    /// </summary>
    public enum CompressionLevel
    {
        /// <summary>None means that the data will be simply stored, with no change at all.</summary>
        None = 0,
        /// <summary>Same as None.</summary>
        Level0 = 0,
        /// <summary>The fastest but least effective compression.</summary>
        BestSpeed = 1,
        /// <summary>A synonym for BestSpeed.</summary>
        Level1 = 1,
        /// <summary>A little slower, but better, than level 1.</summary>
        Level2 = 2,
        /// <summary>A little slower, but better, than level 2.</summary>
        Level3 = 3,
        /// <summary>A little slower, but better, than level 3.</summary>
        Level4 = 4,
        /// <summary>A little slower than level 4, but with better compression.</summary>
        Level5 = 5,
        /// <summary>The default compression level, with a good balance of speed and compression efficiency.</summary>
        Default = 6,
        /// <summary>A synonym for Default.</summary>
        Level6 = 6,
        /// <summary>Pretty good compression!</summary>
        Level7 = 7,
        /// <summary>Better compression than Level7!</summary>
        Level8 = 8,
        /// <summary>The "best" compression, where best means greatest reduction in size of the input data stream.</summary>
        BestCompression = 9,
        /// <summary>A synonym for BestCompression.</summary>
        Level9 = 9
    }

    /// <summary>
    /// Describes options for how the compression algorithm is executed.
    /// </summary>
    public enum CompressionStrategy
    {
        /// <summary>The default strategy is probably the best for normal data.</summary>
        Default,
        /// <summary>
        /// The <c>Filtered</c> strategy is intended to be used most effectively with data produced by a
        /// filter or predictor.
        /// </summary>
        Filtered,
        /// <summary>
        /// Using <c>HuffmanOnly</c> will force the compressor to do Huffman encoding only, with no
        /// string matching.
        /// </summary>
        HuffmanOnly
    }

    /// <summary>
    /// An enum to specify the direction of transcoding - whether to compress or decompress.
    /// </summary>
    public enum CompressionMode
    {
        /// <summary>Used to specify that the stream should compress the data.</summary>
        Compress,
        /// <summary>Used to specify that the stream should decompress the data.</summary>
        Decompress
    }

    /// <summary>
    /// A general purpose exception class for exceptions in the Zlib library.
    /// </summary>
    public class ZlibException : Exception
    {
        /// <summary>
        /// The ZlibException class captures exception information generated
        /// by the Zlib library.
        /// </summary>
        public ZlibException()
        {
        }

        /// <summary>
        /// This ctor collects a message attached to the exception.
        /// </summary>
        /// <param name="s">the message for the exception.</param>
        public ZlibException(string s)
            : base(s)
        {
        }
    }

    internal class SharedUtils
    {
        /// <summary>
        /// Performs an unsigned bitwise right shift with the specified number
        /// </summary>
        /// <param name="number">Number to operate on</param>
        /// <param name="bits">Ammount of bits to shift</param>
        /// <returns>The resulting number from the shift operation</returns>
        public static int URShift(int number, int bits)
        {
            return number >>> bits;
        }

        /// <summary>
        ///   Reads a number of characters from the current source TextReader and writes
        ///   the data to the target array at the specified index.
        /// </summary>
        public static int ReadInput(TextReader sourceTextReader, byte[] target, int start, int count)
        {
            if (target.Length == 0)
            {
                return 0;
            }
            char[] array = new char[target.Length];
            int num = sourceTextReader.Read(array, start, count);
            if (num == 0)
            {
                return -1;
            }
            for (int i = start; i < start + num; i++)
            {
                target[i] = (byte)array[i];
            }
            return num;
        }

        internal static byte[] ToByteArray(string sourceString)
        {
            return Encoding.UTF8.GetBytes(sourceString);
        }

        internal static char[] ToCharArray(byte[] byteArray)
        {
            return Encoding.UTF8.GetChars(byteArray);
        }
    }

    internal static class InternalConstants
    {
        internal static readonly int MAX_BITS = 15;

        internal static readonly int BL_CODES = 19;

        internal static readonly int D_CODES = 30;

        internal static readonly int LITERALS = 256;

        internal static readonly int LENGTH_CODES = 29;

        internal static readonly int L_CODES = LITERALS + 1 + LENGTH_CODES;

        internal static readonly int MAX_BL_BITS = 7;

        internal static readonly int REP_3_6 = 16;

        internal static readonly int REPZ_3_10 = 17;

        internal static readonly int REPZ_11_138 = 18;
    }

    internal sealed class StaticTree
    {
        internal static readonly short[] lengthAndLiteralsTreeCodes;

        internal static readonly short[] distTreeCodes;

        internal static readonly StaticTree Literals;

        internal static readonly StaticTree Distances;

        internal static readonly StaticTree BitLengths;

        internal short[] treeCodes;

        internal int[] extraBits;

        internal int extraBase;

        internal int elems;

        internal int maxLength;

        private StaticTree(short[] treeCodes, int[] extraBits, int extraBase, int elems, int maxLength)
        {
            this.treeCodes = treeCodes;
            this.extraBits = extraBits;
            this.extraBase = extraBase;
            this.elems = elems;
            this.maxLength = maxLength;
        }

        static StaticTree()
        {
            lengthAndLiteralsTreeCodes = new short[576]
            {
                12, 8, 140, 8, 76, 8, 204, 8, 44, 8,
                172, 8, 108, 8, 236, 8, 28, 8, 156, 8,
                92, 8, 220, 8, 60, 8, 188, 8, 124, 8,
                252, 8, 2, 8, 130, 8, 66, 8, 194, 8,
                34, 8, 162, 8, 98, 8, 226, 8, 18, 8,
                146, 8, 82, 8, 210, 8, 50, 8, 178, 8,
                114, 8, 242, 8, 10, 8, 138, 8, 74, 8,
                202, 8, 42, 8, 170, 8, 106, 8, 234, 8,
                26, 8, 154, 8, 90, 8, 218, 8, 58, 8,
                186, 8, 122, 8, 250, 8, 6, 8, 134, 8,
                70, 8, 198, 8, 38, 8, 166, 8, 102, 8,
                230, 8, 22, 8, 150, 8, 86, 8, 214, 8,
                54, 8, 182, 8, 118, 8, 246, 8, 14, 8,
                142, 8, 78, 8, 206, 8, 46, 8, 174, 8,
                110, 8, 238, 8, 30, 8, 158, 8, 94, 8,
                222, 8, 62, 8, 190, 8, 126, 8, 254, 8,
                1, 8, 129, 8, 65, 8, 193, 8, 33, 8,
                161, 8, 97, 8, 225, 8, 17, 8, 145, 8,
                81, 8, 209, 8, 49, 8, 177, 8, 113, 8,
                241, 8, 9, 8, 137, 8, 73, 8, 201, 8,
                41, 8, 169, 8, 105, 8, 233, 8, 25, 8,
                153, 8, 89, 8, 217, 8, 57, 8, 185, 8,
                121, 8, 249, 8, 5, 8, 133, 8, 69, 8,
                197, 8, 37, 8, 165, 8, 101, 8, 229, 8,
                21, 8, 149, 8, 85, 8, 213, 8, 53, 8,
                181, 8, 117, 8, 245, 8, 13, 8, 141, 8,
                77, 8, 205, 8, 45, 8, 173, 8, 109, 8,
                237, 8, 29, 8, 157, 8, 93, 8, 221, 8,
                61, 8, 189, 8, 125, 8, 253, 8, 19, 9,
                275, 9, 147, 9, 403, 9, 83, 9, 339, 9,
                211, 9, 467, 9, 51, 9, 307, 9, 179, 9,
                435, 9, 115, 9, 371, 9, 243, 9, 499, 9,
                11, 9, 267, 9, 139, 9, 395, 9, 75, 9,
                331, 9, 203, 9, 459, 9, 43, 9, 299, 9,
                171, 9, 427, 9, 107, 9, 363, 9, 235, 9,
                491, 9, 27, 9, 283, 9, 155, 9, 411, 9,
                91, 9, 347, 9, 219, 9, 475, 9, 59, 9,
                315, 9, 187, 9, 443, 9, 123, 9, 379, 9,
                251, 9, 507, 9, 7, 9, 263, 9, 135, 9,
                391, 9, 71, 9, 327, 9, 199, 9, 455, 9,
                39, 9, 295, 9, 167, 9, 423, 9, 103, 9,
                359, 9, 231, 9, 487, 9, 23, 9, 279, 9,
                151, 9, 407, 9, 87, 9, 343, 9, 215, 9,
                471, 9, 55, 9, 311, 9, 183, 9, 439, 9,
                119, 9, 375, 9, 247, 9, 503, 9, 15, 9,
                271, 9, 143, 9, 399, 9, 79, 9, 335, 9,
                207, 9, 463, 9, 47, 9, 303, 9, 175, 9,
                431, 9, 111, 9, 367, 9, 239, 9, 495, 9,
                31, 9, 287, 9, 159, 9, 415, 9, 95, 9,
                351, 9, 223, 9, 479, 9, 63, 9, 319, 9,
                191, 9, 447, 9, 127, 9, 383, 9, 255, 9,
                511, 9, 0, 7, 64, 7, 32, 7, 96, 7,
                16, 7, 80, 7, 48, 7, 112, 7, 8, 7,
                72, 7, 40, 7, 104, 7, 24, 7, 88, 7,
                56, 7, 120, 7, 4, 7, 68, 7, 36, 7,
                100, 7, 20, 7, 84, 7, 52, 7, 116, 7,
                3, 8, 131, 8, 67, 8, 195, 8, 35, 8,
                163, 8, 99, 8, 227, 8
            };
            distTreeCodes = new short[60]
            {
                0, 5, 16, 5, 8, 5, 24, 5, 4, 5,
                20, 5, 12, 5, 28, 5, 2, 5, 18, 5,
                10, 5, 26, 5, 6, 5, 22, 5, 14, 5,
                30, 5, 1, 5, 17, 5, 9, 5, 25, 5,
                5, 5, 21, 5, 13, 5, 29, 5, 3, 5,
                19, 5, 11, 5, 27, 5, 7, 5, 23, 5
            };
            Literals = new StaticTree(lengthAndLiteralsTreeCodes, Tree.ExtraLengthBits, InternalConstants.LITERALS + 1, InternalConstants.L_CODES, InternalConstants.MAX_BITS);
            Distances = new StaticTree(distTreeCodes, Tree.ExtraDistanceBits, 0, InternalConstants.D_CODES, InternalConstants.MAX_BITS);
            BitLengths = new StaticTree(null, Tree.extra_blbits, 0, InternalConstants.BL_CODES, InternalConstants.MAX_BL_BITS);
        }
    }

    /// <summary>
    /// Computes an Adler-32 checksum.
    /// </summary>
    public sealed class Adler
    {
        private static readonly uint BASE = 65521u;

        private static readonly int NMAX = 5552;

        /// <summary>
        ///   Calculates the Adler32 checksum.
        /// </summary>
        public static uint Adler32(uint adler, byte[] buf, int index, int len)
        {
            if (buf == null)
            {
                return 1u;
            }
            uint num = adler & 0xFFFF;
            uint num2 = (adler >> 16) & 0xFFFF;
            while (len > 0)
            {
                int num3 = ((len < NMAX) ? len : NMAX);
                len -= num3;
                while (num3 >= 16)
                {
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num += buf[index++];
                    num2 += num;
                    num3 -= 16;
                }
                if (num3 != 0)
                {
                    do
                    {
                        num += buf[index++];
                        num2 += num;
                    }
                    while (--num3 != 0);
                }
                num %= BASE;
                num2 %= BASE;
            }
            return (num2 << 16) | num;
        }
    }

    public sealed class ZlibCodec
    {
        /// <summary>
        /// The buffer from which data is taken.
        /// </summary>
        public byte[] InputBuffer;

        /// <summary>
        /// An index into the InputBuffer array, indicating where to start reading.
        /// </summary>
        public int NextIn;

        /// <summary>
        /// The number of bytes available in the InputBuffer, starting at NextIn.
        /// </summary>
        public int AvailableBytesIn;

        /// <summary>
        /// Total number of bytes read so far, through all calls to Deflate().
        /// </summary>
        public long TotalBytesIn;

        /// <summary>
        /// Buffer to store output data.
        /// </summary>
        public byte[] OutputBuffer;

        /// <summary>
        /// An index into the OutputBuffer array, indicating where to start writing.
        /// </summary>
        public int NextOut;

        /// <summary>
        /// The number of bytes available in the OutputBuffer, starting at NextOut.
        /// </summary>
        public int AvailableBytesOut;

        /// <summary>
        /// Total number of bytes written to the output so far, through all calls to Deflate().
        /// </summary>
        public long TotalBytesOut;

        /// <summary>
        /// used for diagnostics, when something goes wrong!
        /// </summary>
        public string Message;

        internal DeflateManager dstate;

        internal uint _Adler32;

        /// <summary>
        /// The compression level to use in this codec.  Useful only in compression mode.
        /// </summary>
        public CompressionLevel CompressLevel = CompressionLevel.Default;

        /// <summary>
        /// The number of Window Bits to use.
        /// </summary>
        public int WindowBits = 15;

        /// <summary>
        /// The compression strategy to use.
        /// </summary>
        public CompressionStrategy Strategy;

        /// <summary>
        /// The Adler32 checksum on the data transferred through the codec so far.
        /// </summary>
        public int Adler32 => (int)_Adler32;

        /// <summary>
        /// Create a ZlibCodec.
        /// </summary>
        public ZlibCodec()
        {
        }

        /// <summary>
        /// Create a ZlibCodec that compresses.
        /// </summary>
        /// <param name="mode">
        /// Indicates whether the codec should compress (deflate). Only Compress is supported here.
        /// </param>
        public ZlibCodec(CompressionMode mode)
        {
            switch (mode)
            {
            case CompressionMode.Compress:
                if (InitializeDeflate() != 0)
                {
                    throw new ZlibException("Cannot initialize for deflate.");
                }
                break;
            default:
                throw new ZlibException("Invalid ZlibStreamFlavor.");
            }
        }

        /// <summary>
        /// Initialize the ZlibCodec for deflation operation.
        /// </summary>
        /// <returns>Z_OK if all goes well.</returns>
        public int InitializeDeflate()
        {
            return _InternalInitializeDeflate(wantRfc1950Header: true);
        }

        /// <summary>
        /// Initialize the ZlibCodec for deflation operation, using the specified CompressionLevel.
        /// </summary>
        /// <param name="level">The compression level for the codec.</param>
        /// <returns>Z_OK if all goes well.</returns>
        public int InitializeDeflate(CompressionLevel level)
        {
            CompressLevel = level;
            return _InternalInitializeDeflate(wantRfc1950Header: true);
        }

        /// <summary>
        /// Initialize the ZlibCodec for deflation operation, using the specified CompressionLevel,
        /// and the explicit flag governing whether to emit an RFC1950 header byte pair.
        /// </summary>
        /// <param name="level">The compression level for the codec.</param>
        /// <param name="wantRfc1950Header">whether to emit an initial RFC1950 byte pair in the compressed stream.</param>
        /// <returns>Z_OK if all goes well.</returns>
        public int InitializeDeflate(CompressionLevel level, bool wantRfc1950Header)
        {
            CompressLevel = level;
            return _InternalInitializeDeflate(wantRfc1950Header);
        }

        /// <summary>
        /// Initialize the ZlibCodec for deflation operation, using the specified CompressionLevel,
        /// and the specified number of window bits.
        /// </summary>
        /// <param name="level">The compression level for the codec.</param>
        /// <param name="bits">the number of window bits to use.</param>
        /// <returns>Z_OK if all goes well.</returns>
        public int InitializeDeflate(CompressionLevel level, int bits)
        {
            CompressLevel = level;
            WindowBits = bits;
            return _InternalInitializeDeflate(wantRfc1950Header: true);
        }

        /// <summary>
        /// Initialize the ZlibCodec for deflation operation, using the specified
        /// CompressionLevel, the specified number of window bits, and the explicit flag
        /// governing whether to emit an RFC1950 header byte pair.
        /// </summary>
        /// <param name="level">The compression level for the codec.</param>
        /// <param name="wantRfc1950Header">whether to emit an initial RFC1950 byte pair in the compressed stream.</param>
        /// <param name="bits">the number of window bits to use.</param>
        /// <returns>Z_OK if all goes well.</returns>
        public int InitializeDeflate(CompressionLevel level, int bits, bool wantRfc1950Header)
        {
            CompressLevel = level;
            WindowBits = bits;
            return _InternalInitializeDeflate(wantRfc1950Header);
        }

        private int _InternalInitializeDeflate(bool wantRfc1950Header)
        {
            dstate = new DeflateManager();
            dstate.WantRfc1950HeaderBytes = wantRfc1950Header;
            return dstate.Initialize(this, CompressLevel, WindowBits, Strategy);
        }

        /// <summary>
        /// Deflate one batch of data.
        /// </summary>
        /// <param name="flush">whether to flush all data as you deflate.</param>
        /// <returns>Z_OK if all goes well.</returns>
        public int Deflate(FlushType flush)
        {
            if (dstate == null)
            {
                throw new ZlibException("No Deflate State!");
            }
            return dstate.Deflate(flush);
        }

        /// <summary>
        /// End a deflation session.
        /// </summary>
        /// <returns>Z_OK if all goes well.</returns>
        public int EndDeflate()
        {
            if (dstate == null)
            {
                throw new ZlibException("No Deflate State!");
            }
            dstate = null;
            return 0;
        }

        /// <summary>
        /// Reset a codec for another deflation session.
        /// </summary>
        public void ResetDeflate()
        {
            if (dstate == null)
            {
                throw new ZlibException("No Deflate State!");
            }
            dstate.Reset();
        }

        /// <summary>
        /// Set the CompressionStrategy and CompressionLevel for a deflation session.
        /// </summary>
        /// <param name="level">the level of compression to use.</param>
        /// <param name="strategy">the strategy to use for compression.</param>
        /// <returns>Z_OK if all goes well.</returns>
        public int SetDeflateParams(CompressionLevel level, CompressionStrategy strategy)
        {
            if (dstate == null)
            {
                throw new ZlibException("No Deflate State!");
            }
            return dstate.SetParams(level, strategy);
        }

        /// <summary>
        /// Set the dictionary to be used for Deflation.
        /// </summary>
        /// <param name="dictionary">The dictionary bytes to use.</param>
        /// <returns>Z_OK if all goes well.</returns>
        public int SetDictionary(byte[] dictionary)
        {
            if (dstate != null)
            {
                return dstate.SetDictionary(dictionary);
            }
            throw new ZlibException("No Deflate state!");
        }

        internal void flush_pending()
        {
            int num = dstate.pendingCount;
            if (num > AvailableBytesOut)
            {
                num = AvailableBytesOut;
            }
            if (num != 0)
            {
                if (dstate.pending.Length <= dstate.nextPending || OutputBuffer.Length <= NextOut || dstate.pending.Length < dstate.nextPending + num || OutputBuffer.Length < NextOut + num)
                {
                    throw new ZlibException($"Invalid State. (pending.Length={dstate.pending.Length}, pendingCount={dstate.pendingCount})");
                }
                Array.Copy(dstate.pending, dstate.nextPending, OutputBuffer, NextOut, num);
                NextOut += num;
                dstate.nextPending += num;
                TotalBytesOut += num;
                AvailableBytesOut -= num;
                dstate.pendingCount -= num;
                if (dstate.pendingCount == 0)
                {
                    dstate.nextPending = 0;
                }
            }
        }

        internal int read_buf(byte[] buf, int start, int size)
        {
            int num = AvailableBytesIn;
            if (num > size)
            {
                num = size;
            }
            if (num == 0)
            {
                return 0;
            }
            AvailableBytesIn -= num;
            if (dstate.WantRfc1950HeaderBytes)
            {
                _Adler32 = Adler.Adler32(_Adler32, InputBuffer, NextIn, num);
            }
            Array.Copy(InputBuffer, NextIn, buf, start, num);
            NextIn += num;
            TotalBytesIn += num;
            return num;
        }
    }

    /// <summary>
    /// A bunch of constants used in the Zlib interface.
    /// </summary>
    public static class ZlibConstants
    {
        /// <summary>
        /// The maximum number of window bits for the Deflate algorithm.
        /// </summary>
        public const int WindowBitsMax = 15;

        /// <summary>
        /// The default number of window bits for the Deflate algorithm.
        /// </summary>
        public const int WindowBitsDefault = 15;

        /// <summary>
        /// indicates everything is A-OK
        /// </summary>
        public const int Z_OK = 0;

        /// <summary>
        /// Indicates that the last operation reached the end of the stream.
        /// </summary>
        public const int Z_STREAM_END = 1;

        /// <summary>
        /// The operation ended in need of a dictionary.
        /// </summary>
        public const int Z_NEED_DICT = 2;

        /// <summary>
        /// There was an error with the stream - not enough data, not open and readable, etc.
        /// </summary>
        public const int Z_STREAM_ERROR = -2;

        /// <summary>
        /// There was an error with the data - not enough data, bad data, etc.
        /// </summary>
        public const int Z_DATA_ERROR = -3;

        /// <summary>
        /// There was an error with the working buffer.
        /// </summary>
        public const int Z_BUF_ERROR = -5;

        /// <summary>
        /// The size of the working buffer used in the ZlibCodec class.
        /// </summary>
        public const int WorkingBufferSizeDefault = 16384;

        /// <summary>
        /// The minimum size of the working buffer used in the ZlibCodec class.
        /// </summary>
        public const int WorkingBufferSizeMin = 1024;
    }

    /// <summary>
    /// Pure-managed zlib deflate helper. Produces a full zlib stream
    /// (RFC1950: 2-byte header + deflate body + adler32) byte-identical to
    /// native zlib compress2().
    /// </summary>
    public static class ZlibManaged
    {
        // Full zlib stream (RFC1950: 2-byte header + deflate + adler32) at the given
        // level (1 = BestSpeed). Byte-identical to native zlib compress2().
        public static byte[] Deflate(byte[] data, int level)
        {
            if (data == null) data = System.Array.Empty<byte>();
            var codec = new ZlibCodec();
            CompressionLevel cl = (CompressionLevel)level; // enum values map to zlib levels (BestSpeed=1)
            int rc = codec.InitializeDeflate(cl, true);    // true = emit zlib (RFC1950) header
            if (rc != ZlibConstants.Z_OK) throw new System.Exception("deflate init failed: " + codec.Message);
            codec.InputBuffer = data; codec.NextIn = 0; codec.AvailableBytesIn = data.Length;
            var outChunks = new System.Collections.Generic.List<byte[]>();
            byte[] buf = new byte[16384];
            do {
                codec.OutputBuffer = buf; codec.NextOut = 0; codec.AvailableBytesOut = buf.Length;
                rc = codec.Deflate(FlushType.Finish);
                if (rc != ZlibConstants.Z_OK && rc != ZlibConstants.Z_STREAM_END) throw new System.Exception("deflate failed: " + codec.Message);
                int produced = buf.Length - codec.AvailableBytesOut;
                if (produced > 0) { var c = new byte[produced]; System.Array.Copy(buf, 0, c, 0, produced); outChunks.Add(c); }
            } while (rc != ZlibConstants.Z_STREAM_END);
            codec.EndDeflate();
            int total = 0; foreach (var c in outChunks) total += c.Length;
            byte[] result = new byte[total]; int off = 0;
            foreach (var c in outChunks) { System.Array.Copy(c, 0, result, off, c.Length); off += c.Length; }
            return result;
        }
    }
}
