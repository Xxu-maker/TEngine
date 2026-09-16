# Codely Bridge 工具指南

> **适用场景**：通过 DSH 驱动 Unity 编辑器 —— 场景管理、GameObject/组件操作、UI Prefab 拼接、脚本读写、资源操作、编译与 Play 验证、编辑器自动化 | **关联文档**：[bridge-visual.md](bridge-visual.md)（材质/Shader/VFX/动画）、[naming-rules.md](naming-rules.md)（命名约定）、[architecture.md](architecture.md)（目录约定）

## 0. 通道现状（2026-09 变更，必读）

工程**已移除 MCPForUnity**：`Packages/manifest.json` 无 MCP 条目，616 个文件已删除，只剩两个过期的 `MCPForUnity.*.csproj`。

当前唯一的 Unity 通道是 **Codely Bridge**：

| 环节 | 事实 |
|------|------|
| Unity 侧 | `cn.tuanjie.codely.bridge` 1.0.81（`Packages/manifest.json`），随编辑器启动，TCP 监听 |
| 端口发现 | `<UnityProject>/Temp/.com-unity-codely.json` 的 `unity_port`（**不要写死端口**，端口冲突会自动换） |
| DSH 侧 | 动态 Cordis 插件拉起 `.codely-bridge/client.cjs`（仓库根），协议：8 字节大端长度前缀 + JSON |
| 暴露的工具 | `unity_state`（无参状态快照）、`unity_bridge`（`{type, params}` 通用调用） |

> 本文件取代旧的 `mcp-tools.md`。旧文档里的 `batch_execute` / `manage_ui` / `manage_components` / `find_gameobjects` / `manage_prefabs` / `apply_text_edits`(工具) / `run_tests` / `refresh_unity` **在桥上都不存在**，切勿再按那套调用。

---

## 1. 调用约定

```
unity_bridge( type = "<命令名>", params = { "action": "<动作名>", ... }, timeoutMs? )
```

| 概念 | 规则 |
|------|------|
| `type` | 命令名，见 §3 总表 |
| `action` | 桥内部会 `ToLower()`，**白名单项必须全小写**；未知动作报 `Unknown action: 'x'. Valid actions are: ...`（会列出全部合法动作，照抄即可） |
| action 可省的命令 | 只有 `read_console`（默认 `get`）和 `execute_menu_item`（默认 `execute`）；其余命令**必填** |
| 无 action 的命令 | `execute_csharp_script`、`exec_editor_script`、`exec_runtime_script`、`execute_custom_tool`、`get_custom_tools`（参数平铺） |
| 别名 | `manage_gameobject`：`set_component_properties` → `set_component_property`；`manage_job`：`check` → `status`。其余命令无别名 |
| 成功信封 | `{ success:true, message:"Command executed successfully", data:{ ...命令自身结果... }, request_id }` —— 业务结果在 `data` 里，常见再嵌一层 `data` 或 `state` |
| 失败信封 | `{ success:false, message, code, error, data? }`（三者常为同一句话）；写守卫拒绝时 `code = write_blocked_in_play_mode` |
| 超时 | 默认 120s，上限 600s；编译/Play/烘焙这类慢操作要显式调大（`timeoutMs`，配合 `timeoutSeconds`） |
| 无轮询机制 | 响应只在工作完成后返回，**不存在** pending/op_id/poll_interval；需要查后台作业用 `manage_job` |

---

## 2. 核心原则：状态 → 动作 → 验证 → 纠正

1. **写操作前先 `unity_state`**，确认 `playMode`、`isCompiling`、`sceneDirty`。
2. 写操作后**确认新状态**，不要假设成功。
3. **脚本改动必须编译验证**：`manage_editor {action:"request_compile", timeoutSeconds:180}` → `read_console` 零 error 才继续。
4. **关键边界先保存**：进入 Play、烘焙、切场景前 `manage_scene {action:"save"}`。
5. 优先用 `ensure_*` 系列（幂等），避免反复增删。

