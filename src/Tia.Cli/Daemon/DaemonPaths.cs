using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaCli.Daemon
{
    /// <summary>
    /// Where each daemon keeps its pipe, its state file and its log. A Windows user can run several
    /// daemons, one per TIA Portal, each on a pipe of its own named after the daemon's process id.
    /// Daemons are never shared between users: a TIA Portal belongs to a desktop session.
    /// </summary>
    internal static class DaemonPaths
    {
        /// <summary>What every pipe of this user's daemons starts with.</summary>
        public static string PipePrefix
        {
            get
            {
                var user = new string(Environment.UserName
                    .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    .ToArray());
                if (user.Length == 0) user = "user";
                return "tia-cli-" + user.ToLowerInvariant();
            }
        }

        public static string PipeNameFor(int daemonProcessId) => PipePrefix + "-" + daemonProcessId;

        public static string Directory
        {
            get
            {
                // TIA_CLI_HOME redirects everything (state file, log) elsewhere - tests use it to
                // keep clear of a real daemon's advertisement.
                var dir = Environment.GetEnvironmentVariable("TIA_CLI_HOME");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "tia-cli");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public const string StateFilePattern = "session-*.json";

        public static string StateFileFor(int daemonProcessId) =>
            Path.Combine(Directory, "session-" + daemonProcessId + ".json");

        public static string LogFile => Path.Combine(Directory, "daemon.log");

        /// <summary>The running tia.exe, used to spawn the daemon as a copy of ourselves.</summary>
        public static string ExecutablePath =>
            System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Logging must never be the reason a command fails.
            }
        }

        /// <summary>Keeps the log from growing without bound across many daemon lifetimes.</summary>
        public static void TrimLog()
        {
            try
            {
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > 512 * 1024) file.Delete();
            }
            catch { }
        }
    }
}
