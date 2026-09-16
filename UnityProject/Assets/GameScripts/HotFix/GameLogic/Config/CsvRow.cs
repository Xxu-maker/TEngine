using System.Globalization;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// CSV 配置表中的一行。
    /// <remarks>
    /// 取值一律走列名（不区分大小写），列索引由 <see cref="CsvTable"/> 统一维护，
    /// 行对象本身不缓存列映射，避免每行一份字典带来的 GC 压力。
    /// </remarks>
    /// </summary>
    public sealed class CsvRow
    {
        private static readonly string[] BoolTrueValues = { "1", "true", "yes", "y", "是", "t" };

        private readonly CsvTable _table;
        private readonly string[] _fields;

        internal CsvRow(CsvTable table, string[] fields)
        {
            _table = table;
            _fields = fields;
        }

        /// <summary>所属表。</summary>
        public CsvTable Table => _table;

        /// <summary>该行在主键列上的原始值（即 Get 时使用的 key）。</summary>
        public string Key => GetString(_table.KeyColumn);

        /// <summary>字段个数。</summary>
        public int FieldCount => _fields?.Length ?? 0;

        /// <summary>
        /// 按列索引取原始字符串。
        /// </summary>
        /// <param name="columnIndex">列索引。</param>
        /// <returns>原始值；越界返回空串。</returns>
        public string GetRaw(int columnIndex)
        {
            if (_fields == null || columnIndex < 0 || columnIndex >= _fields.Length)
            {
                return string.Empty;
            }

            return _fields[columnIndex];
        }

        /// <summary>该行是否包含指定列。</summary>
        /// <param name="column">列名。</param>
        /// <returns>存在为 true。</returns>
        public bool Has(string column)
        {
            return _table.IndexOf(column) >= 0;
        }

        /// <summary>取字符串。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">缺失或为空时的返回值。</param>
        /// <returns>列值。</returns>
        public string GetString(string column, string defaultValue = "")
        {
            int index = _table.IndexOf(column);
            if (index < 0 || _fields == null || index >= _fields.Length)
            {
                return defaultValue;
            }

            string raw = _fields[index];
            return string.IsNullOrEmpty(raw) ? defaultValue : raw;
        }

        /// <summary>取 int。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">解析失败时的返回值。</param>
        /// <returns>列值。</returns>
        public int GetInt(string column, int defaultValue = 0)
        {
            string raw = GetString(column, null);
            if (string.IsNullOrEmpty(raw))
            {
                return defaultValue;
            }

            return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float asFloat)
                    ? (int)asFloat
                    : defaultValue);
        }

        /// <summary>取 long。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">解析失败时的返回值。</param>
        /// <returns>列值。</returns>
        public long GetLong(string column, long defaultValue = 0L)
        {
            string raw = GetString(column, null);
            if (string.IsNullOrEmpty(raw))
            {
                return defaultValue;
            }

            return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : defaultValue;
        }

        /// <summary>取 float。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">解析失败时的返回值。</param>
        /// <returns>列值。</returns>
        public float GetFloat(string column, float defaultValue = 0f)
        {
            string raw = GetString(column, null);
            if (string.IsNullOrEmpty(raw))
            {
                return defaultValue;
            }

            return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : defaultValue;
        }

        /// <summary>取 double。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">解析失败时的返回值。</param>
        /// <returns>列值。</returns>
        public double GetDouble(string column, double defaultValue = 0d)
        {
            string raw = GetString(column, null);
            if (string.IsNullOrEmpty(raw))
            {
                return defaultValue;
            }

            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : defaultValue;
        }

        /// <summary>取 bool（1/true/yes/y/是 均为真，不区分大小写）。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">缺失或空时的返回值。</param>
        /// <returns>列值。</returns>
        public bool GetBool(string column, bool defaultValue = false)
        {
            string raw = GetString(column, null);
            if (string.IsNullOrEmpty(raw))
            {
                return defaultValue;
            }

            string normalized = raw.Trim().ToLowerInvariant();
            for (int i = 0; i < BoolTrueValues.Length; i++)
            {
                if (normalized == BoolTrueValues[i])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>取 Vector2（单元格写作 "x,y" 或 "x;y"，因含逗号需用双引号包裹）。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">缺失或解析失败时的返回值。</param>
        /// <returns>列值。</returns>
        public Vector2 GetVector2(string column, Vector2 defaultValue = default)
        {
            float[] parts = GetFloatParts(column, 2);
            return parts == null ? defaultValue : new Vector2(parts[0], parts[1]);
        }

        /// <summary>取 Vector3（单元格写作 "x,y,z" 或 "x;y;z"，因含逗号需用双引号包裹）。</summary>
        /// <param name="column">列名。</param>
        /// <param name="defaultValue">缺失或解析失败时的返回值。</param>
        /// <returns>列值。</returns>
        public Vector3 GetVector3(string column, Vector3 defaultValue = default)
        {
            float[] parts = GetFloatParts(column, 3);
            return parts == null ? defaultValue : new Vector3(parts[0], parts[1], parts[2]);
        }

        /// <summary>
        /// 取数组（单元格写作 "a;b;c"；分隔符默认分号，可传 ',' 处理被双引号包裹的逗号列表）。
        /// <remarks>用于替代 Luban 的 list 类型，例如兑换列表 "10001,2;10002,3"。</remarks>
        /// </summary>
        /// <param name="column">列名。</param>
        /// <param name="separator">分隔符。</param>
        /// <returns>拆分结果；缺失返回空数组。</returns>
        public string[] GetArray(string column, char separator = ';')
        {
            string raw = GetString(column, null);
            if (string.IsNullOrEmpty(raw))
            {
                return System.Array.Empty<string>();
            }

            string[] parts = raw.Split(separator);
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            return parts;
        }

        /// <summary>
        /// 取二维数组（单元格写作 "a,b;c,d"），用于 list 的每一项自带子字段的表。
        /// </summary>
        /// <param name="column">列名。</param>
        /// <param name="itemSeparator">项分隔符。</param>
        /// <param name="fieldSeparator">项内字段分隔符。</param>
        /// <returns>二维数组；缺失返回长度为 0 的数组。</returns>
        public string[][] GetArray2D(string column, char itemSeparator = ';', char fieldSeparator = ',')
        {
            string[] items = GetArray(column, itemSeparator);
            string[][] result = new string[items.Length][];
            for (int i = 0; i < items.Length; i++)
            {
                string[] fields = items[i].Split(fieldSeparator);
                for (int j = 0; j < fields.Length; j++)
                {
                    fields[j] = fields[j].Trim();
                }

                result[i] = fields;
            }

            return result;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return _table == null
                ? base.ToString()
                : string.Format(CultureInfo.InvariantCulture, "{0}[{1}]", _table.Name, Key);
        }

        private float[] GetFloatParts(string column, int expected)
        {
            string raw = GetString(column, null);
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            char separator = raw.IndexOf(';') >= 0 ? ';' : ',';
            string[] parts = raw.Split(separator);
            if (parts.Length < expected)
            {
                return null;
            }

            float[] values = new float[expected];
            for (int i = 0; i < expected; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    return null;
                }
            }

            return values;
        }
    }
}