---

## 3. 命令总表

| type | 动作数 | 用途 |
|------|-------|------|
| `manage_editor` | 30 | 编译、Play/暂停/单帧、状态查询、Tag/Layer、窗口焦点 |
| `manage_scene` | 8 | 场景打开/保存/创建/加载、层级树、Build Settings |
| `manage_gameobject` | 16 | GO 创建/修改/删除/查找、组件增删改、批量操作、存 Prefab |
| `manage_asset` | 12 | 资源搜索/信息/增删改移、导入、文件夹、脏 meta 修复 |
| `manage_script` | 8 | 脚本创建/读取/更新/删除/精确编辑/校验/SHA |
| `manage_shader` | 4 | 渲染管线探测、SRP 材质适配、编译、预览 |
| `read_console` | 2 | 读/清 Console |
| `execute_menu_item` | 2 | 执行编辑器菜单、列出可用菜单 |
| `manage_screenshot` | 10 | 场景/相机/资产/UI Toolkit 截图、GameView 录屏 |
| `manage_input` | 3 | 虚拟输入设备、按键、点击 |
| `manage_gameview` | 3 | GameView 分辨率查询/设置 |
| `manage_bake` | 5 | 烘焙 NavMesh / 光照、等待、清理 |
| `manage_package` | 3 | UPM 包列表/安装/移除 |
| `manage_dialog` | 1 | 点击卡住主线程的模态对话框 |
| `manage_job` | 3 | 异步任务状态/列表/取消 |
| `manage_window_bridge` | 9 | 编辑器窗口枚举/焦点/原生窗口/帧流 |
| `_internal_state_dirty` | 3 | 文件变更通知（桥内部用） |
| `execute_csharp_script` / `exec_editor_script` / `exec_runtime_script` | — | 执行 C#（编辑器态 / 运行态） |
| `execute_custom_tool` / `get_custom_tools` | — | 工程自定义工具调用/清单 |

---

## 4. 逐命令动作表

### manage_editor

| action | 说明 | 关键参数 |
|--------|------|---------|
| `request_compile` / `start_compilation_pipeline` | 触发编译并等待完成 | `timeoutSeconds` |
| `get_compilation_summary` | 编译摘要 | — |
| `wait_for_idle` | 等编辑器空闲（编译/导入/刷新结束） | `timeoutSeconds` |
| `refresh` | AssetDatabase 刷新（新资源、新脚本后） | — |
| `play` / `pause` / `resume` / `stop` / `step` | Play 模式控制（`play`/`stop` 会跑完整个域重载流程） | `timeoutSeconds` |
| `get_state` / `get_current_state` | 编辑器状态快照（等价于 `unity_state`） | — |
| `get_project_root` / `get_windows` / `get_active_tool` / `get_selection` | 查询 | — |
| `set_active_tool` | 切换当前工具 | `toolName` |
| `ensure_tag` / `add_tag` / `remove_tag` / `get_tags` | Tag 管理 | `tagName` |
| `ensure_layer` / `add_layer` / `remove_layer` / `get_layers` | Layer 管理 | `layerName` |
| `focus_window` | 聚焦编辑器窗口 | `windowType` |
| `drain_agent_input` | 取走挂起的代理输入 | — |
| `wait_for_compile` / `wait_for_stop` / `publish_dirty_state_if_needed` | **已废弃**，分别改用 `request_compile` / `get_state` | — |

### manage_scene

| action | 说明 | 关键参数 |
|--------|------|---------|
| `get_active` | 当前场景信息 | — |
| `get_hierarchy` | 场景层级（**分页**） | `parent`, `pageSize`, `cursor`, `maxDepth` |
| `save` | 保存当前场景 | — |
| `load` | 加载场景 | `path` / `name` |
| `create` | 新建场景 | `path` / `name` |
| `ensure_scene_open` / `ensure_scene_saved` | 幂等：确保已打开 / 已保存 | `path` |
| `get_build_settings` | Build Settings 场景列表 | — |

