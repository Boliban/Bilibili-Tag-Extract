using System;

namespace VideoTagProcessor;

public static class CsvHelper
{
    public static string EscapeCsvField(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }
}