using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TiaCli.Protocol
{
    /// <summary>One PLC tag as a row of a tag table's CSV.</summary>
    public sealed class TagRow
    {
        /// <summary>Only in a listing of several tables; a table's own file has no such column.</summary>
        public string Table { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Address { get; set; }
        public string Comment { get; set; }
        public bool HmiVisible { get; set; } = true;
        public bool HmiAccessible { get; set; } = true;
        public bool HmiWriteable { get; set; } = true;
    }

    /// <summary>
    /// A tag table as CSV: one row per tag, under the column names TIA Portal's own Excel export
    /// uses, so the file reads the same as the sheet people already know. TIA has no CSV of its own;
    /// this is written and read here, both ways.
    ///
    /// Comma-separated, RFC 4180 quoting, CRLF, UTF-8 with a BOM - the BOM is what makes Excel read
    /// umlauts in a comment as umlauts. Reading is forgiving: the columns may come in any order, the
    /// HMI columns may be left out, and a file without a BOM or with LF endings is fine. A column it
    /// does not know is refused, since dropping it silently would lose what somebody wrote there.
    /// </summary>
    public static class TagCsv
    {
        public const string Table = "Table";
        public const string Name = "Name";
        public const string DataType = "Data Type";
        public const string Address = "Logical Address";
        public const string Comment = "Comment";
        public const string HmiVisible = "Hmi Visible";
        public const string HmiAccessible = "Hmi Accessible";
        public const string HmiWriteable = "Hmi Writeable";

        private static readonly string[] Columns =
            { Name, DataType, Address, Comment, HmiVisible, HmiAccessible, HmiWriteable };

        public static readonly Encoding FileEncoding = new UTF8Encoding(true);

        /// <summary>
        /// The CSV of one table, or with <paramref name="withTable"/> a listing of several, which
        /// leads with a Table column - readable, but not a file any one table can be imported from.
        /// </summary>
        public static string Write(IEnumerable<TagRow> rows, bool withTable = false)
        {
            var text = new StringBuilder();
            var header = withTable ? new[] { Table }.Concat(Columns) : Columns;
            text.Append(string.Join(",", header.Select(Quote))).Append("\r\n");
            foreach (var row in rows)
            {
                var cells = new[]
                {
                    row.Name, row.DataType, row.Address, row.Comment,
                    Bool(row.HmiVisible), Bool(row.HmiAccessible), Bool(row.HmiWriteable),
                };
                if (withTable) cells = new[] { row.Table }.Concat(cells).ToArray();
                text.Append(string.Join(",", cells.Select(Quote))).Append("\r\n");
            }
            return text.ToString();
        }

        public static List<TagRow> Read(string text)
        {
            var records = Records(text ?? "").Where(r => !(r.Count == 1 && r[0].Length == 0)).ToList();
            if (records.Count == 0)
                throw new WireException(WireErrorCodes.InvalidRequest,
                    "The tag table CSV is empty; it needs at least its header line.");

            var header = records[0].Select(h => h.Trim()).ToList();
            var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
            {
                if (string.Equals(header[i], Table, StringComparison.OrdinalIgnoreCase))
                    throw new WireException(WireErrorCodes.InvalidRequest,
                        "This CSV lists the tags of several tables, so no one table can be made from it.",
                        "Write one table's file with 'tia table export', or 'tia tags --table <name>'.");

                var known = Columns.FirstOrDefault(c => string.Equals(c, header[i], StringComparison.OrdinalIgnoreCase));
                if (known == null)
                    throw new WireException(WireErrorCodes.InvalidRequest,
                        $"Unknown column '{header[i]}' in the tag table CSV.",
                        "The columns are: " + string.Join(", ", Columns) + ".");
                if (at.ContainsKey(known))
                    throw new WireException(WireErrorCodes.InvalidRequest, $"Column '{known}' appears twice.");
                at[known] = i;
            }
            if (!at.ContainsKey(Name) || !at.ContainsKey(DataType))
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"The tag table CSV needs at least the '{Name}' and '{DataType}' columns.");

            var rows = new List<TagRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var r = 1; r < records.Count; r++)
            {
                var record = records[r];
                string Cell(string column) =>
                    at.TryGetValue(column, out var i) && i < record.Count ? record[i] : null;

                var line = r + 1;
                var name = (Cell(Name) ?? "").Trim();
                if (name.Length == 0)
                    throw new WireException(WireErrorCodes.InvalidRequest, $"Row {line} of the tag table CSV has no name.");
                if (!seen.Add(name))
                    throw new WireException(WireErrorCodes.InvalidRequest, $"Tag '{name}' appears twice (row {line}).");

                rows.Add(new TagRow
                {
                    Name = name,
                    DataType = (Cell(DataType) ?? "").Trim(),
                    Address = NullIfBlank(Cell(Address)),
                    Comment = NullIfBlank(Cell(Comment)),
                    HmiVisible = ParseBool(Cell(HmiVisible), HmiVisible, line),
                    HmiAccessible = ParseBool(Cell(HmiAccessible), HmiAccessible, line),
                    HmiWriteable = ParseBool(Cell(HmiWriteable), HmiWriteable, line),
                });
            }
            return rows;
        }

        private static string NullIfBlank(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Bool(bool value) => value ? "True" : "False";

        private static bool ParseBool(string value, string column, int line)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            switch (value.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "x": return true;
                case "false": case "0": case "no": return false;
            }
            throw new WireException(WireErrorCodes.InvalidRequest,
                $"Row {line}, '{column}': expected True or False, got '{value}'.");
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || value != value.Trim()
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        /// <summary>RFC 4180 records: quoted fields may hold commas, quotes and line breaks.</summary>
        private static IEnumerable<List<string>> Records(string text)
        {
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            var record = new List<string>();
            var field = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '"': quoted = true; break;
                    case ',': record.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        record.Add(field.ToString()); field.Clear();
                        yield return record;
                        record = new List<string>();
                        break;
                    default: field.Append(c); break;
                }
            }

            if (field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                yield return record;
            }
        }
    }
}
