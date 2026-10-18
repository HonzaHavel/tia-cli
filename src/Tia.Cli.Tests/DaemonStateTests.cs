using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TiaCli.Daemon;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// Points DaemonPaths at a temp directory for the lifetime of the test class, so these tests
    /// can never disturb a real daemon's advertisement in %LOCALAPPDATA%\tia-cli.
    /// </summary>
    public sealed class DaemonStateTests : IDisposable
    {
        private readonly string _home;

        public DaemonStateTests()
        {
            _home = Path.Combine(Path.GetTempPath(), "tia-cli-tests-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("TIA_CLI_HOME", _home);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TIA_CLI_HOME", null);
            Environment.SetEnvironmentVariable(SessionRegistry.PortalVariable, null);
            try { Directory.Delete(_home, recursive: true); } catch { }
        }

        [Fact]
        public void HomeOverrideIsHonoured()
        {
            Assert.StartsWith(_home, DaemonPaths.StateFileFor(42));
        }

        [Fact]
        public void EachDaemonHasAFileOfItsOwn()
        {
            Assert.NotEqual(DaemonPaths.StateFileFor(100), DaemonPaths.StateFileFor(200));
        }

        [Fact]
        public void WriteThenReadRoundTrips()
        {
            DaemonState.Write(new DaemonState
            {
                Pipe = "tia-cli-test-4242",
                ProcessId = 4242,
                StartedAt = "2026-08-31T12:00:00",
                PortalProcessId = 21020,
            });

            var read = DaemonState.Read(4242);
            Assert.Equal("tia-cli-test-4242", read.Pipe);
            Assert.Equal(4242, read.ProcessId);
            Assert.Equal("2026-08-31T12:00:00", read.StartedAt);
            Assert.Equal(21020, read.PortalProcessId);
        }

        [Fact]
        public void ReadAllFindsEverySession()
        {
            DaemonState.Write(new DaemonState { Pipe = "a", ProcessId = 100 });
            DaemonState.Write(new DaemonState { Pipe = "b", ProcessId = 200 });

            var all = DaemonState.ReadAll().Select(s => s.ProcessId).OrderBy(p => p).ToList();
            Assert.Equal(new[] { 100, 200 }, all);
        }

        [Fact]
        public void ReadReturnsNullWhenNoFileExists()
        {
            DaemonState.Delete(4242);
            Assert.Null(DaemonState.Read(4242));
        }

        [Fact]
        public void ACorruptFileIsSkippedNotThrown()
        {
            // A killed daemon can leave anything behind; reading must shrug, not throw.
            Directory.CreateDirectory(DaemonPaths.Directory);
            File.WriteAllText(DaemonPaths.StateFileFor(300), "{ not json");
            DaemonState.Write(new DaemonState { Pipe = "ok", ProcessId = 400 });

            Assert.Null(DaemonState.Read(300));
            Assert.Equal(new[] { 400 }, DaemonState.ReadAll().Select(s => s.ProcessId));
        }

        [Fact]
        public void DeleteIsIdempotentAndOnlyTouchesItsOwnFile()
        {
            DaemonState.Write(new DaemonState { Pipe = "p", ProcessId = 1 });
            DaemonState.Write(new DaemonState { Pipe = "q", ProcessId = 2 });
            DaemonState.Delete(1);
            DaemonState.Delete(1);

            Assert.False(File.Exists(DaemonPaths.StateFileFor(1)));
            Assert.True(File.Exists(DaemonPaths.StateFileFor(2)));
        }

        [Fact]
        public void IsProcessAliveIsTrueForThisProcess()
        {
            var state = new DaemonState { ProcessId = Process.GetCurrentProcess().Id };
            Assert.True(state.IsProcessAlive());
        }

        [Fact]
        public void IsProcessAliveIsFalseForAnImpossiblePid()
        {
            // Pids are multiples of 4 on Windows; an odd one can never name a live process.
            var state = new DaemonState { ProcessId = 999999999 };
            Assert.False(state.IsProcessAlive());
        }

        [Fact]
        public void PipeNamesArePerUserPerDaemonAndSanitised()
        {
            var pipe = DaemonPaths.PipeNameFor(4242);
            Assert.StartsWith("tia-cli-", pipe);
            Assert.EndsWith("-4242", pipe);
            Assert.DoesNotContain(" ", pipe);
            Assert.Equal(pipe, pipe.ToLowerInvariant());
        }

        // ---------------------------------------------------------------- which session a command gets

        private static DaemonState Holding(int portal, int daemon) =>
            new DaemonState { Pipe = "pipe-" + daemon, ProcessId = daemon, PortalProcessId = portal };

        [Fact]
        public void ANamedPortalPicksTheSessionHoldingIt()
        {
            var running = new List<DaemonState> { Holding(21020, 1), Holding(8756, 2) };
            Assert.Equal(2, SessionRegistry.Resolve(running, 8756).ProcessId);
        }

        [Fact]
        public void ANamedPortalNoSessionHoldsIsLeftToAttachDirectly()
        {
            var running = new List<DaemonState> { Holding(21020, 1) };
            Assert.Null(SessionRegistry.Resolve(running, 8756));
        }

        [Fact]
        public void WithNoneNamedTheOnlySessionIsUsed()
        {
            var running = new List<DaemonState> { Holding(21020, 1) };
            Assert.Equal(1, SessionRegistry.Resolve(running, null).ProcessId);
            Assert.Null(SessionRegistry.Resolve(new List<DaemonState>(), null));
        }

        [Fact]
        public void SeveralSessionsAndNoneNamedIsRefusedWithTheirPortals()
        {
            var running = new List<DaemonState> { Holding(21020, 1), Holding(8756, 2) };

            var refused = Assert.Throws<WireException>(() => SessionRegistry.Resolve(running, null));
            Assert.Equal(WireErrorCodes.Ambiguous, refused.Code);
            Assert.Contains("21020", refused.Message);
            Assert.Contains("8756", refused.Message);
            Assert.Contains("--attach", refused.Hint);
        }

        [Theory]
        [InlineData("21020", 21020)]
        [InlineData(" 21020 ", 21020)]
        [InlineData("", null)]
        [InlineData("abc", null)]
        [InlineData("0", null)]
        [InlineData(null, null)]
        public void TheEnvironmentNamesAPortal(string value, int? expected)
        {
            Environment.SetEnvironmentVariable(SessionRegistry.PortalVariable, value);
            Assert.Equal(expected, SessionRegistry.PortalFromEnvironment());
        }
    }
}
