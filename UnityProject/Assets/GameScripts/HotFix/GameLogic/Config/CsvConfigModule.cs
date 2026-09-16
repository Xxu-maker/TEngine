using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TEngine;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// <see cref="ICsvConfigModule"/> 的默认实现。
    /// <remarks>
    /// 模块由 <see cref="ModuleSystem"/> 按接口名约定反射创建（<c>GameLogic.ICsvConfigModule</c> → <c>GameLogic.CsvConfigModule</c>），
    /// 因此类名与命名空间不可随意更改。
    /// 表数据按需加载并缓存，重复加载同一张表会复用同一份解析结果；释放时把 TextAsset 交还资源模块，
    /// 遵守"加载即对应卸载"的资源红线。
    /// </remarks>
    /// </summary>
    public sealed class CsvConfigModule : Module, ICsvConfigModule
    {
        private readonly Dictionary<string, CsvTable> _tables = new Dictionary<string, CsvTable>(StringComparer.Ordinal);
        private readonly Dictionary<string, UniTaskCompletionSource<CsvTable>> _pending = new Dictionary<string, UniTaskCompletionSource<CsvTable>>(StringComparer.Ordinal);

        /// <inheritdoc />
        public override void OnInit()
        {
        }

        /// <inheritdoc />
        public override void Shutdown()
        {
            ReleaseAll();
        }

        /// <inheritdoc />
        public async UniTask<CsvTable> LoadTableAsync(string tableName, CancellationToken cancellationToken = default)
        {
            string location = NormalizeLocation(tableName);
            if (location.Length == 0)
            {
                throw new GameFrameworkException("CSV 表名不能为空。");
            }

            if (_tables.TryGetValue(location, out CsvTable cached))
            {
                return cached;
            }

            // 同一张表并发加载时，后到的调用者等待首个加载完成，避免重复解析。
            if (_pending.TryGetValue(location, out UniTaskCompletionSource<CsvTable> pending))
            {
                return await pending.Task;
            }

            UniTaskCompletionSource<CsvTable> completion = new UniTaskCompletionSource<CsvTable>();
            _pending[location] = completion;

            try
            {
                TextAsset asset = await GameModule.Resource.LoadAssetAsync<TextAsset>(location, cancellationToken);
                if (asset == null)
                {
                    throw new GameFrameworkException(
                        $"CSV 配置表 '{location}' 加载失败：资源不存在。请确认文件位于 Assets/AssetRaw/Configs/ 且文件名与表名一致。");
                }

                CsvTable table = CsvTable.Parse(location, location, asset.text);
                table.SourceAsset = asset;

                _tables[location] = table;
                completion.TrySetResult(table);

                Log.Info("CSV 配置表已加载：{0}（{1} 行，主键 {2}）", location, table.Count, table.KeyColumn);
                return table;
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
            finally
            {
                _pending.Remove(location);
            }
        }

        /// <inheritdoc />
        public CsvTable GetTable(string tableName)
        {
            string location = NormalizeLocation(tableName);
            return _tables.TryGetValue(location, out CsvTable table) ? table : null;
        }

        /// <inheritdoc />
        public bool IsLoaded(string tableName)
        {
            return _tables.ContainsKey(NormalizeLocation(tableName));
        }

        /// <inheritdoc />
        public void ReleaseTable(string tableName)
        {
            string location = NormalizeLocation(tableName);
            if (!_tables.TryGetValue(location, out CsvTable table))
            {
                return;
            }

            _tables.Remove(location);
            Release(table);
        }

        /// <inheritdoc />
        public void ReleaseAll()
        {
            foreach (KeyValuePair<string, CsvTable> pair in _tables)
            {
                Release(pair.Value);
            }

            _tables.Clear();
            _pending.Clear();
        }

        /// <summary>
        /// 把表名归一化成 YooAsset 资源地址：去掉目录与 .csv 扩展名。
        /// <remarks>Collector 用 AddressByFileName，因此地址就是文件名（不含扩展名）。</remarks>
        /// </summary>
        /// <param name="tableName">调用方传入的表名。</param>
        /// <returns>资源地址。</returns>
        private static string NormalizeLocation(string tableName)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                return string.Empty;
            }

            string name = tableName.Trim();
            int slash = Mathf.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            return name;
        }

        private static void Release(CsvTable table)
        {
            if (table?.SourceAsset == null)
            {
                return;
            }

            GameModule.Resource.UnloadAsset(table.SourceAsset);
            table.SourceAsset = null;
        }
    }
}
