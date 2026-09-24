using System.Text;

namespace Nadlan.Import.Csv;

/// <summary>
/// Minimal RFC 4180 reader for Airtable CSV exports (quoted fields, embedded newlines, doubled quotes).
/// Airtable exports can repeat a header name, so columns are looked up by first occurrence.
/// </summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<string[]> Rows { get; }

    private CsvTable(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        Headers = headers;
        Rows = rows;
        for (var i = 0; i < headers.Count; i++)
        {
            _index.TryAdd(headers[i].Trim(), i);
        }
    }

    public bool HasColumn(string name) => _index.ContainsKey(name);

    public string Get(string[] row, string column)
    {
        if (!_index.TryGetValue(column, out var i))
        {
            throw new KeyNotFoundException($"CSV has no column '{column}'.");
        }

        return i < row.Length ? row[i].Trim() : string.Empty;
    }

    public static CsvTable Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static CsvTable Parse(string text)
    {
        var records = ParseRecords(text.TrimStart('﻿'));
        if (records.Count == 0)
        {
            throw new FormatException("CSV is empty.");
        }

        return new CsvTable(records[0], records.Skip(1).Where(r => r.Any(f => f.Length > 0)).ToList());
    }

    private static List<string[]> ParseRecords(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(fields.ToArray());
                    fields.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields.ToArray());
        }

        return records;
    }
}
