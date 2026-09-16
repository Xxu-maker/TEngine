# 配置表（CSV 直读）

> **适用场景**：新增/修改配置表、读表取值、表头格式约定 | **关联文档**：[bridge-tools.md](bridge-tools.md)（Unity 操作）、[resource-api.md](resource-api.md)（资源加载）、[modules.md](modules.md)（模块访问）

## 0. 结论：本项目不走 Luban

本项目**不使用**框架自带的 Luban 导表流程（需 .NET SDK + 克隆 luban 仓库 + 生成 bytes），
配置表以 **CSV 直接作为数据源**，运行时解析。

- 框架自带的 Luban 流程**已从工程中整体移除**（`Configs/GameConfig/`、`Tools/Luban/`、`Tools/build-luban.*`、
  `Assets/GameScripts/HotFix/GameProto/`、`Assets/TEngine/Editor/LubanTools/` 均已删除，热更程序集只剩 `GameLogic`）。
- 不要往 `Assets/GameScripts/HotFix/GameProto/` 放生成代码（该程序集已不存在），也不要把表数据放成 `.bytes`。

---

## 1. 文件放哪

```
Assets/AssetRaw/Configs/*.csv
```

该目录**已在 YooAsset 的 `Configs` 收集组内**（`Assets/Editor/AssetBundleCollector/AssetBundleCollectorSetting.asset`：
`AddressByFileName` + `PackDirectory` + `CollectAll`），因此：

- CSV 丢进去即被收集，**无需任何额外配置**；
- 资源地址 = 文件名（不含扩展名与目录），例如 `item.csv` → `"item"`；
- 数据随 AssetBundle 走，**天然参与热更**。

---

## 2. 表头格式

两种风格都支持，自动判定。

### 风格 A（推荐，Excel 直接导出即可）

```csv
id,name,hp,isBoss,spawnPos,dropList
int,string,int,bool,vector3,string
1001,小怪,100,false,"1,2,3","1001,2;1002,3"
1002,Boss,5000,true,"10,0,5",""
```

- 第 1 行：字段名（必填）
- 第 2 行：类型（**可选**；整行必须是已知类型名才会被当作类型行，否则按数据行处理）
- 以 `#` 开头的行视为注释，跳过
- 空行跳过

### 风格 B（兼容 Luban 表头，现有 xlsx 导出 CSV 后基本可直接用）

```csv
##var,id,name,hp,isBoss
##type,int,string,int,bool
##group,c,s,c,c
##,编号,名字,血量,是否Boss
1001,小怪,100,false
```

- `##var` 行为字段名，`##type` 行为类型，`##group` / `##` 等其它指令行忽略。

### 主键

名为 `id` 的列（不区分大小写）；**没有 `id` 列时取第 1 列**。

### 类型与取值

| 类型 | 取值方式 | 说明 |
|------|---------|------|
| `int` / `long` | `GetInt` / `GetLong` | 解析失败返回默认值 |
| `float` / `double` | `GetFloat` / `GetDouble` | 强制 InvariantCulture，与系统语言无关 |
| `bool` | `GetBool` | `1/true/yes/y/是` 为真，其余为假 |
| `string` | `GetString` | — |
| `vector2` / `vector3` | `GetVector2` / `GetVector3` | 单元格写 `"1,2,3"`（含逗号必须用双引号包裹）或 `1;2;3` |
| 列表 | `GetArray` / `GetArray2D` | 默认按 `;` 拆分，如 `"1001,2;1002,3"` |
| 其它（枚举、`datetime`、`#ref=...` 等） | 按 string 处理 | 类型声明中的 `#ref=`/`(list#sep=;)`/`?` 会被安全剥离，原始文本仍可用 |

---

## 3. 代码位置

