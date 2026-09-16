using System.Collections.Generic;
using System.Text;

namespace GameLogic
{
    /// <summary>
    /// CSV 词法解析器（RFC4180：支持双引号包裹、"" 转义、字段内逗号与换行）。
    /// <remarks>
    /// 与 <see cref="CsvTable"/> 分离，便于单独复用与测试。
    /// </remarks>
    /// </summary>
    internal static class CsvParser
    {
        /// <summary>
        /// 把整份 CSV 文本切成"行 → 字段"的二维结构。
        /// </summary>
        /// <param name="text">CSV 全文（可带 UTF-8 BOM）。</param>
        /// <returns>每一行的字段数组；空文本返回空列表。</returns>
        public static List<string[]> ParseRows(string text)
        {
            List<string[]> rows = new List<string[]>();
            if (string.IsNullOrEmpty(text))
            {
                return rows;
            }

            int index = 0;
            int length = text.Length;

            // Excel 导出的 CSV 常带 UTF-8 BOM，会污染首个字段名。
            if (text[0] == '\uFEFF')
            {
                index = 1;
            }

            List<string> fields = new List<string>();
            StringBuilder builder = new StringBuilder();
            bool inQuotes = false;

            while (index < length)
            {
                char current = text[index];

                if (inQuotes)
                {
                    if (current == '"')
                    {
                        // "" 表示一个字面量双引号。
                        if (index + 1 < length && text[index + 1] == '"')
                        {
                            builder.Append('"');
                            index += 2;
                            continue;
                        }

                        inQuotes = false;
                        index++;
                        continue;
                    }

                    builder.Append(current);
                    index++;
                    continue;
                }

                switch (current)
                {
                    case '"':
                        inQuotes = true;
                        index++;
                        break;
                    case ',':
                        fields.Add(builder.ToString());
                        builder.Length = 0;
                        index++;
                        break;
                    case '\r':
                        index++;
                        break;
                    case '\n':
                        fields.Add(builder.ToString());
                        builder.Length = 0;
                        rows.Add(fields.ToArray());
                        fields.Clear();
                        index++;
                        break;
                    default:
                        builder.Append(current);
                        index++;
                        break;
                }
            }

            // 收尾：最后一行可能没有换行符。
            if (builder.Length > 0 || fields.Count > 0)
            {
                fields.Add(builder.ToString());
                rows.Add(fields.ToArray());
            }

            return rows;
        }

        /// <summary>
        /// 判断一行是否为空行（全部字段为空白）。
        /// </summary>
        /// <param name="row">字段数组。</param>
        /// <returns>空行为 true。</returns>
        public static bool IsBlankRow(string[] row)
        {
            if (row == null || row.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < row.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(row[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
