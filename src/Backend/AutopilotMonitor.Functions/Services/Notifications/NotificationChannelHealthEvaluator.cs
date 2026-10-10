using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>Why a channel was sent to: only a real delivery may raise the "channel failing" bell.</summary>
    public enum ChannelSendKind
    {
        Delivery,
        Test,
    }

    /// <summary>Status change caused by folding one send outcome into a health row.</summary>
    public enum ChannelHealthTransition
    {
        None,
        BecameFailing,
        Recovered,
    }

    /// <summary>A health row after one send, plus what the recorder has to do about it.</summary>
    public sealed class ChannelHealthFold
    {
        public ChannelHealthFold(NotificationChannelHealth next, ChannelHealthTransition transition, bool raiseBell)
        {
            Next = next;
            Transition = transition;
            RaiseBell = raiseBell;
        }

        public NotificationChannelHealth Next { get; }

        public ChannelHealthTransition Transition { get; }

        /// <summary>True exactly once per failing episode: the write that stamped <see cref="NotificationChannelHealth.FailingNotifiedUtc"/>.</summary>
        public bool RaiseBell { get; }
    }

    /// <summary>
    /// The one rule for a notification channel's delivery status, and the one fold of a send
    /// outcome into its <see cref="NotificationChannelHealth"/> row. Pure — the health endpoints
    /// and the recorder share it, so the dot in the portal and the bell can never disagree.
    /// </summary>
    public static class NotificationChannelHealthEvaluator
    {
        /// <summary>
        /// Failed sends in a row that make a channel failing. Each send already went through the
        /// transport's retries, so two in a row is a destination that does not accept anything.
        /// </summary>
        public const int FailingThreshold = 2;

        /// <summary>How long one failure keeps an otherwise delivering channel degraded (while it is among the kept outcomes).</summary>
        public static readonly TimeSpan DegradedWindow = TimeSpan.FromDays(7);

        public const int MaxErrorLength = 200;

        /// <summary>
        /// Hash of everything that decides where and how a send lands. Never the destination itself:
        /// the row may be read by people who cannot see the channel's secrets.
        /// </summary>
        public static string Fingerprint(NotificationChannel channel)
        {
            var material = string.Join("\n",
                channel.ProviderType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                channel.Url ?? string.Empty,
                channel.CustomHeadersJson ?? string.Empty,
                channel.SigningSecret ?? string.Empty);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            return string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
        }

        /// <summary>The row when it describes the channel's current destination, otherwise null.</summary>
        public static NotificationChannelHealth? Current(NotificationChannelHealth? row, string fingerprint)
            => row != null && string.Equals(row.Fingerprint, fingerprint, StringComparison.Ordinal) ? row : null;

        public static NotificationChannelHealthStatus Evaluate(NotificationChannelHealth? current, DateTime nowUtc)
        {
            if (current?.LastAttemptUtc == null)
                return NotificationChannelHealthStatus.Unknown;
            if (current.ConsecutiveFailures >= FailingThreshold)
                return NotificationChannelHealthStatus.Failing;
            // Degraded needs a failure that is both recent and still among the kept outcomes, so
            // the failure rate shown next to it is never zero.
            if (current.Recent.IndexOf('0') >= 0
                && current.LastFailureUtc.HasValue && nowUtc - current.LastFailureUtc.Value < DegradedWindow)
                return NotificationChannelHealthStatus.Degraded;
            return NotificationChannelHealthStatus.Ok;
        }

        public static NotificationChannelHealthDto ToDto(string channelId, NotificationChannelHealth? current, DateTime nowUtc)
        {
            var dto = new NotificationChannelHealthDto
            {
                ChannelId = channelId,
                Status = Evaluate(current, nowUtc),
            };
            if (current == null)
                return dto;

            dto.RecentAttempts = current.Recent.Length;
            dto.RecentFailures = current.Recent.Count(c => c == '0');
            dto.ConsecutiveFailures = current.ConsecutiveFailures;
            dto.LastAttemptUtc = current.LastAttemptUtc;
            dto.LastSuccessUtc = current.LastSuccessUtc;
            dto.LastFailureUtc = current.LastFailureUtc;
            dto.FailingSinceUtc = current.FailingSinceUtc;
            dto.LastStatusCode = current.LastStatusCode;
            dto.LastError = current.LastError;
            return dto;
        }

        /// <summary>
        /// Folds one send outcome into the stored row. A row of an earlier destination is
        /// replaced by a fresh one, so a corrected URL never inherits the old failures. A success
        /// ends a failing episode; the failure that crosses <see cref="FailingThreshold"/> during a
        /// real delivery stamps the episode and asks for the bell — a test never does, because the
        /// admin pressing it already sees the result.
        /// </summary>
        public static ChannelHealthFold Apply(
            NotificationChannelHealth? stored,
            string scopeKey,
            string channelId,
            string fingerprint,
            NotificationSendResult result,
            ChannelSendKind kind,
            DateTime nowUtc)
        {
            var current = Current(stored, fingerprint);
            var next = current != null
                ? Copy(current)
                : new NotificationChannelHealth { ScopeKey = scopeKey, ChannelId = channelId, Fingerprint = fingerprint };
            var wasFailing = next.ConsecutiveFailures >= FailingThreshold;

            next.LastAttemptUtc = nowUtc;
            next.Recent = Append(next.Recent, result.Success ? '1' : '0');

            if (result.Success)
            {
                next.ConsecutiveFailures = 0;
                next.LastSuccessUtc = nowUtc;
                next.FailingSinceUtc = null;
                next.FailingNotifiedUtc = null;
                return new ChannelHealthFold(next, wasFailing ? ChannelHealthTransition.Recovered : ChannelHealthTransition.None, raiseBell: false);
            }

            next.ConsecutiveFailures++;
            next.LastFailureUtc = nowUtc;
            next.FailingSinceUtc ??= nowUtc;
            next.LastStatusCode = result.StatusCode;
            next.LastError = DescribeFailure(result);

            var isFailing = next.ConsecutiveFailures >= FailingThreshold;
            var raiseBell = isFailing && kind == ChannelSendKind.Delivery && next.FailingNotifiedUtc == null;
            if (raiseBell)
                next.FailingNotifiedUtc = nowUtc;

            return new ChannelHealthFold(
                next,
                isFailing && !wasFailing ? ChannelHealthTransition.BecameFailing : ChannelHealthTransition.None,
                raiseBell);
        }

        /// <summary>
        /// Short reason for the health row: the status line when the destination answered (never
        /// its body), otherwise the transport's own message, capped.
        /// </summary>
        public static string DescribeFailure(NotificationSendResult result)
        {
            if (result.StatusCode is int code)
            {
                var phrase = ReasonPhrase(code);
                return phrase.Length > 0 ? $"HTTP {code} {phrase}" : $"HTTP {code}";
            }

            var message = string.IsNullOrWhiteSpace(result.Message) ? "Delivery failed." : result.Message.Trim();
            return message.Length <= MaxErrorLength ? message : message.Substring(0, MaxErrorLength - 1) + "…";
        }

        private static string ReasonPhrase(int code)
        {
            var name = Enum.GetName(typeof(HttpStatusCode), code);
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            // "BadGateway" -> "Bad Gateway"
            var sb = new StringBuilder(name!.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]))
                    sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

        private static string Append(string recent, char outcome)
        {
            var joined = (recent ?? string.Empty) + outcome;
            return joined.Length <= NotificationChannelHealth.RecentCapacity
                ? joined
                : joined.Substring(joined.Length - NotificationChannelHealth.RecentCapacity);
        }

        private static NotificationChannelHealth Copy(NotificationChannelHealth h) => new NotificationChannelHealth
        {
            ScopeKey = h.ScopeKey,
            ChannelId = h.ChannelId,
            Fingerprint = h.Fingerprint,
            Recent = h.Recent,
            ConsecutiveFailures = h.ConsecutiveFailures,
            LastAttemptUtc = h.LastAttemptUtc,
            LastSuccessUtc = h.LastSuccessUtc,
            LastFailureUtc = h.LastFailureUtc,
            FailingSinceUtc = h.FailingSinceUtc,
            LastStatusCode = h.LastStatusCode,
            LastError = h.LastError,
            FailingNotifiedUtc = h.FailingNotifiedUtc,
        };
    }
}
