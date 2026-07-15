# Huaci 模块化管理窗口 Design QA

- Source visual truth: `C:\Users\5D\AppData\Local\Temp\codex-clipboard-7b0d805d-d34e-44ed-804a-dbcd38bd6fc3.png`
- Before-state evidence: `C:\Users\5D\AppData\Local\Temp\codex-clipboard-2ca21060-6138-4077-aa7b-2420eba3cdf5.png`
- Implementation screenshot: `D:\AI\huaci\tests\Huaci.UiTests\bin\Release\net10.0-windows10.0.19041.0\ui-artifacts\main-home.png`
- Viewport: 344 × 438 DIP; captured at 150% Windows scale as 516 × 657 px
- State: 自动划词开启、API 未配置、划词模块选中

## Full-view comparison evidence

The Quicker reference, Huaci before-state, and final Huaci home screen were opened together in one comparison pass. The implementation deliberately carries over Quicker's compact four-column launcher, whole-cell click targets, outline icon family, short labels, selected-module surface, and lightweight status indicators. It preserves Huaci's established dark gray and periwinkle accent rather than copying Quicker's unrelated light theme or tool inventory.

The prior large-form hierarchy has been replaced by four modules: 划词、翻译、取词、服务. Complex controls appear only in the selected module's detail area. The final shell is smaller than the prior 360 × 480 DIP window and the background is effectively opaque enough that underlying page copy no longer competes with Huaci content.

## Focused-region comparison evidence

A separate crop was not required: at original resolution, the complete four-module strip, icon strokes, Chinese labels, selected state, status dots, card spacing, and title bar controls are all readable in both the source and implementation. The detailed translation, capture, and service states were additionally rendered and exercised by the WPF UI test suite.

## Required fidelity surfaces

- Fonts and typography: Segoe UI with the Windows Chinese fallback retains the existing native Windows feel. Module labels, detail headings, helper copy, metrics, and status text use distinct weights and sizes without clipping at 150% scale.
- Spacing and layout rhythm: four equal launcher columns, 2–7 DIP gaps, 8 DIP card radii, 10 DIP detail padding, and a 24 DIP footer create a compact Quicker-like rhythm. No persistent control is clipped at 344 × 438 DIP.
- Colors and visual tokens: the existing Huaci accent `#8397FF` is retained. Success, warning, weak text, fields, and selected modules use semantic tokens. The shell alpha was tightened to `#FC` to eliminate distracting background text.
- Image quality and asset fidelity: no raster imagery from the Quicker screenshot is applicable because those icons represent unrelated products. Huaci uses the native Segoe MDL2 Assets icon library for a consistent Windows outline icon family; no placeholder imagery or handcrafted SVG assets were introduced.
- Copy and content: module and setting labels describe existing Huaci capabilities only. No filler tools or invented features were added.
- Accessibility and states: the full module cells are keyboard-focusable click targets; hover, pressed, selected, checked, success, warning, and focus states are defined. API keys remain masked and stored outside the settings file.

## Comparison history

1. Earlier findings:
   - P1: the old management window was a large two-tab form and did not express the requested Quicker-style modular hierarchy.
   - P1: the title bar's empty area had no hit-test background, so most of the title bar could not begin a drag.
   - P2: the old shell allowed underlying page text to remain visibly legible through fields and controls.
   - P2: 360 × 480 DIP felt oversized at 150% Windows scaling.
2. Fixes made:
   - Added a four-column module launcher and module-specific detail panels.
   - Added a dedicated transparent `HeaderDragArea` covering the entire non-button title region.
   - Tightened the shell alpha to `#FC` and made fields/detail cards substantially opaque.
   - Reduced the fixed footprint to 344 × 438 DIP while preserving all existing workflows.
3. Post-fix evidence:
   - `main-home.png` shows the complete launcher, selected state, status summary, and detail hierarchy without background-copy interference.
   - WPF tests confirm four modules, a hit-testable title-bar blank area, restored saved coordinates, module navigation, draft preservation, and high-DPI layout.

## Findings

No actionable P0, P1, or P2 issues remain for the requested module-home redesign and movable-window fix.

## Follow-up polish

- P3: the 13 DIP native pin glyph could be increased by 1 DIP if later usability testing finds it too subtle.

## Implementation checklist

- [x] Four-column Quicker-style launcher
- [x] Functional module navigation and settings inputs
- [x] Full title-bar drag hit surface
- [x] Saved placement retained; off-screen recovery preserved
- [x] Reduced background interference
- [x] 150% DPI visual and interaction checks

final result: passed

---

# Huaci 0.1.4 极简启动器与独立设置窗 Design QA

- Source visual truth:
  - `C:\Users\5D\AppData\Local\Temp\codex-clipboard-e02bd58a-25b1-44ac-aa81-21bfa4611f6f.png`（Quicker 的图标启动器密度与层级）
  - `C:\Users\5D\AppData\Local\Temp\codex-clipboard-0f36873b-3272-4ed2-b282-b6f8c259f559.png`（Huaci 既有暗色视觉语言）
- Implementation screenshots:
  - `D:\AI\huaci\tests\Huaci.UiTests\bin\Release\net10.0-windows10.0.19041.0\ui-artifacts\launcher.png`
  - `D:\AI\huaci\tests\Huaci.UiTests\bin\Release\net10.0-windows10.0.19041.0\ui-artifacts\settings.png`
  - `D:\AI\huaci\tests\Huaci.UiTests\bin\Release\net10.0-windows10.0.19041.0\ui-artifacts\settings-service.png`
  - `D:\AI\huaci\tests\Huaci.UiTests\bin\Release\net10.0-windows10.0.19041.0\ui-artifacts\manual-translate.png`
