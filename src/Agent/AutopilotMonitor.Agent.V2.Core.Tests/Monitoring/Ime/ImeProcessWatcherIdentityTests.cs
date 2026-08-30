using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// CWE-807 hardening of <see cref="ImeProcessWatcher"/>: a process is only ever attached when
    /// its identity (session 0 + image under the IME install root) says it is the service, and a
    /// reported exit re-arms discovery so the next IME instance is watched too.
    /// </summary>
    [Collection("SerialThreading")] // Process.Exited is delivered on the shared ThreadPool
    public sealed class ImeProcessWatcherIdentityTests
    {
        private const string Root = @"C:\Program Files (x86)\Microsoft Intune Management Extension";
        private static readonly string[] Roots = { Root };
        private static readonly DateTime T0 = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);

        private static ImeProcessCandidate C(int pid, int session, string path, int startOffsetSeconds = 0) =>
            new ImeProcessCandidate(pid, session, path, T0.AddSeconds(startOffsetSeconds));

        // ---- pure identity logic ----

        [Theory]
        [InlineData(0, Root + @"\Microsoft.Management.Services.IntuneWindowsAgent\IntuneManagementExtension.exe", true)]
        [InlineData(0, Root + @"\IntuneManagementExtension.exe", true)]
        [InlineData(0, @"c:\program files (x86)\microsoft intune management extension\INTUNEMANAGEMENTEXTENSION.EXE", true)]
        [InlineData(1, Root + @"\IntuneManagementExtension.exe", false)]                          // user session
        [InlineData(2, @"C:\Users\user\Downloads\IntuneManagementExtension.exe", false)]           // spoof
        [InlineData(0, @"C:\Users\user\Downloads\IntuneManagementExtension.exe", false)]           // wrong root
        [InlineData(0, Root + @"2\IntuneManagementExtension.exe", false)]                         // sibling folder
        [InlineData(0, Root + @"\..\Evil\IntuneManagementExtension.exe", false)]                  // traversal
        [InlineData(0, Root + @"\IntuneManagementExtensionX.exe", false)]                         // name variant
        [InlineData(-1, Root + @"\IntuneManagementExtension.exe", false)]                         // session unknown
        [InlineData(0, "", false)]                                                                // path unresolved
        public void IsTrusted_requires_session0_and_install_root(int session, string path, bool expected)
        {
            Assert.Equal(expected, ImeProcessIdentity.IsTrusted(C(1, session, path), Roots));
        }

        [Fact]
        public void IsTrusted_rejects_null_path()
        {
            Assert.False(ImeProcessIdentity.IsTrusted(C(1, 0, null!), Roots));
        }

        [Fact]
        public void SelectPreferred_skips_untrusted_and_picks_oldest_trusted()
        {
            var fake = C(10, 1, @"C:\Users\user\IntuneManagementExtension.exe", startOffsetSeconds: -100); // oldest, but a spoof
            var young = C(20, 0, Root + @"\IntuneManagementExtension.exe", startOffsetSeconds: 50);
            var service = C(30, 0, Root + @"\IntuneManagementExtension.exe", startOffsetSeconds: 0);

            var chosen = ImeProcessIdentity.SelectPreferred(new[] { fake, young, service }, Roots);

            Assert.Equal(30, chosen?.Pid);
            Assert.Null(ImeProcessIdentity.SelectPreferred(new[] { fake }, Roots));
        }

        [Fact]
        public void DefaultTrustedRoots_are_under_program_files()
        {
            var roots = ImeProcessIdentity.DefaultTrustedRoots();
            Assert.NotEmpty(roots);
            Assert.All(roots, r => Assert.EndsWith(ImeProcessIdentity.InstallFolderName, r));
        }

        [Fact]
        public void Probe_reads_real_facts_for_the_current_process()
        {
            var me = ImeProcessIdentity.Probe(Process.GetCurrentProcess());
            Assert.Equal(Process.GetCurrentProcess().Id, me.Pid);
            Assert.True(me.SessionId >= 0);
            Assert.False(string.IsNullOrEmpty(me.ImagePath));
            Assert.EndsWith(".exe", me.ImagePath, StringComparison.OrdinalIgnoreCase);
        }

        // ---- watcher behaviour with real (stand-in) processes ----

        private sealed class Fixture : IDisposable
        {
            public readonly TempDirectory Tmp = new TempDirectory();
            public readonly FakeSignalIngressSink Ingress = new FakeSignalIngressSink();
            public readonly ImeProcessWatcher Watcher;
            public readonly List<Process> Running = new List<Process>();
            public readonly Dictionary<int, ImeProcessCandidate> Facts = new Dictionary<int, ImeProcessCandidate>();

            public Fixture()
            {
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Watcher = new ImeProcessWatcher("session-ime", "tenant-1", new InformationalEventPost(Ingress, new VirtualClock(T0)), logger, Roots);
                // Fresh handles, like Process.GetProcessesByName: the watcher owns and disposes what it is given.
                Watcher.ProcessSource = () => Running
                    .Where(p => { try { return !p.HasExited; } catch { return false; } })
                    .Select(p => Process.GetProcessById(p.Id))
                    .ToArray();
                Watcher.IdentityProbe = p => Facts.TryGetValue(p.Id, out var f) ? f : ImeProcessIdentity.Probe(p);
            }

            /// <summary>A stand-in process that blocks on stdin until killed; identity facts are injected.</summary>
            public Process Spawn(bool trusted)
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c pause")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                Running.Add(p);
                Facts[p.Id] = trusted
                    ? new ImeProcessCandidate(p.Id, 0, Root + @"\IntuneManagementExtension.exe", p.StartTime.ToUniversalTime())
                    : new ImeProcessCandidate(p.Id, 1, @"C:\Users\user\Downloads\IntuneManagementExtension.exe", p.StartTime.ToUniversalTime());
                return p;
            }

            public IReadOnlyList<FakeSignalIngressSink.PostedSignal> ExitEvents() =>
                Ingress.Posted
                    .Where(s => s.Kind == DecisionSignalKind.InformationalEvent
                                && s.Payload != null
                                && s.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                                && et == SharedEventTypes.ImeProcessExited)
                    .ToList();

            public void Dispose()
            {
                Watcher.Dispose();
                foreach (var p in Running)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
                Tmp.Dispose();
            }
        }

        private static void WaitUntil(Func<bool> cond, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
                Thread.Sleep(25);
            Assert.True(cond(), "condition not met within timeout");
        }

        [Fact]
        public void Spoofed_process_is_never_attached_and_its_exit_emits_nothing()
        {
            using var f = new Fixture();
            var fake = f.Spawn(trusted: false);

            f.Watcher.TryAttachNow();
            f.Watcher.TryAttachNow();
            Assert.Null(f.Watcher.AttachedProcessId);

            fake.Kill();
            fake.WaitForExit();
            Thread.Sleep(200);
            Assert.Empty(f.ExitEvents());

            // The real service arriving later is still attached — discovery was never consumed.
            var real = f.Spawn(trusted: true);
            f.Watcher.TryAttachNow();
            Assert.Equal(real.Id, f.Watcher.AttachedProcessId);
        }

        [Fact]
        public void Real_process_exit_is_reported_and_discovery_rearms_for_the_next_instance()
        {
            using var f = new Fixture();
            var first = f.Spawn(trusted: true);

            f.Watcher.TryAttachNow();
            Assert.Equal(first.Id, f.Watcher.AttachedProcessId);

            first.Kill();
            first.WaitForExit();
            WaitUntil(() => f.ExitEvents().Count == 1);
            Assert.Null(f.Watcher.AttachedProcessId);

            // IME restarted (or a spoof took the name first — still ignored).
            var spoof = f.Spawn(trusted: false);
            var second = f.Spawn(trusted: true);
            f.Watcher.TryAttachNow();
            Assert.Equal(second.Id, f.Watcher.AttachedProcessId);

            second.Kill();
            second.WaitForExit();
            WaitUntil(() => f.ExitEvents().Count == 2);

            spoof.Kill();
            spoof.WaitForExit();
            Thread.Sleep(200);
            Assert.Equal(2, f.ExitEvents().Count);

            var pids = f.ExitEvents()
                .Select(e => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(e.TypedPayload)["pid"])
                .ToList();
            Assert.Equal(new object[] { first.Id, second.Id }, pids);
        }

        [Fact]
        public void With_spoof_and_service_both_running_the_service_wins_regardless_of_order()
        {
            using var f = new Fixture();
            var spoof = f.Spawn(trusted: false);   // listed first
            var real = f.Spawn(trusted: true);

            f.Watcher.TryAttachNow();

            Assert.Equal(real.Id, f.Watcher.AttachedProcessId);
        }
    }
}