| 文件 | 职责 |
|------|------|
| `Assets/GameScripts/HotFix/GameLogic/Config/ICsvConfigModule.cs` | 模块接口 |
| `Assets/GameScripts/HotFix/GameLogic/Config/CsvConfigModule.cs` | 加载/缓存/释放（`GameLogic.CsvConfigModule`，由 ModuleSystem 按接口名约定反射创建） |
| `Assets/GameScripts/HotFix/GameLogic/Config/CsvTable.cs` | 表：表头解析、主键索引、行访问 |
| `Assets/GameScripts/HotFix/GameLogic/Config/CsvRow.cs` | 行：类型化取值 |
| `Assets/GameScripts/HotFix/GameLogic/Config/CsvParser.cs` | RFC4180 词法（引号、`""` 转义、字段内逗号与换行） |

全部位于**热更程序集 GameLogic**，改解析逻辑不需要重新出包。

---

## 4. 用法

```csharp
using GameLogic;
using Cysharp.Threading.Tasks;

// 1) 异步加载（首次加载后缓存）
CsvTable item = await GameModule.Csv.LoadTableAsync("item");

// 2) 按主键取行
CsvRow row = item.Get(10002);
if (item.TryGet(99999, out CsvRow maybe)) { /* 存在才用 */ }

// 3) 取值
string name  = row.GetString("name");
int    price = row.GetInt("price", 0);
bool   batch = row.GetBool("batch_useable");
string[] exchange = row.GetArray("exchange_list");        // "10001,2" / "10002,3"
Vector3 pos  = row.GetVector3("spawnPos");

// 4) 遍历
foreach (CsvRow r in item.Rows) { /* ... */ }
List<string> names = item.Collect("name");

// 5) 只取已加载的表（不触发加载）
CsvTable cached = GameModule.Csv.GetTable("item");

// 6) 释放（归还 TextAsset，遵守"加载即对应卸载"）
GameModule.Csv.ReleaseTable("item");
GameModule.Csv.ReleaseAll();
```

**表名写法**：`"item"` / `"item.csv"` / `"Configs/item.csv"` 都会归一化为资源地址 `"item"`。

### 约定与红线

- **异步优先**：只提供 `LoadTableAsync`，没有同步加载 API；启动期读表用 `await` 或在 Procedure 里串。
- 同一张表**并发加载会合并**为一次，不会重复解析。
- 读表失败（文件不存在）会抛 `GameFrameworkException`，文案里带排查提示。
- 用完要 `ReleaseTable`；常驻表可以不释放（`Shutdown` 时会统一释放）。

---

## 5. 新增一张表的完整流程

1. 在 `Assets/AssetRaw/Configs/` 新建 `monster.csv`，按 §2 写表头与数据（Excel 另存为 CSV 亦可）。
2. 回到 Unity 触发刷新：`unity_bridge("manage_editor", { action = "refresh" })`。
3. 代码里 `await GameModule.Csv.LoadTableAsync("monster")` 即可用。

**没有导表步骤、没有生成代码、没有中间产物。**

---

## 6. 常见错误

| 现象 | 原因 | 解决 |
|------|------|------|
| 抛"资源不存在" | 文件名与表名不一致 / 没刷新 | 确认 CSV 在 `Assets/AssetRaw/Configs/`，执行一次 `refresh` |
| 整表读成字符串 | 类型行没被识别（某格写了非法类型名） | 检查第 2 行是否全部为已知类型 |
| `GetVector3` 总是返回默认值 | 单元格没加双引号，逗号被当成列分隔 | 写成 `"1,2,3"` |
| 主键取错 | 既没有 `id` 列，第 1 列又不是唯一键 | 显式加 `id` 列，或保证首列唯一 |
| 中文乱码 | CSV 不是 UTF-8 | Excel 导出时选「CSV UTF-8」 |
| 热更后表没更新 | 资源没重新打包 | 走 YooAsset 打包流程（CSV 与其它资源同等对待） |

---

## 7. 交叉引用

| 主题 | 文档 |
|------|------|
| 资源加载/释放 | [resource-api.md](resource-api.md) |
| 模块访问入口 | [modules.md](modules.md) |
| 热更边界（哪些改动要出包） | [hotfix-workflow.md](hotfix-workflow.md) |
| 框架自带 Luban 流程（**已移除**） | — |
