using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaCli.Protocol
{
    /// <summary>
    /// A project's hardware as the short YAML a person reads and edits - source/hardware.yaml -
    /// drawn from TIA's CAx export. Written, even where TIA would choose the same: names, types
    /// (order number or GSD module), firmware, slots, I/O start addresses, IP address, router,
    /// subnet and IO system, since TIA assigns those by plug order and a rebuild would differ.
    /// Not written yet: settings that differ from a module's defaults, because the defaults per
    /// type are not learned yet. Nothing reads this file back yet.
    ///
    /// The shape:
    ///   subnets:     name: ethernet
    ///   io-systems:  name: { number, controller: station, interface }
    ///   stations:    name: { gsd?, folder?, rack, modules: { slot: { name, type, fw, I, Q } }, network }
    /// </summary>
    public static class HardwareYaml
    {
        private const string Ungrouped = "Ungrouped devices";

        public static string FromAml(string aml)
        {
            var doc = XDocument.Parse(aml);
            var elements = doc.Descendants().Where(e => e.Name.LocalName == "InternalElement").ToList();
            var byId = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in elements)
            {
                var id = (string)e.Attribute("ID");
                if (id != null && !byId.ContainsKey(id)) byId[id] = e;
            }

            // A link names each side as "<element ID>:<interface>", sometimes bracketed. What a
            // node or interface is linked to is told by the role of the other side.
            var subnetOf = new Dictionary<XElement, XElement>();
            var ioSystemOf = new Dictionary<XElement, XElement>();
            foreach (var link in doc.Descendants().Where(e => e.Name.LocalName == "InternalLink"))
            {
                var a = Side(byId, (string)link.Attribute("RefPartnerSideA"));
                var b = Side(byId, (string)link.Attribute("RefPartnerSideB"));
                if (a == null || b == null) continue;
                Pair(a, b, subnetOf, ioSystemOf);
                Pair(b, a, subnetOf, ioSystemOf);
            }

            var project = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "InstanceHierarchy")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "InternalElement");

            var y = new StringBuilder();
            y.Append("# Hardware of ").Append(Name(project)).Append(", written by 'tia hw export --yaml'.\r\n");
            y.Append("# Settings that differ from a module's defaults are not written yet.\r\n");

            var subnets = elements.Where(e => Role(e) == "Subnet").ToList();
            if (subnets.Count > 0)
            {
                y.Append("\r\nsubnets:\r\n");
                foreach (var s in subnets)
                    y.Append("  ").Append(Quote(Name(s))).Append(": ")
                     .Append(Quote((Value(s, "Type") ?? "ethernet").ToLowerInvariant())).Append("\r\n");
            }

            var ioSystems = elements.Where(e => Role(e) == "IoSystem").ToList();
            if (ioSystems.Count > 0)
            {
                y.Append("\r\nio-systems:\r\n");
                foreach (var io in ioSystems)
                {
                    var iface = io.Parent;
                    var fields = new List<string>();
                    var number = Value(io, "Number");
                    if (number != null) fields.Add("number: " + number);
                    var station = Ancestor(io, "Device");
                    if (station != null) fields.Add("controller: " + Quote(Name(station)));
                    if (iface != null && Role(iface) == "CommunicationInterface") fields.Add("interface: " + Quote(Name(iface)));
                    y.Append("  ").Append(Quote(Name(io))).Append(": ").Append(Flow(fields)).Append("\r\n");
                }
            }

            y.Append("\r\nstations:\r\n");
            if (project != null)
                foreach (var child in Children(project))
                    Station(y, child, "", subnetOf, ioSystemOf);

            return y.ToString();
        }

        private static void Station(StringBuilder y, XElement element, string folder,
            Dictionary<XElement, XElement> subnetOf, Dictionary<XElement, XElement> ioSystemOf)
        {
            var role = Role(element);
            if (role == "DeviceUserFolder")
            {
                var name = Name(element);
                var path = folder.Length == 0 && name == Ungrouped ? "" : (folder.Length == 0 ? name : folder + "/" + name);
                foreach (var child in Children(element)) Station(y, child, path, subnetOf, ioSystemOf);
                return;
            }
            if (role != "Device") return;

            y.Append("\r\n  ").Append(Quote(Name(element))).Append(":\r\n");
            var type = Value(element, "TypeIdentifier");
            if (type != null && type.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase))
                y.Append("    gsd: ").Append(Quote(type.Substring(4).Split('/')[0])).Append("\r\n");
            if (folder.Length > 0) y.Append("    folder: ").Append(Quote(folder)).Append("\r\n");

            var racks = Children(element).Where(e => Role(e) == "DeviceItem").ToList();
            if (racks.Count == 1)
            {
                Rack(y, racks[0], "    ");
            }
            else if (racks.Count > 1)
            {
                y.Append("    racks:\r\n");
                foreach (var rack in racks)
                {
                    y.Append("      ").Append(Quote(Name(rack))).Append(":\r\n");
                    Modules(y, rack, "        ");
                }
            }

            var network = Network(element, subnetOf, ioSystemOf);
            if (network.Count > 0)
            {
                y.Append("    network:\r\n");
                foreach (var line in network) y.Append("      ").Append(line).Append("\r\n");
            }
        }

        private static void Rack(StringBuilder y, XElement rack, string indent)
        {
            y.Append(indent).Append("rack: ").Append(Quote(Name(rack))).Append("\r\n");
            Modules(y, rack, indent);
        }

        private static void Modules(StringBuilder y, XElement rack, string indent)
        {
            var modules = Children(rack).Where(e => Role(e) == "DeviceItem").ToList();
            if (modules.Count == 0) return;
            y.Append(indent).Append("modules:\r\n");
            foreach (var module in modules)
            {
                var fields = new List<string> { "name: " + Quote(Name(module)) };
                var type = ShortType(Value(module, "TypeIdentifier"));
                if (type != null) fields.Add("type: " + Quote(type));
                var fw = Value(module, "FirmwareVersion");
                if (fw != null) fields.Add("fw: " + Quote(fw));

                var inputs = new List<int>();
                var outputs = new List<int>();
                foreach (var item in new[] { module }.Concat(Descendants(module)))
                    Addresses(item, inputs, outputs);
                if (inputs.Count > 0) fields.Add("I: " + Numbers(inputs));
                if (outputs.Count > 0) fields.Add("Q: " + Numbers(outputs));

                y.Append(indent).Append("  ").Append(Value(module, "PositionNumber") ?? "?")
                 .Append(": ").Append(Flow(fields)).Append("\r\n");
            }
        }

        private static List<string> Network(XElement device,
            Dictionary<XElement, XElement> subnetOf, Dictionary<XElement, XElement> ioSystemOf)
        {
            var lines = new List<string>();
            foreach (var iface in Descendants(device).Where(e => Role(e) == "CommunicationInterface"))
            {
                foreach (var node in Children(iface).Where(e => Role(e) == "Node"))
                {
                    var ip = Value(node, "NetworkAddress");
                    if (string.IsNullOrEmpty(ip)) continue;

                    var fields = new List<string> { "ip: " + Quote(Address(ip, Value(node, "SubnetMask"))) };
                    var router = Value(node, "RouterAddress");
                    if (!string.IsNullOrEmpty(router)) fields.Add("router: " + Quote(router));
                    if (subnetOf.TryGetValue(node, out var subnet)) fields.Add("subnet: " + Quote(Name(subnet)));
                    if (ioSystemOf.TryGetValue(iface, out var io)) fields.Add("io-system: " + Quote(Name(io)));
                    lines.Add(Quote(Name(iface)) + ": " + Flow(fields));
                }
            }
            return lines;
        }

        private static void Pair(XElement from, XElement to,
            Dictionary<XElement, XElement> subnetOf, Dictionary<XElement, XElement> ioSystemOf)
        {
            var role = Role(to);
            if (role == "Subnet" && !subnetOf.ContainsKey(from)) subnetOf[from] = to;
            else if (role == "IoSystem" && Role(from) != "IoSystem" && !ioSystemOf.ContainsKey(from)) ioSystemOf[from] = to;
        }

        private static XElement Side(Dictionary<string, XElement> byId, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            var colon = reference.IndexOf(':');
            var id = (colon < 0 ? reference : reference.Substring(0, colon)).Trim('[', ']');
            return byId.TryGetValue(id, out var element) ? element : null;
        }

        /// <summary>One address entry per direction: StartAddress and IoType under an "Address" list.</summary>
        private static void Addresses(XElement item, List<int> inputs, List<int> outputs)
        {
            var list = Attributes(item).FirstOrDefault(a => (string)a.Attribute("Name") == "Address");
            if (list == null) return;
            foreach (var entry in Attributes(list))
            {
                var start = Value(entry, "StartAddress");
                if (!int.TryParse(start, NumberStyles.Integer, CultureInfo.InvariantCulture, out var address)) continue;
                var io = Value(entry, "IoType");
                if (string.Equals(io, "Input", StringComparison.OrdinalIgnoreCase)) inputs.Add(address);
                else if (string.Equals(io, "Output", StringComparison.OrdinalIgnoreCase)) outputs.Add(address);
            }
        }

        private static string Numbers(List<int> addresses) =>
            addresses.Count == 1 ? addresses[0].ToString(CultureInfo.InvariantCulture)
                : "[" + string.Join(", ", addresses.Select(a => a.ToString(CultureInfo.InvariantCulture))) + "]";

        /// <summary>10.15.239.100 and 255.255.240.0 as 10.15.239.100/20; a mask that is no prefix stays a mask.</summary>
        internal static string Address(string ip, string mask)
        {
            if (string.IsNullOrEmpty(mask)) return ip;
            var parts = mask.Split('.');
            if (parts.Length != 4) return ip + "/" + mask;
            uint bits = 0;
            foreach (var p in parts)
            {
                if (!byte.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)) return ip + "/" + mask;
                bits = (bits << 8) | b;
            }
            var prefix = 0;
            while (prefix < 32 && (bits & (0x80000000u >> prefix)) != 0) prefix++;
            var contiguous = prefix == 32 || (bits << prefix) == 0;
            return ip + "/" + (contiguous ? prefix.ToString(CultureInfo.InvariantCulture) : mask);
        }

        /// <summary>The order number of a Siemens module, the module ID of a GSD one.</summary>
        internal static string ShortType(string typeIdentifier)
        {
            if (string.IsNullOrEmpty(typeIdentifier)) return null;
            if (typeIdentifier.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
                return typeIdentifier.Substring("OrderNumber:".Length).Split('/')[0];
            if (typeIdentifier.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase))
                return typeIdentifier.Split('/').Last();
            return typeIdentifier;
        }

        private static readonly Regex Plain = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_ .()/+-]*$");

        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "true", "false", "yes", "no", "on", "off", "null", "y", "n", "~" };

        /// <summary>
        /// Plain where YAML reads it back as the same string, single-quoted otherwise: anything
        /// with a colon, comma, bracket, quote or hash, leading or trailing blanks, a leading dash,
        /// and words YAML takes as booleans, null or numbers.
        /// </summary>
        internal static string Quote(string s)
        {
            if (s == null) return "''";
            var plain = s.Length > 0 && Plain.IsMatch(s) && !s.EndsWith(" ", StringComparison.Ordinal) &&
                        !Reserved.Contains(s) && !LooksNumeric(s);
            return plain ? s : "'" + s.Replace("'", "''") + "'";
        }

        // Numbers in any YAML spelling (1_000, 0x1F, 1:30), and dates, which YAML reads as timestamps.
        private static bool LooksNumeric(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            Regex.IsMatch(s, @"^0[xob]|^[0-9_]+$|^[0-9]+(:[0-9]+)+$|^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}", RegexOptions.IgnoreCase) ||
            s.StartsWith(".", StringComparison.Ordinal);

        private static string Flow(List<string> fields) => "{ " + string.Join(", ", fields) + " }";

        private static IEnumerable<XElement> Children(XElement e) =>
            e.Elements().Where(c => c.Name.LocalName == "InternalElement");

        private static IEnumerable<XElement> Descendants(XElement e) =>
            e.Descendants().Where(c => c.Name.LocalName == "InternalElement");

        private static IEnumerable<XElement> Attributes(XElement e) =>
            e.Elements().Where(c => c.Name.LocalName == "Attribute");

        private static string Value(XElement e, string name)
        {
            var attribute = Attributes(e).FirstOrDefault(a => (string)a.Attribute("Name") == name);
            return attribute?.Elements().FirstOrDefault(v => v.Name.LocalName == "Value")?.Value;
        }

        private static string Name(XElement e) => (string)e?.Attribute("Name") ?? "";

        private static string Role(XElement e)
        {
            var path = (string)e.Elements().FirstOrDefault(c => c.Name.LocalName == "SupportedRoleClass")?
                .Attribute("RefRoleClassPath");
            return path?.Substring(path.LastIndexOf('/') + 1);
        }

        private static XElement Ancestor(XElement e, string role) =>
            e.Ancestors().FirstOrDefault(a => a.Name.LocalName == "InternalElement" && Role(a) == role);
    }
}
