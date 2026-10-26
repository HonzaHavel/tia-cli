using System.Collections.Generic;
using System.Linq;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// The CSV carries what TIA's XML does - comments in every language, user constants - in
    /// columns that appear only when a table has them, so a table without them reads as before.
    /// </summary>
    public class TagCsvColumnsTests
    {
        private static TagRow Tag(string name, string comment = null, Dictionary<string, string> comments = null) =>
            new TagRow { Name = name, DataType = "Bool", Address = "%I0.0", Comment = comment, Comments = comments };

        private static Dictionary<string, string> In(params string[] cultureAndText)
        {
            var comments = new Dictionary<string, string>();
            for (var i = 0; i < cultureAndText.Length; i += 2) comments[cultureAndText[i]] = cultureAndText[i + 1];
            return comments;
        }

        [Fact]
        public void ATableWithoutLanguagesOrConstantsKeepsTheSevenColumns()
        {
            var header = TagCsv.Write(new[] { Tag("Start", "Start button") }).Split('\r')[0];
            Assert.Equal("Name,Data Type,Logical Address,Comment,Hmi Visible,Hmi Accessible,Hmi Writeable", header);
        }

        [Fact]
        public void EachOtherLanguageWithTextGetsAColumnInCultureOrder()
        {
            var csv = TagCsv.Write(new[]
            {
                Tag("Start", "Start button", In("fr-FR", "Bouton", "de-DE", "Starttaste", "es-ES", "")),
                Tag("Stop", "Stop button", In("de-DE", "Stopptaste")),
            });
            var lines = csv.Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("Name,Data Type,Logical Address,Comment,Comment de-DE,Comment fr-FR,Hmi Visible,Hmi Accessible,Hmi Writeable", lines[0]);
            Assert.Equal("Start,Bool,%I0.0,Start button,Starttaste,Bouton,True,True,True", lines[1]);
            Assert.Equal("Stop,Bool,%I0.0,Stop button,Stopptaste,,True,True,True", lines[2]);
        }

        [Fact]
        public void ConstantsAreRowsWithAValueAndNoAddressOrHmiFlags()
        {
            var csv = TagCsv.Write(new[]
            {
                Tag("Start"),
                new TagRow { Name = "MaxSpeed", DataType = "Int", Value = "1500", Comment = "rpm" },
            });
            var lines = csv.Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("Name,Data Type,Logical Address,Value,Comment,Hmi Visible,Hmi Accessible,Hmi Writeable", lines[0]);
            Assert.Equal("Start,Bool,%I0.0,,,True,True,True", lines[1]);
            Assert.Equal("MaxSpeed,Int,,1500,rpm,,,", lines[2]);
        }

        [Fact]
        public void EverythingComesBackAsItWent()
        {
            var rows = new[]
            {
                Tag("Start", "Start button", In("de-DE", "Starttaste")),
                new TagRow { Name = "MaxSpeed", DataType = "Int", Value = "1500", Comments = In("de-DE", "U/min") },
            };
            var table = TagCsv.ReadTable(TagCsv.Write(rows));

            Assert.True(table.HasConstants);
            Assert.Equal(new[] { "de-DE" }, table.Languages);
            Assert.False(table.Rows[0].IsConstant);
            Assert.Equal("Starttaste", table.Rows[0].Comments["de-DE"]);
            Assert.True(table.Rows[1].IsConstant);
            Assert.Equal("1500", table.Rows[1].Value);
            Assert.Equal("U/min", table.Rows[1].Comments["de-DE"]);
            Assert.Null(table.Rows[1].Address);
        }

        [Fact]
        public void AnEmptyLanguageCellClearsThatComment()
        {
            var table = TagCsv.ReadTable("Name,Data Type,Comment de-DE\r\nStart,Bool,\r\n");
            Assert.Equal("", table.Rows[0].Comments["de-DE"]);
        }

        [Fact]
        public void AFileWithoutTheColumnsLeavesConstantsAndLanguagesAlone()
        {
            var table = TagCsv.ReadTable("Name,Data Type,Comment\r\nStart,Bool,go\r\n");
            Assert.False(table.HasConstants);
            Assert.Empty(table.Languages);
            Assert.Null(table.Rows[0].Comments);
        }

        [Fact]
        public void AValueColumnWithNoConstantsLeftDeletesThemAll()
        {
            // The last constant deleted from the file: the column stays, so the import knows.
            var table = TagCsv.ReadTable("Name,Data Type,Logical Address,Value\r\nStart,Bool,%I0.0,\r\n");
            Assert.True(table.HasConstants);
            Assert.DoesNotContain(table.Rows, r => r.IsConstant);
        }

        [Theory]
        [InlineData("Name,Data Type,Logical Address,Value\r\nMax,Int,%MW0,5\r\n", "not both")]
        [InlineData("Name,Data Type,Value,Hmi Visible\r\nMax,Int,5,True\r\n", "Hmi Visible")]
        [InlineData("Name,Data Type,Comment Klingon\r\nStart,Bool,x\r\n", "Unknown column")]
        [InlineData("Name,Data Type,Comment de-DE,comment DE-de\r\nStart,Bool,a,b\r\n", "twice")]
        [InlineData("Name,Data Type,Logical Address,Value\r\nStart,Bool,%I0.0,\r\nStart,Int,,5\r\n", "twice")]
        public void ContradictionsAreRefused(string csv, string named)
        {
            var error = Assert.Throws<WireException>(() => TagCsv.ReadTable(csv));
            Assert.Contains(named, error.Message);
        }

        [Fact]
        public void ACommentWithACommaOrQuoteInAnotherLanguageIsQuoted()
        {
            var csv = TagCsv.Write(new[] { Tag("Start", null, In("de-DE", "Start, \"jetzt\"")) });
            Assert.Contains(",\"Start, \"\"jetzt\"\"\",", csv);
            Assert.Equal("Start, \"jetzt\"", TagCsv.Read(csv)[0].Comments["de-DE"]);
        }
    }
}
