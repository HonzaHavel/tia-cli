using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaCli.Protocol;

namespace TiaCli.Daemon
{
    /// <summary>
    /// One daemon's advertisement on disk, in a file of its own named after its process id. It is a
    /// hint, not a lock: the pipe is the real test of whether a daemon is alive, and a stale file
    /// left by a killed process is expected and handled.
    /// </summary>
    internal sealed class DaemonState
    {
        [JsonPropertyName("pipe")] public string Pipe { get; set; }
        [JsonPropertyName("processId")] public int ProcessId { get; set; }
        [JsonPropertyName("startedAt")] public string StartedAt { get; set; }

        /// <summary>
        /// The TIA Portal this daemon holds, zero until it has one. It is what '--attach &lt;pid&gt;'
        /// and TIA_PORTAL are matched against, so a command finds its session without asking
        /// every daemon in turn.
        /// </summary>
        [JsonPropertyName("portalProcessId")] public int PortalProcessId { get; set; }

        /// <summary>
        /// The project file the session has open, null when none. A command run inside a workspace
        /// finds its session by this, so a TIA Portal restarted under a new pid is still found.
        /// </summary>
        [JsonPropertyName("projectPath")] public string ProjectPath { get; set; }

        public static void Write(DaemonState state)
        {
            File.WriteAllText(DaemonPaths.StateFileFor(state.ProcessId),
                JsonSerializer.Serialize(state, WireJson.Pretty), new UTF8Encoding(false));
        }

        public static DaemonState Read(int daemonProcessId) => ReadFile(DaemonPaths.StateFileFor(daemonProcessId));

        /// <summary>Every advertisement on disk, live or not. Unreadable files are skipped.</summary>
        public static List<DaemonState> ReadAll()
        {
            var states = new List<DaemonState>();
            try
            {
                foreach (var file in System.IO.Directory.GetFiles(DaemonPaths.Directory, DaemonPaths.StateFilePattern))
                {
                    var state = ReadFile(file);
                    if (state != null && state.ProcessId > 0) states.Add(state);
                }
            }
            catch
            {
                // An unreadable directory means no sessions can be found, not a failed command.
            }
            return states;
        }

        private static DaemonState ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<DaemonState>(File.ReadAllText(path), WireJson.Options);
            }
            catch
            {
                return null;
            }
        }

        public static void Delete(int daemonProcessId)
        {
            var path = DaemonPaths.StateFileFor(daemonProcessId);
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        public bool IsProcessAlive()
        {
            try
            {
                using (var process = Process.GetProcessById(ProcessId)) return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }
}
