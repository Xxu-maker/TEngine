# Codely Bridge 视觉操作（材质 / Shader / 纹理 / VFX / 动画）

> **适用场景**：通过 DSH 驱动 Unity 处理材质、Shader、纹理导入、粒子特效、动画控制器 | **关联文档**：[bridge-tools.md](bridge-tools.md)（通用桥命令）、[naming-rules.md](naming-rules.md)（资源命名）、[resource-api.md](resource-api.md)（资源加载）

## 0. 结论先行：桥没有专门的视觉工具

旧的 `mcp-visual.md` 里的 `manage_material` / `manage_texture` / `manage_vfx` / `manage_animation` **在 Codely Bridge 中都不存在**。视觉类操作走两条路：

| 路线 | 适用 | 手段 |
|------|------|------|
| **A. 桥内建动作** | 管线探测、材质-SRP 适配、Shader 编译/预览 | `manage_shader`（4 个动作） |
| **B. 编辑器 C# 配方** | 建材质、改纹理导入、做粒子、编动画 | `exec_editor_script` + `capture_logs` |
| **C. 关联动作** | 给渲染器挂材质、改组件属性 | `manage_gameobject` 的 `ensure_renderer_material` / `set_component_property` |

> 通则：**能用 B 的一次脚本搞定，就不要拆成十次调用**。桥的单次 C# 脚本可以引用完整 `UnityEditor` API。

---

## 1. 路线 A：manage_shader 内建动作

| action | 说明 | 关键参数 |
|--------|------|---------|
| `detect_render_pipeline` | 探测工程当前渲染管线（builtin / URP / HDRP） | — |
| `ensure_material_shader_for_srp` | 幂等：把材质 Shader 适配到当前 SRP | `path` / `target` |
| `compile` | 编译 Shader 并报错 | `path` |
| `preview` | 预览 Shader | `path` |

**先探测管线再决定属性名**——这是最容易出错的地方（见 §4）。

---

## 2. 路线 B：编辑器 C# 配方

统一用：

```
unity_bridge(
  type   = "exec_editor_script",
  params = { script = "<C# 语句>", description = "...", capture_logs = true })
```

### 铁律（实测）
1. 这是 **Roslyn 脚本提交**：顶层可以写 `using`/变量/语句，**但结尾不能是裸表达式**（会 `CS0201` 直接编译失败）。
2. 要拿结果就 `UnityEngine.Debug.Log(...)` + `capture_logs: true`，从返回的 `logs` 数组里读。
3. Play 模式下 `exec_editor_script` 会被拒；运行态要用 `exec_runtime_script`。

### 2.1 创建材质

```csharp
var shader = Shader.Find("Universal Render Pipeline/Lit"); // 或 "Standard"
var mat = new UnityEngine.Material(shader);
System.IO.Directory.CreateDirectory("Assets/AssetRaw/Materials");
UnityEditor.AssetDatabase.CreateAsset(mat, "Assets/AssetRaw/Materials/EnemyMat.mat");
UnityEditor.AssetDatabase.SaveAssets();
UnityEngine.Debug.Log("mat created: " + mat.name + " shader=" + shader.name);
```

### 2.2 改材质颜色 / 属性

```csharp
var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>("Assets/AssetRaw/Materials/EnemyMat.mat");
mat.SetColor("_BaseColor", new UnityEngine.Color(0.8f, 0.2f, 0.2f, 1f)); // URP 用 _BaseColor，Standard 用 _Color
UnityEditor.EditorUtility.SetDirty(mat);
UnityEditor.AssetDatabase.SaveAssets();
UnityEngine.Debug.Log("color set");
```

### 2.3 改纹理导入设置

```csharp
var imp = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath("Assets/AssetRaw/UI/Atlas/hero.png");
imp.textureType = UnityEditor.TextureImporterType.Sprite;
imp.maxTextureSize = 2048;
imp.mipmapEnabled = false;
imp.SaveAndReimport();
UnityEngine.Debug.Log("texture imported");
```

### 2.4 粒子系统

