using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// hardware.yaml drawn from a CAx export. The AML here is cut down from Foodtec's: a CPU station
    /// that owns the IO system, and a GSD drive in TIA's "Ungrouped devices" linked to it.
    /// </summary>
    public class HardwareYamlTests
    {
        private static string Attr(string name, string value) =>
            $"<Attribute Name=\"{name}\" AttributeDataType=\"xs:string\"><Value>{value}</Value></Attribute>";

        private static string Role(string role) =>
            $"<SupportedRoleClass RefRoleClassPath=\"AutomationProjectConfigurationRoleClassLib/{role}\" />";

        private static string Address(int n, int start, string io) =>
            $"<Attribute Name=\"{n}\">{Attr("StartAddress", start.ToString())}{Attr("Length", "160")}{Attr("IoType", io)}</Attribute>";

        private const string Aml =
            "<CAEXFile><InstanceHierarchy Name=\"APC Sample Instance Hierarchy\">" +
            "<InternalElement ID=\"p\" Name=\"Line\">" +
            "<InternalElement ID=\"subnet\" Name=\"PN/IE_1\">{TYPE_ETHERNET}{ROLE_SUBNET}</InternalElement>" +
            "<InternalElement ID=\"folder\" Name=\"Ungrouped devices\">" +
            "<InternalElement ID=\"fan\" Name=\"Extraction Fan Z1\">{FAN_TYPE}" +
            "<InternalElement ID=\"fanrack\" Name=\"Rack\">{POS0}" +
            "<InternalElement ID=\"head\" Name=\"Z1-20U2\">{POS0}{DAP}" +
            "<InternalElement ID=\"fanif\" Name=\"Interface\">{POS1}" +
            "<InternalElement ID=\"fannode\" Name=\"IE1\">{FAN_IP}{ROLE_NODE}</InternalElement>" +
            "{ROLE_IF}</InternalElement>{ROLE_ITEM}</InternalElement>" +
            "<InternalElement ID=\"tgm\" Name=\"Telegram 101 (4PKW/6PZD)_1\">{POS1}{TGM}" +
            "<InternalElement ID=\"tgmsub\" Name=\"Telegram 101\">{POS0}<Attribute Name=\"Address\">{IN}{OUT}</Attribute>{ROLE_ITEM}</InternalElement>" +
            "{ROLE_ITEM}</InternalElement>" +
            "{ROLE_ITEM}</InternalElement>{ROLE_DEVICE}</InternalElement>" +
            "{ROLE_FOLDER}</InternalElement>" +
            "<InternalElement ID=\"st\" Name=\"S7-1500/ET200MP station_1\">" +
            "<InternalElement ID=\"rail\" Name=\"Rail_0\">{POS0}" +
            "<InternalElement ID=\"cpu\" Name=\"BakingLine\">{POS1}{CPU}{FW}" +
            "<InternalElement ID=\"cpuif\" Name=\"PROFINET interface_1\">" +
            "<InternalElement ID=\"cpunode\" Name=\"E1\">{CPU_IP}{ROLE_NODE}</InternalElement>" +
            "<InternalElement ID=\"ios\" Name=\"PROFINET IO-System\">{NUMBER}{ROLE_IOS}</InternalElement>" +
            "{ROLE_IF}</InternalElement>{ROLE_ITEM}</InternalElement>" +
            "{ROLE_ITEM}</InternalElement>{ROLE_DEVICE}</InternalElement>" +
            "{ROLE_PROJECT}</InternalElement>" +
            "<InternalLink Name=\"Link To Subnet_1\" RefPartnerSideA=\"[fannode]:[LogicalEndPoint_Node]\" RefPartnerSideB=\"subnet:LogicalEndPoint_Subnet\" />" +
            "<InternalLink Name=\"Link To Subnet_2\" RefPartnerSideA=\"cpunode:LogicalEndPoint_Node\" RefPartnerSideB=\"subnet:LogicalEndPoint_Subnet\" />" +
            "<InternalLink Name=\"Link To IoSystem_1\" RefPartnerSideA=\"fanif:LogicalEndPoint_Interface\" RefPartnerSideB=\"ios:LogicalEndPoint_IoSystem\" />" +
            "</InstanceHierarchy></CAEXFile>";

        private static string Build() => Aml
            .Replace("{TYPE_ETHERNET}", Attr("Type", "Ethernet"))
            .Replace("{FAN_TYPE}", Attr("TypeIdentifier", "GSD:GSDML-V2.3-SCHNEIDER-ATV320-20170209.XML/D"))
            .Replace("{DAP}", Attr("TypeIdentifier", "GSD:GSDML-V2.3-SCHNEIDER-ATV320-20170209.XML/DAP/DAP V2.0"))
            .Replace("{TGM}", Attr("TypeIdentifier", "GSD:GSDML-V2.3-SCHNEIDER-ATV320-20170209.XML/M/ID_MODULE_TGM101"))
            .Replace("{CPU}", Attr("TypeIdentifier", "OrderNumber:6ES7 515-2FN03-0AB0/V4.0"))
            .Replace("{FW}", Attr("FirmwareVersion", "V4.0"))
            .Replace("{FAN_IP}", Attr("NetworkAddress", "10.15.238.110") + Attr("SubnetMask", "255.255.240.0") + Attr("RouterAddress", "10.15.224.1"))
            .Replace("{CPU_IP}", Attr("NetworkAddress", "10.15.239.250") + Attr("SubnetMask", "255.255.240.0"))
            .Replace("{NUMBER}", Attr("Number", "100"))
            .Replace("{IN}", Address(1, 2100, "Input"))
            .Replace("{OUT}", Address(2, 2100, "Output"))
            .Replace("{POS0}", Attr("PositionNumber", "0"))
            .Replace("{POS1}", Attr("PositionNumber", "1"))
            .Replace("{ROLE_SUBNET}", Role("Subnet"))
            .Replace("{ROLE_NODE}", Role("Node"))
            .Replace("{ROLE_IF}", Role("CommunicationInterface"))
            .Replace("{ROLE_ITEM}", Role("DeviceItem"))
            .Replace("{ROLE_DEVICE}", Role("Device"))
            .Replace("{ROLE_FOLDER}", Role("DeviceUserFolder"))
            .Replace("{ROLE_IOS}", Role("IoSystem"))
            .Replace("{ROLE_PROJECT}", Role("AutomationProject"));

        [Fact]
        public void TheHardwareReadsAsTheShortYaml()
        {
            var expected =
                "# Hardware of Line, written by 'tia hw export --yaml'.\r\n" +
                "# Settings that differ from a module's defaults are not written yet.\r\n" +
                "\r\n" +
                "subnets:\r\n" +
                "  PN/IE_1: ethernet\r\n" +
                "\r\n" +
                "io-systems:\r\n" +
                "  PROFINET IO-System: { number: 100, controller: S7-1500/ET200MP station_1, interface: PROFINET interface_1 }\r\n" +
                "\r\n" +
                "stations:\r\n" +
                "\r\n" +
                "  Extraction Fan Z1:\r\n" +
                "    gsd: GSDML-V2.3-SCHNEIDER-ATV320-20170209.XML\r\n" +
                "    rack: Rack\r\n" +
                "    modules:\r\n" +
                "      0: { name: Z1-20U2, type: DAP V2.0 }\r\n" +
                "      1: { name: Telegram 101 (4PKW/6PZD)_1, type: ID_MODULE_TGM101, I: 2100, Q: 2100 }\r\n" +
                "    network:\r\n" +
                "      Interface: { ip: 10.15.238.110/20, router: 10.15.224.1, subnet: PN/IE_1, io-system: PROFINET IO-System }\r\n" +
                "\r\n" +
                "  S7-1500/ET200MP station_1:\r\n" +
                "    rack: Rail_0\r\n" +
                "    modules:\r\n" +
                "      1: { name: BakingLine, type: 6ES7 515-2FN03-0AB0, fw: V4.0 }\r\n" +
                "    network:\r\n" +
                "      PROFINET interface_1: { ip: 10.15.239.250/20, subnet: PN/IE_1 }\r\n";

            Assert.Equal(expected, HardwareYaml.FromAml(Build()));
        }

        [Fact]
        public void AStationInAFolderOfItsOwnSaysSo()
        {
            var yaml = HardwareYaml.FromAml(Build().Replace("Name=\"Ungrouped devices\"", "Name=\"Drives\""));
            Assert.Contains("  Extraction Fan Z1:\r\n    gsd: GSDML-V2.3-SCHNEIDER-ATV320-20170209.XML\r\n    folder: Drives\r\n", yaml);
        }

        [Theory]
        [InlineData("Main", "Main")]
        [InlineData("1900U2", "1900U2")]
        [InlineData("6ES7 155-6AU02-0BN0", "6ES7 155-6AU02-0BN0")]
        [InlineData("10.15.239.100/20", "10.15.239.100/20")]
        [InlineData("-110A1-01", "'-110A1-01'")]
        [InlineData("4 Input Bytes, Bits 0..31", "'4 Input Bytes, Bits 0..31'")]
        [InlineData("100", "'100'")]
        [InlineData("1.5", "'1.5'")]
        [InlineData("V4.0", "V4.0")]
        [InlineData("yes", "'yes'")]
        [InlineData("Off", "'Off'")]
        [InlineData("a: b", "'a: b'")]
        [InlineData("it's", "'it''s'")]
        [InlineData("#1", "'#1'")]
        [InlineData("2026-10-26", "'2026-10-26'")]
        [InlineData("", "''")]
        public void NamesAreQuotedOnlyWhereYamlWouldReadThemOtherwise(string name, string written)
        {
            Assert.Equal(written, HardwareYaml.Quote(name));
        }

        [Theory]
        [InlineData("255.255.240.0", "10.0.0.1/20")]
        [InlineData("255.255.255.0", "10.0.0.1/24")]
        [InlineData("255.255.255.255", "10.0.0.1/32")]
        [InlineData("0.0.0.0", "10.0.0.1/0")]
        [InlineData("255.0.255.0", "10.0.0.1/255.0.255.0")]
        [InlineData(null, "10.0.0.1")]
        public void AMaskIsWrittenAsAPrefix(string mask, string written)
        {
            Assert.Equal(written, HardwareYaml.Address("10.0.0.1", mask));
        }

        [Theory]
        [InlineData("OrderNumber:6ES7 515-2FN03-0AB0/V4.0", "6ES7 515-2FN03-0AB0")]
        [InlineData("GSD:GSDML-V2.3-SCHNEIDER-ATV320-20170209.XML/M/ID_MODULE_TGM101", "ID_MODULE_TGM101")]
        [InlineData("System:Rack.ET200SP", "System:Rack.ET200SP")]
        public void ATypeIsItsOrderNumberOrGsdModule(string typeIdentifier, string written)
        {
            Assert.Equal(written, HardwareYaml.ShortType(typeIdentifier));
        }
    }
}
