using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// <see cref="LogLineReader"/>, <see cref="LogTextEncoding"/> and <see cref="LogFileHead"/>:
    /// exact byte positions, StreamReader-compatible line ends, byte order marks only at offset 0,
    /// and the code-unit view of UTF-16/UTF-32 files.
    /// </summary>
    public sealed class LogLineReaderTests
    {
        private static byte[] Bytes(Encoding encoding, string text, bool withBom = true)
        {
            var body = encoding.GetBytes(text);
            return withBom ? encoding.GetPreamble().Concat(body).ToArray() : body;
        }

        private static (List<string> Lines, List<long> EndPositions, List<bool> Terminated) ReadAll(
            byte[] file, long start = 0, int maxLineBytes = 1024)
        {
            var encoding = LogFileHead.FromBytes(file.Take(LogFileHead.MaxBytes).ToArray()).Encoding;
            using (var stream = new MemoryStream(file))
            {
                stream.Seek(start, SeekOrigin.Begin);
                var reader = new LogLineReader(stream, encoding, maxLineBytes);
                var lines = new List<string>();
                var ends = new List<long>();
                var terminated = new List<bool>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                    ends.Add(reader.Position);
                    terminated.Add(reader.LastLineTerminated);
                }
                return (lines, ends, terminated);
            }
        }

        [Fact]
        public void Line_ends_match_StreamReader_including_a_lone_carriage_return()
        {
            var file = Encoding.UTF8.GetBytes("one\ntwo\r\nthree\rfour");

            var result = ReadAll(file);

            Assert.Equal(new[] { "one", "two", "three", "four" }, result.Lines);
            Assert.Equal(new long[] { 4, 9, 15, 19 }, result.EndPositions);
            Assert.Equal(new[] { true, true, true, false }, result.Terminated);
        }

        [Fact]
        public void Position_after_each_line_is_the_offset_to_resume_from()
        {
            var file = Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n");

            var first = ReadAll(file);
            var resumed = ReadAll(file, start: first.EndPositions[0]);

            Assert.Equal(new[] { "beta", "gamma" }, resumed.Lines);
            Assert.Equal(file.Length, resumed.EndPositions.Last());
        }

        [Fact]
        public void Utf8_byte_order_mark_is_skipped_only_at_offset_0()
        {
            var file = Bytes(Encoding.UTF8, "first\nsecond\n");

            var result = ReadAll(file);

            Assert.Equal(new[] { "first", "second" }, result.Lines);
            Assert.Equal(LogTextEncoding.Utf8WithBom, LogFileHead.FromBytes(file).Encoding);
        }

        [Theory]
        [InlineData("utf-16le")]
        [InlineData("utf-16be")]
        [InlineData("utf-32le")]
        [InlineData("utf-32be")]
        public void Unicode_files_are_read_in_their_code_units_from_any_resume_point(string name)
        {
            var encoding = name == "utf-16le" ? Encoding.Unicode
                : name == "utf-16be" ? Encoding.BigEndianUnicode
                : name == "utf-32le" ? Encoding.UTF32
                : new UTF32Encoding(bigEndian: true, byteOrderMark: true);
            var file = Bytes(encoding, "ERROR first\r\nERROR zweite Zeile mit Umlaut äöü\r\n");

            var whole = ReadAll(file);
            var resumed = ReadAll(file, start: whole.EndPositions[0]);

            Assert.Equal(name, LogFileHead.FromBytes(file).Encoding.Name);
            Assert.Equal(new[] { "ERROR first", "ERROR zweite Zeile mit Umlaut äöü" }, whole.Lines);
            Assert.Equal(new[] { "ERROR zweite Zeile mit Umlaut äöü" }, resumed.Lines);
            Assert.Equal(file.Length, whole.EndPositions.Last());
        }

        [Fact]
        public void File_without_byte_order_mark_is_utf8_and_ansi_bytes_decode_as_before()
        {
            var file = new byte[] { (byte)'M', (byte)'a', (byte)'n', 0xFC, (byte)'\n' }; // "Man" + Windows-1252 'ü'

            var result = ReadAll(file);

            Assert.Equal(LogTextEncoding.Utf8, LogFileHead.FromBytes(file).Encoding);
            Assert.Equal(new StreamReader(new MemoryStream(file), Encoding.UTF8).ReadLine(), result.Lines.Single());
        }

        [Fact]
        public void Carriage_return_at_end_of_file_ends_the_line_and_its_late_line_feed_is_skipped()
        {
            var firstWrite = Encoding.UTF8.GetBytes("done\r");
            var later = Encoding.UTF8.GetBytes("done\r\nnext\n");

            var first = ReadAll(firstWrite);
            var resumed = ReadAll(later, start: first.EndPositions.Last());

            Assert.Equal(new[] { "done" }, first.Lines);
            Assert.True(first.Terminated.Single());
            Assert.Equal(new[] { "next" }, resumed.Lines);
        }

        [Fact]
        public void Overlong_line_is_consumed_without_being_kept_and_reading_continues_behind_it()
        {
            var file = Encoding.UTF8.GetBytes(new string('x', 50) + "\nshort\n");
            using (var stream = new MemoryStream(file))
            {
                var reader = new LogLineReader(stream, LogTextEncoding.Utf8, maxLineBytes: 10);

                Assert.Equal(string.Empty, reader.ReadLine());
                Assert.True(reader.LastLineTruncated);
                Assert.Equal(51, reader.Position);
                Assert.Equal("short", reader.ReadLine());
                Assert.False(reader.LastLineTruncated);
                Assert.Null(reader.ReadLine());
            }
        }

        [Fact]
        public void Line_spanning_many_read_buffers_is_returned_whole()
        {
            var longLine = new string('y', 200_000);
            var file = Encoding.UTF8.GetBytes(longLine + "\nend\n");

            var result = ReadAll(file, maxLineBytes: 1_000_000);

            Assert.Equal(new[] { longLine, "end" }, result.Lines);
        }

        [Fact]
        public void Half_written_utf16_character_at_end_of_file_stays_in_the_unterminated_line()
        {
            var full = Bytes(Encoding.Unicode, "ok\r\nhalf");
            var file = full.Concat(new byte[] { 0x41 }).ToArray(); // first byte of the next UTF-16 unit

            var result = ReadAll(file);

            Assert.Equal("ok", result.Lines[0]);
            Assert.False(result.Terminated.Last());
            Assert.Equal(file.Length, result.EndPositions.Last());
        }

        [Fact]
        public void AlignDown_snaps_to_code_units_behind_the_byte_order_mark()
        {
            Assert.Equal(0, LogTextEncoding.Utf16LittleEndian.AlignDown(1));
            Assert.Equal(2, LogTextEncoding.Utf16LittleEndian.AlignDown(3));
            Assert.Equal(6, LogTextEncoding.Utf16LittleEndian.AlignDown(7));
            Assert.Equal(8, LogTextEncoding.Utf32LittleEndian.AlignDown(11));
            Assert.Equal(7, LogTextEncoding.Utf8.AlignDown(7));
        }

        [Fact]
        public void Head_hash_of_a_prefix_matches_the_hash_of_the_shorter_file()
        {
            var shortFile = LogFileHead.FromBytes(Encoding.UTF8.GetBytes("2026-09-28 start"));
            var grown = LogFileHead.FromBytes(Encoding.UTF8.GetBytes("2026-09-28 start\nmore lines"));

            Assert.Equal(shortFile.Hash(shortFile.Length), grown.Hash(shortFile.Length));
            Assert.NotEqual(shortFile.Hash(shortFile.Length), grown.Hash(grown.Length));
        }
    }
}