`get_hierarchy` 返回带游标，`truncated=true` 时按 `next_cursor` 翻页。

### manage_gameobject

| action | 说明 | 关键参数 |
|--------|------|---------|
| `create` | 创建 GO | `name`, `parent`, `position`, `rotation`, `scale`, `primitiveType`, `componentType`；**`saveAsPrefab: true` + `prefabPath` 可同时存成 Prefab** |
| `modify` | 修改属性/父子/激活 | `target`, `name`, `parent`, `position`, `rotation`, `scale`, `setActive` |
| `delete` | 删除 | `target` |
| `find` | 查找 GO | `name`/`path`, `searchMethod`, `maxResults` |
| `list_children` | 列子节点 | `target`, `depth` |
| `get_components` | 列组件 | `target` |
| `add_component` / `remove_component` | 增删组件 | `target`, `componentType` |
| `set_component_property` | 改组件属性 | `target`, `componentType`, `propertyName`, `value` |
| `select` | 选中 | `target` |
| `create_batch` / `edit_batch` | **批量创建/编辑（多对象优先用它）** | 数组参数 |
| `ensure_component` | 幂等确保组件存在 | `target`, `componentType` |
| `ensure_renderer_material` | 幂等设置渲染器材质 | `target`, `materialPath` |
| `ensure_mesh_collider_mesh` | 幂等设置 MeshCollider 网格 | `target` |
| `ensure_prefab_default_sprite` | 幂等设置 Prefab 默认图 | `target` |

### manage_asset

| action | 说明 | 关键参数 |
|--------|------|---------|
| `search` | 搜索资源 | `query`, `type`, `path` |
| `get_info` | 资源信息（类型/GUID/大小/依赖） | `path`, `generatePreview` |
| `create` | 创建资源 | `path`, …（Prefab 也可走 `manage_gameobject.create` 的 `saveAsPrefab`） |
| `modify` | 改资源 | `path`, `properties` |
| `move` | 移动/重命名 | `path`, `destination` |
| `duplicate` | 复制 | `path`, `destination` |
| `delete` | 删除 | `path` |
| `create_folder` | 建文件夹 | `path` |
| `import` | 重新导入 | `path` |
| `get_components` | 取 Prefab/SO 上的组件 | `path` |
| `ensure_has_meta` / `ensure_meta_integrity` | 幂等修复缺失/损坏的 `.meta` | `path` |

### manage_script

| action | 说明 | 关键参数 |
|--------|------|---------|
| `create` | 新建脚本 | `name`, `path`, `contents`, `namespace` |
| `read` | 读取内容 | `name` / `path` |
| `get_sha` | 取 SHA256（**编辑前必须先取**） | `name` / `path` |
| `apply_text_edits` | **精确区间编辑（推荐）** | `precondition_sha256`, `edits[]`（起始/结束行列 + 新文本） |
| `update` / `edit` | 整体或局部更新 | `name`, `path`, `contents` |
| `validate` | 语法校验 | `name`, `path`, `level` |
| `delete` | 删除 | `name`, `path` |

编辑流程固定为：`read` → `get_sha` → `apply_text_edits`（带 `precondition_sha256`）→ `manage_editor request_compile` → `read_console`。

### execute_menu_item

| action | 说明 | 关键参数 |
|--------|------|---------|
| `execute` | 执行菜单项 | `path`（如 `Assets/Refresh`） |
| `get_available_menus` | 列出可用菜单 | — |

TEngine 常用菜单：`Assets/Refresh`、`File/Save Project`、`HybridCLR/Generate/All`、`HybridCLR/Build/BuildAssets And CopyTo AssemblyPath`、`Tools/YooAsset/...`。

### read_console

| action | 说明 | 关键参数 |
|--------|------|---------|
| `get` | 读日志 | `types`（`error`/`warning`/`log`/`exception`/`assert`）、`count` |
| `clear` | 清空 | — |

