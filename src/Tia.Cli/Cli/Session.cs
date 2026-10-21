using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaCli.Daemon;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// The session verbs. These are the ones that exist because a CLI process is short-lived and an
    /// Openness connection is not: a portal this tool starts belongs to the process that started it,
    /// so keeping one across commands means keeping a process around to hold it.
    /// </summary>
    internal static class Session
    {
        public static int Start(CommandLine cmd, Output output, Action<IExecutor> afterStart = null)
        {
            // A session that was told to stop deletes its state file at once, so one on its way out is
            // never found here and never joined - 'session stop; session start' on one line once
            // reported a session that was gone a moment later.
            var running = SessionRegistry.Running();
            var attach = cmd.NullableInt("attach");

            if (attach.HasValue)
            {
                var holder = running.FirstOrDefault(s => s.PortalProcessId == attach.Value);
                if (holder != null)
                {
                    output.Warn($"A tia session already holds TIA Portal {attach.Value}.");
                    return Status(cmd, new DaemonExecutor(holder.Pipe), output);
                }
            }
            else if (!cmd.Has("new") && running.Count > 0)
            {
                // Joining "whatever is running" next to an existing session could put two sessions on
                // one portal. A second session has to say which portal, or ask for a new one.
                output.Warn("A tia session is already running. To start another, name its TIA Portal " +
                            "with --attach <pid>, or use --new for a portal of its own.");
                return running.Count == 1
                    ? Status(cmd, new DaemonExecutor(running[0].Pipe), output)
                    : List(output);
            }

            var serveArgs = new List<string>();
            if (attach.HasValue) serveArgs.Add("--attach " + attach.Value);
            if (cmd.Has("new")) serveArgs.Add("--new");
            if (cmd.Has("headless")) serveArgs.Add("--headless");
            if (cmd.Has("idle")) serveArgs.Add("--idle " + cmd.Int("idle", 0));
            if (cmd.Has("openness-version")) serveArgs.Add("--openness-version " + cmd.Value("openness-version"));

            // Worth saying before the daemon goes into the background: the dialog it may block on
            // belongs to TIA Portal, and nothing in this terminal would explain the wait.
            if (!Openness.OpennessWhitelist.IsApproved(DaemonPaths.ExecutablePath))
                output.Warn(Openness.OpennessWhitelist.PendingApprovalNotice);

            var started = DaemonClient.Start(serveArgs.ToArray());
            var executor = new DaemonExecutor(started.Pipe);

            output.Detail(cmd.Has("new") || cmd.Has("headless")
                ? "Starting TIA Portal - the first launch takes a minute or two."
                : "Looking for a TIA Portal to join, and starting one if there is none.");

            try
            {
                Commands.Call(executor, "session.ensure");
            }
            catch
            {
                // A daemon with no session is a trap: the next command would fail the same way with
                // no explanation of where the stale process came from.
                try { Commands.Call(executor, "daemon.stop"); } catch { }
                throw;
            }

            var project = cmd.Value("project");
            if (!string.IsNullOrEmpty(project))
            {
                output.Detail("Opening " + project + " - this takes minutes, not seconds.");
                Commands.Call(executor, "project.open", new { path = Path.GetFullPath(project), upgrade = cmd.Has("upgrade") });
            }

            afterStart?.Invoke(executor);
            return Status(cmd, executor, output);
        }

        /// <summary>
        /// Stops the session for the named portal, the only one running, or with --all every one.
        /// Several running and none named is refused: stopping a session can close a TIA Portal.
        /// </summary>
        public static int Stop(CommandLine cmd, Output output)
        {
            var running = SessionRegistry.Running();
            List<DaemonState> targets;

            if (cmd.Has("all"))
            {
                targets = running;
            }
            else
            {
                var portal = Program.TargetPortal(cmd, strict: false);
                var target = SessionRegistry.Resolve(running, portal);
                if (target == null && portal.HasValue)
                    throw new WireException(WireErrorCodes.NotFound,
                        $"No tia session holds TIA Portal {portal.Value}.",
                        "'tia session list' shows the sessions that are running.");
                targets = target == null ? new List<DaemonState>() : new List<DaemonState> { target };
            }

            if (targets.Count == 0)
            {
                if (output.AsJson) output.JsonValue(new { stopped = 0 });
                else output.Line("No tia session is running.");
                return 0;
            }

            foreach (var target in targets) StopOne(target, output);

            if (output.AsJson) output.JsonValue(new { stopped = targets.Count });
            return 0;
        }

        private static void StopOne(DaemonState target, Output output)
        {
            var executor = new DaemonExecutor(target.Pipe);

            // Ask before stopping, so the message can say whether TIA Portal is about to close.
            SessionStateDto session = null;
            try
            {
                session = JsonUtil.To<DaemonStatusDto>(Commands.Call(executor, "daemon.status"))?.Session;
            }
            catch { }

            Commands.Call(executor, "daemon.stop");

            // Stopped means gone: the daemon answers for as long as closing TIA Portal takes, and a
            // script's next line should not have to guess when that is over.
            if (!DaemonClient.WaitUntilGone(target.Pipe))
                output.Warn("The session is still closing TIA Portal. Give it a moment before starting another.");

            if (output.AsJson) return;

            if (session != null && session.Connected)
            {
                output.Line(session.OwnsPortal
                    ? $"Session stopped. TIA Portal (pid {session.ProcessId}) was started by this tool and is closing."
                    : $"Session stopped. TIA Portal (pid {session.ProcessId}) was already running and stays open.");
            }
            else
            {
                output.Line("Session stopped.");
            }
        }

        /// <summary>Every running session and the portal it holds.</summary>
        public static int List(Output output)
        {
            var statuses = new List<DaemonStatusDto>();
            foreach (var state in SessionRegistry.Running())
            {
                try
                {
                    statuses.Add(JsonUtil.To<DaemonStatusDto>(
                        Commands.Call(new DaemonExecutor(state.Pipe), "daemon.status")));
                }
                catch
                {
                    // Gone between being found and being asked; it is no longer running.
                }
            }

            if (output.AsJson) { output.JsonValue(statuses); return 0; }

            if (statuses.Count == 0)
            {
                output.Line("No tia session is running.");
                return 0;
            }

            output.Table(
                new[] { "portal", "mode", "project", "origin", "daemon" },
                statuses.Select(s => new[]
                {
                    s.Session != null && s.Session.Connected ? s.Session.ProcessId.ToString() : "-",
                    s.Session == null || !s.Session.Connected ? "-"
                        : s.Session.WithUserInterface ? "ui" : "headless",
                    s.Session?.Project?.Name ?? "-",
                    s.Session == null || !s.Session.Connected ? "-"
                        : s.Session.Origin == "created" ? "started by tia" : "already running",
                    s.ProcessId.ToString(),
                }).ToList());
            return 0;
        }

        /// <summary>
        /// 'session status' on its own: the session for the named portal, the only one, or - when
        /// several are running and none is named - the list of them, which is the useful answer.
        /// </summary>
        public static int StatusCommand(CommandLine cmd, Output output)
        {
            var running = SessionRegistry.Running();
            var portal = Program.TargetPortal(cmd, strict: false);

            if (!cmd.Has("no-daemon"))
            {
                if (!portal.HasValue && running.Count > 1)
                {
                    output.Line($"{running.Count} tia sessions are running. Name one with --attach <pid>.");
                    output.Line();
                    return List(output);
                }

                var session = SessionRegistry.Resolve(running, portal);
                if (session != null) return Status(cmd, new DaemonExecutor(session.Pipe), output);

                if (portal.HasValue && running.Count > 0 && !output.AsJson)
                {
                    output.Line($"No tia session holds TIA Portal {portal.Value}; commands naming it attach on their own.");
                    output.Line();
                    return List(output);
                }
            }

            using (var executor = Program.CreateExecutor(cmd, output))
                return Status(cmd, executor, output);
        }

        public static int Status(CommandLine cmd, IExecutor executor, Output output)
        {
            if (executor.Kind != "daemon")
            {
                // No daemon: the useful answer is what there is to attach to.
                if (output.AsJson)
                {
                    output.JsonValue(new DaemonStatusDto { Running = false, LogPath = DaemonPaths.LogFile });
                    return 0;
                }

                output.Line("No tia session is running (each command attaches on its own).");
                output.Line();
                output.Line("TIA Portal instances you can use:");
                return Commands.Execute(CommandLine.Parse(new[] { "portals" }), executor, output);
            }

            var result = Commands.Call(executor, "daemon.status");
            if (output.AsJson) { output.Json(result); return 0; }

            var status = JsonUtil.To<DaemonStatusDto>(result);
            var session = status.Session;

            if (status.Stopping)
            {
                // Nothing below would still be true by the time it was read.
                output.Line("Session stopping - it was asked to stop and is closing now.");
                return 0;
            }

            output.Line("Session running.");
            output.Pairs(new[]
            {
                Output.KV("daemon pid", status.ProcessId.ToString()),
                Output.KV("pipe", status.Pipe),
                Output.KV("started", status.StartedAt),
                Output.KV("log", status.LogPath),
            });

            output.Line();
            if (session == null || !session.Connected)
            {
                output.Line("No TIA Portal is attached yet - the next command will acquire one.");
                return 0;
            }

            output.Line("TIA Portal");
            output.Pairs(new[]
            {
                Output.KV("pid", session.ProcessId.ToString()),
                Output.KV("mode", session.WithUserInterface ? "with user interface" : "headless"),
                Output.KV("origin", session.Origin == "created"
                    ? "started by tia (closes on 'session stop')"
                    : "already running (left open on 'session stop')"),
                Output.KV("openness", session.OpennessVersion),
            });

            if (status.PortalExited)
            {
                output.Line();
                output.Warn("That process is gone - TIA Portal exited since the last command, and " +
                            "everything above describes the session as it was. Run 'tia session stop' " +
                            "and start a new one.");
                return 0;
            }

            if (session.Project != null)
            {
                output.Line();
                output.Line("Project");
                output.Pairs(new[]
                {
                    Output.KV("name", session.Project.Name),
                    Output.KV("path", session.Project.Path),
                    Output.KV("devices", session.Project.DeviceCount.ToString()),
                    Output.KV("unsaved changes", session.Project.IsModified ? "yes" : "no"),
                });
            }
            else
            {
                output.Line("No project open.");
            }

            return 0;
        }
    }
}
