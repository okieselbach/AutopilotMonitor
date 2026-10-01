using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// Serialises the test classes that replace <see cref="UserProfileResolver"/>'s static state.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class UserProfileResolverCollection
    {
        public const string Name = "UserProfileResolver";
    }

    /// <summary>
    /// The agent starts before anyone has signed in. A lookup in that window must not pin
    /// "no user" for the rest of the process — the %LOGGED_ON_USER_PROFILE% gather rules and the
    /// RealmJoin user logs of the diagnostics package would never resolve.
    /// </summary>
    [Collection(UserProfileResolverCollection.Name)]
    public sealed class UserProfileResolverTests : IDisposable
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose() => UserProfileResolver.Reset();

        [Fact]
        public void User_who_signs_in_after_the_first_lookup_is_found_later()
        {
            string? signedIn = null;
            var now = Start;
            UserProfileResolver.SetDetectorForTesting(() => signedIn, () => now);

            Assert.Null(UserProfileResolver.GetLoggedOnUserProfilePath());

            signedIn = @"C:\Users\enduser";
            now += UserProfileResolver.RetryInterval;

            Assert.Equal(@"C:\Users\enduser", UserProfileResolver.GetLoggedOnUserProfilePath());
        }

        [Fact]
        public void No_user_is_detected_again_only_after_the_retry_interval()
        {
            var detections = 0;
            var now = Start;
            UserProfileResolver.SetDetectorForTesting(() => { detections++; return null; }, () => now);

            UserProfileResolver.GetLoggedOnUserProfilePath();
            now += TimeSpan.FromSeconds(10);
            UserProfileResolver.GetLoggedOnUserProfilePath();
            UserProfileResolver.GetLoggedOnUserProfilePath();
            Assert.Equal(1, detections);

            now += UserProfileResolver.RetryInterval;
            UserProfileResolver.GetLoggedOnUserProfilePath();
            Assert.Equal(2, detections);
        }

        [Fact]
        public void A_clock_stepping_backwards_does_not_delay_the_next_detection()
        {
            var detections = 0;
            var now = Start;
            UserProfileResolver.SetDetectorForTesting(() => { detections++; return null; }, () => now);

            UserProfileResolver.GetLoggedOnUserProfilePath();
            now -= TimeSpan.FromHours(1);   // OOBE time sync
            UserProfileResolver.GetLoggedOnUserProfilePath();

            Assert.Equal(2, detections);
        }

        [Fact]
        public void A_found_user_is_kept_without_detecting_again()
        {
            var detections = 0;
            UserProfileResolver.SetDetectorForTesting(() => { detections++; return @"C:\Users\enduser"; }, () => Start);

            UserProfileResolver.GetLoggedOnUserProfilePath();
            UserProfileResolver.GetLoggedOnUserProfilePath();

            Assert.Equal(1, detections);
        }

        [Fact]
        public void Pinned_no_user_stays_null_without_real_detection()
        {
            UserProfileResolver.SetForTesting(null!);

            Assert.Null(UserProfileResolver.GetLoggedOnUserProfilePath());
            Assert.Null(UserProfileResolver.ExpandCustomTokens(@"%LOGGED_ON_USER_PROFILE%\AppData\Local\x.log"));
        }
    }
}
