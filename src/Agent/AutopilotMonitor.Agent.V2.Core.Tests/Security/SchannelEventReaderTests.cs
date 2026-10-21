using System;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    public sealed class SchannelEventReaderTests
    {
        [Fact]
        public void Token_is_event_id_with_numeric_properties()
        {
            Assert.Equal("36888:40/1205", SchannelEventReader.FormatToken(36888, new[] { "40", "1205" }));
        }

        [Fact]
        public void Token_drops_text_properties()
        {
            // Newer builds add target name and certificate subjects; they stay on the device.
            Assert.Equal("36887:116", SchannelEventReader.FormatToken(
                36887, new[] { "116", "autopilotmonitor-api.example.test", "CN=device", "" }));
        }

        [Fact]
        public void Token_without_numbers_is_the_event_id()
        {
            Assert.Equal("36871", SchannelEventReader.FormatToken(36871, new[] { "TLS 1.2" }));
            Assert.Equal("36871", SchannelEventReader.FormatToken(36871, null!));
        }

        [Fact]
        public void Token_keeps_at_most_three_numbers()
        {
            Assert.Equal("36888:1/2/3", SchannelEventReader.FormatToken(36888, new[] { "1", "2", "3", "4" }));
        }

        [Fact]
        public void Token_rejects_non_ascii_digits()
        {
            Assert.Equal("36888", SchannelEventReader.FormatToken(36888, new[] { "٤٠" }));
        }

        [Fact]
        public void Query_is_accepted_by_the_event_log_service()
        {
            // A malformed XPath would only surface as "no events" through ReadSince (it never
            // throws), so the query itself is run here.
            using (var reader = new EventLogReader(new EventLogQuery(
                "System", PathType.LogName, SchannelEventReader.BuildXPath(DateTime.UtcNow.AddDays(-1))) { ReverseDirection = true }))
            {
                using (reader.ReadEvent()) { }
            }
        }

        [Fact]
        public void No_events_after_now()
        {
            Assert.Equal(SchannelEventReader.NoEvents, SchannelEventReader.ReadSince(DateTime.UtcNow.AddHours(1)));
        }
    }
}
