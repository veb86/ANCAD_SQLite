using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;

namespace AcadDevices.DbEditor
{
    internal static class TableFilter
    {
        internal static void Apply(DataView view, IEnumerable<KeyValuePair<string, string>> filters)
        {
            var clauses = filters.Where(filter => filter.Value.Length > 0).Select(filter =>
            {
                var name = filter.Key.Replace("\\", "\\\\").Replace("]", "\\]");
                return $"CONVERT([{name}], 'System.String') LIKE '%{EscapePattern(filter.Value)}%'";
            });
            view.RowFilter = string.Join(" AND ", clauses);
        }

        private static string EscapePattern(string text)
        {
            var pattern = new StringBuilder();
            foreach (var character in text)
            {
                switch (character)
                {
                    case '\'': pattern.Append("''"); break;
                    case '%':
                    case '*':
                    case '[':
                    case ']':
                        pattern.Append('[').Append(character).Append(']');
                        break;
                    default: pattern.Append(character); break;
                }
            }

            return pattern.ToString();
        }
    }
}
