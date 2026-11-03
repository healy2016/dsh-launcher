# DSH 启动器

> 把 `dsh web` 变成托盘常驻的后台服务 —— 从此不用再挂一个 CMD 黑窗。

<p align="center">
  <a href="https://github.com/healy2016/dsh-launcher"><img src="https://img.shields.io/badge/source-GitHub-181717" alt="source"></a>
  <img src="https://img.shields.io/badge/build-self--compile-orange" alt="build">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6" alt="platform">
  <img src="https://img.shields.io/badge/.NET%20Framework-4.x-512BD4" alt="dotnet">
  <img src="https://img.shields.io/badge/license-MIT-green" alt="license">
</p>

---

## 它是干什么的

平时启动 [DeepSeek Harness](https://github.com/deepseek-ai) 的 Web 界面，得开一个命令行窗口：

```bash
npx @deepseek-ai/dsh web
```

这个窗口**必须一直开着**，一关服务就停，误关一下全部断掉，还白占一个任务栏位置。

**DSH 启动器**把这个过程包起来：双击一次，服务在后台静默运行，托盘留一枚鲸鱼图标，随时右键管理。

## 特性

| | 能力 | 说明 |
|---|---|---|
| 🪟 | **无黑窗** | 编译为 GUI 子系统，全程不弹控制台 |
| 🐳 | **托盘常驻** | 右键菜单管理一切 |
| 🚀 | **开机自启** | 一次勾选，永久生效（写当前用户注册表，免管理员） |
| 🛡️ | **安全启动** | 插件装坏时一键只加载内置插件，进得去界面再卸载坏插件 |
| ♻️ | **崩溃自愈** | 服务异常退出自动重启，最多连续 3 次 |
| 📝 | **日志落盘** | 服务输出全量写入文件，出问题可回溯 |
| 🔒 | **单实例** | 重复双击不会开第二个 |
| 📦 | **零依赖** | 单个约 94 KB exe，除 .NET Framework 外无需安装任何运行时 |
| 🔎 | **自动识别** | 自动定位 Node.js / DSH，旧配置失效会自动回退 |
| 🧰 | **安装 / 修复** | 找不到 DSH 时可从托盘菜单一键 `npm install -g`，并自动加入 PATH |
| 🧪 | **测试版隔离** | 自带 `-Test` 构建，使用独立 profile 与端口，不碰生产配置 |
| ⚙️ | **可配置** | 端口、profile、工作区、启动模式、额外参数都可改 |

## 系统要求

- Windows 10 / 11（x64）
- **.NET Framework 4.x**（Win10 1903 之后系统自带，无需安装）
- **Node.js（含 npm）**；DeepSeek Harness 本身可通过托盘菜单「安装 / 修复 DSH」自动安装

## 安装

> 本项目只提供源码，不发布预编译的 exe —— 请按下述任一方式自行编译。
> 编译产物是单个免安装的 exe，可以直接拷到任意目录使用。

### 方式一：用构建脚本编译（推荐）

```powershell
git clone https://github.com/healy2016/dsh-launcher.git
cd dsh-launcher
.\build.ps1                      # 生产版：DSHWebLauncher.exe + settings.ini（端口 9000）
.\build.ps1 -Test                # 测试版：DSHWebLauncher-Test.exe + settings.test.ini（端口 9090）
.\build.ps1 -NoSmoke             # 跳过编译后的 --status 冒烟测试
.\build.ps1 -WorkDir D:\code\deepseek-harness   # 指定写进 settings.ini 的 DSH 路径
```

用的是 Windows 自带的 .NET Framework 编译器，**不需要装 .NET SDK 或 Visual Studio**。

编译完成后：

1. 把 `DSHWebLauncher.exe` 放到一个固定的文件夹（比如 `C:\Users\你\Tools\DSH启动器\`）
2. 双击运行，托盘出现鲸鱼图标即成功
3. 右键图标 → 勾选 **开机自启**

### 方式二：手敲 csc 命令

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
  /win32icon:"assets\whale.ico" /win32manifest:"src\app.manifest" `
  /resource:"assets\whale.ico",whale.ico `
  /out:"DSHWebLauncher.exe" `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Core.dll /reference:System.Web.Extensions.dll `
  "src\DshWebLauncher.cs"
```

> `/target:winexe` 是无黑窗的关键，换成 `exe` 就会弹控制台。
> `/reference:System.Web.Extensions.dll` 提供 `JavaScriptSerializer`，安全启动改 `package.json` 时用到。

## 用法

双击托盘图标 → 打开界面。右键菜单：

| 菜单项 | 作用 |
|---|---|
| 状态：运行中 PID xxxx · 端口 9000 | 实时状态（只读） |
| 打开 Web UI | 浏览器打开 `http://127.0.0.1:<端口>/`；非 web 类 profile 时置灰 |
| 重启服务 | 结束进程树后重新拉起（改完配置用这个） |
| 停止服务 | 停服务但保留托盘图标 |
| 安装 / 修复 DSH | 通过 npm 安装或修复 DSH；**仅 `LaunchMode=global` 时显示** |
| 查看当前日志 | 弹窗查看本次运行的日志 |
| 打开日志目录 | 直达 `%LOCALAPPDATA%\DSHWebLauncher\logs` |
| 开机自启 | 勾选/取消，写当前用户注册表，免管理员 |
| 安全启动 / 退出安全启动 | 见下节；处于安全启动时该项自动变为「退出安全启动」并带勾 |
| 关于 | 版本、适用端、作者与仓库 |
| 退出 | 停服务 + 退出托盘 |

完整教程（配置项、常见问题、排查步骤）见 **[docs/使用说明.md](docs/使用说明.md)**。

## 安全启动

**什么时候用**：装了某个插件之后 DSH 起不来、白屏、或者行为异常。

**原理**：DSH 启动时只读 profile `package.json` 里的 `dsh.profile.bundles` 清单。安全启动把这份清单裁剪成只剩 `@deepseek-ai/` 开头的内置 bundle，第三方插件**根本不会被加载**（连代码都不执行）。它们的 `dependencies` 条目原样保留，所以 DSH 起来后插件管理页仍能看到它们、并能逐个移除。

**操作**：

1. 托盘右键 → **安全启动**（服务在跑时会先问是否停止，选「是」）
2. 右键 → **重启服务**
3. 打开 Web UI → 设置 → 插件，此时第三方插件全部显示「已安装、未启用」
4. 把坏插件移除
5. 修好后右键 → **退出安全启动** → 重启服务

**行为细节**：

| 事项 | 说明 |
|---|---|
| 备份 | 进入时把原文件存为 `package.json.safeboot.bak`，退出时据此恢复 |
| 不还原已卸载的插件 | 退出时只恢复「仍在 `dependencies` 里」的插件，你在插件页删掉的不会回来 |
| 状态识别 | 备份文件存在 = 处于安全启动 |
| 重复进入 | 已在安全启动时再点会提示「无需重复进入」，不会破坏备份 |
| 备份损坏 | 删除 `package.json.safeboot.bak` 即可回到普通启动（清单已是裁剪后的状态） |

**命令行**（托盘起不来时也能用）：

```powershell
DSHWebLauncher.exe --safe-boot-status   # 查看状态与目标 package.json 路径
DSHWebLauncher.exe --safe-boot          # 进入安全启动
DSHWebLauncher.exe --exit-safe-boot     # 退出安全启动
```

## 配置

配置文件：`%LOCALAPPDATA%\DSHWebLauncher\settings.ini`

如果 exe 同目录下存在 `settings.ini` / `settings.test.ini`，则**优先用同目录那份**（便携模式），否则用 `%LOCALAPPDATA%` 下的。

### 配置项

| 键 | 默认值 | 说明 |
|---|---|---|
| `LaunchMode` | `global` | `global` 用 npm 全局安装的 dsh；`local` 用本地 `apps/cli` 源码 |
| `NodePath` | 空（自动识别） | `node.exe` 的绝对路径 |
| `DshBin` | 空（自动识别） | `global` 模式下的 dsh 入口 `lib/bin.js` |
| `Profile` | `headless` | **仅 `local` 模式生效**。`web` / `headless` / 任意自定义 profile |
| `Port` | `3080` | 监听端口。`web` 或 `web-*` 类 profile 会追加 `--port` |
| `WorkDir` | 空（回退用户目录） | dsh 的工作目录（会话默认根目录） |
| `LaunchArgs` | 空 | 追加到 dsh 命令行的额外参数，原样透传 |
| `NodeNoWarnings` | `1` | `1` 时设置 `NODE_NO_WARNINGS=1`，抑制 Node 警告输出 |
| `AutoRestart` | `1` | `1` 时服务异常退出自动重启，最多连续 3 次 |

> `NodeNoWarnings` 也接受别名 `Node_No_Warnings`。

### 示例

```ini
# DSH Web Launcher 配置（改完重启托盘程序生效）
# LaunchMode=global 使用 npm 全局 dsh；LaunchMode=local 使用本地 apps/cli
LaunchMode=local
NodePath=C:\nvm4w\nodejs\node.exe
DshBin=
# Profile 仅在 local 模式下生效；空表示 headless
Profile=web
Port=9000
WorkDir=D:\openworkspace\deepseek-harness
LaunchArgs=
NodeNoWarnings=1
AutoRestart=1
```

改完**重启托盘程序**生效（右键 → 退出，再双击 exe）。

### 自动识别顺序

首次运行会自动定位 Node.js 和 DSH，不再依赖固定用户名或安装盘符：

1. 环境变量 `DSH_NODE` / `DSH_BIN`
2. `%APPDATA%\npm\node_modules\@deepseek-ai\dsh\lib\bin.js`
3. `npm root -g` 返回的全局目录
4. `where.exe dsh.cmd` 和 `PATH` 中的 npm 全局目录
5. 常见 Node.js 安装路径

旧配置里的路径失效时，启动会自动回退并重新识别；找不到时会在托盘提示，不会陷入无限重启。识别成功后会把 Node.js 所在目录和 npm 全局 bin（通常是 `%APPDATA%\npm`）自动加入当前用户 PATH。

## 命令行参数

```powershell
DSHWebLauncher.exe --status              # 打印当前配置与识别结果
DSHWebLauncher.exe --detect              # 强制重新识别 Node/DSH
DSHWebLauncher.exe --install-dsh         # 静默安装/修复 DSH
DSHWebLauncher.exe --selftest            # 自检：拉起→等端口→报告→退出
DSHWebLauncher.exe --about               # 打印版本与适用端说明
DSHWebLauncher.exe --render-about <png>  # 把「关于」窗口导出成图片
```

**覆盖配置**（会写回 `settings.ini`）：

```powershell
DSHWebLauncher.exe --port 9001
DSHWebLauncher.exe --profile web
DSHWebLauncher.exe --workdir D:\code
DSHWebLauncher.exe --mode local          # global | local
DSHWebLauncher.exe --launchargs "--verbose"
```

**开机自启**：

```powershell
DSHWebLauncher.exe --enable-autostart
DSHWebLauncher.exe --disable-autostart
DSHWebLauncher.exe --autostart-status
```

**安全启动**：

```powershell
DSHWebLauncher.exe --safe-boot-status
DSHWebLauncher.exe --safe-boot
DSHWebLauncher.exe --exit-safe-boot
```

> **动作类参数**（`--status` / `--detect` / `--safe-boot` / `--enable-autostart` 等）执行完即退出。
> **改配置类参数**（`--port` / `--profile` / `--workdir` / `--mode` / `--launchargs`）只写回 `settings.ini`，
> 随后**照常进入托盘常驻模式**；也可以和动作类参数组合使用，例如
> `DSHWebLauncher.exe --profile web-test --safe-boot-status`。

## 生产版与测试版

| | 生产版 | 测试版 |
|---|---|---|
| exe | `DSHWebLauncher.exe` | `DSHWebLauncher-Test.exe` |
| 配置 | `settings.ini` | `settings.test.ini` |
| 默认 profile | `web` | `web-test` |
| 默认端口 | `9000` | `9090` |
| 单实例 | 独立互斥量 | 独立互斥量 |

两者**可以同时运行**，互不干扰。测试版默认指向 `web-test` 这个独立 profile，改 bundle 列表不会影响生产环境的 `web` profile。

首次使用测试版前，先准备一份隔离 profile：

```powershell
$src = "$env:USERPROFILE\.dsh\profiles\web"
$dst = "$env:USERPROFILE\.dsh\profiles\web-test"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
Copy-Item $src $dst -Recurse
cd $dst; pnpm install
```

## 工作原理

它只做一件事：把下面这条命令以**隐藏窗口**方式启动，把输出重定向到日志，再用 WinForms 的 `NotifyIcon` 在托盘上挂个图标管理它。

**local 模式**（`LaunchMode=local`）：

```bash
node "<仓库>/apps/cli/lib/bin.js" --profile web --port 9000
```

若只有 `src/bin.ts`（未编译），则自动改为 `node --import tsx/esm "<仓库>/apps/cli/src/bin.ts" ...`。

**global 模式**（`LaunchMode=global`）：

```bash
node "<npm 全局>/node_modules/@deepseek-ai/dsh/lib/bin.js" web --port 9000 --no-open
```

所以行为与你在命令行里手敲**完全一致**，只是外面套了层便于管理的壳。

加 `--no-open` 是因为 `dsh web` 默认会自动打开浏览器，而开机自启时不该突然弹窗 —— 要看界面用托盘菜单。

## 项目结构

```
├── src/DshWebLauncher.cs        主程序（C# 5，单文件约 2500 行）
├── src/app.manifest             Win32 manifest（asInvoker，避免提权弹窗）
├── build.ps1                    本地构建脚本（生产版 / 测试版）
├── assets/whale.ico             7 尺寸图标（16/24/32/48/64/128/256）
├── assets/whale-icon.png        原始 1000×1000 图标源
├── scripts/make-icon.ps1        从 PNG 生成多尺寸 ICO
├── scripts/check-icon.mjs       校验图标是否已嵌入 exe
├── scripts/push-latest.ps1      一键提交（单提交仓库约定）
├── docs/使用说明.md              完整使用教程
├── docs/提交约定.md              提交与发版约定
└── .github/workflows/build.yml  CI：每次提交自动编译 + 校验
```

图标嵌了两份：Win32 资源（资源管理器图标）+ .NET 资源（托盘图标按 DPI 取尺寸）。

## 常见问题

见 [docs/使用说明.md 第六节](docs/使用说明.md#六常见问题)，包含：

- 双击后托盘没图标
- 显示"已在运行（外部实例）"
- 开机自启没生效
- 端口被占用
- 日志在哪
- 怎么卸载

## 关于

- 作者：liuxili
- 遇到问题联系 xili.liu@foxmail.com 反馈

## 免责声明

本程序为免费工具。
DeepSeek Harness 本体版权归 DeepSeek 所有，本项目不包含、不修改其任何代码。

## License

[MIT](LICENSE)
