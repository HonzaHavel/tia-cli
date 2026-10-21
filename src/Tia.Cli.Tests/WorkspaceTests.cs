using System;
using System.IO;
using System.Linq;
using TiaCli.Cli;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// The workspace: a folder found by walking up to .tia/config, holding the program in source/
    /// and the working project and archive in .tia/. What matters most is that commands inside it
    /// reach this project and no other, so the path rules get the closest look.
    /// </summary>
    public sealed class WorkspaceTests : IDisposable
    {
        private readonly string _root;

        public WorkspaceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "tia-ws-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("TIA_WORKSPACE", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TIA_WORKSPACE", null);
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string Dir(params string[] parts)
        {
            var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        [Fact]
        public void TheFileRoundTrips()
        {
            var text = new Workspace { Name = "Line3", TiaVersion = "V20", Station = "PLC_1" }.Render();
            var read = Workspace.Parse(text);

            Assert.Equal("Line3", read.Name);
            Assert.Equal("V20", read.TiaVersion);
            Assert.Equal("PLC_1", read.Station);
            Assert.Equal("source", read.Source);
        }

        [Fact]
        public void AnUnsetStationIsWrittenAsAHintNotAValue()
        {
            var read = Workspace.Parse(new Workspace { Name = "Line3" }.Render());
            Assert.True(string.IsNullOrEmpty(read.Station));
        }

        [Fact]
        public void CommentsQuotesAndUnknownKeysAreTolerated()
        {
            var read = Workspace.Parse("# hello\nname: \"Line 3\"   # trailing\r\nfuture: thing\nsource: src\n");
            Assert.Equal("Line 3", read.Name);
            Assert.Equal("src", read.Source);
        }

        [Theory]
        [InlineData("source: ../elsewhere")]
        [InlineData("source: C:\\outside")]
        [InlineData("source: ")]
        public void FoldersMustStayInsideTheWorkspace(string line)
        {
            Assert.Throws<WireException>(() => Workspace.Parse(line));
        }

        [Fact]
        public void ALineThatIsNotKeyValueIsRefusedWithItsNumber()
        {
            var refused = Assert.Throws<WireException>(() => Workspace.Parse("name: a\njust words\n"));
            Assert.Contains("line 2", refused.Message);
        }

        [Fact]
        public void ItIsFoundFromAnyFolderInside()
        {
            Workspace.Init(_root, "Line3", null);
            var deep = Dir("source", "PLC_1", "Program blocks");

            var found = Workspace.Find(deep);
            Assert.NotNull(found);
            Assert.True(Workspace.SamePath(_root, found.Root));
            Assert.Equal("Line3", found.Name);
        }

        [Fact]
        public void OutsideAWorkspaceThereIsNone()
        {
            Assert.Null(Workspace.Find(Dir("not-a-workspace")));
        }

        [Fact]
        public void InitMakesTheTiaFolderAndSourceBesideIt()
        {
            var workspace = Workspace.Init(_root, null, null, "PLC_1");

            Assert.True(File.Exists(Path.Combine(_root, ".tia", "config")));
            Assert.True(Workspace.SamePath(Path.Combine(_root, "source"), workspace.SourceDir));
            Assert.True(Workspace.SamePath(Path.Combine(_root, ".tia", "project"), workspace.ProjectDir));
            Assert.True(Workspace.SamePath(Path.Combine(_root, ".tia", "archive"), workspace.ArchiveDir));
            Assert.True(Directory.Exists(workspace.SourceDir));
            Assert.True(Directory.Exists(workspace.ArchiveDir));
            Assert.True(Directory.Exists(workspace.ProjectDir));
            Assert.Equal(Path.GetFileName(_root), workspace.Name);
            Assert.Equal("PLC_1", Workspace.Find(_root).Station);
        }

        [Fact]
        public void GitIsToldAboutTheConfigAndNothingElseInTia()
        {
            Workspace.Init(_root, "Line3", null);

            var lines = File.ReadAllLines(Path.Combine(_root, ".tia", ".gitignore"));
            Assert.Contains("*", lines);
            Assert.Contains("!config", lines);
            Assert.Contains("!.gitignore", lines);
        }

        [Fact]
        public void InitLeavesTheRootGitignoreAlone()
        {
            File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\n");
            Workspace.Init(_root, "Line3", null);
            Assert.Equal("bin/\n", File.ReadAllText(Path.Combine(_root, ".gitignore")));
        }

        [Fact]
        public void InitRefusesAWorkspaceTwiceAndOneInsideAnother()
        {
            Workspace.Init(_root, "Line3", null);
            Assert.Throws<WireException>(() => Workspace.Init(_root, "Line3", null));
            Assert.Throws<WireException>(() => Workspace.Init(Dir("source", "inner"), "Inner", null));
        }

        [Fact]
        public void AnEmptyTiaFolderIsNotAWorkspace()
        {
            // Some other tool's .tia, or an init that failed before writing the config.
            Dir(".tia", "project");
            Assert.Null(Workspace.Find(_root));
        }

        [Fact]
        public void InitFromAnArchiveCopiesItIntoArchiveAndNamesTheWorkspaceAfterIt()
        {
            var zap = Path.Combine(Dir("elsewhere"), "Line3.zap20");
            File.WriteAllText(zap, "archive bytes");
            var target = Path.Combine(_root, "ws");

            var workspace = Workspace.Init(target, null, zap);

            Assert.Equal("Line3", workspace.Name);
            Assert.Equal("V20", workspace.TiaVersion);
            Assert.True(File.Exists(zap), "the original must stay where it was");
            Assert.True(Workspace.SamePath(Path.Combine(workspace.ArchiveDir, "Line3.zap20"), workspace.ArchiveFile));
        }

        [Fact]
        public void InitFromAProjectCopiesTheWholeProjectFolder()
        {
            var folder = Dir("elsewhere", "Line3");
            var ap = Path.Combine(folder, "Line3.ap20");
            File.WriteAllText(ap, "project");
            File.WriteAllText(Path.Combine(Dir("elsewhere", "Line3", "System"), "data.bin"), "x");

            var workspace = Workspace.Init(Path.Combine(_root, "ws"), null, ap);

            Assert.True(Workspace.SamePath(Path.Combine(workspace.ProjectDir, "Line3", "Line3.ap20"), workspace.ProjectFile));
            Assert.True(File.Exists(Path.Combine(workspace.ProjectDir, "Line3", "System", "data.bin")));
            Assert.True(File.Exists(ap));
        }

        [Fact]
        public void InitFromAnythingElseIsRefused()
        {
            var other = Path.Combine(Dir("elsewhere"), "notes.txt");
            File.WriteAllText(other, "x");
            Assert.Throws<WireException>(() => Workspace.Init(Path.Combine(_root, "ws"), null, other));
        }

        [Fact]
        public void TheProjectNamedAfterTheWorkspaceWinsOverOthers()
        {
            var workspace = Workspace.Init(_root, "Line3", null);
            File.WriteAllText(Path.Combine(Dir(".tia", "project", "Aardvark"), "Aardvark.ap20"), "");
            File.WriteAllText(Path.Combine(Dir(".tia", "project", "Line3"), "Line3.ap20"), "");

            Assert.EndsWith(Path.Combine("Line3", "Line3.ap20"), workspace.ProjectFile);
        }

        [Fact]
        public void NoProjectOrArchiveYetIsNull()
        {
            var workspace = Workspace.Init(_root, "Line3", null);
            Assert.Null(workspace.ProjectFile);
            Assert.Null(workspace.ArchiveFile);
        }

        [Theory]
        [InlineData("Line3.ap20", "V20")]
        [InlineData("Line3.zap19", "V19")]
        [InlineData("Line3.ap15_1", "V15_1")]
        [InlineData("Line3.txt", "")]
        public void TheVersionComesFromTheExtension(string file, string expected)
        {
            Assert.Equal(expected, Workspace.VersionOf(file));
        }

        [Fact]
        public void OnlyAProjectInsideProjectFolderBelongsToTheWorkspace()
        {
            var workspace = Workspace.Init(_root, "Line3", null);

            Assert.True(WorkspaceCommands.Holds(workspace, Path.Combine(workspace.ProjectDir, "Line3", "Line3.ap20")));
            Assert.True(WorkspaceCommands.Holds(workspace, Path.Combine(workspace.ProjectDir, "Line3", "Line3.ap20").ToUpperInvariant()));
            // A sibling whose name merely starts the same way must not count.
            Assert.False(WorkspaceCommands.Holds(workspace, Path.Combine(_root, ".tia", "project-old", "Line3", "Line3.ap20")));
            Assert.False(WorkspaceCommands.Holds(workspace, @"C:\other\Line3\Line3.ap20"));
            Assert.False(WorkspaceCommands.Holds(workspace, null));
        }

        // ---------------------------------------------------------------- the station

        private static CommandLine Filled(string line, string station = "PLC_1")
        {
            var cmd = CommandLine.Parse(CommandLine.Tokenize(line));
            WorkspaceCommands.FillStation(cmd, new Workspace { Name = "Line3", Station = station });
            return cmd;
        }

        [Theory]
        [InlineData("blocks", "PLC_1")]
        [InlineData("blocks --tree", "PLC_1")]
        [InlineData("block export Motor", "PLC_1 Motor")]
        [InlineData("block export --all --source", "PLC_1")]
        [InlineData("block source --all", "PLC_1")]
        [InlineData("block rename Motor Pump", "PLC_1 Motor Pump")]
        [InlineData("compile", "PLC_1")]
        [InlineData("tag add Speed --type Int", "PLC_1 Speed")]
        // Every call GIT-WORKFLOW.md's helpers make, as they make it.
        [InlineData("tables --json", "PLC_1")]
        [InlineData("tags --table Default --json", "PLC_1")]
        [InlineData("block export --all --source --out source/program --quiet", "PLC_1")]
        [InlineData("block delete Pumps/Gone --force", "PLC_1 Pumps/Gone")]
        [InlineData("block import source/program/Pumps/Motor.xml --overwrite --folder Pumps", "PLC_1 source/program/Pumps/Motor.xml")]
        [InlineData("source add \"New Valve.db\" --file \"source/program/Pumps/New Valve.db\" --folder Pumps", "PLC_1 New Valve.db")]
        [InlineData("source delete Motor.scl --force", "PLC_1 Motor.scl")]
        public void ALeftOutStationIsFilledIn(string line, string positionals)
        {
            Assert.Equal(positionals, string.Join(" ", Filled(line).Positionals));
        }

        [Theory]
        [InlineData("blocks PLC_2", "PLC_2")]
        [InlineData("block export PLC_2 Motor", "PLC_2 Motor")]
        [InlineData("block export PLC_2 --all", "PLC_2")]
        [InlineData("block rename PLC_2 Motor Pump", "PLC_2 Motor Pump")]
        [InlineData("devices", "")]
        [InlineData("project open C:\\p\\Line.ap20", "C:\\p\\Line.ap20")]
        [InlineData("blocks --no-workspace", "")]
        public void AGivenStationAndVerbsWithoutOneAreLeftAlone(string line, string positionals)
        {
            Assert.Equal(positionals, string.Join(" ", Filled(line).Positionals));
        }

        [Fact]
        public void WithNoStationConfiguredNothingIsFilledIn()
        {
            Assert.Empty(Filled("blocks", station: "").Positionals);
        }

        // ---------------------------------------------------------------- which TIA Portal

        private static readonly System.Collections.Generic.Dictionary<string, string> Installed =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "TIAP19", @"C:\V19\Bin\Siemens.Automation.Portal.exe" },
                { "TIAP20", @"C:\V20\Bin\Siemens.Automation.Portal.exe" },
                { "TIAP15_1", @"C:\V15_1\Bin\Siemens.Automation.Portal.exe" },
                { "TIAAdminV3", @"C:\Admin\Bin\Siemens.Automation.Portal.exe" },
            };

        [Theory]
        [InlineData("V20", @"C:\V20\Bin\Siemens.Automation.Portal.exe")]
        [InlineData("v19", @"C:\V19\Bin\Siemens.Automation.Portal.exe")]
        [InlineData("V15_1", @"C:\V15_1\Bin\Siemens.Automation.Portal.exe")]
        [InlineData("", @"C:\V20\Bin\Siemens.Automation.Portal.exe")]
        public void TheWorkspacesVersionPicksThePortal(string version, string expected)
        {
            Assert.Equal(expected, WorkspaceCommands.PickPortal(Installed, version));
        }

        [Fact]
        public void AVersionThatIsNotInstalledIsRefusedNotUpgraded()
        {
            var refused = Assert.Throws<WireException>(() => WorkspaceCommands.PickPortal(Installed, "V18"));
            Assert.Contains("V20", refused.Hint);
        }

        // ---------------------------------------------------------------- a portal named from outside

        [Fact]
        public void InsideAWorkspaceAPortalWithoutItsProjectIsRefused()
        {
            Workspace.Init(_root, "Line3", null);
            Environment.SetEnvironmentVariable("TIA_WORKSPACE", _root);

            var cmd = CommandLine.Parse(new[] { "blocks", "PLC_1", "--attach", "999999" });
            var refused = Assert.Throws<WireException>(() => WorkspaceCommands.PortalFor(cmd, 999999, strict: true));
            Assert.Contains("--attach", refused.Message);
            Assert.Contains("--no-workspace", refused.Hint);
        }

        [Fact]
        public void NoWorkspaceLetsTheNamedPortalThrough()
        {
            Workspace.Init(_root, "Line3", null);
            Environment.SetEnvironmentVariable("TIA_WORKSPACE", _root);

            var cmd = CommandLine.Parse(new[] { "blocks", "PLC_1", "--no-workspace" });
            Assert.Equal(999999, WorkspaceCommands.PortalFor(cmd, 999999, strict: true));
        }

        [Fact]
        public void TheSessionVerbsMayNameAnyPortal()
        {
            Workspace.Init(_root, "Line3", null);
            Environment.SetEnvironmentVariable("TIA_WORKSPACE", _root);

            var cmd = CommandLine.Parse(new[] { "session", "stop", "--attach", "999999" });
            Assert.Equal(999999, WorkspaceCommands.PortalFor(cmd, 999999, strict: false));
        }
    }
}