- Combined comparison evidence:
  - `D:\AI\huaci\artifacts\design-qa\launcher-comparison.png`
  - `D:\AI\huaci\artifacts\design-qa\settings-comparison.png`
- Viewports:
  - Launcher: 292 × 156 DIP; 438 × 234 px at 150% Windows scale.
  - Settings: 336 × 438 DIP; 504 × 657 px at 150% Windows scale.
  - Manual translation: 360 × 402 DIP; 540 × 603 px at 150% Windows scale.
- State: 自动划词开启、API 未配置；设置窗分别检查顶部取词区与滚动后的服务区；手动翻译显示有效结果。

## Full-view comparison evidence

`launcher-comparison.png` 将 Quicker 参考、0.1.3 Huaci 和 0.1.4 启动器放在同一画布中比较。新启动器保留 Quicker 的整格图标入口、短标签、状态点和高密度布局，同时沿用 Huaci 的灰黑半透明壳体、蓝紫强调色和 Windows 线性图标。原有下方详情卡全部移除，窗口从 344 × 438 DIP 收敛为 292 × 156 DIP。

`settings-comparison.png` 在同一画布中对照旧主窗与新设置窗。所有取词行为、响应时间、Toast 停留、API 地址、模型和 API Key 均集中到独立窗口；内容区明确可滚动，关闭与保存固定在底部，不会随表单滚走。“服务”入口已改名为“设置”。

## Focused-region comparison evidence

无需再制作单独裁切：两张组合图在原始分辨率下已经能清楚读取启动器图标、短标签、状态点、字段文字、输入框边界、滚动条和固定底栏。服务区另有 `settings-service.png` 验证滚动后的 API 字段完整可用。

## Required fidelity surfaces

- Fonts and typography: 使用 Segoe UI 与 Windows 中文回退；标题 13.5 DIP、模块标签 10.5 DIP、字段标签 9.5 DIP，层级清楚。150% DPI 下无裁切、异常换行或拥挤。
- Spacing and layout rhythm: 三个入口使用等宽网格，点击热区完整；42 DIP 标题栏与 8–12 DIP 间距形成紧凑节奏。设置卡片保持 9 DIP 间隔，底栏不覆盖可操作字段。
- Colors and visual tokens: 主窗 `#F4292C31`、设置窗 `#FA292C31`，既保留半透明感又避免后景文字干扰。绿色只表达自动划词运行，琥珀色只表达服务未配置，蓝紫色用于主操作与焦点。
- Image quality and asset fidelity: 界面无需要复制的产品图或插画。功能图标统一使用 Windows 原生 Segoe MDL2 Assets，不使用占位图、Emoji、手绘 SVG 或 CSS 图形。
- Copy and content: 主窗口仅保留“划词 / 翻译 / 设置”；设置窗文字全部对应现有功能，不引入填充式功能。API Key 安全存储说明保留。
- Accessibility and interactions: 三个模块都有唯一自动化名称、Tooltip、键盘焦点和完整点击热区；三个窗口的标题空白区域均可拖动；结果框只读；设置滚动条与固定保存按钮可用。

## Primary interactions tested

- 划词图标发出开启/暂停请求并同步主窗、托盘与设置窗状态。
- 翻译和设置图标分别打开独立单实例窗口。
- 设置窗滚动高度大于 0，服务区可进入视口，保存发出全部字段。
- 外部划词状态更新不会覆盖未保存的响应时间、API 地址、模型或 API Key 草稿。
- 手动翻译会去除原文首尾空格，结果区域保持只读。
- 用户关闭只隐藏窗口；程序退出时才真正关闭。
- WPF 无浏览器控制台；严格编译为 0 警告、0 错误，烟测与 UI 测试均通过。

## Comparison history

1. First comparison finding:
   - P2: 三个新窗口使用 `#2EFFFFFF` 外边框，在透明画布上呈现出偏亮的白色轮廓，与“只保留灰色主体”的极简方向不一致。
2. Fix made:
   - 移除启动器、设置窗和手动翻译窗的外层描边，保留灰黑圆角主体与内部控件边界。
3. Post-fix visual evidence:
   - `launcher.png`、`settings.png` 与两张组合对照图均显示外层白框已消失；自动化测试新增 `BorderThickness == 0` 契约。

## Findings

No actionable P0, P1, or P2 findings remain. The three-icon consolidation is an intentional product simplification: the former “取词” configuration page no longer needs a launcher entry because all configuration now lives under “设置”.

## Follow-up polish

- P3: 如果后续用户测试认为绿色选中块过强，可把划词开启背景从 `#2863D48C` 再降低约 20% 透明度，仅保留绿色状态点。

## Implementation checklist

- [x] 主窗口只保留划词、翻译、设置三个图标入口
- [x] “服务”更名为“设置”
- [x] 所有配置迁移到独立可滚动设置小窗
- [x] 手动翻译迁移到独立小窗
- [x] 三窗口单实例、可拖动、关闭时隐藏
- [x] API Key 安全存储与设置草稿保护保留
- [x] 150% DPI 视觉对照与核心交互检查通过

final result: passed
