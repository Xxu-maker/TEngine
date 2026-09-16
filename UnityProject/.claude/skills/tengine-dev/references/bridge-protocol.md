# Codely Bridge 底层契约（协议 / 分发 / 守卫）

> **适用场景**：排查桥调用失败、确认信封字段、判断某动作在 Play 模式下是否受写保护、理解作业返回时机 | **关联文档**：[bridge-tools.md](bridge-tools.md)（怎么用、动作总表）

信封：`{"type":"<命令名>","params":{"action":"<动作名>", ...}}`（`Command` 类：`type`、`@params`，UnityTcpBridge.cs:17-21）
提取自 `cn.tuanjie.codely.bridge@1.0.81` 源码，逐条带行号，无推测。

---

## 0. 全局机制（ActionRouter / Response / WriteGuard / 派发）

## 0.1 命令分发（UnityTcpBridge.ExecuteCommand，UnityTcpBridge.cs:473-614）

| 事实 | 源码行 |
|---|---|
| 空 type → `Response.Error("Command type cannot be empty", "A valid command type is required for processing")` | 477-482 |
| 空文本命令 → `Response.Error("Empty command received")`；非法 JSON → `Response.Error("Invalid JSON format", { receivedText })`；反序列化为 null → `Response.Error("Command deserialized to null")` | 357-377 |
| 取值：`JObject paramsObject = command.@params ?? new JObject(); string action = paramsObject["action"]?.ToString();` | 485-486 |
| **写保护检查在此处、以“原始未归一化的 action 字符串”进行**：`WriteGuard.CheckCommandWriteAllowed(command.type, action)`；被拒时返回 `Response.Error("Command blocked by write guard", writeBlocked)` | 488-496 |
| 命令名 → 处理器 switch | 500-583 |
| 处理器返回 `JobContext` 时**不立即响应**（`return null`），响应由 runner 稍后入队 | 585-588 |
| 正常响应外层信封：`Response.Success("Command executed successfully", <handler result>)` | 593-594 |
| 异常响应：`Response.Error(ex.Message, { command, stackTrace, paramsSummary })` | 604-612 |
| `manage_scene` 独占主线程派发 `MainThreadHelper.InvokeOnMainThreadWithTimeout(..., FrameIOTimeoutMs)`；`FrameIOTimeoutMs = 3000`；超时 null → `TimeoutException($"manage_scene timed out after {FrameIOTimeoutMs} ms on main thread")` / `Response.Error("manage_scene returned null (timeout or error)")` | 44, 513-516, 616-631 |
| 后台 worker 线程白名单：`BackgroundTypes = { "manage_job", "manage_dialog" }`；其它类型落到后台线程的响应为 `Response.Error($"Command '{command?.type}' cannot run on the background executor.")` | BackgroundCommandPump.cs:215, 222-247；UnityTcpBridge.cs:410-452 |
| 后台路径信封同样是 `Response.Success("Command executed successfully", result)` | UnityTcpBridge.cs:433-434 |
| 域重载前广播 `script_session_destroyed`（payload 带 `script_session_id`） | 645-666 |

### 命令名清单（switch 全量，UnityTcpBridge.cs:500-583）

