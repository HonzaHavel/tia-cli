using TiaCli.Openness;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// Where 'tia project archive' writes. The rules are small, but a wrong one puts a backup
    /// somewhere nobody looks - or under a name TIA will not offer to retrieve.
    /// </summary>
    public class ArchiveTargetTests
    {
        private const string Project = @"C:\p\Line\Line.ap20";

        [Fact]
        public void WithNoTargetItLandsBesideTheProjectFolder()
        {
            Assert.Equal(@"C:\p\Line.zap20", TiaSession.ArchiveTarget(Project, null, false));
            Assert.Equal(@"C:\p\Line.zap20", TiaSession.ArchiveTarget(Project, "", false));
        }

        [Theory]
        [InlineData(@"C:\p\Line\Line.ap20", @"C:\p\Line.zap20")]
        [InlineData(@"C:\p\Line\Line.ap19", @"C:\p\Line.zap19")]
        [InlineData(@"C:\p\Line\Line.ap15_1", @"C:\p\Line.zap15_1")]
        public void TheExtensionFollowsTheProjectsVersion(string project, string expected)
        {
            Assert.Equal(expected, TiaSession.ArchiveTarget(project, null, false));
        }

        [Fact]
        public void ADirectoryGetsTheProjectsNameInIt()
        {
            Assert.Equal(@"D:\backup\Line.zap20", TiaSession.ArchiveTarget(Project, @"D:\backup", true));
        }

        [Theory]
        [InlineData(@"D:\backup\")]
        [InlineData("D:/backup/")]
        public void ATrailingSeparatorMeansADirectoryEvenBeforeItExists(string target)
        {
            Assert.Equal(System.IO.Path.Combine(target, "Line.zap20"),
                TiaSession.ArchiveTarget(Project, target, false));
        }

        [Theory]
        [InlineData(@"D:\backup\Friday", @"D:\backup\Friday.zap20")]
        [InlineData(@"D:\backup\Friday.zap20", @"D:\backup\Friday.zap20")]
        [InlineData(@"D:\backup\Friday.ZAP20", @"D:\backup\Friday.ZAP20")]
        // Appended, never swapped: "Line.2026-10-13" has an extension only by accident.
        [InlineData(@"D:\backup\Line.2026-10-13", @"D:\backup\Line.2026-10-13.zap20")]
        [InlineData(@"D:\backup\Friday.zip", @"D:\backup\Friday.zip.zap20")]
        public void AFileNameKeepsItsNameAndGainsTheExtensionWhenMissing(string target, string expected)
        {
            Assert.Equal(expected, TiaSession.ArchiveTarget(Project, target, false));
        }
    }
}
