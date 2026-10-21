using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using TiaCli.Protocol;

namespace TiaCli.Openness
{
    /// <summary>
    /// Hardware configuration in and out of the project as CAx data - AutomationML, the format
    /// behind TIA Portal's own "Export CAx data". It is the only native way to take a station's
    /// hardware out of one project and put it into another, which is what a project rebuilt from
    /// a repository needs. What it carries and what it drops is exactly what tools/caxtest.ps1
    /// exists to find out.
    /// </summary>
    internal sealed partial class TiaSession
    {
        public CaxResultDto ExportHardware(string targetPath, string deviceName)
        {
            var project = RequireProject();
            var cax = RequireCax(project);
            var file = Fresh(targetPath);

            TransferResult result;
            if (string.IsNullOrEmpty(deviceName))
            {
                result = cax.Export(project, file);
            }
            else
            {
                result = cax.Export(FindDevice(project, deviceName), file);
            }

            file.Refresh();
            var dto = Describe(result, "export", file);
            dto.Device = string.IsNullOrEmpty(deviceName) ? null : deviceName;
            return dto;
        }

        public CaxResultDto ImportHardware(string filePath, string existing)
        {
            var project = RequireProject();
            var cax = RequireCax(project);

            var file = new FileInfo(Path.GetFullPath(filePath));
            if (!file.Exists)
                throw new SessionException(WireErrorCodes.NotFound, $"CAx file not found: {file.FullName}");

            return Describe(cax.Import(file, ImportOption(existing)), "import", file);
        }

        /// <summary>
        /// What happens to a device the project already has under the same name. Retain is the
        /// default because it is the one that cannot destroy anything.
        /// </summary>
        internal static CaxImportOptions ImportOption(string existing)
        {
            switch ((existing ?? "retain").Trim().ToLowerInvariant())
            {
                case "retain": return CaxImportOptions.RetainTiaDevice;
                case "overwrite": return CaxImportOptions.OverwriteTiaDevice;
                case "park": return CaxImportOptions.MoveToParkingLot;
                default:
                    throw new SessionException(WireErrorCodes.InvalidRequest,
                        $"--existing must be retain, overwrite or park, not '{existing}'.",
                        "retain keeps the project's device, overwrite replaces it, park moves it to the parking lot.");
            }
        }

        private static CaxProvider RequireCax(Project project)
        {
            var cax = project.GetService<CaxProvider>();
            if (cax == null)
                throw new SessionException(WireErrorCodes.OpennessError,
                    "This TIA Portal offers no CAx export or import.",
                    "CAx data exchange is part of TIA Portal's Openness; check the installed version and its options.");
            return cax;
        }

        private static CaxResultDto Describe(TransferResult result, string operation, FileInfo file)
        {
            var dto = new CaxResultDto
            {
                Operation = operation,
                FilePath = file.FullName,
                Bytes = file.Exists ? file.Length : 0,
                State = result?.State.ToString() ?? "Unknown",
                ErrorCount = result?.ErrorCount ?? 0,
                WarningCount = result?.WarningCount ?? 0,
                Messages = new List<CaxMessageDto>(),
            };
            if (result != null) Flatten(result.Messages, 0, dto.Messages);
            return dto;
        }

        // Nested like the compiler's: a group repeats the worst state below it, and the leaves say what happened.
        private static void Flatten(TransferResultMessageComposition messages, int depth, List<CaxMessageDto> sink)
        {
            if (messages == null) return;
            foreach (TransferResultMessage message in messages)
            {
                sink.Add(new CaxMessageDto
                {
                    State = message.State.ToString(),
                    Message = message.Message,
                    Depth = depth,
                });
                Flatten(message.Messages, depth + 1, sink);
            }
        }
    }
}
