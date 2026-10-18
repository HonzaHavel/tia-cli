using TiaCli.Cli;
using TiaCli.Openness;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// What a repository of exports needs from tia: the same block exported twice is the same file,
    /// and a compile error says which block it is in. tools/gittest.ps1 found both missing.
    /// </summary>
    public class VersionControlTests
    {
        private const string Export =
            "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Document>\r\n" +
            "  <Engineering version=\"V20\" />\r\n" +
            "  <DocumentInfo>\r\n" +
            "    <Created>2026-10-18T17:51:22.3040142Z</Created>\r\n" +
            "    <ExportSetting>None</ExportSetting>\r\n" +
            "  </DocumentInfo>\r\n" +
            "  <SW.Blocks.OB ID=\"0\" />\r\n" +
            "</Document>";

        [Fact]
        public void TheExportStampIsDropped()
        {
            var stable = TiaSession.WithoutCreatedStamp(Export);

            Assert.DoesNotContain("<Created>", stable);
            Assert.Contains("  <DocumentInfo>\r\n    <ExportSetting>None</ExportSetting>\r\n", stable);
        }

        [Fact]
        public void TwoExportsMinutesApartAreTheSameFile()
        {
            var later = Export.Replace("17:51:22.3040142Z", "17:52:04.2412434Z");
            Assert.Equal(TiaSession.WithoutCreatedStamp(Export), TiaSession.WithoutCreatedStamp(later));
        }

        [Fact]
        public void ACreatedElementOutsideDocumentInfoIsTheBlocksAndStays()
        {
            var xml = "<Document>\n  <DocumentInfo>\n  </DocumentInfo>\n  <Created>kept</Created>\n</Document>";
            Assert.Equal(xml, TiaSession.WithoutCreatedStamp(xml));
        }

        [Fact]
        public void LfOnlyExportsAreHandledToo()
        {
            var lf = Export.Replace("\r\n", "\n");
            Assert.DoesNotContain("<Created>", TiaSession.WithoutCreatedStamp(lf));
        }

        [Theory]
        [InlineData(null, "Program blocks", "Program blocks")]
        [InlineData("Program blocks", "Twice", "Program blocks/Twice")]
        [InlineData("Program blocks/Twice", "2", "Program blocks/Twice/2")]
        [InlineData("Program blocks/Twice", "", "Program blocks/Twice")]
        [InlineData("Program blocks/Twice", null, "Program blocks/Twice")]
        public void ACompileMessageCarriesTheBlockAboveIt(string parent, string path, string expected)
        {
            Assert.Equal(expected, TiaSession.JoinLocation(parent, path));
        }

        [Fact]
        public void ALongLocationKeepsItsEnd()
        {
            var location = "Program blocks/Conveyors/Line 3/Infeed/Drives/Motor/12";
            var shown = Output.TruncateStart(location, 20);

            Assert.Equal(20, shown.Length);
            Assert.EndsWith("Drives/Motor/12", shown);
            Assert.Equal("Program blocks/Twice", Output.TruncateStart("Program blocks/Twice", 40));
        }
    }
}
