using System.Threading;
using Cysharp.Threading.Tasks;

namespace GameLogic
{
    /// <summary>
    /// CSV 配置表模块：直接读取 CSV 作为数据源，不经过 Luban 导表流程。
    /// <para>
    /// 资源位置：<c>Assets/AssetRaw/Configs/*.csv</c>。该目录已在 YooAsset 的 Configs 收集组内
    /// （AddressByFileName + CollectAll），CSV 放进即被收集，资源地址等于文件名（不含扩展名），
    /// 因此表数据天然参与热更，无需任何额外配置。
    /// </para>
    /// <para>
    /// 访问入口：<c>GameModule.Csv</c>。
    /// </para>
    /// </summary>
    public interface ICsvConfigModule
    {
        /// <summary>
        /// 异步加载一张表（已加载则直接返回缓存）。
        /// </summary>
        /// <param name="tableName">表名，可写 <c>item</c> / <c>item.csv</c> / <c>Configs/item.csv</c>，都会归一化为资源地址。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>解析后的表。</returns>
        UniTask<CsvTable> LoadTableAsync(string tableName, CancellationToken cancellationToken = default);

        /// <summary>
        /// 取已加载的表，不触发加载。
        /// </summary>
        /// <param name="tableName">表名。</param>
        /// <returns>已加载的表；未加载返回 null。</returns>
        CsvTable GetTable(string tableName);

        /// <summary>
        /// 表是否已加载。
        /// </summary>
        /// <param name="tableName">表名。</param>
        /// <returns>已加载为 true。</returns>
        bool IsLoaded(string tableName);

        /// <summary>
        /// 释放一张表并归还其 TextAsset。
        /// </summary>
        /// <param name="tableName">表名。</param>
        void ReleaseTable(string tableName);

        /// <summary>
        /// 释放全部已加载的表。
        /// </summary>
        void ReleaseAll();
    }
}
