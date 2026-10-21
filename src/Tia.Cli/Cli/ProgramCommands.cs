using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// 'tia export' and 'tia sync': a workspace's program as files in source/&lt;station&gt;/, and
    /// back. Export writes all of it; sync brings back only what git says changed, then compiles -
    /// the compile is what tells you whether the files were right, so it always runs.
    /// </summary>
    internal static class ProgramCommands
    {
        private static Workspace Require() =>
            Workspace.Find() ?? throw new WireException(WireErrorCodes.InvalidRequest,
                $"'tia export' and 'tia sync' work inside a workspace: no {Workspace.DirName} folder here or above.",
                "'tia init' makes one; 'tia block export <device> --all' exports without one.");

        private static string StationDir(Workspace workspace, string device) =>
            Path.GetFullPath(Path.Combine(workspace.SourceDir, device));

        // ---------------------------------------------------------------- tia export

        public static int Export(CommandLine cmd, IExecutor executor, Output output)
        {
            var workspace = Require();
            var device = cmd.Positional(0, "station");
            var directory = StationDir(workspace, device);

            var result = Commands.Call(executor, "program.export", new { device, directory });
            var export = JsonUtil.To<BlockExportAllDto>(result);
            var failed = export.Failed?.Count ?? 0;
            if (output.AsJson) { output.Json(result); return failed > 0 ? 1 : 0; }

            var files = export.Exported ?? new List<ExportResultDto>();
            foreach (var file in files) output.Detail(file.Name);
            foreach (var skipped in export.Skipped ?? new List<BlockProblemDto>())
                output.Warn($"skipped {skipped.Block}: {skipped.Reason}");
            foreach (var problem in export.Failed ?? new List<BlockProblemDto>())
                output.Warn($"failed {problem.Block}: {problem.Reason}");

            var parts = files
                .GroupBy(f => f.Name.Split('/')[0], StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Count()} in {g.Key}");
            output.Line($"{files.Count} file(s) -> {Relative(workspace, export.Directory)}" +
                        (files.Count > 0 ? ": " + string.Join(", ", parts) : "") +
                        (failed > 0 ? $", {failed} failed" : ""));
            return failed > 0 ? 1 : 0;
        }

        // ---------------------------------------------------------------- tia sync

        public static int Sync(CommandLine cmd, IExecutor executor, Output output)
        {
            var workspace = Require();
            var device = cmd.Positional(0, "station");
            var directory = StationDir(workspace, device);

            var changed = new List<string>();
            var deleted = new List<string>();
            var only = cmd.Value("file");
            var paths = !string.IsNullOrWhiteSpace(only)
                ? new List<string> { Path.GetFullPath(only) }
                : ChangedFiles(workspace, directory);

            foreach (var path in paths)
            {
                var relative = Under(directory, path);
                if (relative == null)
                    throw new WireException(WireErrorCodes.InvalidRequest,
                        $"{path} is not in {Relative(workspace, directory)}, where {device}'s program is.");
                (File.Exists(path) ? changed : deleted).Add(relative);
            }

            JsonElement? syncResult = null;
            if (changed.Count + deleted.Count > 0)
                syncResult = Commands.Call(executor, "program.sync", new { device, directory, changed, deleted });
            var sync = syncResult.HasValue ? JsonUtil.To<ProgramSyncDto>(syncResult.Value) : null;
            var failed = sync?.Failed?.Count ?? 0;

            if (output.AsJson)
            {
                JsonElement? compileResult = cmd.Has("no-compile")
                    ? (JsonElement?)null
                    : Commands.Call(executor, "plc.compile", new { device });
                var errors = compileResult.HasValue ? JsonUtil.To<CompileResultDto>(compileResult.Value).ErrorCount : 0;
                output.JsonValue(new { sync = syncResult, compile = compileResult });
                return failed > 0 || errors > 0 ? 1 : 0;
            }

            if (sync == null)
                output.Line($"Nothing changed under {Relative(workspace, directory)}.");
            foreach (var file in sync?.Applied ?? new List<SyncedFileDto>())
                output.Line($"{file.Action,-9} {file.File}");
            foreach (var problem in sync?.Failed ?? new List<BlockProblemDto>())
                output.Warn($"failed {problem.Block}: {problem.Reason}");

            if (cmd.Has("no-compile")) return failed > 0 ? 1 : 0;

            output.Line();
            var compile = Commands.Compile(cmd, executor, output);
            return failed > 0 || compile != 0 ? 1 : 0;
        }

        // ---------------------------------------------------------------- one part on its own

        /// <summary>
        /// Where an --all export goes when --out does not say: the station's folder - under source/
        /// in a workspace, under the current folder elsewhere - and in it the folder TIA's tree
        /// names the part ("Program blocks", "PLC tags", "PLC data types"), which only the session
        /// knows, hence <paramref name="underRoot"/>. The layout is the one 'tia export' writes.
        /// </summary>
        public static string AllDirectory(CommandLine cmd, string device, out bool underRoot)
        {
            var given = cmd.Value("out");
            underRoot = given == null;
            if (given != null) return Path.GetFullPath(given);

            var workspace = cmd.Has("no-workspace") ? null : Workspace.Find();
            return Path.GetFullPath(Path.Combine(workspace?.SourceDir ?? Environment.CurrentDirectory, device));
        }

        public static int PrintAll(JsonElement result, string what, Output output)
        {
            var all = JsonUtil.To<BlockExportAllDto>(result);
            var failed = all.Failed?.Count ?? 0;
            if (output.AsJson) { output.Json(result); return failed > 0 ? 1 : 0; }

            foreach (var file in all.Exported ?? new List<ExportResultDto>())
                output.Detail($"{file.Name} -> {Under(all.Directory, file.FilePath) ?? file.FilePath}");
            foreach (var skipped in all.Skipped ?? new List<BlockProblemDto>())
                output.Warn($"skipped {skipped.Block}: {skipped.Reason}");
            foreach (var problem in all.Failed ?? new List<BlockProblemDto>())
                output.Warn($"failed {problem.Block}: {problem.Reason}");

            output.Line($"{all.Exported?.Count ?? 0} {what} -> {all.Directory}" +
                        (all.Skipped?.Count > 0 ? $", {all.Skipped.Count} skipped" : "") +
                        (failed > 0 ? $", {failed} failed" : ""));
            return failed > 0 ? 1 : 0;
        }

        public static int TableExport(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "station");
            if (cmd.Has("all"))
            {
                NoName(cmd, "tag table");
                var directory = AllDirectory(cmd, device, out var underRoot);
                return PrintAll(Commands.Call(executor, "tag.exportTables", new { device, directory, underRoot }),
                    "tag table(s)", output);
            }

            var table = cmd.Positional(1, "table");
            var target = Path.GetFullPath(cmd.Value("out") ?? table + ".csv");
            return PrintOne(Commands.Call(executor, "tag.exportTable", new { device, table, targetPath = target }), output);
        }

        public static int TableImport(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Commands.Call(executor, "tag.importTable", new
            {
                device = cmd.Positional(0, "station"),
                filePath = Path.GetFullPath(cmd.Positional(1, "file")),
                name = cmd.Value("name"),
                folder = cmd.Value("folder"),
            });
            if (output.AsJson) { output.Json(result); return 0; }

            var name = cmd.Value("name") ?? Path.GetFileNameWithoutExtension(cmd.Positional(1, "file"));
            output.Line($"Tag table {name} now holds the tags in {Path.GetFileName(cmd.Positional(1, "file"))}.");
            output.Detail("Tags the file does not list were deleted; user constants were left as they are.");
            return 0;
        }

        public static int Types(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Commands.Call(executor, "type.list", new { device = cmd.Positional(0, "station") });
            if (output.AsJson) { output.Json(result); return 0; }

            var types = JsonUtil.To<List<TypeDto>>(result) ?? new List<TypeDto>();
            output.Table(
                new[] { "type", "folder", "consistent", "protected" },
                types.Select(t => new[]
                {
                    t.Name,
                    t.Path,
                    t.IsConsistent ? "yes" : "no",
                    t.IsKnowHowProtected ? "yes" : "",
                }).ToList());
            return 0;
        }

        public static int TypeExport(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "station");
            if (cmd.Has("all"))
            {
                NoName(cmd, "data type");
                var directory = AllDirectory(cmd, device, out var underRoot);
                return PrintAll(Commands.Call(executor, "type.exportAll", new { device, directory, underRoot }),
                    "data type(s)", output);
            }

            var type = cmd.Positional(1, "type");
            var target = Path.GetFullPath(cmd.Value("out") ?? type + ".udt");
            return PrintOne(Commands.Call(executor, "type.export", new { device, type, targetPath = target }), output);
        }

        public static int TypeImport(CommandLine cmd, IExecutor executor, Output output)
        {
            var file = cmd.Positional(1, "file");
            var result = Commands.Call(executor, "type.import", new
            {
                device = cmd.Positional(0, "station"),
                filePath = Path.GetFullPath(file),
                folder = cmd.Value("folder"),
            });
            if (output.AsJson) { output.Json(result); return 0; }

            output.Line($"Generated the data type(s) in {Path.GetFileName(file)}.");
            return 0;
        }

        private static int PrintOne(JsonElement result, Output output)
        {
            if (output.AsJson) { output.Json(result); return 0; }
            var export = JsonUtil.To<ExportResultDto>(result);
            output.Line($"{export.Name} -> {export.FilePath} ({export.Bytes:n0} bytes)");
            return 0;
        }

        private static void NoName(CommandLine cmd, string what)
        {
            if (cmd.PositionalOrNull(1) != null)
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"--all exports every {what}; give a device and no {what} name.");
        }

        /// <summary>
        /// The files git reports as changed, added, untracked or gone under the station's folder,
        /// as full paths. Renames give both sides. Whether a path is a change or a deletion is read
        /// off the disk afterwards rather than off git's letters, which have a dozen combinations
        /// and one question behind them: is the file there now?
        /// </summary>
        private static List<string> ChangedFiles(Workspace workspace, string directory)
        {
            var top = Git(workspace.Root, "rev-parse", "--show-toplevel").Trim();
            var status = Git(workspace.Root, "-c", "core.quotePath=false", "status", "--porcelain=v1", "-z",
                "--untracked-files=all", "--", directory);
            return ParseStatus(status).Select(p => Path.GetFullPath(Path.Combine(top, p))).Distinct().ToList();
        }

        /// <summary>Paths out of 'git status --porcelain=v1 -z', a rename's source included.</summary>
        internal static List<string> ParseStatus(string status)
        {
            var paths = new List<string>();
            var entries = (status ?? "").Split('\0');
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Length < 4) continue;
                paths.Add(entry.Substring(3));
                // A rename or copy is followed by the path it came from, as an entry of its own.
                if ((entry[0] == 'R' || entry[0] == 'C') && i + 1 < entries.Length)
                    paths.Add(entries[++i]);
            }
            return paths;
        }

        /// <summary>The path relative to the directory, with '/' between folders; null when outside it.</summary>
        internal static string Under(string directory, string path)
        {
            var root = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).Replace('\\', '/')
                : null;
        }

        private static string Relative(Workspace workspace, string path) =>
            Under(workspace.Root, path) ?? path;

        private static string Git(string workingDirectory, params string[] args)
        {
            var start = new ProcessStartInfo("git", string.Join(" ", args.Select(Quote)))
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            Process git;
            try
            {
                git = Process.Start(start);
            }
            catch (Win32Exception)
            {
                throw new WireException(WireErrorCodes.InvalidRequest,
                    "'tia sync' asks git what changed, and git is not on PATH.",
                    "Install git, or push one file with 'tia sync --file <path>'.");
            }

            using (git)
            {
                var stderr = git.StandardError.ReadToEndAsync();
                var stdout = git.StandardOutput.ReadToEnd();
                git.WaitForExit();
                if (git.ExitCode != 0)
                    throw new WireException(WireErrorCodes.InvalidRequest,
                        "git could not say what changed: " + stderr.Result.Trim(),
                        "The workspace has to be in a git repository - 'git init' there - or push one file " +
                        "with 'tia sync --file <path>'.");
                return stdout;
            }
        }

        private static string Quote(string arg) =>
            arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
    }
}