```csharp
var go = new UnityEngine.GameObject("FX_Hit");
var ps = go.AddComponent<UnityEngine.ParticleSystem>();
var main = ps.main; main.duration = 1f; main.loop = false;
main.startLifetime = 0.6f; main.startSpeed = 3f; main.startSize = 0.5f;
var em = ps.emission; em.rateOverTime = 0f; em.SetBursts(new[]{ new UnityEngine.ParticleSystem.Burst(0f, 20) });
var shape = ps.shape; shape.shapeType = UnityEngine.ParticleSystemShapeType.Sphere; shape.radius = 0.3f;
UnityEngine.Debug.Log("particle created");
```

### 2.5 动画控制器

```csharp
var ctrl = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath("Assets/AssetRaw/Anim/Hero.controller");
ctrl.AddParameter("Speed", UnityEngine.AnimatorControllerParameterType.Float);
var sm = ctrl.layers[0].stateMachine;
var idle = sm.AddState("Idle");
var run = sm.AddState("Run");
sm.defaultState = idle;
var t = idle.AddTransition(run);
t.AddCondition(UnityEditor.Animations.AnimatorConditionMode.Greater, 0.1f, "Speed");
UnityEngine.Debug.Log("animator built");
```

### 2.6 给场景对象挂材质

优先用幂等动作，而不是手写脚本：

```
unity_bridge("manage_gameobject", { action = "ensure_renderer_material",
                                     target = "Enemy", materialPath = "Assets/AssetRaw/Materials/EnemyMat.mat" })
```

---

## 3. 路线 C：走通用组件的场景

| 目标 | 命令 |
|------|------|
| 加 ParticleSystem / TrailRenderer / LineRenderer | `manage_gameobject` `add_component` + `set_component_property` |
| 改 Light / Camera / Canvas / CanvasScaler 参数 | `manage_gameobject` `set_component_property` |
| 改 Animator 参数（运行时） | `exec_runtime_script` 里 `animator.SetFloat(...)` |
| 播放 / 停止粒子（运行时） | `exec_runtime_script` 里 `ps.Play()` / `ps.Stop()` |

---

## 4. 领域知识（与旧文档一致，仍然有效）

### Shader 名称

| 管线 | Shader |
|------|--------|
| URP 不透明 | `Universal Render Pipeline/Lit` |
| URP 无光照 | `Universal Render Pipeline/Unlit` |
| 标准管线 | `Standard` |
| 精灵 / 2D | `Sprites/Default` |
| UI | `UI/Default` |

### 主颜色属性名（**最容易踩的坑**）

| 管线 | 属性 |
|------|------|
| URP Lit / Unlit | `_BaseColor` |
| 标准管线 | `_Color` |
| 自发光 | `_EmissionColor` |

`manage_shader detect_render_pipeline` 先探一下管线，再用对应属性名。

### TextureImporter 关键枚举

| 参数 | 取值 |
|------|------|
| `textureType` | `Default`、`Sprite`、`NormalMap`、`GUI`、`Cubemap` |
| `maxTextureSize` | 32 ~ 8192 |
| `format` | `Automatic`、`RGBACompressed*`、`RGBCompressed*` |

---

## 5. 常见错误

| 报错 / 现象 | 原因 | 解决 |
|------|------|------|
| `CS0201` | C# 脚本以裸表达式结尾 | 改成语句 + `Debug.Log` |
| `Unknown action: 'create_material'` | 用了不存在的旧 MCP 动作 | 走 §2.1 的 C# 配方 |
| 材质颜色没变 | URP 材质用了 `_Color` | 改 `_BaseColor`（先 `detect_render_pipeline`） |
| 锚点/尺寸不对 | 改了 RectTransform 但没改 anchor | 先设 `anchorMin/Max` 再设 `anchoredPosition` |
| 贴图设置改了没生效 | 忘了 `SaveAndReimport()` | 必须调用 |
| Play 模式下改材质报写守卫 | `writeGuardInPlayMode = deny` | 先 `manage_editor stop` |

---

## 6. 交叉引用

| 主题 | 文档 |
|------|------|
| 通用桥命令（场景/GO/脚本/资源/Play） | [bridge-tools.md](bridge-tools.md) |
| 资源命名规范 | [naming-rules.md](naming-rules.md) |
| 资源加载与释放 | [resource-api.md](resource-api.md) |
| UI Prefab 拼接与组件操作 | [bridge-tools.md](bridge-tools.md#51-ui-prefab-拼接旧-manage_ui-的等价做法) |
