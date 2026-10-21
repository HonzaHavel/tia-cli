using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// A folder that holds one TIA project, marked by a .tia folder at its root the way git marks a
    /// repository with .git. Found by walking up from the current folder, so every command run
    /// anywhere inside it knows its project.
    ///
    /// .tia/config is the one file in .tia that is committed - a handful of flat "key: value" lines
    /// meant to be read and edited by people. .tia/.gitignore keeps everything else in there out of
    /// git: the working project and the archive, which are far too big for it. The program as files
    /// lives next to .tia, in source/, and is committed.
    /// </summary>
    internal sealed class Workspace
    {
        public const string DirName = ".tia";
        public const string ConfigName = "config";

        public string Root { get; private set; }
        public string Name { get; set; }
        /// <summary>The TIA Portal version the project is in, "V20" and the like. Empty when unknown.</summary>
        public string TiaVersion { get; set; }
        /// <summary>The station a command uses when it is given none. Empty when not set.</summary>
        public string Station { get; set; }
        public string Source { get; set; } = "source";

        public string TiaDir => Path.Combine(Root, DirName);
        public string ConfigPath => Path.Combine(TiaDir, ConfigName);
        public string SourceDir => Path.Combine(Root, Source);
        public string ArchiveDir => Path.Combine(TiaDir, "archive");
        public string ProjectDir => Path.Combine(TiaDir, "project");

        private static readonly Regex ProjectExtension = new Regex(@"^\.ap\d+(_\d+)?$", RegexOptions.IgnoreCase);
        private static readonly Regex ArchiveExtension = new Regex(@"^\.zap\d+(_\d+)?$", RegexOptions.IgnoreCase);

        public static bool IsProjectFile(string path) => ProjectExtension.IsMatch(Path.GetExtension(path) ?? "");
        public static bool IsArchiveFile(string path) => ArchiveExtension.IsMatch(Path.GetExtension(path) ?? "");

        /// <summary>
        /// The working project: the .apNN file one folder down in .tia/project, where TIA puts a
        /// project called Line3 (.tia/project/Line3/Line3.ap20). Null when there is none yet.
        /// </summary>
        public string ProjectFile
        {
            get
            {
                if (!Directory.Exists(ProjectDir)) return null;
                var candidates = Directory.GetDirectories(ProjectDir)
                    .SelectMany(d => Directory.GetFiles(d))
                    .Where(IsProjectFile)
                    .ToList();
                // The one named after the workspace wins; otherwise any, so a project renamed in TIA
                // is still found.
                return candidates.FirstOrDefault(f =>
                           string.Equals(Path.GetFileNameWithoutExtension(f), Name, StringComparison.OrdinalIgnoreCase))
                       ?? candidates.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
        }

        /// <summary>The newest .zapNN in .tia/archive, or null.</summary>
        public string ArchiveFile
        {
            get
            {
                if (!Directory.Exists(ArchiveDir)) return null;
                return Directory.GetFiles(ArchiveDir)
                    .Where(IsArchiveFile)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }

        /// <summary>The workspace containing this folder, or null. TIA_WORKSPACE overrides the search.</summary>
        public static Workspace Find(string startDirectory = null)
        {
            var overridden = Environment.GetEnvironmentVariable("TIA_WORKSPACE");
            var dir = new DirectoryInfo(string.IsNullOrWhiteSpace(overridden)
                ? (startDirectory ?? Environment.CurrentDirectory)
                : overridden);

            for (; dir != null; dir = dir.Parent)
            {
                var config = Path.Combine(dir.FullName, DirName, ConfigName);
                if (File.Exists(config)) return Load(dir.FullName);
            }
            return null;
        }

        public static Workspace Load(string root)
        {
            root = Path.GetFullPath(root);
            var workspace = Parse(File.ReadAllText(Path.Combine(root, DirName, ConfigName)));
            workspace.Root = root;
            if (string.IsNullOrWhiteSpace(workspace.Name)) workspace.Name = Path.GetFileName(root.TrimEnd('\\', '/'));
            return workspace;
        }

        internal static Workspace Parse(string text)
        {
            var workspace = new Workspace();
            var lineNumber = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                lineNumber++;
                var line = raw.TrimEnd('\r');
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                if (line.Trim().Length == 0) continue;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    throw new WireException(WireErrorCodes.InvalidRequest,
                        $"{DirName}/{ConfigName} line {lineNumber} is not 'key: value': {raw.Trim()}");

                var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                var value = Unquote(line.Substring(colon + 1).Trim());

                switch (key)
                {
                    case "name": workspace.Name = value; break;
                    case "tia": workspace.TiaVersion = value; break;
                    case "station": workspace.Station = value; break;
                    case "source": workspace.Source = Folder(value, key); break;
                    // Unknown keys are kept for later versions of this file, not refused.
                }
            }
            return workspace;
        }

        private static string Unquote(string value) =>
            value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0]
                ? value.Substring(1, value.Length - 2)
                : value;

        // The program lives inside the workspace; anywhere else would put files the workspace
        // cannot vouch for under its name.
        private static string Folder(string value, string key)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Split('/', '\\').Contains(".."))
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"'{key}' in {DirName}/{ConfigName} must be a folder inside the workspace, not '{value}'.");
            return value;
        }

        public string Render()
        {
            var text = new StringBuilder();
            text.AppendLine("# tia-cli workspace. tia finds this file by walking up from the current folder,");
            text.AppendLine("# so every command run inside this folder works on this project. Commit it;");
            text.AppendLine("# the rest of .tia/ - the working project and the archive - stays out of git.");
            text.AppendLine("name: " + Name);
            if (!string.IsNullOrWhiteSpace(TiaVersion)) text.AppendLine("tia: " + TiaVersion);
            text.AppendLine();
            text.AppendLine("# the station block, tag and compile commands use when none is given");
            text.AppendLine(string.IsNullOrWhiteSpace(Station) ? "# station: PLC_1" : "station: " + Station);
            text.AppendLine();
            text.AppendLine("# the program as files, committed");
            text.AppendLine("source: " + Source);
            return text.ToString();
        }

        /// <summary>Everything in .tia stays out of git except the config and this file itself.</summary>
        internal const string IgnoreText =
            "# tia-cli: the working project and the archive are far too big for git.\n" +
            "*\n" +
            "!" + ConfigName + "\n" +
            "!.gitignore\n";

        /// <summary>"V20" from ".ap20", "V15_1" from ".zap15_1"; empty for anything else.</summary>
        internal static string VersionOf(string path)
        {
            var match = Regex.Match(Path.GetExtension(path) ?? "", @"^\.z?ap(\d+(_\d+)?)$", RegexOptions.IgnoreCase);
            return match.Success ? "V" + match.Groups[1].Value : "";
        }

        /// <summary>
        /// Makes a workspace in <paramref name="root"/>: .tia with its config, .gitignore, project
        /// and archive folders, source/ beside it, and - with <paramref name="from"/> - an existing
        /// project or archive copied into place. The original is copied, never moved: it may be
        /// somebody's only copy.
        /// </summary>
        public static Workspace Init(string root, string name, string from, string station = null)
        {
            root = Path.GetFullPath(root);
            if (File.Exists(Path.Combine(root, DirName, ConfigName)))
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"{Path.Combine(root, DirName, ConfigName)} already exists: this is a workspace already.");

            var enclosing = Find(Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root);
            if (enclosing != null)
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"{root} is inside the workspace at {enclosing.Root}.",
                    "A workspace inside another would make every command inside it ambiguous.");

            string fromPath = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                fromPath = Path.GetFullPath(from);
                if (!File.Exists(fromPath))
                    throw new WireException(WireErrorCodes.NotFound, "Not found: " + fromPath);
                if (!IsProjectFile(fromPath) && !IsArchiveFile(fromPath))
                    throw new WireException(WireErrorCodes.InvalidRequest,
                        $"--from takes a project (.apNN) or an archive (.zapNN), not {Path.GetFileName(fromPath)}.");
            }

            var workspace = new Workspace
            {
                Root = root,
                Name = !string.IsNullOrWhiteSpace(name) ? name
                    : fromPath != null ? Path.GetFileNameWithoutExtension(fromPath)
                    : Path.GetFileName(root.TrimEnd('\\', '/')),
                TiaVersion = fromPath != null ? VersionOf(fromPath) : "",
                Station = station ?? "",
            };

            Directory.CreateDirectory(workspace.SourceDir);
            Directory.CreateDirectory(workspace.ArchiveDir);
            Directory.CreateDirectory(workspace.ProjectDir);

            if (fromPath != null && IsArchiveFile(fromPath))
            {
                File.Copy(fromPath, Path.Combine(workspace.ArchiveDir, Path.GetFileName(fromPath)), overwrite: false);
            }
            else if (fromPath != null)
            {
                // A project is its whole folder, not the .apNN file inside it.
                var folder = Path.GetDirectoryName(fromPath);
                CopyDirectory(folder, Path.Combine(workspace.ProjectDir, Path.GetFileName(folder)));
            }

            File.WriteAllText(Path.Combine(workspace.TiaDir, ".gitignore"), IgnoreText, new UTF8Encoding(false));
            // Written last: the config is what makes this a workspace, so a copy that failed above
            // does not leave behind something every command inside would take for one.
            File.WriteAllText(workspace.ConfigPath, workspace.Render(), new UTF8Encoding(false));
            return workspace;
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
            foreach (var dir in Directory.GetDirectories(from))
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        /// <summary>Two paths to the same file, whatever their case or separators.</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
