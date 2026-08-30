using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    public sealed class BoundedLineReaderTests
    {
        private static BoundedLineReader Over(byte[] bytes, int cap = 1024, long start = 0)
        {
            var ms = new MemoryStream(bytes);
            ms.Position = start;
            return new BoundedLineReader(ms, cap);
        }

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public async Task Splits_on_LF_and_strips_one_CR_with_exact_positions()
        {
            var r = Over(B("ab\r\ncd\n\r\nef"));

            Assert.Equal("ab", await r.ReadLineAsync(CancellationToken.None));
            Assert.Equal(0, r.LastLineStart);
            Assert.Equal(4, r.Position);
            Assert.True(r.LastLineTerminated);

            Assert.Equal("cd", await r.ReadLineAsync(CancellationToken.None));
            Assert.Equal(4, r.LastLineStart);
            Assert.Equal(7, r.Position);

            Assert.Equal("", await r.ReadLineAsync(CancellationToken.None));
            Assert.Equal(7, r.LastLineStart);
            Assert.Equal(9, r.Position);

            Assert.Equal("ef", await r.ReadLineAsync(CancellationToken.None));
            Assert.Equal(9, r.LastLineStart);
            Assert.Equal(11, r.Position);
            Assert.False(r.LastLineTerminated);

            Assert.Null(await r.ReadLineAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Bom_is_skipped_only_at_file_offset_zero()
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var bytes = new byte[bom.Length * 2 + 4];
            bom.CopyTo(bytes, 0);
            B("a\n").CopyTo(bytes, 3);
            bom.CopyTo(bytes, 5);
            B("b\n").CopyTo(bytes, 8);

            var r = Over(bytes);
            Assert.Equal("a", await r.ReadLineAsync(CancellationToken.None));
            Assert.Equal("﻿b", await r.ReadLineAsync(CancellationToken.None));

            // Starting mid-file (a later pass) never treats the first bytes as a BOM.
            var r2 = Over(bytes, start: 5);
            Assert.Equal("﻿b", await r2.ReadLineAsync(CancellationToken.None));
            Assert.Equal(5, r2.LastLineStart);
        }

        [Fact]
        public async Task Multibyte_utf8_across_the_read_buffer_boundary_decodes_intact()
        {
            // 64 KB read buffer: put a 3-byte char straddling the boundary.
            var sb = new StringBuilder();
            while (Encoding.UTF8.GetByteCount(sb.ToString()) < 64 * 1024 - 1) sb.Append('x');
            var text = sb + "€tail\n";
            var r = Over(B(text), cap: 1024 * 1024);
            Assert.Equal(text.TrimEnd('\n'), await r.ReadLineAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Line_over_the_cap_is_truncated_to_the_cap_and_the_rest_discarded()
        {
            var big = new string('z', 5000);
            var r = Over(B("<![LOG[" + big + "\nnext\n"), cap: 100);

            var first = await r.ReadLineAsync(CancellationToken.None);
            Assert.True(r.LastLineTruncated);
            Assert.True(r.LastLineTerminated);
            Assert.Equal(100, first.Length);
            Assert.StartsWith("<![LOG[", first);
            Assert.Equal(0, r.LastLineStart);
            Assert.Equal(7 + 5000 + 1, r.Position);

            Assert.Equal("next", await r.ReadLineAsync(CancellationToken.None));
            Assert.False(r.LastLineTruncated);
        }

        [Fact]
        public async Task Unterminated_tail_over_the_cap_is_reported_as_truncated_and_unterminated()
        {
            var r = Over(B(new string('q', 300)), cap: 50);
            var line = await r.ReadLineAsync(CancellationToken.None);
            Assert.Equal(50, line.Length);
            Assert.True(r.LastLineTruncated);
            Assert.False(r.LastLineTerminated);
            Assert.Equal(300, r.Position);
            Assert.Null(await r.ReadLineAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Empty_stream_returns_null_immediately()
        {
            var r = Over(new byte[0]);
            Assert.Null(await r.ReadLineAsync(CancellationToken.None));
            Assert.Equal(0, r.Position);
        }
    }
}