| # | type | 处理器 | 行 |
|---|---|---|---|
| 1 | `manage_script` | `ManageScript.HandleCommand` | 502-503 |
| 2 | `manage_workflow` | **已移除**，返回 `Response.ErrorWithCode("manage_workflow_removed", "manage_workflow has been removed. Use manage_editor with action 'request_compile' to compile and validate scripts instead.")` | 507-512 |
| 3 | `manage_scene` | `ManageScene.HandleCommand`（主线程，3s 超时） | 513-516 |
| 4 | `manage_editor` | `ManageEditor.HandleCommand` | 517-519 |
| 5 | `manage_gameobject` | `ManageGameObject.HandleCommand` | 520-522 |
| 6 | `manage_asset` | `ManageAsset.HandleCommand` | 523-525 |
| 7 | `manage_shader` | `ManageShader.HandleCommand` | 526-528 |
| 8 | `read_console` | `ReadConsole.HandleCommand` | 529-531 |
| 9 | `execute_menu_item` | `ExecuteMenuItem.HandleCommand` | 532-534 |
| 10 | `execute_csharp_script` | `ExecuteCSharpScript.HandleCommand` | 535-537 |
| 11 | `exec_editor_script` | `ExecuteScriptCommand.HandleEditorScript` | 538-540 |
| 12 | `exec_runtime_script` | `ExecuteScriptCommand.HandleRuntimeScript` | 541-543 |
| 13 | `manage_package` | `ManagePackage.HandleCommand` | 544-546 |
| 14 | `manage_bake` | `ManageBake.HandleCommand` | 547-549 |
| 15 | `execute_custom_tool` | `ExecuteCustomTool.HandleCommand` | 550-552 |
| 16 | `get_custom_tools` | `ExecuteCustomTool.GetCustomToolsInfo()`（无 params） | 553-555 |
| 17 | `_internal_state_dirty` | `_InternalStateDirtyNotifier.HandleCommand` | 556-558 |
| 18 | `manage_window_bridge` | `ManageWindowBridge.HandleCommand` | 559-561 |
| 19 | `manage_gameview` | `ManageGameView.HandleCommand` | 562-564 |
| 20 | `manage_screenshot` | `ManageScreenshot.HandleCommand` | 565-567 |
| 21 | `manage_input` | `ManageInput.HandleCommand` | 568-570 |
| 22 | `manage_dialog` | `ManageDialog.HandleCommand` | 571-573 |
| 23 | `manage_job` | `ManageJob.HandleCommand`（主线程回退路径；正常走后台 worker） | 574-578 |
| — | 其他 | `throw new ArgumentException($"Unknown or unsupported command type: {command.type}")` → 由 catch 包成 `Response.Error(ex.Message, {...})` | 579-582, 596-613 |

## 0.2 ActionRouter 解析规则（Helpers/ActionRouter.cs，全文 86 行）

| 规则 | 事实 | 行 |
|---|---|---|
| action 取值 | `action = @params?["action"]?.ToString()?.ToLower()` —— **强制小写** | 21 |
| action 可省条件 | 仅当调用方传入 `defaultAction` 时才可省：为空则 `action = defaultAction` | 22-25 |
| 不提供默认且 action 空 | `Response.Error("Action parameter is required.")`，返回 false | 27-31 |
| 别名映射 | `aliases.TryGetValue(action, out canonical)` → 用 canonical 覆盖 action；**别名表键为小写**，查表在“白名单校验之前” | 33-36 |
| 白名单校验 | 对 `validActions` 做 `valid == action` **区分大小写的顺序比较**（action 已小写，故白名单项必须全小写） | 38-48 |
| 未知 action 报错文案 | `Response.Error($"Unknown action: '{action}'. Valid actions are: {string.Join(", ", validActions)}")`（顺序即白名单/字典顺序） | 50-56 |
| Route 的异常包装 | `catch (Exception e) { return Response.Error($"Internal error processing action '{action}': {e.Message}"); }` | 74-83 |
| Route 默认参数 | `aliases = null`、`defaultAction = null` | 63-67 |

各命令的 action 是否必填 / 默认值（由调用点决定）：

