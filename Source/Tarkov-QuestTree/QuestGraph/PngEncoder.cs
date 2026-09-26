using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// WP4 B1: a PNG writer for the capture's pictures and distance sidecars, Unity-free so it runs on a worker. 8-bit
    /// RGBA (colour type 6) or 8-bit RGB (colour type 2), non-interlaced, one zlib stream (header 78 9C, deflate at
    /// System.IO.Compression.CompressionLevel.Optimal, Adler-32 trailer) cut into IDAT chunks of <see cref="ChunkBytes"/>,
    /// a CRC-32 on every chunk, and IHDR / IDAT / IEND and nothing else - which is what Unity's EncodeToPNG writes for
    /// these files too (tools/check-capture.py --png-info on a floor and a sidecar: no gAMA, sRGB, iCCP or pHYs), so no
    /// ancillary chunk is copied. Rows are supplied TOP FIRST by the caller.
    ///
    /// Its bytes are not Unity's and do not need to be: the pixels are (a PNG decodes by the spec, not by its encoder),
    /// and nothing hashes a floor, side or sidecar PNG. <see cref="AtlasPng"/> is a separate encoder and is not touched -
    /// atlas pages are hashed into the meta.
    ///
    /// <see cref="Encode"/> never throws: any failure is <see cref="Result.Error"/>, so a worker task never faults and
    /// the caller falls back to Unity's encoder.
    /// </summary>
    internal static class PngEncoder
    {
        /// <summary>The largest IDAT chunk, and the size of the parts the file is collected in.</summary>
        internal const int ChunkBytes = 1 << 20;

        /// <summary>The row filter: one of PNG's five for every row, or Adaptive - libpng's heuristic, the filter with
        /// the smallest sum of |(sbyte)byte| over the filtered row, the lowest type on a tie.</summary>
        internal enum Filter : byte
        {
            None = 0,
            Sub = 1,
            Up = 2,
            Average = 3,
            Paeth = 4,
            Adaptive = 255,
        }

        /// <summary>One encode's outcome.</summary>
        internal sealed class Result
        {
            /// <summary>The file, in order: every part <see cref="ChunkBytes"/> long except the last.</summary>
            internal List<byte[]> Parts;

            /// <summary>How many bytes of the last part are the file.</summary>
            internal int LastLength;

            /// <summary>The file's length.</summary>
            internal long Length;

            /// <summary>The encode's wall time on the worker, the round trip included.</summary>
            internal double Milliseconds;

            /// <summary>Why there is no file - never thrown out of the worker. Null on success.</summary>
            internal Exception Error;

            /// <summary>The file was inflated, un-filtered and compared with every row it was made from.</summary>
            internal bool RoundTripChecked;

            /// <summary>The error came from the round trip (the file was made but does not decode to its rows).</summary>
            internal bool RoundTripFailed;
        }

        private static readonly uint[] Crc = MakeCrc();

        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Encodes a picture. Never throws.</summary>
        /// <param name="width">The width, 1 or more.</param>
        /// <param name="height">The height, 1 or more.</param>
        /// <param name="colourType">6 (RGBA) or 2 (RGB).</param>
        /// <param name="filter">The row filter.</param>
        /// <param name="fillRow">Called for PNG row 0..height-1 (top first) with a buffer of width*bpp bytes, which it
        /// fills completely. Called again for every row when <paramref name="roundTrip"/>, so it must be
        /// deterministic.</param>
        /// <param name="roundTrip">Re-read the file (every chunk's CRC, the zlib header, the inflate, the Adler-32, the
        /// un-filter) and compare every row with <paramref name="fillRow"/>'s; a difference is an Error.</param>
        internal static Result Encode(int width, int height, byte colourType, Filter filter, Action<int, byte[]> fillRow,
            bool roundTrip)
        {
            var clock = Stopwatch.StartNew();
            var result = new Result();

            try
            {
                if (width <= 0 || height <= 0) throw new ArgumentException($"a {width}x{height} picture");
                if (colourType != 6 && colourType != 2) throw new ArgumentException($"colour type {colourType}");
                if (fillRow == null) throw new ArgumentNullException(nameof(fillRow));
                if (filter > Filter.Paeth && filter != Filter.Adaptive) throw new ArgumentException($"filter {filter}");

                var bpp = colourType == 6 ? 4 : 3;
                var stride = (long)width * bpp;
                if (stride + 1 > int.MaxValue / 2) throw new ArgumentException($"a row of {stride} bytes");

                var sink = new ChunkedSink();
                sink.Write(Signature, 0, Signature.Length);

                var header = new byte[13];
                BigEndian(header, 0, (uint)width);
                BigEndian(header, 4, (uint)height);
                header[8] = 8;              // bit depth
                header[9] = colourType;
                header[10] = 0;             // compression: deflate
                header[11] = 0;             // filter method 0
                header[12] = 0;             // no interlace
                Chunk(sink, "IHDR", header, header.Length);

                var idat = new Idat(sink);
                idat.Write(new byte[] { 0x78, 0x9C }, 0, 2);    // zlib: deflate, 32K window, default level; 0x789C % 31 == 0

                var adler = new Adler();

                using (var deflate = new System.IO.Compression.DeflateStream(idat, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    var rows = new RowFilter((int)stride, bpp);

                    for (var row = 0; row < height; row++)
                    {
                        fillRow(row, rows.Current);
                        var filtered = rows.Apply(filter);
                        adler.Add(filtered, 0, filtered.Length);
                        deflate.Write(filtered, 0, filtered.Length);
                        rows.Advance();
                    }
                }

                var trailer = new byte[4];
                BigEndian(trailer, 0, adler.Value);
                idat.Write(trailer, 0, 4);
                idat.Flush();

                Chunk(sink, "IEND", new byte[0], 0);

                result.Parts = sink.Parts;
                result.LastLength = sink.LastLength;
                result.Length = sink.Length;

                if (roundTrip)
                {
                    var why = RoundTrip(result, width, height, colourType, fillRow);

                    if (why != null)
                    {
                        result.RoundTripFailed = true;
                        throw new InvalidOperationException("the round trip failed: " + why);
                    }

                    result.RoundTripChecked = true;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex;
                result.Parts = null;
                result.LastLength = 0;
                result.Length = 0;
            }

            result.Milliseconds = clock.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>The whole file as one array - the harness's and a fallback's convenience; the capture writes the
        /// parts.</summary>
        /// <param name="result">A successful result.</param>
        internal static byte[] ToArray(Result result)
        {
            var bytes = new byte[result.Length];
            var at = 0;

            for (var i = 0; i < result.Parts.Count; i++)
            {
                var n = i == result.Parts.Count - 1 ? result.LastLength : result.Parts[i].Length;
                Buffer.BlockCopy(result.Parts[i], 0, bytes, at, n);
                at += n;
            }

            return bytes;
        }

        /// <summary>Reads the encoded file back: the signature, every chunk's CRC, IHDR's fields, the IDAT stream's zlib
        /// header, the inflate to exactly height rows and nothing after, the Adler-32 trailer, the un-filter, and every
        /// row against <paramref name="fillRow"/>'s. Null when it all holds, else what did not.</summary>
        private static string RoundTrip(Result result, int width, int height, byte colourType, Action<int, byte[]> fillRow)
        {
            var bpp = colourType == 6 ? 4 : 3;
            var stride = width * bpp;
            var file = new PartsReader(result.Parts, result.LastLength);

            var head = new byte[8];
            if (!ReadFull(file, head, 8)) return "the file is shorter than its signature";
            for (var i = 0; i < 8; i++)
                if (head[i] != Signature[i]) return "the signature is wrong";

            var chunks = new ChunkReader(file);
            var ihdr = chunks.Next();
            if (chunks.Error != null) return chunks.Error;
            if (ihdr == null || chunks.Type != "IHDR" || ihdr.Length != 13) return "the first chunk is not a 13-byte IHDR";
            if (ReadBigEndian(ihdr, 0) != (uint)width || ReadBigEndian(ihdr, 4) != (uint)height || ihdr[8] != 8 ||
                ihdr[9] != colourType || ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] != 0)
                return "IHDR does not say what was encoded";

            var zlib = new IdatStream(chunks);
            var zhead = new byte[2];
            if (!ReadFull(zlib, zhead, 2)) return "the zlib stream has no header";
            if (zhead[0] != 0x78 || zhead[1] != 0x9C) return "the zlib header is not 78 9C";

            var raw = new byte[stride + 1];
            var prior = new byte[stride];
            var current = new byte[stride];
            var expected = new byte[stride];
            var adler = new Adler();

            // The trailer is read by hand after the inflate: the deflate stream is handed everything but it (the
            // IdatStream holds back the last four bytes of the IDAT data), so an inflate that ends early or late is seen.
            using (var inflate = new System.IO.Compression.DeflateStream(zlib.WithoutTrailer(), System.IO.Compression.CompressionMode.Decompress))
            {
                for (var y = 0; y < height; y++)
                {
                    if (!ReadFull(inflate, raw, raw.Length)) return $"the data ends at row {y} of {height}";

                    adler.Add(raw, 0, raw.Length);
                    if (!Unfilter(raw[0], raw, current, prior, stride, bpp)) return $"row {y} has filter type {raw[0]}";

                    fillRow(y, expected);

                    for (var i = 0; i < stride; i++)
                        if (current[i] != expected[i]) return $"row {y} byte {i} decodes to {current[i]}, not {expected[i]}";

                    var swap = prior;
                    prior = current;
                    current = swap;
                }

                if (inflate.ReadByte() >= 0) return "the data runs past the last row";
            }

            if (chunks.Error != null) return chunks.Error;
            if (zlib.Trailer == null) return "the zlib stream has no Adler-32 trailer";
            if (ReadBigEndian(zlib.Trailer, 0) != adler.Value) return "the Adler-32 does not match the data";
            if (zlib.Leftover > 0) return $"{zlib.Leftover} byte(s) of the IDAT data follow the deflate stream";

            // Everything after the IDATs: IEND, and nothing after IEND.
            if (!chunks.Ended) return "no IEND";
            if (chunks.Error != null) return chunks.Error;
            if (file.ReadByte() >= 0) return "bytes after IEND";

            return null;
        }

        // --- the writer's parts -------------------------------------------------------------------------------------

        /// <summary>Adler-32 kept modulo 65521 with the NMAX deferral: 5552 bytes between reductions is the most that
        /// cannot overflow 32 bits.</summary>
        private sealed class Adler
        {
            private uint _a = 1;
            private uint _b;
            private int _pending;

            internal uint Value => ((_b % 65521) << 16) | (_a % 65521);

            internal void Add(byte[] data, int offset, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    _a += data[offset + i];
                    _b += _a;

                    if (++_pending == 5552)
                    {
                        _a %= 65521;
                        _b %= 65521;
                        _pending = 0;
                    }
                }
            }
        }

        /// <summary>The raw rows and the filtered row: <see cref="Current"/> is filled by the caller, <see cref="Apply"/>
        /// filters it against the previous raw row (zeroes above the first), <see cref="Advance"/> makes it the previous
        /// one.</summary>
        private sealed class RowFilter
        {
            private readonly int _stride;
            private readonly int _bpp;
            private byte[] _prior;
            private readonly byte[][] _candidates = new byte[5][];

            internal byte[] Current;

            internal RowFilter(int stride, int bpp)
            {
                _stride = stride;
                _bpp = bpp;
                Current = new byte[stride];
                _prior = new byte[stride];
                for (var i = 0; i < _candidates.Length; i++) _candidates[i] = new byte[stride + 1];
            }

            internal void Advance()
            {
                var swap = _prior;
                _prior = Current;
                Current = swap;
            }

            internal byte[] Apply(Filter filter)
            {
                if (filter != Filter.Adaptive)
                {
                    var into = _candidates[(int)filter];
                    Write((int)filter, into);
                    return into;
                }

                var best = 0;
                var bestSum = long.MaxValue;

                for (var type = 0; type < 5; type++)
                {
                    var into = _candidates[type];
                    Write(type, into);

                    long sum = 0;

                    for (var i = 1; i < into.Length && sum < bestSum; i++)
                    {
                        var v = into[i];
                        sum += v < 128 ? v : 256 - v;
                    }

                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        best = type;
                    }
                }

                return _candidates[best];
            }

            private void Write(int type, byte[] into)
            {
                var raw = Current;
                var up = _prior;
                var bpp = _bpp;
                into[0] = (byte)type;

                switch (type)
                {
                    case 0:
                        Buffer.BlockCopy(raw, 0, into, 1, _stride);
                        break;
                    case 1:
                        for (var i = 0; i < _stride; i++) into[i + 1] = (byte)(raw[i] - (i >= bpp ? raw[i - bpp] : 0));
                        break;
                    case 2:
                        for (var i = 0; i < _stride; i++) into[i + 1] = (byte)(raw[i] - up[i]);
                        break;
                    case 3:
                        for (var i = 0; i < _stride; i++)
                            into[i + 1] = (byte)(raw[i] - (((i >= bpp ? raw[i - bpp] : 0) + up[i]) >> 1));
                        break;
                    default:
                        for (var i = 0; i < _stride; i++)
                        {
                            int left = i >= bpp ? raw[i - bpp] : 0, above = up[i], corner = i >= bpp ? up[i - bpp] : 0;
                            into[i + 1] = (byte)(raw[i] - Paeth(left, above, corner));
                        }

                        break;
                }
            }
        }

        private static int Paeth(int left, int above, int corner)
        {
            var p = left + above - corner;
            int pa = Math.Abs(p - left), pb = Math.Abs(p - above), pc = Math.Abs(p - corner);
            return pa <= pb && pa <= pc ? left : pb <= pc ? above : corner;
        }

        /// <summary>One row un-filtered (PNG's five filters). False for an unknown filter type.</summary>
        private static bool Unfilter(byte filter, byte[] row, byte[] current, byte[] prior, int stride, int bpp)
        {
            switch (filter)
            {
                case 0:
                    Buffer.BlockCopy(row, 1, current, 0, stride);
                    return true;
                case 1:
                    for (var i = 0; i < stride; i++) current[i] = (byte)(row[i + 1] + (i >= bpp ? current[i - bpp] : 0));
                    return true;
                case 2:
                    for (var i = 0; i < stride; i++) current[i] = (byte)(row[i + 1] + prior[i]);
                    return true;
                case 3:
                    for (var i = 0; i < stride; i++)
                        current[i] = (byte)(row[i + 1] + (((i >= bpp ? current[i - bpp] : 0) + prior[i]) >> 1));
                    return true;
                case 4:
                    for (var i = 0; i < stride; i++)
                        current[i] = (byte)(row[i + 1] +
                                            Paeth(i >= bpp ? current[i - bpp] : 0, prior[i], i >= bpp ? prior[i - bpp] : 0));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The file, collected in ChunkBytes parts - no MemoryStream doubling, so the output costs at most the
        /// file plus one part.</summary>
        private sealed class ChunkedSink
        {
            internal readonly List<byte[]> Parts = new List<byte[]>();
            internal int LastLength;
            internal long Length;

            internal void Write(byte[] data, int offset, int count)
            {
                while (count > 0)
                {
                    if (Parts.Count == 0 || LastLength == ChunkBytes)
                    {
                        Parts.Add(new byte[ChunkBytes]);
                        LastLength = 0;
                    }

                    var part = Parts[Parts.Count - 1];
                    var take = Math.Min(count, ChunkBytes - LastLength);
                    Buffer.BlockCopy(data, offset, part, LastLength, take);
                    LastLength += take;
                    Length += take;
                    offset += take;
                    count -= take;
                }
            }
        }

        /// <summary>The zlib stream's sink: bytes collect in one ChunkBytes buffer and go out as an IDAT chunk each
        /// time it fills, and once more on Flush.</summary>
        private sealed class Idat : System.IO.Stream
        {
            private readonly ChunkedSink _sink;
            private readonly byte[] _buffer = new byte[ChunkBytes];
            private int _count;

            internal Idat(ChunkedSink sink)
            {
                _sink = sink;
            }

            public override void Write(byte[] data, int offset, int count)
            {
                while (count > 0)
                {
                    var take = Math.Min(count, _buffer.Length - _count);
                    Buffer.BlockCopy(data, offset, _buffer, _count, take);
                    _count += take;
                    offset += take;
                    count -= take;

                    if (_count == _buffer.Length) Flush();
                }
            }

            public override void Flush()
            {
                if (_count == 0) return;
                Chunk(_sink, "IDAT", _buffer, _count);
                _count = 0;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static void Chunk(ChunkedSink sink, string type, byte[] data, int length)
        {
            var head = new byte[8];
            BigEndian(head, 0, (uint)length);
            for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];

            var crc = 0xFFFFFFFFu;
            for (var i = 4; i < 8; i++) crc = Crc[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
            for (var i = 0; i < length; i++) crc = Crc[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

            var tail = new byte[4];
            BigEndian(tail, 0, crc ^ 0xFFFFFFFFu);

            sink.Write(head, 0, 8);
            sink.Write(data, 0, length);
            sink.Write(tail, 0, 4);
        }

        // --- the round trip's readers -------------------------------------------------------------------------------

        /// <summary>The parts as one forward-only stream.</summary>
        private sealed class PartsReader : System.IO.Stream
        {
            private readonly List<byte[]> _parts;
            private readonly int _last;
            private int _part;
            private int _at;

            internal PartsReader(List<byte[]> parts, int last)
            {
                _parts = parts;
                _last = last;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (_part < _parts.Count)
                {
                    var end = _part == _parts.Count - 1 ? _last : _parts[_part].Length;

                    if (_at < end)
                    {
                        var take = Math.Min(count, end - _at);
                        Buffer.BlockCopy(_parts[_part], _at, buffer, offset, take);
                        _at += take;
                        return take;
                    }

                    _part++;
                    _at = 0;
                }

                return 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>Walks the chunks after the signature, checking each CRC. Next returns a chunk's data; IDAT chunks
        /// are streamed instead (<see cref="IdatStream"/> reads them through <see cref="NextIdat"/>).</summary>
        private sealed class ChunkReader
        {
            private readonly System.IO.Stream _file;
            internal string Type;
            internal string Error;
            internal bool Ended;

            internal ChunkReader(System.IO.Stream file)
            {
                _file = file;
            }

            /// <summary>The next chunk's data (its type in Type), CRC checked; null at the end or on an error.</summary>
            internal byte[] Next()
            {
                var head = new byte[8];
                if (!ReadFull(_file, head, 8))
                {
                    Error = Error ?? "the file ends inside a chunk header";
                    return null;
                }

                var length = ReadBigEndian(head, 0);
                if (length > ChunkBytes * 2u)
                {
                    Error = Error ?? $"a chunk of {length} bytes";
                    return null;
                }

                Type = new string(new[] { (char)head[4], (char)head[5], (char)head[6], (char)head[7] });
                var body = new byte[length];
                var tail = new byte[4];

                if (!ReadFull(_file, body, (int)length) || !ReadFull(_file, tail, 4))
                {
                    Error = Error ?? $"the file ends inside {Type}";
                    return null;
                }

                var crc = 0xFFFFFFFFu;
                for (var i = 4; i < 8; i++) crc = Crc[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
                for (var i = 0; i < body.Length; i++) crc = Crc[(crc ^ body[i]) & 0xFF] ^ (crc >> 8);

                if ((crc ^ 0xFFFFFFFFu) != ReadBigEndian(tail, 0))
                {
                    Error = Error ?? $"{Type}'s CRC is wrong";
                    return null;
                }

                if (Type == "IEND") Ended = true;
                return body;
            }

            /// <summary>The next IDAT's data, or null once the IDATs are over - the chunk after them must be IEND.</summary>
            internal byte[] NextIdat()
            {
                if (Ended || Error != null) return null;

                var data = Next();
                if (data == null) return null;
                if (Type == "IDAT") return data;

                if (Type != "IEND") Error = Error ?? $"a {Type} chunk after the image data";
                return null;
            }
        }

        /// <summary>The concatenated IDAT data as a stream, holding back its last four bytes (the Adler-32 trailer) -
        /// <see cref="WithoutTrailer"/> is what the inflate reads; <see cref="Trailer"/> is filled when it runs out.</summary>
        private sealed class IdatStream : System.IO.Stream
        {
            private readonly ChunkReader _chunks;
            private byte[] _data = new byte[0];
            private int _at;
            private bool _done;

            internal byte[] Trailer;

            /// <summary>Bytes the deflate stream did not consume before the trailer.</summary>
            internal long Leftover;

            internal IdatStream(ChunkReader chunks)
            {
                _chunks = chunks;
            }

            /// <summary>This stream minus its last four bytes.</summary>
            internal System.IO.Stream WithoutTrailer() => new HoldBack(this);

            /// <summary>The raw bytes, trailer included.</summary>
            public override int Read(byte[] buffer, int offset, int count)
            {
                while (_at >= _data.Length)
                {
                    if (_done) return 0;

                    var next = _chunks.NextIdat();

                    if (next == null)
                    {
                        _done = true;
                        return 0;
                    }

                    _data = next;
                    _at = 0;
                }

                var take = Math.Min(count, _data.Length - _at);
                Buffer.BlockCopy(_data, _at, buffer, offset, take);
                _at += take;
                return take;
            }

            /// <summary>Hands the inflate every byte but the last four, which it keeps as the trailer.</summary>
            private sealed class HoldBack : System.IO.Stream
            {
                private readonly IdatStream _source;
                private readonly byte[] _window = new byte[4];
                private int _held;
                private bool _ended;
                private byte[] _read = new byte[0];

                internal HoldBack(IdatStream source)
                {
                    _source = source;
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    if (_ended || count <= 0) return 0;

                    while (_held < 4)
                    {
                        var got = _source.Read(_window, _held, 4 - _held);

                        if (got == 0)
                        {
                            _ended = true;
                            return 0;
                        }

                        _held += got;
                    }

                    if (_read.Length < count) _read = new byte[count];

                    var n = _source.Read(_read, 0, count);

                    if (n == 0)
                    {
                        _ended = true;
                        _source.Trailer = (byte[])_window.Clone();
                        return 0;
                    }

                    // Out: the held four and the first n-4 new ones; held: the last four of (held + new).
                    if (n >= 4)
                    {
                        Buffer.BlockCopy(_window, 0, buffer, offset, 4);
                        Buffer.BlockCopy(_read, 0, buffer, offset + 4, n - 4);
                        Buffer.BlockCopy(_read, n - 4, _window, 0, 4);
                    }
                    else
                    {
                        var joined = new byte[4 + n];
                        Buffer.BlockCopy(_window, 0, joined, 0, 4);
                        Buffer.BlockCopy(_read, 0, joined, 4, n);
                        Buffer.BlockCopy(joined, 0, buffer, offset, n);
                        Buffer.BlockCopy(joined, n, _window, 0, 4);
                    }

                    return n;
                }

                protected override void Dispose(bool disposing)
                {
                    // The inflate is closed: whatever it did not read before the trailer is counted, and the trailer
                    // is taken from what is left.
                    if (disposing && !_ended)
                    {
                        var rest = new byte[4096];
                        int n;
                        while ((n = Read(rest, 0, rest.Length)) > 0) _source.Leftover += n;
                    }

                    base.Dispose(disposing);
                }

                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => throw new NotSupportedException();

                public override long Position
                {
                    get => throw new NotSupportedException();
                    set => throw new NotSupportedException();
                }

                public override void Flush()
                {
                }

                public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static bool ReadFull(System.IO.Stream stream, byte[] buffer, int count)
        {
            var done = 0;

            while (done < count)
            {
                var n = stream.Read(buffer, done, count - done);
                if (n <= 0) return false;
                done += n;
            }

            return true;
        }

        private static uint ReadBigEndian(byte[] buffer, int at) =>
            ((uint)buffer[at] << 24) | ((uint)buffer[at + 1] << 16) | ((uint)buffer[at + 2] << 8) | buffer[at + 3];

        private static void BigEndian(byte[] buffer, int at, uint value)
        {
            buffer[at] = (byte)(value >> 24);
            buffer[at + 1] = (byte)(value >> 16);
            buffer[at + 2] = (byte)(value >> 8);
            buffer[at + 3] = (byte)value;
        }

        private static uint[] MakeCrc()
        {
            var table = new uint[256];

            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }
    }
}
