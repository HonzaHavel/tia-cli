using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaCli.Protocol
{
    /// <summary>
    /// CAx data that only changes when the hardware does. TIA writes every export with a fresh ID
    /// on every element, the moment of writing and the file's name, so two exports of the same
    /// project share almost no lines and git shows the whole file as changed. Without those, two
    /// exports are identical (tools/caxtest.sh, 2026-10-19), and TIA imports the result.
    ///
    /// - Each ID is derived from the element's path - its kind and name, and those of everything
    ///   above it - so adding a module changes only that module's IDs, not every one after it.
    ///   Links keep pointing at the same elements: every reference is rewritten with its ID.
    /// - The file name and the writing time are emptied, as caxtest's import of a normalized file
    ///   proved TIA accepts.
    /// - A module's InstallationDate goes: it says when the module was plugged into this project,
    ///   so a project rebuilt from the file gets new ones, and it is no setting of the hardware.
    ///
    /// The text is rewritten in place, never re-serialized, so TIA's layout and line endings stay.
    /// </summary>
    public static class CaxAml
    {
        private static readonly Regex Guid =
            new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        private static readonly Regex FileName = new Regex(@"(<CAEXFile\b[^>]*?\bFileName="")[^""]*("")");

        private static readonly Regex WritingTime = new Regex(@"(<LastWritingDateTime>)[^<]*(</LastWritingDateTime>)");

        private static readonly Regex InstallationDate = new Regex(
            @"^[ \t]*<Attribute Name=""InstallationDate""[^>]*>\s*<Value>[^<]*</Value>\s*</Attribute>[ \t]*\r?\n",
            RegexOptions.Multiline);

        public static string Normalize(string aml)
        {
            var ids = StableIds(aml);
            var text = Guid.Replace(aml, m => ids.TryGetValue(m.Value.ToLowerInvariant(), out var id) ? id : m.Value);
            text = FileName.Replace(text, "$1$2");
            text = WritingTime.Replace(text, "$1$2");
            return InstallationDate.Replace(text, "");
        }

        /// <summary>
        /// Every element ID in the file, mapped to one derived from the element's path. Two siblings
        /// of the same kind and name - TIA should not write any - are told apart by their order.
        /// GUIDs that are not element IDs, like the WriterID that names TIA Portal, are not in the map.
        /// </summary>
        internal static Dictionary<string, string> StableIds(string aml)
        {
            var doc = XDocument.Parse(aml);
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var element in doc.Descendants())
            {
                var id = (string)element.Attribute("ID");
                if (id == null || !Guid.IsMatch(id) || ids.ContainsKey(id)) continue;

                var path = Path(element);
                seen.TryGetValue(path, out var count);
                seen[path] = count + 1;
                if (count > 0) path += "#" + (count + 1);

                ids[id.ToLowerInvariant()] = NameBasedGuid(path);
            }
            return ids;
        }

        internal static string Path(XElement element)
        {
            return string.Join("/", element.AncestorsAndSelf().Reverse()
                .Select(e => e.Name.LocalName + ":" + ((string)e.Attribute("Name") ?? "")));
        }

        /// <summary>A version-5 style UUID: the first 16 bytes of the path's SHA-1.</summary>
        internal static string NameBasedGuid(string path)
        {
            byte[] hash;
            using (var sha1 = SHA1.Create())
                hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(path));

            hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

            var hex = string.Concat(hash.Take(16).Select(b => b.ToString("x2")));
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20, 12)}";
        }
    }
}
