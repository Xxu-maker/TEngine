using System;
using System.Collections.Generic;
using System.Globalization;
using TEngine;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 一张 CSV 配置表：表头解析 + 主键索引 + 行访问。
    /// <para>
    /// 文件格式约定（两种表头风格都支持，自动判定）：
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>风格 A（推荐，Excel 直接导出即可）</b>：第 1 行字段名，第 2 行类型（可省略，缺省按 string 处理），
    /// 其后的行是数据。以 # 开头的行视为注释跳过。
    /// </item>
    /// <item>
    /// <b>风格 B（兼容 Luban 表头）</b>：以 ##var 开头的行为字段名，##type 行为类型，
    /// ##group / ## 等其它指令行忽略。现有 xlsx 导出 CSV 后基本可直接使用。
    /// </item>
    /// </list>
    /// <para>
    /// 支持的字段类型：int / long / float / double / bool / string / vector2 / vector3。
    /// 类型串中的修饰（如 <c>int#ref=item.TbItem</c>、<c>(list#sep=;),item.ItemExchange</c>、<c>datetime?</c>）
    /// 会被剥离；无法识别的类型按 string 处理，原始文本仍可通过 <see cref="CsvRow.GetString"/> 取到。
    /// </para>
    /// <para>
    /// 主键：名为 <c>id</c> 的列（不区分大小写）；没有该列时取第 1 列。
    /// </para>
    /// </summary>
    public sealed class CsvTable
    {
        private static readonly HashSet<string> KnownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "int", "integer", "long", "float", "double", "bool", "boolean",
            "string", "text", "vector2", "vector3", "json", "array"
        };

        private readonly List<string> _columns = new List<string>();
        private readonly List<string> _types = new List<string>();
        private readonly Dictionary<string, int> _columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<CsvRow> _rows = new List<CsvRow>();
        private readonly Dictionary<string, CsvRow> _rowIndex = new Dictionary<string, CsvRow>(StringComparer.Ordinal);

        private CsvTable(string name, string location)
        {
            Name = name;
            Location = location;
        }

        /// <summary>表名（等于资源地址，例如 item）。</summary>
        public string Name { get; }

        /// <summary>YooAsset 资源地址（LoadAssetAsync 用的 location）。</summary>
        public string Location { get; }

        /// <summary>主键列名。</summary>
        public string KeyColumn { get; private set; } = string.Empty;

        /// <summary>列名列表（表头顺序）。</summary>
        public IReadOnlyList<string> Columns => _columns;

        /// <summary>列类型列表，与 <see cref="Columns"/> 一一对应。</summary>
        public IReadOnlyList<string> Types => _types;

        /// <summary>全部数据行。</summary>
        public IReadOnlyList<CsvRow> Rows => _rows;

        /// <summary>数据行数。</summary>
        public int Count => _rows.Count;

        /// <summary>加载该表时持有的 TextAsset，释放时交还资源模块。</summary>
        internal UnityEngine.Object SourceAsset { get; set; }

        /// <summary>
        /// 解析 CSV 文本为表对象。
        /// </summary>
        /// <param name="name">表名（资源地址）。</param>
        /// <param name="location">YooAsset location。</param>
        /// <param name="text">CSV 全文。</param>
        /// <returns>解析后的表。</returns>
        /// <exception cref="GameFrameworkException">缺少表头或列名时抛出。</exception>
        public static CsvTable Parse(string name, string location, string text)
        {
            CsvTable table = new CsvTable(name, location);
            List<string[]> rawRows = CsvParser.ParseRows(text);

            List<string> header = null;
            List<string> types = null;
            bool headerFromDirective = false;
            bool typesFromDirective = false;
            int cursor = 0;

            // 1) 先吃掉开头的指令行（##var / ##type / ##group / ## ...）。
            for (; cursor < rawRows.Count; cursor++)
            {
                string[] row = rawRows[cursor];
                string head = row.Length > 0 ? row[0].Trim() : string.Empty;
                if (!head.StartsWith("#", StringComparison.Ordinal))
                {
                    break;
                }

                string tag = head.TrimStart('#').Trim().ToLowerInvariant();
                if (tag == "var" && header == null)
                {
                    header = Slice(row);
                    headerFromDirective = true;
                }
                else if (tag == "type" && types == null)
                {
                    types = Slice(row);
                    typesFromDirective = true;
                }
            }

            // 2) 没有 ##var 行时按风格 A：第一行就是字段名。
            if (header == null)
            {
                while (cursor < rawRows.Count && CsvParser.IsBlankRow(rawRows[cursor]))
                {
                    cursor++;
                }

                if (cursor < rawRows.Count)
                {
                    header = Slice(rawRows[cursor]);
                    cursor++;
                }
            }

            if (header == null || header.Count == 0)
            {
                throw new GameFrameworkException($"CSV 配置表 '{name}' 缺少表头（字段名行）。");
            }

            for (int i = 0; i < header.Count; i++)
            {
                string column = header[i]?.Trim() ?? string.Empty;
                header[i] = column;
                if (column.Length > 0 && !table._columnIndex.ContainsKey(column))
                {
                    table._columnIndex[column] = i;
                }
            }

            // 3) 风格 A 的可选类型行：整行（除空单元格外）都必须是已知类型名才认。
            if (!typesFromDirective && cursor < rawRows.Count && LooksLikeTypeRow(rawRows[cursor]))
            {
                types = Slice(rawRows[cursor]);
                cursor++;
            }

            for (int i = 0; i < header.Count; i++)
            {
                string declared = types != null && i < types.Count ? types[i] : string.Empty;
                table._types.Add(NormalizeType(declared));
                table._columns.Add(header[i]);
            }

            // 4) 主键列。
            table.KeyColumn = table._columnIndex.TryGetValue("id", out _)
                ? header[table._columnIndex["id"]]
                : header[0];

            int keyIndex = table._columnIndex.TryGetValue(table.KeyColumn, out int found) ? found : 0;

            // 5) 数据行。
            for (; cursor < rawRows.Count; cursor++)
            {
                string[] row = rawRows[cursor];
                if (CsvParser.IsBlankRow(row))
                {
                    continue;
                }

                string first = row[0].Trim();
                if (first.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                CsvRow csvRow = new CsvRow(table, row);
                table._rows.Add(csvRow);

                string key = keyIndex < row.Length ? row[keyIndex].Trim() : string.Empty;
                if (key.Length > 0 && !table._rowIndex.ContainsKey(key))
                {
                    table._rowIndex[key] = csvRow;
                }
            }

            return table;
        }

        /// <summary>列名 → 列索引；不存在返回 -1。</summary>
        /// <param name="column">列名（不区分大小写）。</param>
        /// <returns>列索引。</returns>
        public int IndexOf(string column)
        {
            if (string.IsNullOrEmpty(column))
            {
                return -1;
            }

            return _columnIndex.TryGetValue(column, out int index) ? index : -1;
        }

        /// <summary>该列声明的类型（已归一化）。</summary>
        /// <param name="column">列名。</param>
        /// <returns>类型名；列不存在返回 string。</returns>
        public string TypeOf(string column)
        {
            int index = IndexOf(column);
            return index >= 0 && index < _types.Count ? _types[index] : "string";
        }

        /// <summary>按主键取行（整数主键）。</summary>
        /// <param name="id">主键值。</param>
        /// <returns>数据行。</returns>
        /// <exception cref="GameFrameworkException">主键不存在时抛出。</exception>
        public CsvRow Get(int id)
        {
            return Get(id.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>按主键取行。</summary>
        /// <param name="key">主键值。</param>
        /// <returns>数据行。</returns>
        /// <exception cref="GameFrameworkException">主键不存在时抛出。</exception>
        public CsvRow Get(string key)
        {
            if (key != null && _rowIndex.TryGetValue(key.Trim(), out CsvRow row))
            {
                return row;
            }

            throw new GameFrameworkException($"CSV 配置表 '{Name}' 中不存在主键 '{key}'。");
        }

        /// <summary>按主键取行，不抛异常。</summary>
        /// <param name="key">主键值。</param>
        /// <param name="row">取到的行。</param>
        /// <returns>存在为 true。</returns>
        public bool TryGet(string key, out CsvRow row)
        {
            row = null;
            return key != null && _rowIndex.TryGetValue(key.Trim(), out row);
        }

        /// <summary>按主键取行，不抛异常。</summary>
        /// <param name="id">主键值。</param>
        /// <param name="row">取到的行。</param>
        /// <returns>存在为 true。</returns>
        public bool TryGet(int id, out CsvRow row)
        {
            return TryGet(id.ToString(CultureInfo.InvariantCulture), out row);
        }

        /// <summary>按列名取该表所有行的某个字段。</summary>
        /// <param name="column">列名。</param>
        /// <returns>该列的字符串值列表。</returns>
        public List<string> Collect(string column)
        {
            List<string> values = new List<string>(_rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                values.Add(_rows[i].GetString(column));
            }

            return values;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "CsvTable({0}, {1} rows, key={2})", Name, _rows.Count, KeyColumn);
        }

        private static List<string> Slice(string[] row)
        {
            List<string> list = new List<string>(row.Length > 0 ? row.Length - 1 : 0);
            for (int i = 1; i < row.Length; i++)
            {
                list.Add(row[i]?.Trim() ?? string.Empty);
            }

            return list;
        }

        private static bool LooksLikeTypeRow(string[] row)
        {
            if (row == null || row.Length == 0)
            {
                return false;
            }

            bool any = false;
            for (int i = 0; i < row.Length; i++)
            {
                string cell = row[i]?.Trim() ?? string.Empty;
                if (cell.Length == 0)
                {
                    continue;
                }

                if (!KnownTypes.Contains(BaseTypeToken(cell)))
                {
                    return false;
                }

                any = true;
            }

            return any;
        }

        private static string NormalizeType(string declared)
        {
            string token = BaseTypeToken(declared ?? string.Empty);
            switch (token)
            {
                case "integer":
                    return "int";
                case "boolean":
                    return "bool";
                case "text":
                    return "string";
                default:
                    return KnownTypes.Contains(token) ? token : "string";
            }
        }

        /// <summary>把 int#ref=xxx、(list#sep=;),item.X、datetime? 之类的声明压成基础类型名。</summary>
        private static string BaseTypeToken(string declared)
        {
            string token = declared.Trim().TrimEnd('?').Trim();
            int hash = token.IndexOf('#');
            if (hash >= 0)
            {
                token = token.Substring(0, hash);
            }

            int open = token.IndexOf('(');
            if (open >= 0)
            {
                token = token.Substring(0, open);
            }

            int comma = token.IndexOf(',');
            if (comma >= 0)
            {
                token = token.Substring(comma + 1);
            }

            return token.Trim().TrimEnd('?').Trim().ToLowerInvariant();
        }
    }
}