**注意**：策略是 `clear_then_read` —— 读会清空未读标记，排查编译错误时要一次性读全。

### manage_bake / manage_package / manage_job / manage_input / manage_gameview

| 命令 | 动作 |
|------|------|
| `manage_bake` | `bake_navmesh`、`bake_lighting`、`wait_for_bake`、`clear_navmesh`、`clear_baked_data` |
| `manage_package` | `list_packages`、`install_package`、`remove_package` |
| `manage_job` | `status`、`list`、`cancel`（异步任务的查询与取消） |
| `manage_input` | `key_press`、`mouse_click`、`create_virtual_devices` |
| `manage_gameview` | `get_resolution`、`set_resolution`、`list_resolutions` |

### 执行 C#

| type | 用途 | 关键参数 |
|------|------|---------|
| `exec_editor_script` | **编辑器态**执行 C#（不进入 Play） | `script`、`description`、`capture_logs` |
| `exec_runtime_script` | **运行态**执行 C#（需已在 Play） | `script`、`record_game_view` |
| `execute_csharp_script` | 旧版通用入口，支持 REPL 会话 | `script`、`enable_repl`、`script_session_id` |

**Roslyn 提交的硬规则（实测）**：
- 脚本是**脚本提交**，不是完整编译单元；**结尾不能是裸表达式**（会报 `CS0201`）。
- 需要看结果就传 `capture_logs: true` + `UnityEngine.Debug.Log(...)`，结果从返回的 `logs` 数组里取。
- 编辑器态与运行态互斥：`exec_editor_script` 在 Play 中会被拒，`exec_runtime_script` 在非 Play 会被拒。

---

## 5. 典型工作流

### 5.1 UI Prefab 拼接（旧 `manage_ui` 的等价做法）

桥**没有** `manage_ui`，用 `manage_gameobject` + `add_component` 组合：

```
1. manage_gameobject create      name=XxxUI, componentType=Canvas
2. manage_gameobject add_component target=XxxUI, componentType=CanvasScaler
3. manage_gameobject add_component target=XxxUI, componentType=GraphicRaycaster
4. manage_gameobject create_batch  [ 所有子节点：m_tf_/m_img_/m_tmp_/m_btn_ ... ]
5. manage_gameobject add_component 给交互节点挂对应组件
6. manage_gameobject create       让根节点存盘：name=XxxUI, saveAsPrefab=true,
                                  prefabPath=Assets/AssetRaw/UI/Prefabs/XxxUI.prefab
7. manage_gameobject delete       清理场景里的临时 GO
```

