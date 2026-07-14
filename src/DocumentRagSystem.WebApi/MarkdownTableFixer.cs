namespace DocumentRagSystem.WebApi
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    public static class MarkdownTableFixer
    {
        public static string Fix(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string Result = text;
            int header_Start = text.IndexOf("|");
            if (header_Start == -1)
            {
                return Result;
            }

            int divider_Start = text.IndexOf("| -", header_Start);
            if (divider_Start == -1)
            {
                return Result;
            }

            int divider_End = text.LastIndexOf("- |");

            if (divider_End == -1 || divider_End < divider_Start)
            {
                return Result;
            }

            string text_Beginning = text.Substring(0, header_Start);
            int columnCount = text.Substring(divider_Start, divider_End - divider_Start).Split('|').Length - 1;
            if (columnCount <= 0)
            {
                return Result;
            }

            string table_header = text.Substring(header_Start, divider_Start - header_Start);
            string table_separator = text.Substring(divider_Start, (divider_End + 3) - divider_Start);
            string table_body = text.Substring(divider_End + 3);

            List<string> rows = new List<string>();

            int current_column = 0;

            int pipe_location = table_body.IndexOf('|');
            if (pipe_location == -1)
            {
                return Result;
            }

            int table_row_start = pipe_location;
            int table_row_end = pipe_location;
            int previous_end = table_row_end;

            while (pipe_location != -1)
            {
                previous_end = table_row_end;
                table_row_end = table_body.IndexOf('|', pipe_location + 1);
                if (table_row_end == -1)
                {
                    break;
                }
                current_column++;

                pipe_location = table_row_end;
                if (current_column == columnCount)
                {
                    rows.Add(table_body.Substring(table_row_start, (table_row_end - table_row_start) + 1));
                    current_column = 0;
                    table_row_start = table_body.IndexOf('|', pipe_location + 1);

                    if (table_row_start == -1)
                    {
                        previous_end = table_body.IndexOf('|', pipe_location);
                        break;
                    }
                    pipe_location = table_row_start;
                }
            }
            string end_text = "";
            if (previous_end + 1 < table_body.Length)
            {
                end_text = table_body.Substring(previous_end, table_body.Length - previous_end);
            }
            Result = text_Beginning + "\n\n";
            Result += table_header + "\n" + table_separator + "\n";
            foreach (string row in rows)
            {
                Result += row + "\n";
            }
            Result += end_text + "\n\n";
            return Result;
        }
    }
}
