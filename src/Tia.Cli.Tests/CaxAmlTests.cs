using System.Text.RegularExpressions;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// A hardware export in git: the same hardware exported twice is the same file, and a change to
    /// one module changes that module's lines, not every ID after it. Shaped like Foodtec's export.
    /// </summary>
    public class CaxAmlTests
    {
        private const string Export =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<CAEXFile FileName=\"A.aml\" SchemaVersion=\"2.15\">\r\n" +
            "  <AdditionalInformation>\r\n" +
            "    <WriterHeader>\r\n" +
            "      <WriterID>1d4fcebb-1ad6-4881-b01d-bca335d94a46:V1.0</WriterID>\r\n" +
            "      <LastWritingDateTime>2026-10-26T03:07:48.3793361Z</LastWritingDateTime>\r\n" +
            "    </WriterHeader>\r\n" +
            "  </AdditionalInformation>\r\n" +
            "  <InstanceHierarchy Name=\"APC Sample Instance Hierarchy\">\r\n" +
            "    <InternalElement ID=\"7d5a0038-2420-4e2e-a0b8-42533f8f2b3a\" Name=\"Foodtec\">\r\n" +
            "      <InternalElement ID=\"60b5dcee-5f54-433f-a668-0dafb345270a\" Name=\"PN/IE_1\">\r\n" +
            "        <ExternalInterface ID=\"42a5101c-868a-4567-a117-d774954edd51\" Name=\"LogicalEndPoint_Subnet\" />\r\n" +
            "      </InternalElement>\r\n" +
            "      <InternalElement ID=\"065a657a-f8ee-4112-b71a-93e6d86da7e2\" Name=\"IM 155-6 PN HF_1\">\r\n" +
            "        <Attribute Name=\"InstallationDate\" AttributeDataType=\"xs:dateTime\">\r\n" +
            "          <Value>2026-09-17T12:54:49.4923558Z</Value>\r\n" +
            "        </Attribute>\r\n" +
            "        <Attribute Name=\"FirmwareVersion\" AttributeDataType=\"xs:string\">\r\n" +
            "          <Value>V6.2</Value>\r\n" +
            "        </Attribute>\r\n" +
            "        <ExternalInterface ID=\"73b374dc-a214-4e1f-87d1-75b0ae459eac\" Name=\"LogicalEndPoint_Node\" />\r\n" +
            "      </InternalElement>\r\n" +
            "      <InternalLink Name=\"Link To Subnet_1\" RefPartnerSideA=\"73b374dc-a214-4e1f-87d1-75b0ae459eac:LogicalEndPoint_Node\" RefPartnerSideB=\"42a5101c-868a-4567-a117-d774954edd51:LogicalEndPoint_Subnet\" />\r\n" +
            "    </InternalElement>\r\n" +
            "  </InstanceHierarchy>\r\n" +
            "</CAEXFile>";

        private static readonly Regex Guid =
            new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");

        /// <summary>What the next export of the same hardware looks like: new IDs, new time, new name.</summary>
        private static string Reexport(string aml)
        {
            var fresh = new System.Collections.Generic.Dictionary<string, string>();
            return Guid.Replace(aml, m => m.Value == "1d4fcebb-1ad6-4881-b01d-bca335d94a46"
                    ? m.Value
                    : fresh.TryGetValue(m.Value, out var id) ? id : fresh[m.Value] = System.Guid.NewGuid().ToString())
                .Replace("2026-10-26T03:07:48.3793361Z", "2026-10-26T03:09:12.0000000Z")
                .Replace("FileName=\"A.aml\"", "FileName=\"A2.aml\"");
        }

        [Fact]
        public void TwoExportsOfTheSameHardwareAreTheSameFile()
        {
            Assert.Equal(CaxAml.Normalize(Export), CaxAml.Normalize(Reexport(Export)));
        }

        [Fact]
        public void LinksStillPointAtTheirElements()
        {
            var normal = CaxAml.Normalize(Export);
            var node = Regex.Match(normal, "ID=\"([^\"]+)\" Name=\"LogicalEndPoint_Node\"").Groups[1].Value;
            var subnet = Regex.Match(normal, "ID=\"([^\"]+)\" Name=\"LogicalEndPoint_Subnet\"").Groups[1].Value;

            Assert.Contains($"RefPartnerSideA=\"{node}:LogicalEndPoint_Node\"", normal);
            Assert.Contains($"RefPartnerSideB=\"{subnet}:LogicalEndPoint_Subnet\"", normal);
            Assert.NotEqual(node, subnet);
        }

        [Fact]
        public void AddingAModuleChangesNoOtherId()
        {
            var added = Export.Replace(
                "      <InternalElement ID=\"065a657a",
                "      <InternalElement ID=\"11111111-2222-3333-4444-555555555555\" Name=\"DI 16x24VDC_1\">\r\n" +
                "      </InternalElement>\r\n" +
                "      <InternalElement ID=\"065a657a");

            var before = CaxAml.StableIds(Export);
            var after = CaxAml.StableIds(added);

            foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
            Assert.Equal(before.Count + 1, after.Count);
        }

        [Fact]
        public void FileNameTimeAndInstallationDateAreBlanked()
        {
            var normal = CaxAml.Normalize(Export);

            Assert.Contains("<CAEXFile FileName=\"\" SchemaVersion=\"2.15\">", normal);
            Assert.Contains("<LastWritingDateTime></LastWritingDateTime>", normal);
            Assert.DoesNotContain("InstallationDate", normal);
            Assert.Contains(
                "IM 155-6 PN HF_1\">\r\n        <Attribute Name=\"FirmwareVersion\"", normal);
        }

        [Fact]
        public void TheWriterIdNamesTiaAndStays()
        {
            Assert.Contains("<WriterID>1d4fcebb-1ad6-4881-b01d-bca335d94a46:V1.0</WriterID>", CaxAml.Normalize(Export));
        }

        [Fact]
        public void LayoutAndLineEndingsStay()
        {
            var normal = CaxAml.Normalize(Export);
            Assert.Equal(Export.Split('\n').Length - 3, normal.Split('\n').Length);
            Assert.DoesNotContain("\n", normal.Replace("\r\n", ""));
        }

        [Fact]
        public void TwinSiblingsGetDifferentIds()
        {
            var twins = Export.Replace("Name=\"PN/IE_1\"", "Name=\"IM 155-6 PN HF_1\"");
            var ids = CaxAml.StableIds(twins);
            Assert.NotEqual(ids["60b5dcee-5f54-433f-a668-0dafb345270a"], ids["065a657a-f8ee-4112-b71a-93e6d86da7e2"]);
        }

        [Fact]
        public void NormalizingTwiceChangesNothing()
        {
            var once = CaxAml.Normalize(Export);
            Assert.Equal(once, CaxAml.Normalize(once));
        }
    }
}