- 前缀 → 组件绑定见 [naming-rules.md](naming-rules.md#ui-节点命名规范)；**生成器只认 ruler 表里列出的前缀**，别照文档臆造前缀。
- 根节点必须有 Canvas（UIModule 加载时强制检查）。
- Canvas 适配：Scale With Screen Size / 1920×1080 / Match 0.5。

### 5.2 脚本改动

```
manage_script read → get_sha → apply_text_edits(precondition_sha256)
→ manage_editor request_compile(timeoutSeconds=180) → read_console(types=[error])
```

### 5.3 Play 模式验证

```
unity_state 确认 sceneDirty=false
→ manage_editor play(timeoutSeconds=180)   # 会经历域重载，客户端会自动重连
→ unity_state 确认 playMode=playing
→ read_console 查运行期 error
→ manage_editor stop(timeoutSeconds=120)
```

### 5.4 出错先看状态再重试

任何 `success:false` 先看 `message` 的 `code`/文案；`Unknown action` 类错误会直接列出合法动作，照抄即可。

---

## 6. 守卫与坑（均为实测或源码确认）

### 6.1 Play 模式写守卫（精确范围）

桥在派发**之前**用**原始 action 字符串**查写表；Play/Paused 且策略为 `deny` 时直接拒绝：

```
{ success:false, code:"write_blocked_in_play_mode",
  data:{ operation:"manage_gameobject.create", currentPlayMode:"playing", policy:"deny",
         suggestion:"Stop Play mode or change policy to 'allow_with_warning'" } }
```

**被列为写的动作**（只有这些会被拦）：

| 命令 | 受保护动作 |
|------|-----------|
| `manage_gameobject` | `create` `create_batch` `edit_batch` `modify` `delete` `add_component` `remove_component` `set_component_property` `ensure_component` `ensure_renderer_material` `ensure_mesh_collider_mesh` `ensure_prefab_default_sprite` |
| `manage_asset` | `import` `create` `modify` `delete` `duplicate` `move` `create_folder` `ensure_has_meta` |
| `manage_script` | `create` `update` `edit` `delete` `apply_text_edits` |
| `manage_scene` | `create` `load` `save` `ensure_scene_open` `ensure_scene_saved` |
| `manage_editor` | `add_tag` `remove_tag` `ensure_tag` `add_layer` `remove_layer` `ensure_layer` `set_active_tool` |
| `manage_package` | `install_package` `remove_package` |
| `manage_bake` | `bake_navmesh` `bake_lighting` `clear_navmesh` `clear_baked_data` |

**未列入 ⇒ 在 Play 下也可用**：`read_console`、`execute_menu_item`、`manage_screenshot`、`manage_input`、
`manage_window_bridge`、`manage_gameview`、`manage_job`、`manage_dialog`、全部 `exec_*_script`，
以及 `manage_editor` 的 `refresh` / `request_compile` / `play` / `stop` / `pause` / `step`（这些动作自带 Play 模式校验）。

> ⚠️ 守卫用的是**归一化前的原始字符串**：用别名 `set_component_properties` 调用可绕过写保护——**不要利用这一点**。

### 6.2 其它守卫与坑

| 现象 | 原因 | 应对 |
|------|------|------|
| `manage_asset` 传 `rename` 报 Unknown action | **不存在** `rename`（写表里有该条目，但动作表里没有） | 用 `move`（`path` + `destination`） |
| `capture_game_view` 报 "is disabled" | Play 模式静态截图被禁，桥要求用动态验证 | 用 `exec_runtime_script` + `record_game_view` 录 MP4；确实只要静态画面时传 `allow_static_in_play_mode: true` + 非空 `static_reason` |
| `capture_main_camera` 在 Play 中被拒 | 同上策略 | 同上 |
| C# 脚本报 `CS0201` | 脚本提交不接受尾表达式 | 改成语句 + `Debug.Log` + `capture_logs` |
| 改完脚本没生效 | 未编译 | 必须 `request_compile` 并读 Console 确认 0 error |
| `manage_scene` 偶发超时 | 它独占主线程派发，超时 3s | 减少并发调用，或退避后重试 |
| 端口连不上 | Unity 正在编译/域重载 | 客户端会自动重连；持续失败再查 `.com-unity-codely.json` 的 `reason` |
| 场景操作被拒 | 场景有未保存改动 | `manage_scene save` / `ensure_scene_saved` |

底层契约（信封字段、ActionRouter 规则、WriteGuard 全表、作业投递时机）见 [bridge-protocol.md](bridge-protocol.md)。

---

## 7. 与 TEngine 的目录约定

| 类型 | 路径 |
|------|------|
| UIWindow/Widget | `Assets/GameScripts/HotFix/GameLogic/UI/<模块名>/` |
| 模块代码 | `Assets/GameScripts/HotFix/GameLogic/Module/<ModuleName>/` |
| 事件接口 | `Assets/GameScripts/HotFix/GameLogic/IEvent/` |
| UI Prefab | `Assets/AssetRaw/UI/Prefabs/` |
| 配置表（CSV 直读） | `Assets/AssetRaw/Configs/*.csv` |
| 热更 DLL | `Assets/AssetRaw/DLL/` |

视觉类操作（材质/Shader/纹理/粒子/动画）见 [bridge-visual.md](bridge-visual.md)。