| 命令 | 调用形式 | action 必填? | 别名 | 默认 action |
|---|---|---|---|---|
| manage_editor | `ActionRouter.Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| manage_gameobject | `TryResolve(@params, ActionHandlers.Keys, ..., ActionAliases)` 后 `Route(@params, ActionHandlers, ActionAliases)` | 必填 | `set_component_properties`→`set_component_property` | 无 |
| manage_asset | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| manage_shader | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| manage_scene | `TryResolve(new JObject{["action"]=action}, ValidActions, ...)`（未传 defaultAction） | 必填 | 无 | 无 |
| manage_script | `TryResolve(@params, ScriptActions, ...)` 后 `Route(@params, BuildActionHandlers(...))` | 必填 | 无 | 无 |
| manage_screenshot | `TryResolve(@params, ValidActions, ...)` | 必填 | 无（`capture`/`capture_scene_camera` 是同分发的合法 action，不是别名表项） | 无 |
| manage_input | `TryResolve(@params, ValidActions, ...)` | 必填 | 无 | 无 |
| manage_gameview | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| manage_bake | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| manage_package | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| manage_dialog | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| manage_job | `Route(@params, ActionHandlers, ActionAliases)` | 必填 | `check`→`status` | 无 |
| read_console | `Route(@params, ActionHandlers, defaultAction: "get")` | **可省**（省略即 `get`） | 无 | `get` |
| execute_menu_item | `Route(@params, ActionHandlers, defaultAction: "execute")` | **可省**（省略即 `execute`） | 无 | `execute` |
| manage_window_bridge | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| _internal_state_dirty | `Route(@params, ActionHandlers)` | 必填 | 无 | 无 |
| execute_csharp_script / exec_editor_script / exec_runtime_script | **不使用 ActionRouter，无 action 字段**（参数平铺） | — | — | — |
| execute_custom_tool / get_custom_tools | **不使用 ActionRouter，无 action 字段** | — | — | — |

## 0.3 Response 帮助类（Helpers/Response.cs，全文 85 行）

| 方法 | 返回信封字段 | 行 |
|---|---|---|
| `Response.Success(string message, object data = null)` | `{ "success": true, "message": message }`，仅当 `data != null` 时追加 `"data"` | 27-41 |
| `Response.Error(string errorCodeOrMessage, object data = null)` | `{ "success": false, "message": X, "code": X, "error": X }`（同一字符串填入三者），仅当 `data != null` 时追加 `"data"` | 49-65 |
| `Response.ErrorWithCode(string code, string message)` | `{ "success": false, "message": message, "code": code, "error": code }`，**无 data 字段** | 76-83 |

类注释事实（Response.cs:8-18）：

- 响应格式为 `{ success, message, data? }`；
- **不存在 pending / op_id / poll_interval 变体**：工具调用只在工作完成后返回，客户端无需轮询或关联；
- 响应只携带工具自身结果；编辑器状态必须显式通过 `manage_editor.get_state` 读取，不作为每个响应的附带字段。

嵌套事实：主线程/后台派发路径都会把 handler 返回值作为外层 `data` 再包一层 `Response.Success("Command executed successfully", result)`（UnityTcpBridge.cs:593-594、433-434）。因此典型响应对大多数命令是双层：`{success, message:"Command executed successfully", data:{success, message, data}}`。

## 0.4 WriteGuard（Helpers/WriteGuard.cs，全文 319 行）

| 项 | 事实 | 行 |
|---|---|---|
| 策略枚举 | `Policy.Deny`（默认）、`Policy.AllowWithWarning`；当前策略默认 `Deny` | 17-31 |
| 判定时机 | 桥接在派发前调用 `WriteGuard.CheckCommandWriteAllowed(command.type, action)`（action 为**原始字符串**，未小写/未做别名归一化） | UnityTcpBridge.cs:491 |
| 判定函数 | `IsWriteAction`：`WriteActions` 字典用 `StringComparer.OrdinalIgnoreCase`，内层 HashSet 同样忽略大小写；`commandName`/`action` 任一为空返回 false | 138-147 |
| Play/Paused 判定 | `EditorApplication.isPlaying \|\| EditorApplication.isPaused` | 173 |
| 非 Play 模式 | 直接放行（返回 null） | 175-179 |
| Deny 时错误体 | `Response.Error("write_blocked_in_play_mode", new { code="write_blocked_in_play_mode", message, currentPlayMode = "playing"/"paused", policy="deny", suggestion="Stop Play mode or change policy to 'allow_with_warning'" })`；其中 `message = $"Cannot perform {operationName} in Play/Paused mode. Stop Play mode first, or change write guard policy to 'allow_with_warning' (experimental)."`；`operationName` 为 `"{command}.{action}"` | 154-162, 184-198 |
| AllowWithWarning 时 | 记 warning 日志、写审计日志、放行（返回 null） | 200-211 |
| 其它 helper | `IsWriteBlocked()`（Play/Paused 且策略为 Deny）、`SetPolicy(string)`（接受 `"deny"`、`"allow_with_warning"`，否则 `Response.Error($"Invalid policy: '{policy}'. Valid policies are: 'deny', 'allow_with_warning'")`）、`GetPolicyString()`、`CreateBlockedResponse(...)`（返回 `write_blocked_in_play_mode` + `{code, operation, currentMode, policy, message, remediation{options[]}}`） | 221-316 |

### WriteActions 全量写表（Helpers/WriteGuard.cs:44-123，逐条照抄）

| 命令名 | 被列为写的顶层 action（穷尽） |
|---|---|
| `manage_gameobject` | `create_batch`, `edit_batch`, `ensure_component`, `ensure_renderer_material`, `ensure_mesh_collider_mesh`, `ensure_prefab_default_sprite`, `create`, `modify`, `delete`, `add_component`, `remove_component`, `set_component_property` |
| `manage_asset` | `create_batch`, `edit_batch`, `import`, `ensure_has_meta`, `create`, `modify`, `delete`, `duplicate`, `move`, `rename`, `create_folder` |
| `manage_shader` | `ensure_material_shader_for_srp` |
| `manage_script` | `create`, `update`, `delete`, `apply_text_edits`, `edit` |
| `manage_scene` | `ensure_scene_open`, `ensure_scene_saved`, `create`, `load`, `save` |
| `manage_package` | `install_package`, `remove_package` |
| `manage_bake` | `bake_navmesh`, `bake_lighting`, `clear_navmesh`, `clear_baked_data` |
| `manage_editor` | `add_tag`, `remove_tag`, `ensure_tag`, `add_layer`, `remove_layer`, `ensure_layer`, `set_active_tool` |
| 其它命令 | 无条目 → 不受写保护（含 `manage_input`、`manage_screenshot`、`manage_window_bridge`、`manage_gameview`、`read_console`、`execute_menu_item`、`execute_csharp_script` 等） |

写表与真实 action 的逐条对照（用于判定死条目/缺口）：

| 命令 | 写表条目是否存在于该命令的 action 集 | 备注 |
|---|---|---|
| `manage_gameobject` | 12/12 全部存在 | 但别名 `set_component_properties` 不在表中（写保护先于别名解析，见 0.2/0.4） |
| `manage_asset` | `create_batch`、`edit_batch`、`rename` **不存在**（全文 grep 仅命中 ManageGameObject.cs）；其余 8 项存在 | 死条目 3 个；`ensure_meta_integrity`、`search`、`get_info`、`get_components` 为只读，未列 |
| `manage_shader` | 存在 | — |
| `manage_script` | 5/5 存在 | — |
| `manage_scene` | 5/5 存在（ValidActions 8 项中的 5 项） | 3 个 get_* 未列 |
| `manage_package` | 2/2 存在 | `list_packages` 未列（只读） |
| `manage_bake` | 4/4 存在 | `wait_for_bake` 未列（废弃 action） |
| `manage_editor` | 7/7 存在 | `refresh`、`request_compile`、`start_compilation_pipeline` 不在表中，但自带 Play 模式拒绝（见 manage_editor 节） |

### 处理器内部额外调用 WriteGuard 的位置（grep 全量）

| 文件:行 | 调用 |
|---|---|
| ManageShader.cs:764 | `WriteGuard.CheckWriteAllowed("ensure_material_shader_for_srp")` |
| ManageGameObject.cs:3433 / 3483 / 3536 / 3588 | `ensure_component` / `ensure_renderer_material` / `ensure_mesh_collider_mesh` / `ensure_prefab_default_sprite` |
| ManageScene.cs:563 / 626 | `ensure_scene_open({scenePath})` / `ensure_scene_saved` |
| Helpers/StateComposer.cs:423 | 只读：`writeGuardInPlayMode = WriteGuard.GetPolicyString()` |
| 其它 | 无（WriteGuardTests 未随包发布：包内 grep `WriteGuardTests` 仅命中 WriteGuard.cs 注释本身） |

## 0.5 作业/异步基础设施（用于理解返回时机）

| 组件 | 事实 | 源码 |
|---|---|---|
| `JobContext` | 处理器返回它时桥接不立即响应 | UnityTcpBridge.cs:585-588 |
| `CoroutineRunner.CreateJob/RunJob` | `manage_input`、`manage_screenshot` 用它；响应在协程结束时投递 | CoroutineRunner.cs:43-55 |
| `StepJobRunner.Start(requestId, commandType, job, timeoutSeconds)` | `manage_editor`(play/stop/compile/refresh/wait_for_idle)、`manage_package`、`manage_bake`(bake_lighting)、`exec_editor_script`/`exec_runtime_script` 用它 | 各文件 |
| `DetachedJobs` / `JobRegistry` / `JobControl` | `manage_job` 的 status/list/cancel 读写对象；`DetachedJobs.Status = { Pending, Complete, Unknown }`；完成项 TTL 300s（`CompletedTtlSeconds = 300`） | DetachedJobs.cs:36-43, 56 |
| 桥停止/域重载 | `AsyncTaskRunner.CancelAll(reason)`、`CoroutineRunner.CancelAll(reason)`；`StepJobRunner.CancelAll("Bridge stopped while step job was running.")`；退出 Play 模式时 `DrainPendingCommandsWithError("Play mode exiting.")` | UnityTcpBridge.cs:270-285, 634-642 |
