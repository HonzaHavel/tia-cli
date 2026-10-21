using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using TiaCli.Daemon;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// The workspace verbs - init, open, workspace - and the rule that makes a workspace useful:
    /// inside one, a command goes to the TIA Portal that has the workspace's project open, found by
    /// the project's path, never to whatever portal happens to be running.
    ///
    /// No daemon is needed for that. A TIA Portal that 'tia open' starts is an ordinary program, as
    /// if double-clicked, so it belongs to the engineer and outlives every command; each command
    /// finds it by its project and attaches for as long as it runs. Only a headless portal needs a
    /// session to hold it, because Openness closes a portal it started when its starter exits.
    /// </summary>
    internal static class WorkspaceCommands
    {
        /// <summary>
        /// Verbs that pick their project themselves, or need none, so a workspace does not decide
        /// where they go.
        /// </summary>
        private static readonly HashSet<string> ChooseTheirOwnProject = new HashSet<string>(StringComparer.Ordinal)
        {
            "portals", "catalog", "project new", "project open", "project retrieve",
        };

        /// <summary>
        /// How many positional arguments each verb that starts with a station takes. Given one fewer
        /// inside a workspace with a station set, the station is put in front. Counting, rather than
        /// guessing from the words, is what keeps "block export Motor" from being read as a station.
        /// </summary>
        private static readonly Dictionary<string, int> StationArguments = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "export", 1 }, { "sync", 1 }, { "blocks", 1 }, { "sources", 1 }, { "tables", 1 }, { "tags", 1 }, { "compile", 1 },
            { "download", 1 }, { "sim create", 1 }, { "sim start", 1 }, { "show device", 1 },
            { "device attrs", 1 }, { "device protection", 1 }, { "device ip", 1 },
            { "block show", 2 }, { "block source", 2 }, { "block export", 2 }, { "block import", 2 },
            { "block delete", 2 }, { "folder add", 2 }, { "folder delete", 2 }, { "source add", 2 },
            { "source generate", 2 }, { "source delete", 2 }, { "scl import", 2 }, { "table add", 2 },
            { "tag add", 2 }, { "show block", 2 }, { "show table", 2 },
            { "table export", 2 }, { "table import", 2 }, { "types", 1 }, { "type export", 2 }, { "type import", 2 },
            { "blocks export", 1 }, { "blocks import", 1 }, { "types export", 1 }, { "types import", 1 },
            { "tables export", 1 }, { "tables import", 1 },
            { "block rename", 3 }, { "device set", 3 },
        };

        /// <summary>True when the project file lives in this workspace's project folder.</summary>
        internal static bool Holds(Workspace workspace, string projectPath)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(projectPath)) return false;
            try
            {
                var dir = Path.GetFullPath(workspace.ProjectDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return Path.GetFullPath(projectPath).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The portal a command goes to, given the one --attach or TIA_PORTAL named. Outside a
        /// workspace, and for verbs that choose their own project, that name stands. Inside one, the
        /// portal is the one whose session, or failing that whose window, has the workspace's project
        /// open - and a name pointing anywhere else is refused, not obeyed: a TIA_PORTAL left over in
        /// the shell would otherwise quietly send every command to some other project. With nothing
        /// holding the project, strict refuses and lenient returns null, for the verbs that only look.
        /// </summary>
        public static int? PortalFor(CommandLine cmd, int? named, bool strict)
        {
            if (cmd.Has("no-workspace") || ChooseTheirOwnProject.Contains(cmd.Verb)) return named;

            var workspace = Workspace.Find();
            if (workspace == null) return named;

            // The session verbs only look or tidy up; a portal they were pointed at is theirs to name.
            if (named.HasValue && !strict) return named;

            var found = Holder(workspace, cmd);
            if (named.HasValue && found != named)
            {
                var by = cmd.Has("attach") ? "--attach" : SessionRegistry.PortalVariable;
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"{by} names TIA Portal {named.Value}, but this is the {workspace.Name} workspace and its project is " +
                    (found.HasValue ? $"open in TIA Portal {found.Value}." : "open in no TIA Portal."),
                    $"Drop {by} inside the workspace, or add --no-workspace to work on TIA Portal {named.Value} anyway.");
            }

            if (found.HasValue || !strict) return found;
            throw new WireException(WireErrorCodes.NotConnected,
                $"No TIA Portal has this workspace's project open ({workspace.Name}, in {workspace.ProjectDir}).",
                "Run 'tia open' to open it, or add --no-workspace to work outside it.");
        }

        /// <summary>The portal holding the workspace's project: through a session first, then on screen.</summary>
        private static int? Holder(Workspace workspace, CommandLine cmd)
        {
            var held = SessionRegistry.Running().FirstOrDefault(s => Holds(workspace, s.ProjectPath));
            if (held != null) return held.PortalProcessId;

            var open = ListPortals(cmd).FirstOrDefault(p => Holds(workspace, p.ProjectPath));
            return open?.ProcessId;
        }

        /// <summary>
        /// Puts the workspace's station in front of a command's arguments when it was left out:
        /// "tia blocks" becomes "tia blocks PLC_1". Nothing changes when the station was given, when
        /// none is configured, or for a verb that takes no station.
        /// </summary>
        public static void FillStation(CommandLine cmd, Workspace workspace)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(workspace.Station) || cmd.Has("no-workspace")) return;
            if (!StationArguments.TryGetValue(cmd.Verb, out var count)) return;

            // Exporting everything takes the station alone.
            if (cmd.Has("all") && (cmd.Verb == "block export" || cmd.Verb == "block source" ||
                                   cmd.Verb == "table export" || cmd.Verb == "type export")) count = 1;

            if (cmd.Positionals.Count == count - 1) cmd.Positionals.Insert(0, workspace.Station);
        }

        /// <summary>The portals running, enumerated in this process. Needs no Openness approval.</summary>
        private static List<PortalProcessDto> ListPortals(CommandLine cmd)
        {
            try
            {
                using (var executor = ExecutorFactory.CreateDirect(new Openness.AcquireOptions(), cmd.Value("openness-version")))
                    return Portals(executor);
            }
            catch
            {
                // No Openness here, or no TIA Portal installed: nothing has the project open.
                return new List<PortalProcessDto>();
            }
        }

        private static List<PortalProcessDto> Portals(IExecutor executor) =>
            JsonUtil.To<List<PortalProcessDto>>(Commands.Call(executor, "portal.list")) ?? new List<PortalProcessDto>();

        private static Workspace Require()
        {
            return Workspace.Find() ?? throw new WireException(WireErrorCodes.InvalidRequest,
                $"Not inside a tia workspace: no {Workspace.DirName} folder here or in any folder above.",
                "'tia init' makes one in the current folder.");
        }

        // ---------------------------------------------------------------- tia init

        public static int Init(CommandLine cmd, Output output)
        {
            var root = cmd.Positionals.Count > 0 ? cmd.Positional(0, "folder") : Environment.CurrentDirectory;
            var workspace = Workspace.Init(root, cmd.Value("name"), cmd.Value("from"), cmd.Value("station"));

            if (output.AsJson) return Show(workspace, output);

            output.Line($"Workspace {workspace.Name} in {workspace.Root}");
            output.Pairs(new[]
            {
                Output.KV(Workspace.DirName + "/" + Workspace.ConfigName, "what makes this folder a workspace - commit it"),
                Output.KV(Workspace.DirName + "/project/", "the working project - kept out of git"),
                Output.KV(Workspace.DirName + "/archive/", "the archive - kept out of git, for blob storage"),
                Output.KV(workspace.Source + "/", "the program as files - commit it"),
            });

            var from = cmd.Value("from");
            if (!string.IsNullOrWhiteSpace(from))
                output.Detail($"Copied {Path.GetFileName(from)} into " + Workspace.DirName + "/" +
                              (Workspace.IsArchiveFile(from) ? "archive" : "project") + "/; the original is untouched.");
            WarnIfLong(workspace, output);
            if (string.IsNullOrWhiteSpace(workspace.Station))
                output.Detail($"Set 'station:' in {Workspace.DirName}/{Workspace.ConfigName} and commands here need no station name.");
            output.Detail("Next: 'tia open' here opens the project in TIA Portal.");
            return 0;
        }

        // ---------------------------------------------------------------- tia open

        /// <summary>
        /// Gets the workspace's project open, whichever way is cheapest: it may be open already, a
        /// session with nothing open can take it, or a TIA Portal is started for it - an ordinary one
        /// with a window, or with --headless a session holding one without.
        /// </summary>
        public static int Open(CommandLine cmd, Output output)
        {
            var workspace = Require();
            var running = SessionRegistry.Running();

            var held = running.FirstOrDefault(s => Holds(workspace, s.ProjectPath));
            if (held != null)
            {
                output.Detail($"A session already has {workspace.Name} open.");
                return Session.Status(cmd, new DaemonExecutor(held.Pipe), output);
            }

            var open = ListPortals(cmd).FirstOrDefault(p => Holds(workspace, p.ProjectPath));
            if (open != null)
            {
                if (output.AsJson) return Show(workspace, output, cmd);
                output.Line($"TIA Portal {open.ProcessId} already has {workspace.Name} open.");
                return 0;
            }

            var project = workspace.ProjectFile;
            var archive = workspace.ArchiveFile;
            if (project == null && archive == null)
                throw new WireException(WireErrorCodes.NotFound,
                    $"Nothing to open: {Workspace.DirName}/project has no project and {Workspace.DirName}/archive no archive.",
                    "Copy one in with 'tia init --from <file>' in a new folder, or put the project folder into " +
                    workspace.ProjectDir + ". Building the project from " + workspace.Source + "/ is not there yet.");

            // A session somebody started and left empty - 'tia session start --headless', say - is a
            // portal waiting for a project. Taking it saves starting another; with several there is
            // no telling which one was meant.
            var idle = running.Where(s => s.PortalProcessId > 0 && string.IsNullOrEmpty(s.ProjectPath)).ToList();
            if (idle.Count == 1)
            {
                output.Detail($"Opening {workspace.Name} in the session on TIA Portal {idle[0].PortalProcessId}.");
                var executor = new DaemonExecutor(idle[0].Pipe);
                Load(executor, workspace, project, archive, cmd, output);
                return Session.Status(cmd, executor, output);
            }
            if (idle.Count > 1)
                throw new WireException(WireErrorCodes.Ambiguous,
                    $"{idle.Count} tia sessions have no project open (TIA Portal " +
                    string.Join(", ", idle.Select(s => s.PortalProcessId)) + ") and any of them could take this one.",
                    "Stop the ones you do not need with 'tia session stop --attach <pid>', then run 'tia open' again.");

            if (cmd.Has("headless"))
            {
                // Openness closes a portal it started when the starter exits, so a headless one has
                // to live in a session.
                if (project != null)
                    return Session.Start(Args(cmd, "--new", "--headless", "--project", project), output);
                return Session.Start(Args(cmd, "--new", "--headless"), output,
                    executor => Load(executor, workspace, null, archive, cmd, output));
            }

            return Launch(workspace, project, archive, cmd, output);
        }

        /// <summary>Opens the project, or retrieves the archive into the project folder, through one executor.</summary>
        private static void Load(IExecutor executor, Workspace workspace, string project, string archive,
            CommandLine cmd, Output output)
        {
            if (project != null)
            {
                output.Detail("Opening " + project + " - this takes minutes, not seconds.");
                Commands.Call(executor, "project.open", new { path = project, upgrade = cmd.Has("upgrade") });
                return;
            }

            output.Detail($"No project in {Workspace.DirName}/project yet; retrieving {Path.GetFileName(archive)} into it - " +
                          "this takes minutes, not seconds.");
            Commands.Call(executor, "project.retrieve", new
            {
                archive,
                directory = workspace.ProjectDir,
                upgrade = cmd.Has("upgrade"),
            });
        }

        /// <summary>
        /// Starts TIA Portal as an ordinary program, the way a double-click does, and waits until it
        /// has the project open. Started like this it belongs to nobody but the engineer: no command
        /// holds it, and closing it is closing the window. A project is handed over on the command
        /// line; an archive needs Openness, so it is retrieved over one attach that ends with this
        /// command - TIA keeps a project open after the connection that opened it has gone.
        /// </summary>
        private static int Launch(Workspace workspace, string project, string archive, CommandLine cmd, Output output)
        {
            if (cmd.Has("upgrade") && project != null)
                throw new WireException(WireErrorCodes.InvalidRequest,
                    "--upgrade needs a session to open the project through.",
                    "Use 'tia open --headless --upgrade', or open it in TIA Portal and let it ask.");

            var exe = PortalExe(workspace.TiaVersion);
            WarnIfLong(workspace, output);

            var process = StartDetached(exe, project);
            output.Detail($"Started TIA Portal {process.Id}" +
                          (project != null ? $" with {workspace.Name} - opening a project takes a minute or two." : "."));

            using (var executor = ExecutorFactory.CreateDirect(new Openness.AcquireOptions(), cmd.Value("openness-version")))
            {
                var deadline = DateTime.UtcNow.AddSeconds(cmd.Int("timeout", 900));
                var nudge = DateTime.UtcNow.AddSeconds(90);
                while (true)
                {
                    var portal = Portals(executor).FirstOrDefault(p => p.ProcessId == process.Id);
                    if (portal != null && (project == null || Holds(workspace, portal.ProjectPath))) break;

                    // TIA asks its questions in its own window - a project not closed properly, one
                    // from another version - and nothing here can see them. Seen: a silent wait of
                    // ten minutes on exactly that.
                    if (DateTime.UtcNow > nudge)
                    {
                        output.Warn($"Still waiting for TIA Portal {process.Id}. If its window shows a question, answer it there.");
                        nudge = DateTime.MaxValue;
                    }

                    if (process.HasExited)
                        throw new WireException(WireErrorCodes.PortalUnrecoverable,
                            $"TIA Portal {process.Id} closed before it had {workspace.Name} open.");
                    if (DateTime.UtcNow > deadline)
                        throw new WireException(WireErrorCodes.Internal,
                            $"TIA Portal {process.Id} still does not have {workspace.Name} open; stopped waiting.",
                            "It is still running - look at its window. --timeout <seconds> waits longer.");
                    Thread.Sleep(2000);
                }
            }

            if (project == null)
            {
                using (var executor = ExecutorFactory.CreateDirect(
                           new Openness.AcquireOptions { AttachToProcessId = process.Id }, cmd.Value("openness-version")))
                    Load(executor, workspace, null, archive, cmd, output);
            }

            if (output.AsJson) return Show(workspace, output, cmd);
            output.Line($"{workspace.Name} is open in TIA Portal {process.Id}.");
            output.Detail("Commands in this folder now work on it. Close TIA Portal when you are done; nothing else holds it.");
            return 0;
        }

        private const string PortalProcessName = "Siemens.Automation.Portal";

        /// <summary>
        /// Starts TIA Portal through 'start', so its parent is a cmd that is gone a moment later.
        /// Started directly it would be this command's child, and whatever ends this command's
        /// process tree - an agent stopping a task, taskkill /T - would close it, project and all.
        /// Seen: stopping a 'tia open' that was still waiting took its TIA Portal down with it.
        ///
        /// The cmd is started by ShellExecute, which hands down none of this process's handles.
        /// Inherited, TIA Portal would hold this command's stdout open for as long as it runs, and
        /// anything reading that output - a script's pipe, $(tia open) - would wait for TIA to close.
        /// Seen too: a script stuck on 'tia open | ...' long after tia had exited.
        /// </summary>
        private static Process StartDetached(string exe, string project)
        {
            var before = new HashSet<int>(Process.GetProcessesByName(PortalProcessName).Select(p => p.Id));

            var arguments = "/c start \"\" \"" + exe + "\"" + (project == null ? "" : " \"" + project + "\"");
            using (var cmd = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
                   {
                       UseShellExecute = true,
                       WindowStyle = ProcessWindowStyle.Hidden,
                       CreateNoWindow = true,
                   }))
            {
                cmd?.WaitForExit(30000);
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var started = Process.GetProcessesByName(PortalProcessName).FirstOrDefault(p => !before.Contains(p.Id));
                if (started != null) return started;
                Thread.Sleep(500);
            }

            throw new WireException(WireErrorCodes.Internal, "Started " + exe + ", but no TIA Portal process appeared.");
        }

        /// <summary>
        /// TIA Portal refuses a project whose folder path is too long, with a dialog that says so and
        /// nothing an Openness client can see - well short of Windows' own limit. Seen on V20: a
        /// project folder of 151 characters refused, 141 opened. Worth saying before the wait.
        /// </summary>
        internal static void WarnIfLong(Workspace workspace, Output output)
        {
            var project = workspace.ProjectFile;
            var folder = project == null
                ? Path.Combine(workspace.ProjectDir, workspace.Name)
                : Path.GetDirectoryName(project) ?? project;
            if (folder.Length > 140)
                output.Warn($"The project folder's path is {folder.Length} characters long. TIA Portal refuses long " +
                            "ones (seen: 151 refused, 141 opened); move the workspace somewhere shorter if it does.");
        }

        /// <summary>
        /// The TIA Portal executable for a version: "V20" is looked up as TIAP20 in the Siemens
        /// install keys. With no version the newest installed one is used; a version that is not
        /// installed is refused rather than opened in another - TIA would offer to upgrade it.
        /// </summary>
        private static string PortalExe(string tiaVersion)
        {
            var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var root = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW"))
            {
                foreach (var name in root?.GetSubKeyNames() ?? new string[0])
                {
                    using (var global = root.OpenSubKey(name + @"\Global"))
                    {
                        var path = global?.GetValue("Path") as string;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        var exe = Path.Combine(path, "Bin", "Siemens.Automation.Portal.exe");
                        if (File.Exists(exe)) installed[name] = exe;
                    }
                }
            }

            return PickPortal(installed, tiaVersion);
        }

        internal static string PickPortal(IDictionary<string, string> installed, string tiaVersion)
        {
            var portals = installed
                .Select(kv => new { kv.Value, Version = Regex.Match(kv.Key, @"^TIAP(\d+)(?:_(\d+))?$", RegexOptions.IgnoreCase) })
                .Where(p => p.Version.Success)
                .Select(p => new
                {
                    Exe = p.Value,
                    Name = "V" + p.Version.Groups[1].Value + (p.Version.Groups[2].Success ? "_" + p.Version.Groups[2].Value : ""),
                    Major = int.Parse(p.Version.Groups[1].Value),
                    Minor = p.Version.Groups[2].Success ? int.Parse(p.Version.Groups[2].Value) : 0,
                })
                .OrderByDescending(p => p.Major).ThenByDescending(p => p.Minor)
                .ToList();

            if (portals.Count == 0)
                throw new WireException(WireErrorCodes.NotFound, "No TIA Portal is installed here.");

            if (string.IsNullOrWhiteSpace(tiaVersion)) return portals[0].Exe;

            var wanted = portals.FirstOrDefault(p => string.Equals(p.Name, tiaVersion.Trim(), StringComparison.OrdinalIgnoreCase));
            if (wanted != null) return wanted.Exe;

            throw new WireException(WireErrorCodes.NotFound,
                $"The workspace is TIA Portal {tiaVersion}, which is not installed here.",
                "Installed: " + string.Join(", ", portals.Select(p => p.Name)) +
                $". Change 'tia:' in {Workspace.DirName}/{Workspace.ConfigName} only if the project should be upgraded.");
        }

        /// <summary>A 'session start' carrying over the options that mean the same to it.</summary>
        private static CommandLine Args(CommandLine cmd, params string[] extra)
        {
            var args = new List<string> { "session", "start" };
            args.AddRange(extra);
            if (cmd.Has("upgrade") && extra.Contains("--project")) args.Add("--upgrade");
            if (cmd.Has("openness-version")) args.AddRange(new[] { "--openness-version", cmd.Value("openness-version") });
            if (cmd.Has("idle")) args.AddRange(new[] { "--idle", cmd.Value("idle") });
            return CommandLine.Parse(args.ToArray());
        }

        // ---------------------------------------------------------------- tia workspace

        public static int Show(CommandLine cmd, Output output) => Show(Require(), output, cmd);

        private static int Show(Workspace workspace, Output output, CommandLine cmd = null)
        {
            var project = workspace.ProjectFile;
            var archive = workspace.ArchiveFile;
            var held = SessionRegistry.Running().FirstOrDefault(s => Holds(workspace, s.ProjectPath));
            var open = held == null && cmd != null
                ? ListPortals(cmd).FirstOrDefault(p => Holds(workspace, p.ProjectPath))
                : null;

            if (output.AsJson)
            {
                output.JsonValue(new
                {
                    root = workspace.Root,
                    name = workspace.Name,
                    tia = string.IsNullOrWhiteSpace(workspace.TiaVersion) ? null : workspace.TiaVersion,
                    station = string.IsNullOrWhiteSpace(workspace.Station) ? null : workspace.Station,
                    config = workspace.ConfigPath,
                    source = workspace.SourceDir,
                    archive = workspace.ArchiveDir,
                    projectDir = workspace.ProjectDir,
                    projectFile = project,
                    archiveFile = archive,
                    sessionPortal = held?.PortalProcessId,
                    openInPortal = open?.ProcessId,
                });
                return 0;
            }

            output.Line($"Workspace {workspace.Name}");
            output.Pairs(new[]
            {
                Output.KV("root", workspace.Root),
                Output.KV("tia", string.IsNullOrWhiteSpace(workspace.TiaVersion) ? "-" : workspace.TiaVersion),
                Output.KV("station", string.IsNullOrWhiteSpace(workspace.Station) ? "- (commands need one)" : workspace.Station),
                Output.KV("source", workspace.SourceDir),
                Output.KV("project", project ?? $"none yet in {workspace.ProjectDir}"),
                Output.KV("archive", archive ?? $"none yet in {workspace.ArchiveDir}"),
                Output.KV("open in", held != null ? $"TIA Portal {held.PortalProcessId}, through its session"
                    : open != null ? $"TIA Portal {open.ProcessId} (commands attach to it)"
                    : "nothing - 'tia open' opens it"),
            });
            return 0;
        }
    }
}
