using System;
using System.Collections.Generic;
using System.Linq;
using TiaCli.Protocol;

namespace TiaCli.Daemon
{
    /// <summary>
    /// Finds this user's running sessions - one daemon per TIA Portal - and picks the one a command
    /// is meant for. A session is named by the process id of the portal it holds, because that is
    /// the number 'tia portals' and Task Manager show; the daemon's own pid is an implementation
    /// detail nobody should have to know.
    /// </summary>
    internal static class SessionRegistry
    {
        /// <summary>
        /// The environment variable that points every command in a shell at one portal, so a script
        /// working on one of several sessions does not repeat --attach on every line.
        /// </summary>
        public const string PortalVariable = "TIA_PORTAL";

        /// <summary>
        /// Every session that answers its pipe. Advertisements of daemons that are gone are deleted
        /// on the way; a daemon that is alive but does not answer is left alone and not listed.
        /// </summary>
        public static List<DaemonState> Running()
        {
            var running = new List<DaemonState>();
            foreach (var state in DaemonState.ReadAll())
            {
                if (!state.IsProcessAlive())
                {
                    DaemonState.Delete(state.ProcessId);
                    continue;
                }
                if (DaemonClient.IsAvailable(state.Pipe)) running.Add(state);
            }
            return running.OrderBy(s => s.StartedAt, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The session a command goes to. With a portal named, the session holding it, or null when
        /// none does - the command then attaches to that portal by itself. With none named, the only
        /// session, null when there is none, and a refusal when there are several: guessing would
        /// send a change to somebody else's project.
        /// </summary>
        public static DaemonState Resolve(IList<DaemonState> running, int? portalProcessId)
        {
            if (portalProcessId.HasValue)
                return running.FirstOrDefault(s => s.PortalProcessId == portalProcessId.Value);

            if (running.Count == 0) return null;
            if (running.Count == 1) return running[0];

            throw new WireException(WireErrorCodes.Ambiguous,
                $"{running.Count} tia sessions are running, holding TIA Portal " + Describe(running) + ".",
                $"Name the portal with --attach <pid>, or set {PortalVariable}=<pid> for this shell. " +
                "'tia session list' shows them.");
        }

        /// <summary>The portal named by TIA_PORTAL, or null when it is unset or not a number.</summary>
        public static int? PortalFromEnvironment()
        {
            var value = Environment.GetEnvironmentVariable(PortalVariable);
            return int.TryParse(value?.Trim(), out var pid) && pid > 0 ? pid : (int?)null;
        }

        internal static string Describe(IEnumerable<DaemonState> sessions) =>
            string.Join(", ", sessions.Select(s => s.PortalProcessId > 0
                ? s.PortalProcessId.ToString()
                : $"(none yet, daemon {s.ProcessId})"));
    }
}
