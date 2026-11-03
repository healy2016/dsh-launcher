// DSHWebLauncher — 托盘常驻的 DSH 启动器（C# 5 / .NET Framework 4.x）
// 无黑窗、托盘菜单、开机自启、崩溃自动拉起、本地/全局 DSH 双模式、启动进度、日志查看。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DSH 启动器")]
[assembly: AssemblyDescription("DeepSeek Harness 托盘启动器")]
[assembly: AssemblyCompany("liuxili")]
[assembly: AssemblyProduct("DSH 启动器")]
[assembly: AssemblyCopyright("Copyright © 2026 liuxili")]
[assembly: AssemblyInformationalVersion("1.3.0")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
[assembly: ComVisible(false)]

namespace DshWebLauncher
{
    internal static class Settings
    {
        public static string NodePath = "";
        public static string DshBin = "";
        public static string LaunchMode = "global";   // global | local
        public static string Profile = "headless";    // local 模式下有效：headless | web | ...
        public static int Port = 3080;
        public static string WorkDir = "";
        public static bool AutoRestart = true;
        public static bool NodeNoWarnings = true;
        public static string LaunchArgs = "";

        public static string AppDir
        {
            get
            {
                string overrideDir = Environment.GetEnvironmentVariable("DSHLAUNCHER_HOME");
                if (!string.IsNullOrEmpty(overrideDir))
                {
                    Directory.CreateDirectory(overrideDir);
                    return overrideDir;
                }
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHWebLauncher");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string LogDir
        {
            get
            {
                string p = Path.Combine(AppDir, "logs");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string IniFileName = "settings.ini";
        public static string IniPath
        {
            get
            {
                try
                {
                    string sideBySide = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), IniFileName);
                    if (File.Exists(sideBySide)) return sideBySide;
                }
                catch { }
                return Path.Combine(AppDir, IniFileName);
            }
        }

        public static void Load()
        {
            if (File.Exists(IniPath))
            {
                try
                {
                    foreach (string raw in File.ReadAllLines(IniPath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "nodepath") NodePath = v;
                        else if (k == "dshbin") DshBin = v;
                        else if (k == "launchmode") LaunchMode = v.ToLowerInvariant();
                        else if (k == "profile") Profile = v;
                        else if (k == "port") { int tmp; if (int.TryParse(v, out tmp)) Port = tmp; }
                        else if (k == "workdir") WorkDir = v;
                        else if (k == "autorestart") AutoRestart = v != "0";
                        else if (k == "nodenowarnings") NodeNoWarnings = v != "0";
                        else if (k == "node_no_warnings") NodeNoWarnings = v != "0";
                        else if (k == "launchargs") LaunchArgs = v;
                    }
                }
                catch { }
            }
            if (Port <= 0) Port = 3080;
            if (string.IsNullOrEmpty(WorkDir)) WorkDir = DefaultWorkDir();
            if (LaunchMode != "local") LaunchMode = "global";
            ResolveRuntime(false);
            Save();
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# DSH Web Launcher 配置（改完重启托盘程序生效）");
                sb.AppendLine("# LaunchMode=global 使用 npm 全局 dsh；LaunchMode=local 使用本地 apps/cli");
                sb.AppendLine("LaunchMode=" + LaunchMode);
                sb.AppendLine("NodePath=" + NodePath);
                sb.AppendLine("DshBin=" + DshBin);
                sb.AppendLine("# Profile 仅在 local 模式下生效；空表示 headless");
                sb.AppendLine("Profile=" + Profile);
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("WorkDir=" + WorkDir);
                sb.AppendLine("LaunchArgs=" + LaunchArgs);
                sb.AppendLine("NodeNoWarnings=" + (NodeNoWarnings ? "1" : "0"));
                sb.AppendLine("AutoRestart=" + (AutoRestart ? "1" : "0"));
                File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>运行时是否已经找到可执行的 Node 和 DSH。</summary>
        public static bool RuntimeReady
        {
            get
            {
                if (LaunchMode == "local")
                {
                    string entry, error;
                    bool tsx;
                    return IsUsableFile(NodePath) && DetectLocalEntry(ResolvedWorkDir(), out entry, out tsx, out error);
                }
                return IsUsableFile(NodePath) && IsUsableFile(DshBin);
            }
        }

        /// <summary>用于日志和状态栏的可读摘要。</summary>
        public static string RuntimeSummary()
        {
            if (LaunchMode == "local")
            {
                string entry, error;
                bool tsx;
                bool ok = DetectLocalEntry(ResolvedWorkDir(), out entry, out tsx, out error);
                return "mode=local; Node=" + DisplayPath(NodePath) + "; entry=" + (ok ? entry : error);
            }
            return "mode=global; Node=" + DisplayPath(NodePath) + "; DSH=" + DisplayPath(DshBin);
        }

        /// <summary>
        /// 自动识别 Node 与 DSH。force=false 时只修复失效路径，force=true 时强制重新探测。
        /// 环境变量 DSH_NODE / DSH_BIN 优先，其次 npm 全局目录、where.exe 和 PATH。
        /// </summary>
        public static bool ResolveRuntime(bool force)
        {
            if (LaunchMode == "local")
                return ResolveLocalRuntime(force);

            bool changed = false;

            if (force || !IsUsableFile(NodePath))
            {
                string found = DetectNodePath();
                if (!string.IsNullOrEmpty(found) && !string.Equals(found, NodePath, StringComparison.OrdinalIgnoreCase))
                {
                    NodePath = found;
                    changed = true;
                }
            }

            if (force || !IsUsableFile(DshBin))
            {
                string found = DetectDshBin();
                if (!string.IsNullOrEmpty(found) && !string.Equals(found, DshBin, StringComparison.OrdinalIgnoreCase))
                {
                    DshBin = found;
                    changed = true;
                }
            }

            if (changed) Save();

            // 找到 DSH 后顺手把 Node 目录和 npm 全局 bin 加到用户 PATH。
            if (RuntimeReady) AddRuntimeToUserPath();
            return RuntimeReady;
        }

        private static bool ResolveLocalRuntime(bool force)
        {
            bool changed = false;
            if (force || !IsUsableFile(NodePath))
            {
                string found = DetectNodePath();
                if (!string.IsNullOrEmpty(found) && !string.Equals(found, NodePath, StringComparison.OrdinalIgnoreCase))
                {
                    NodePath = found;
                    changed = true;
                }
            }
            string entry, error;
            bool tsx;
            bool ok = DetectLocalEntry(ResolvedWorkDir(), out entry, out tsx, out error);
            if (changed) Save();
            if (ok) AddRuntimeToUserPath();
            return ok;
        }

        /// <summary>在本地工作目录下寻找 DSH 入口。</summary>
        public static bool DetectLocalEntry(string workDir, out string entry, out bool useTsx, out string error)
        {
            useTsx = false;
            error = null;
            entry = null;
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
            {
                error = "工作目录不存在: " + workDir;
                return false;
            }
            string bin = Path.Combine(workDir, "apps", "cli", "lib", "bin.js");
            if (IsUsableFile(bin))
            {
                entry = bin;
                return true;
            }
            string src = Path.Combine(workDir, "apps", "cli", "src", "bin.ts");
            if (IsUsableFile(src))
            {
                entry = src;
                useTsx = true;
                return true;
            }
            error = "找不到 apps/cli/lib/bin.js 或 apps/cli/src/bin.ts";
            return false;
        }

        /// <summary>构造最终启动命令。</summary>
        public static bool BuildCommand(out string fileName, out string arguments, out string workDir, out string error)
        {
            fileName = null;
            arguments = null;
            workDir = ResolvedWorkDir();
            error = null;

            if (!IsUsableFile(NodePath))
            {
                error = "找不到 node.exe";
                return false;
            }
            fileName = NodePath;

            StringBuilder sb = new StringBuilder();
            if (LaunchMode == "local")
            {
                string entry;
                bool tsx;
                if (!DetectLocalEntry(workDir, out entry, out tsx, out error))
                    return false;

                if (tsx)
                    sb.Append("--import tsx/esm ");
                sb.Append("\"").Append(entry).Append("\"");

                string profileArgs = BuildProfileArgs();
                if (!string.IsNullOrEmpty(profileArgs))
                    sb.Append(" ").Append(profileArgs);

                if (!string.IsNullOrEmpty(LaunchArgs))
                    sb.Append(" ").Append(LaunchArgs);
            }
            else
            {
                if (!IsUsableFile(DshBin))
                {
                    error = "找不到 DSH";
                    return false;
                }
                sb.Append("\"").Append(DshBin).Append("\" web --port ").Append(Port).Append(" --no-open");
                if (!string.IsNullOrEmpty(LaunchArgs))
                    sb.Append(" ").Append(LaunchArgs);
            }

            arguments = sb.ToString();
            return true;
        }

        /// <summary>
        /// 该 profile 是否提供 Web UI。
        /// 除了 "web" 本身，还包含由 web 派生的隔离 profile（如 "web-test"）——
        /// 否则测试版不会带上 --port，会去抢生产版默认的 9000 端口。
        /// </summary>
        public static bool IsWebLikeProfile()
        {
            if (LaunchMode == "global") return true;
            if (string.IsNullOrEmpty(Profile)) return false;
            return string.Equals(Profile, "web", StringComparison.OrdinalIgnoreCase)
                || Profile.StartsWith("web-", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildProfileArgs()
        {
            StringBuilder sb = new StringBuilder();
            bool hasProfile = !string.IsNullOrEmpty(LaunchArgs) && LaunchArgs.IndexOf("--profile", StringComparison.Ordinal) >= 0;
            bool hasPort = !string.IsNullOrEmpty(LaunchArgs) && LaunchArgs.IndexOf("--port", StringComparison.Ordinal) >= 0;

            if (!hasProfile && !string.IsNullOrEmpty(Profile))
            {
                sb.Append("--profile ").Append(Profile);
            }
            if (!hasPort && IsWebLikeProfile())
            {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append("--port ").Append(Port);
            }
            return sb.ToString();
        }

        /// <summary>查找当前机器可用的 npm.cmd。</summary>
        public static string FindNpmCommand()
        {
            string fromWhere = FirstWhere("npm.cmd");
            if (fromWhere != null) return fromWhere;

            string nodeDir = Path.GetDirectoryName(NodePath);
            string nearNode = Combine(nodeDir, "npm.cmd");
            if (IsUsableFile(nearNode)) return nearNode;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return FirstExisting(
                Combine(programFiles, @"nodejs\npm.cmd"),
                Combine(programFilesX86, @"nodejs\npm.cmd"));
        }

        /// <summary>npm 全局安装前缀，例如 C:\Users\你\AppData\Roaming\npm。</summary>
        public static string GetNpmPrefix()
        {
            string marker = @"\node_modules\@deepseek-ai\dsh\";
            if (!string.IsNullOrEmpty(DshBin))
            {
                int idx = DshBin.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) return DshBin.Substring(0, idx).TrimEnd('\\');
            }

            string root = FindNpmRoot();
            if (!string.IsNullOrEmpty(root))
            {
                string parent = Path.GetDirectoryName(root.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent)) return parent;
            }

            string npm = FindNpmCommand();
            if (!string.IsNullOrEmpty(npm)) return Path.GetDirectoryName(npm);

            return Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        }

        /// <summary>把 Node 目录与 npm 全局 bin 自动加入当前用户 PATH。</summary>
        public static bool AddRuntimeToUserPath()
        {
            string nodeDir = Path.GetDirectoryName(NodePath);
            string npmPrefix = GetNpmPrefix();
            return AddToUserPath(new string[] { nodeDir, npmPrefix });
        }

        private static bool AddToUserPath(string[] dirs)
        {
            try
            {
                string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
                if (userPath == null) userPath = "";

                StringBuilder userBuilder = new StringBuilder(userPath);
                bool changed = false;
                foreach (string raw in dirs)
                {
                    string dir = NormalizePathEntry(raw);
                    if (dir.Length == 0 || PathListContains(userPath, dir)) continue;
                    if (userBuilder.Length > 0 && userBuilder[userBuilder.Length - 1] != ';') userBuilder.Append(';');
                    userBuilder.Append(dir);
                    changed = true;
                }

                if (!changed) return false;
                Environment.SetEnvironmentVariable("Path", userBuilder.ToString(), EnvironmentVariableTarget.User);

                // 当前进程也要更新，否则后续查找 npm/dsh 仍可能用旧 PATH。
                string processPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process);
                if (processPath == null) processPath = "";
                StringBuilder processBuilder = new StringBuilder(processPath);
                foreach (string raw in dirs)
                {
                    string dir = NormalizePathEntry(raw);
                    if (dir.Length == 0 || PathListContains(processPath, dir)) continue;
                    if (processBuilder.Length > 0 && processBuilder[processBuilder.Length - 1] != ';') processBuilder.Append(';');
                    processBuilder.Append(dir);
                }
                Environment.SetEnvironmentVariable("Path", processBuilder.ToString(), EnvironmentVariableTarget.Process);
                BroadcastEnvironmentChange();
                return true;
            }
            catch { return false; }
        }

        private static string NormalizePathEntry(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                return expanded.Length > 3 ? expanded.TrimEnd('\\') : expanded;
            }
            catch { return ""; }
        }

        private static bool PathListContains(string pathList, string dir)
        {
            if (string.IsNullOrEmpty(pathList) || string.IsNullOrEmpty(dir)) return false;
            foreach (string raw in pathList.Split(';'))
            {
                string entry = NormalizePathEntry(raw);
                if (entry.Length > 0 && string.Equals(entry, dir, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void BroadcastEnvironmentChange()
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 1000, out result);
            }
            catch { }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
            uint flags, uint timeout, out UIntPtr result);

        private static string DetectNodePath()
        {
            string fromEnv = FirstExisting(
                Environment.GetEnvironmentVariable("DSH_NODE"),
                Environment.GetEnvironmentVariable("DSHLAUNCHER_NODE"));
            if (fromEnv != null) return fromEnv;

            string fromWhere = FirstWhere("node.exe");
            if (fromWhere != null) return fromWhere;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");

            string[] candidates = new string[] {
                Combine(programFiles, @"nodejs\node.exe"),
                Combine(programFilesX86, @"nodejs\node.exe"),
                Combine(localAppData, @"Programs\nodejs\node.exe"),
                Combine(nvmSymlink, "node.exe"),
                Combine(userProfile, @"scoop\apps\nodejs\current\node.exe"),
                Combine(userProfile, @"AppData\Local\Programs\nodejs\node.exe")
            };
            return FirstExisting(candidates);
        }

        private static string DetectDshBin()
        {
            string fromEnv = FirstExisting(
                Environment.GetEnvironmentVariable("DSH_BIN"),
                Environment.GetEnvironmentVariable("DSHLAUNCHER_DSH_BIN"));
            if (fromEnv != null) return fromEnv;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");

            string[] prefixes = new string[] {
                Combine(appData, "npm"),
                Combine(localAppData, "npm"),
                Combine(programFiles, "nodejs"),
                Combine(programFilesX86, "nodejs"),
                nvmSymlink,
                Combine(userProfile, @"scoop\apps\nodejs\current"),
                Combine(userProfile, @"AppData\Roaming\npm")
            };
            string found = FindDshInPrefixes(prefixes);
            if (found != null) return found;

            string npmRoot = FindNpmRoot();
            if (!string.IsNullOrEmpty(npmRoot))
            {
                found = FindDshInPrefix(npmRoot);
                if (found != null) return found;
            }

            string shims = RunCommand("where.exe", "dsh.cmd", 5000);
            foreach (string line in SplitLines(shims))
            {
                string shim = line.Trim();
                if (shim.Length == 0) continue;
                string prefix = Path.GetDirectoryName(shim);
                found = FindDshInPrefix(prefix);
                if (found != null) return found;
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string raw in pathEnv.Split(';'))
                {
                    string dir = raw.Trim().Trim('"');
                    if (dir.Length == 0) continue;
                    dir = Environment.ExpandEnvironmentVariables(dir);
                    found = FindDshInPathEntry(dir);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static string FindNpmRoot()
        {
            string output = RunCommand("cmd.exe", "/d /c npm root -g", 8000);
            string root = FirstExistingLine(output);
            if (root != null) return root;

            string npmCmd = FirstWhere("npm.cmd");
            if (npmCmd != null)
            {
                output = RunCommand("cmd.exe", "/d /s /c \"\"" + npmCmd + "\" root -g\"", 8000);
                root = FirstExistingLine(output);
                if (root != null) return root;
            }
            return null;
        }

        private static string FindDshInPrefixes(string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                string found = FindDshInPrefix(prefix);
                if (found != null) return found;
            }
            return null;
        }

        private static string FindDshInPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return null;
            string[] candidates = new string[] {
                Path.Combine(prefix, @"node_modules\@deepseek-ai\dsh\lib\bin.js"),
                Path.Combine(prefix, @"@deepseek-ai\dsh\lib\bin.js")
            };
            return FirstExisting(candidates);
        }

        private static string FindDshInPathEntry(string dir)
        {
            string found = FindDshInPrefix(dir);
            if (found != null) return found;

            // PATH 里常见的是 ...\npm 或 ...\npm\node_modules\.bin；父目录再试一次。
            string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(parent)) return FindDshInPrefix(parent);
            return null;
        }

        private static string FirstWhere(string exe)
        {
            string output = RunCommand("where.exe", exe, 5000);
            foreach (string line in SplitLines(output))
            {
                string path = line.Trim();
                if (IsUsableFile(path)) return path;
            }
            return null;
        }

        private static string FirstExistingLine(string text)
        {
            foreach (string line in SplitLines(text))
            {
                string path = line.Trim();
                if (Directory.Exists(path)) return path;
            }
            return null;
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string path in paths)
            {
                if (!IsUsableFile(path)) continue;
                return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            }
            return null;
        }

        private static bool IsUsableFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                return expanded.Length > 0 && File.Exists(expanded);
            }
            catch { return false; }
        }

        private static string Combine(string dir, string relative)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, relative);
        }

        private static string DisplayPath(string path)
        {
            return string.IsNullOrEmpty(path) ? "(未找到)" : path;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            return text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string RunCommand(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                    }
                    return stdout;
                }
            }
            catch { return ""; }
        }

        private static string DefaultWorkDir()
        {
            string guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"deepseek-harness\default-workspace");
            if (Directory.Exists(guess)) return guess;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        public static string ResolvedWorkDir()
        {
            if (!string.IsNullOrEmpty(WorkDir) && Directory.Exists(WorkDir)) return WorkDir;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>安全启动操作结果。</summary>
    internal struct SafeBootResult
    {
        public readonly bool Ok;
        public readonly string Message;
        public SafeBootResult(bool ok, string message) { Ok = ok; Message = message; }
    }

    /// <summary>
    /// 在不修改 DSH 源码的前提下，通过裁剪 profile 的 bundle 列表实现"安全启动"。
    /// 只保留 @deepseek-ai/ 前缀的内置 bundle，第三方 bundle 从 dsh.profile.bundles 移除但保留在 dependencies 中，
    /// 以便 DSH 启动后插件管理页仍能列出它们并提供移除按钮。
    /// </summary>
    internal static class SafeBootManager
    {
        private const string BuiltinPrefix = "@deepseek-ai/";

        /// <summary>
        /// 写 package.json 用的 UTF-8 编码：**不带 BOM**。
        /// DSH 用 JSON.parse 读 profile manifest，开头的 BOM 会让它直接抛
        /// SyntaxError（Unexpected token '\uFEFF'），所以这里不能复用 Encoding.UTF8
        /// ——.NET Framework 的 Encoding.UTF8 会写 BOM。
        /// </summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>当前 profile 的 package.json 路径。</summary>
        public static string ProfilePackageJsonPath
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("DSH_HOME");
                if (string.IsNullOrWhiteSpace(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                return Path.Combine(home, "profiles", Settings.Profile, "package.json");
            }
        }

        public static string BackupPath
        {
            get { return ProfilePackageJsonPath + ".safeboot.bak"; }
        }

        public static bool IsSafeBootActive()
        {
            return File.Exists(BackupPath);
        }

        public static SafeBootResult EnterSafeBoot()
        {
            string path = ProfilePackageJsonPath;
            if (!File.Exists(path))
                return new SafeBootResult(false, "找不到 profile package.json: " + path);

            // 已处于安全启动：直接返回，绝不能删备份，否则状态失联、无法恢复。
            if (IsSafeBootActive())
                return new SafeBootResult(false, "已经处于安全启动状态，无需重复进入");

            try
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var manifest = serializer.Deserialize<Dictionary<string, object>>(raw);

                var bundles = GetBundles(manifest);
                if (bundles == null)
                    return new SafeBootResult(false, "package.json 缺少 dsh.profile.bundles");

                var kept = new ArrayList();
                int removed = 0;
                foreach (object item in bundles)
                {
                    string name = item as string;
                    if (name != null && name.StartsWith(BuiltinPrefix))
                        kept.Add(name);
                    else
                        removed++;
                }

                // 没有可移除的第三方 bundle：不写备份，也不改文件。
                if (removed == 0)
                    return new SafeBootResult(false, "当前没有第三方 bundle，无需进入安全启动");

                // 先备份、再改文件；顺序反了会在写入失败时丢掉原始配置。
                // 备份原样保存，读回时由 ReadAllText 处理 BOM。
                File.WriteAllText(BackupPath, raw, Utf8NoBom);

                SetBundles(manifest, kept);
                File.WriteAllText(path, FormatJson(manifest), Utf8NoBom);

                return new SafeBootResult(true,
                    string.Format("已进入安全启动：已移除 {0} 个第三方 bundle，请手动重启 DSH 服务。", removed));
            }
            catch (Exception ex)
            {
                return new SafeBootResult(false, "进入安全启动失败: " + ex.Message);
            }
        }

        public static SafeBootResult ExitSafeBoot()
        {
            string path = ProfilePackageJsonPath;
            string backup = BackupPath;
            if (!File.Exists(backup))
                return new SafeBootResult(false, "未找到安全启动备份");

            try
            {
                var serializer = new JavaScriptSerializer();
                var backupManifest = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(backup, Encoding.UTF8));
                var currentManifest = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));

                var currentDeps = GetDependencyNames(currentManifest);
                var backupBundles = GetBundles(backupManifest);
                var currentBundles = GetBundles(currentManifest) ?? new ArrayList();

                // 保留当前内置 bundle（防止 DSH 升级后内置 bundle 有变化）
                var restored = new ArrayList();
                foreach (object item in currentBundles)
                {
                    string name = item as string;
                    if (name != null && name.StartsWith(BuiltinPrefix) && !restored.Contains(name))
                        restored.Add(name);
                }

                int restoredCount = 0;
                foreach (object item in backupBundles)
                {
                    string name = item as string;
                    if (name == null || name.StartsWith(BuiltinPrefix)) continue;
                    // 只恢复仍然存在于 dependencies 中的包；已被用户在插件页卸载的不再加回
                    if (currentDeps.Contains(name) && !restored.Contains(name))
                    {
                        restored.Add(name);
                        restoredCount++;
                    }
                }

                SetBundles(currentManifest, restored);
                File.WriteAllText(path, FormatJson(currentManifest), Utf8NoBom);
                File.Delete(backup);

                return new SafeBootResult(true,
                    string.Format("已退出安全启动：恢复了 {0} 个第三方 bundle，请手动重启 DSH 服务。", restoredCount));
            }
            catch (Exception ex)
            {
                return new SafeBootResult(false, "退出安全启动失败: " + ex.Message);
            }
        }

        private static ArrayList GetBundles(Dictionary<string, object> manifest)
        {
            object dshObj, profileObj, bundlesObj;
            if (!manifest.TryGetValue("dsh", out dshObj) || !(dshObj is Dictionary<string, object>)) return null;
            Dictionary<string, object> dsh = (Dictionary<string, object>)dshObj;
            if (!dsh.TryGetValue("profile", out profileObj) || !(profileObj is Dictionary<string, object>)) return null;
            Dictionary<string, object> profile = (Dictionary<string, object>)profileObj;
            if (!profile.TryGetValue("bundles", out bundlesObj) || !(bundlesObj is ArrayList)) return null;
            return (ArrayList)bundlesObj;
        }

        private static void SetBundles(Dictionary<string, object> manifest, ArrayList bundles)
        {
            Dictionary<string, object> dsh = (Dictionary<string, object>)manifest["dsh"];
            Dictionary<string, object> profile = (Dictionary<string, object>)dsh["profile"];
            profile["bundles"] = bundles;
        }

        private static HashSet<string> GetDependencyNames(Dictionary<string, object> manifest)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object depsObj;
            if (manifest.TryGetValue("dependencies", out depsObj) && depsObj is Dictionary<string, object>)
            {
                Dictionary<string, object> deps = (Dictionary<string, object>)depsObj;
                foreach (string key in deps.Keys) set.Add(key);
            }
            return set;
        }

        /// <summary>把 manifest 格式化为 2 空格缩进的 JSON，与 DSH 写回风格一致（末尾带换行）。</summary>
        private static string FormatJson(Dictionary<string, object> manifest)
        {
            var sb = new StringBuilder();
            FormatObject(manifest, sb, 0);
            // DSH 用 JSON.stringify(manifest, undefined, 2) + '\n' 写回，保持一致
            sb.Append('\n');
            return sb.ToString();
        }

        private static void FormatObject(Dictionary<string, object> obj, StringBuilder sb, int indent)
        {
            string pad = new string(' ', indent * 2);
            sb.AppendLine("{");
            int i = 0;
            foreach (var kv in obj)
            {
                sb.Append(pad).Append("  ").Append(JsonString(kv.Key)).Append(": ");
                FormatValue(kv.Value, sb, indent + 1);
                if (i < obj.Count - 1) sb.Append(",");
                sb.AppendLine();
                i++;
            }
            sb.Append(pad).Append("}");
        }

        private static void FormatArray(ArrayList arr, StringBuilder sb, int indent)
        {
            string pad = new string(' ', indent * 2);
            if (arr.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.AppendLine("[");
            for (int i = 0; i < arr.Count; i++)
            {
                sb.Append(pad).Append("  ");
                FormatValue(arr[i], sb, indent + 1);
                if (i < arr.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.Append(pad).Append("]");
        }

        private static void FormatValue(object value, StringBuilder sb, int indent)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string)
            {
                sb.Append(JsonString((string)value));
                return;
            }
            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is Dictionary<string, object>)
            {
                FormatObject((Dictionary<string, object>)value, sb, indent);
                return;
            }
            if (value is ArrayList)
            {
                FormatArray((ArrayList)value, sb, indent);
                return;
            }
            // number: int / long / double / decimal
            sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static string JsonString(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>通过 npm 安装/修复 DSH，并在成功后把 Node/npm 全局目录加入用户 PATH。</summary>
    internal static class DshSetup
    {
        private const string PackageName = "@deepseek-ai/dsh";
        private const string AllowScripts = "--allow-scripts=@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs";

        public static bool InstallDsh(out string logPath, out string error)
        {
            logPath = Path.Combine(Settings.LogDir, "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            error = null;

            string npm = Settings.FindNpmCommand();
            if (string.IsNullOrEmpty(npm))
            {
                error = "找不到 npm.cmd。请先安装 Node.js（含 npm），再重试。";
                Append(logPath, error);
                return false;
            }
            if (!File.Exists(Settings.NodePath))
            {
                error = "找不到 node.exe。请先安装 Node.js。";
                Append(logPath, error);
                return false;
            }

            Append(logPath, "# npm=" + npm);
            Append(logPath, "# node=" + Settings.NodePath);
            Append(logPath, "# 开始安装 " + PackageName + " ...");

            StringBuilder output = new StringBuilder();
            int code = RunNpm(npm, "install -g " + PackageName + " --no-audit --no-fund", logPath, output);
            string text = output.ToString();
            if (code != 0 || text.IndexOf("allow-scripts", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Append(logPath, "# 检测到 npm 安装脚本限制，使用 allow-scripts 重试 ...");
                output.Length = 0;
                code = RunNpm(npm, "install -g " + PackageName + " " + AllowScripts + " --no-audit --no-fund", logPath, output);
            }

            if (code != 0)
            {
                error = "npm install 失败（退出码 " + code + "），详见日志：" + logPath;
                Append(logPath, "# " + error);
                return false;
            }

            if (!Settings.ResolveRuntime(true))
            {
                error = "安装完成，但仍未找到 DSH。请查看日志：" + logPath;
                Append(logPath, "# " + error);
                return false;
            }

            Settings.AddRuntimeToUserPath();
            Append(logPath, "# 已识别: " + Settings.RuntimeSummary());
            Append(logPath, "# 已加入 PATH: " + Settings.GetNpmPrefix());
            return true;
        }

        private static int RunNpm(string npm, string args, string logPath, StringBuilder output)
        {
            string commandLine = "/d /s /c \"\"" + npm + "\" " + args + "\"";
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", commandLine);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Settings.ResolvedWorkDir();

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Append(logPath, e.Data, output); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Append(logPath, e.Data, output); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(600000))
                {
                    try { p.Kill(); } catch { }
                    Append(logPath, "# npm 超时（10 分钟）");
                    return -1;
                }
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private static void Append(string path, string line)
        {
            Append(path, line, null);
        }

        private static void Append(string path, string line, StringBuilder output)
        {
            try
            {
                if (output != null)
                {
                    lock (output) { output.AppendLine(line); }
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal sealed class Launcher : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string RunValue { get { return Program.AppId; } }

        private readonly Mutex _mutex;
        private readonly NotifyIcon _tray;
        private readonly MenuItem _miAutostart;
        private readonly MenuItem _miSafeBoot;
        private readonly MenuItem _miStatus;
        private readonly MenuItem _miOpenWeb;
        private readonly MenuItem _miViewLog;
        private Process _proc;
        private bool _stopping;
        private int _restarts;
        private volatile bool _installing;
        private volatile bool _webReadyLogged;
        private readonly object _lock = new object();
        private string _logPath;

        public Launcher()
        {
            bool created;
            _mutex = new Mutex(true, "Global\\" + Program.AppId + ".SingleInstance", out created);
            if (!created) { _mutex = null; return; }

            _miStatus = new MenuItem("状态：启动中…");
            _miStatus.Enabled = false;
            _miAutostart = new MenuItem("开机自启", ToggleAutostart);
            _miAutostart.Checked = IsAutostartEnabled();
            _miSafeBoot = new MenuItem("安全启动", ToggleSafeBoot);
            UpdateSafeBootMenuItem();

            _miOpenWeb = new MenuItem("打开 Web UI", delegate { OpenWebUi(); });
            _miOpenWeb.Enabled = IsWebProfile();

            MenuItem restart = new MenuItem("重启服务", delegate { Restart(); });
            MenuItem stop = new MenuItem("停止服务", delegate { StopService(true); });
            _miViewLog = new MenuItem("查看当前日志", delegate { ViewCurrentLog(); });
            MenuItem logs = new MenuItem("打开日志目录", delegate { OpenLogs(); });
            MenuItem about = new MenuItem("关于", delegate { ShowAbout(); });
            MenuItem quit = new MenuItem("退出", delegate { Quit(); });

            List<MenuItem> items = new List<MenuItem>();
            items.Add(_miStatus);
            items.Add(new MenuItem("-"));
            items.Add(_miOpenWeb);
            items.Add(restart);
            items.Add(stop);
            if (Settings.LaunchMode == "global")
            {
                items.Add(new MenuItem("安装 / 修复 DSH", delegate { InstallOrRepair(); }));
            }
            items.Add(new MenuItem("-"));
            items.Add(_miViewLog);
            items.Add(logs);
            items.Add(new MenuItem("-"));
            items.Add(_miAutostart);
            items.Add(_miSafeBoot);
            items.Add(new MenuItem("-"));
            items.Add(about);
            items.Add(quit);

            _tray = new NotifyIcon();
            _tray.Icon = BuildIcon();
            _tray.Text = Program.AppId.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0
                ? "DSH Web Launcher (Test)"
                : "DSH Web Launcher";
            _tray.ContextMenu = new ContextMenu(items.ToArray());
            _tray.DoubleClick += delegate { if (IsWebProfile()) OpenWebUi(); };
            _tray.Visible = true;

            AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); };
        }

        public void Start()
        {
            if (_mutex == null) return;

            using (SplashForm splash = new SplashForm())
            {
                splash.Show();
                bool ok = StartServiceWithProgress(splash);
                if (!ok && !splash.Cancelled)
                {
                    // 出错时保持 splash 显示，等用户点确定
                    while (splash.Visible)
                    {
                        Application.DoEvents();
                        Thread.Sleep(50);
                    }
                }
                else
                {
                    try { splash.Close(); } catch { }
                }
            }
        }

        private bool IsWebProfile()
        {
            return Settings.IsWebLikeProfile();
        }

        private bool TryPrepareStart(out string fileName, out string arguments, out string workDir, out string error)
        {
            fileName = null;
            arguments = null;
            workDir = null;
            error = null;

            if (!Settings.ResolveRuntime(false))
            {
                error = "找不到 Node.js 或 DSH 入口。\n" + Settings.RuntimeSummary()
                    + "\n\n右键托盘图标 → “安装 / 修复 DSH” 可自动安装（global 模式）。"
                    + "\n也可以手动安装后重启启动器，或在 settings.ini 中填写正确路径。";
                return false;
            }

            if (!Settings.BuildCommand(out fileName, out arguments, out workDir, out error))
                return false;

            return true;
        }

        private bool StartProcess(string fileName, string arguments, string workDir)
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return true;

                _logPath = Path.Combine(Settings.LogDir, "web-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = workDir;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                if (Settings.NodeNoWarnings)
                    psi.EnvironmentVariables["NODE_NO_WARNINGS"] = "1";

                Process p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendLog(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { AppendLog(e.Data); };
                p.Exited += delegate { OnExited(); };

                _proc = p;
                _stopping = false;
                _webReadyLogged = false;
                try
                {
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    UpdateStatus("运行中 PID " + p.Id + (IsWebProfile() ? " · 端口 " + Settings.Port : ""));
                    AppendLog("# config: " + Settings.RuntimeSummary());
                    AppendLog("# started: " + fileName + " " + arguments);
                    AppendLog("# workdir: " + workDir);
                    return true;
                }
                catch (Exception ex)
                {
                    _proc = null;
                    AppendLog("# start failed: " + ex.Message);
                    UpdateStatus("启动失败");
                    Balloon("启动失败", ex.Message);
                    return false;
                }
            }
        }

        private bool StartService()
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return true;
                if (IsWebProfile() && IsPortOpen(Settings.Port))
                {
                    UpdateStatus("已在运行（外部实例，端口 " + Settings.Port + "）");
                    return true;
                }
            }

            string fileName, arguments, workDir, error;
            if (!TryPrepareStart(out fileName, out arguments, out workDir, out error))
            {
                AppendLog("# config error: " + error.Replace("\n", " | "));
                UpdateStatus("配置错误");
                Balloon("无法启动", error);
                return false;
            }

            return StartProcess(fileName, arguments, workDir);
        }

        private bool StartServiceWithProgress(SplashForm splash)
        {
            splash.SetProgress(0, "正在初始化...");

            splash.SetProgress(10, "正在检测运行环境...");
            string fileName, arguments, workDir, error;
            if (!TryPrepareStart(out fileName, out arguments, out workDir, out error))
            {
                AppendLog("# config error: " + error.Replace("\n", " | "));
                splash.SetError("检测失败：找不到运行环境", error);
                UpdateStatus("配置错误");
                return false;
            }

            splash.SetProgress(30, "正在组装启动参数...");
            // 端口是否被外部占用（仅 web）
            if (IsWebProfile() && IsPortOpen(Settings.Port))
            {
                splash.SetError("端口已被占用", "127.0.0.1:" + Settings.Port + " 上已有服务在运行。");
                UpdateStatus("已在运行（外部实例，端口 " + Settings.Port + "）");
                return false;
            }

            splash.SetProgress(40, "正在启动 DSH 进程...");
            if (!StartProcess(fileName, arguments, workDir))
            {
                splash.SetError("启动失败", "无法启动子进程，请查看日志。");
                return false;
            }

            splash.SetProgress(50, "正在等待服务就绪...");
            bool ready = WaitForReady(splash);
            if (splash.Cancelled)
            {
                StopService(false);
                return false;
            }
            if (!ready)
            {
                splash.SetError("服务未就绪", "等待超时或 DSH 进程提前退出，请查看日志。");
                StopService(false);
                return false;
            }

            splash.SetProgress(100, "启动完成");
            Thread.Sleep(200);
            if (IsWebProfile())
                Balloon("已启动", "dsh web 正在后台运行（端口 " + Settings.Port + "）");
            else
                Balloon("已启动", "dsh " + Settings.Profile + " 正在后台运行");
            return true;
        }

        private bool WaitForReady(SplashForm splash)
        {
            DateTime start = DateTime.UtcNow;
            TimeSpan timeout = TimeSpan.FromSeconds(90);
            bool isWeb = IsWebProfile();

            if (isWeb)
            {
                // 第一阶段：等 TCP 端口通（DSH 刚拉起 HTTP server）
                while (DateTime.UtcNow - start < timeout)
                {
                    if (splash.Cancelled) return false;
                    lock (_lock) { if (_proc == null || _proc.HasExited) return false; }

                    double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                    int pct = 50 + Math.Min(20, (int)(elapsed / 10.0 * 20));
                    splash.SetProgress(pct, "正在等待 Web 端口就绪（" + Settings.Port + "）...");
                    if (IsPortOpen(Settings.Port)) break;

                    Thread.Sleep(200);
                    Application.DoEvents();
                }
                if (!IsPortOpen(Settings.Port)) return false;

                // 第二阶段：等 DSH 打印 "dsh web: http://..."，说明初始化完成、即将开浏览器
                DateTime readyStart = DateTime.UtcNow;
                while (DateTime.UtcNow - start < timeout)
                {
                    if (splash.Cancelled) return false;
                    lock (_lock) { if (_proc == null || _proc.HasExited) return false; }
                    if (_webReadyLogged) return true;

                    double elapsed = (DateTime.UtcNow - readyStart).TotalSeconds;
                    int pct = 70 + Math.Min(25, (int)(elapsed / 30.0 * 25));
                    splash.SetProgress(pct, "正在等待 DSH 初始化完成（MCP 服务等）...");

                    Thread.Sleep(200);
                    Application.DoEvents();
                }
                return false;
            }
            else
            {
                while (DateTime.UtcNow - start < timeout)
                {
                    if (splash.Cancelled) return false;
                    lock (_lock) { if (_proc == null || _proc.HasExited) return false; }

                    double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                    int pct = 50 + Math.Min(40, (int)(elapsed / timeout.TotalSeconds * 40));
                    splash.SetProgress(pct, "正在等待 " + Settings.Profile + " 服务稳定...");
                    if (elapsed >= 2.0) return true;

                    Thread.Sleep(200);
                    Application.DoEvents();
                }
                return false;
            }
        }

        private void OnExited()
        {
            lock (_lock)
            {
                if (_stopping) return;
                UpdateStatus("服务已退出");
                int code = -1;
                try { code = _proc == null ? -1 : _proc.ExitCode; } catch { }
                AppendLog("# process exited, code=" + code);
                if (Settings.AutoRestart && _restarts < 3)
                {
                    _restarts++;
                    AppendLog("# auto-restart attempt " + _restarts);
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(5000);
                        StartService();
                    });
                }
                else if (Settings.AutoRestart)
                {
                    Balloon("反复退出", "已连续重启 3 次，停止自动拉起。请查看日志。");
                }
            }
        }

        private void Restart()
        {
            StopService(false);
            _restarts = 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1200);
                StartService();
            });
        }

        private void InstallOrRepair()
        {
            if (_installing)
            {
                Balloon("正在安装", "DSH 安装任务还在进行，请稍候。");
                return;
            }

            Settings.ResolveRuntime(false);
            string npm = Settings.FindNpmCommand();
            if (string.IsNullOrEmpty(npm))
            {
                DialogResult answer = MessageBox.Show(
                    "没有找到 Node.js / npm。\n\n请先安装 Node.js LTS，安装时保持默认的 npm 组件。\n是否现在打开 Node.js 下载页？",
                    "缺少 Node.js",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes)
                {
                    try { Process.Start("https://nodejs.org/zh-cn/download"); } catch { }
                }
                return;
            }

            DialogResult confirm = MessageBox.Show(
                "将通过 npm 安装或修复 DSH：\n\n"
                + "npm install -g " + "@deepseek-ai/dsh\n\n"
                + "安装完成后会自动识别 DSH，并把 Node 目录和 npm 全局目录加入当前用户 PATH。\n"
                + "需要联网，可能耗时几分钟。继续吗？",
                "安装 / 修复 DSH",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            _installing = true;
            UpdateStatus("正在安装 DSH…");
            Balloon("正在安装", "正在通过 npm 安装 DSH，请稍候…");
            StopService(false);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string logPath = null;
                string error = null;
                bool ok = false;
                try { ok = DshSetup.InstallDsh(out logPath, out error); }
                catch (Exception ex) { error = ex.Message; }

                _installing = false;
                if (ok)
                {
                    UpdateStatus("DSH 已安装");
                    Balloon("安装完成", "DSH 已安装并加入 PATH，正在启动服务…");
                    StartService();
                }
                else
                {
                    UpdateStatus("安装失败");
                    string detail = error ?? "未知错误";
                    if (!string.IsNullOrEmpty(logPath)) detail += "\n日志：" + logPath;
                    Balloon("安装失败", detail);
                }
            });
        }

        private void StopService(bool manual)
        {
            lock (_lock)
            {
                _stopping = true;
                Process current = _proc;
                _proc = null;
                if (current != null && !current.HasExited)
                {
                    try
                    {
                        ProcessStartInfo kpsi = new ProcessStartInfo("taskkill", "/PID " + current.Id + " /T /F");
                        kpsi.CreateNoWindow = true;
                        kpsi.UseShellExecute = false;
                        Process k = Process.Start(kpsi);
                        k.WaitForExit(6000);
                    }
                    catch
                    {
                        try { current.Kill(); } catch { }
                    }
                }
                else if (IsWebProfile() && IsPortOpen(Settings.Port))
                {
                    // 当前服务不是本启动器启动的（外部实例），但用户点了停止/重启，
                    // 强制杀掉占用端口的进程，让后续操作可以继续。
                    KillExternalInstanceOnPort(Settings.Port);
                }
                UpdateStatus(manual ? "已停止" : "正在重启…");
            }
        }

        /// <summary>通过 netstat 找到监听指定端口的进程并用 taskkill /T 强制结束。</summary>
        private static bool KillExternalInstanceOnPort(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c netstat -ano | findstr \":" + port + "\"");
                psi.RedirectStandardOutput = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    foreach (string raw in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.Trim();
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0 &&
                            line.IndexOf("监听", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) continue;
                        int pid;
                        if (int.TryParse(parts[parts.Length - 1], out pid))
                        {
                            try
                            {
                                ProcessStartInfo kpsi = new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F");
                                kpsi.CreateNoWindow = true;
                                kpsi.UseShellExecute = false;
                                Process k = Process.Start(kpsi);
                                k.WaitForExit(6000);
                                return true;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void OpenWebUi()
        {
            if (!IsWebProfile()) return;
            try { Process.Start("http://127.0.0.1:" + Settings.Port + "/"); }
            catch (Exception ex) { Balloon("打开失败", ex.Message); }
        }

        private void ViewCurrentLog()
        {
            string path = _logPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try
                {
                    string[] files = Directory.GetFiles(Settings.LogDir, "web-*.log");
                    if (files.Length == 0)
                    {
                        Balloon("暂无日志", "还没有生成服务日志。");
                        return;
                    }
                    Array.Sort(files, delegate(string a, string b)
                    {
                        return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
                    });
                    path = files[0];
                }
                catch (Exception ex)
                {
                    Balloon("读取日志失败", ex.Message);
                    return;
                }
            }
            try
            {
                LogForm form = new LogForm(path);
                form.Show();
            }
            catch (Exception ex)
            {
                Balloon("打开日志失败", ex.Message);
            }
        }

        private void OpenLogs()
        {
            try { Process.Start("explorer.exe", "\"" + Settings.LogDir + "\""); } catch { }
        }

        private static void ShowAbout()
        {
            try
            {
                using (AboutForm f = new AboutForm())
                {
                    f.ShowDialog();
                }
            }
            catch { }
        }

        public static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        private void ToggleAutostart(object sender, EventArgs e)
        {
            _miAutostart.Checked = !_miAutostart.Checked;
            SetAutostart(_miAutostart.Checked);
        }

        private void UpdateSafeBootMenuItem()
        {
            bool active = SafeBootManager.IsSafeBootActive();
            _miSafeBoot.Text = active ? "退出安全启动" : "安全启动";
            _miSafeBoot.Checked = active;
        }

        private void ToggleSafeBoot(object sender, EventArgs e)
        {
            bool active = SafeBootManager.IsSafeBootActive();

            // 如果 DSH 正在运行，先询问并停止服务，避免并发写 package.json
            if (_proc != null && !_proc.HasExited)
            {
                DialogResult dr = MessageBox.Show(
                    "DSH 服务正在运行，修改 bundle 列表前需要先停止服务。\n\n是否立即停止？",
                    active ? "退出安全启动" : "安全启动",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (dr != DialogResult.Yes) return;
                StopService(false);
            }

            SafeBootResult result = active ? SafeBootManager.ExitSafeBoot() : SafeBootManager.EnterSafeBoot();
            UpdateSafeBootMenuItem();

            if (result.Ok)
            {
                MessageBox.Show(
                    result.Message + "\n\n请手动点击“重启服务”使配置生效。",
                    active ? "退出安全启动" : "安全启动",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    result.Message,
                    active ? "退出安全启动" : "安全启动",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }

        private void Balloon(string title, string text)
        {
            try { _tray.BalloonTipTitle = title; _tray.BalloonTipText = text; _tray.ShowBalloonTip(3000); } catch { }
        }

        private void UpdateStatus(string text)
        {
            try
            {
                _miStatus.Text = "状态：" + text;
                _tray.Text = Truncate("DSH Web — " + text, 63);
            }
            catch { }
        }

        private void AppendLog(string line)
        {
            if (line == null) return;
            try
            {
                if (line.IndexOf("dsh web:", StringComparison.OrdinalIgnoreCase) >= 0)
                    _webReadyLogged = true;
                string entry = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine;
                File.AppendAllText(_logPath ?? Path.Combine(Settings.LogDir, "launcher.log"), entry, Encoding.UTF8);
            }
            catch { }
        }

        private void Quit()
        {
            _stopping = true;
            StopService(false);
            Shutdown();
            Application.Exit();
        }

        private void Shutdown()
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }
        }

        public void Dispose() { Shutdown(); }

        private static bool IsPortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        private static string Truncate(string s, int max) { return s.Length <= max ? s : s.Substring(0, max); }

        /// <summary>托盘图标：优先用嵌入的 whale.ico（7 个尺寸），失败则回退到运行时绘制的方块图标。</summary>
        private static Icon BuildIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("whale.ico"))
                {
                    if (s != null)
                    {
                        Size target = SystemInformation.SmallIconSize;
                        return new Icon(s, target);
                    }
                }
            }
            catch { }
            return BuildFallbackIcon();
        }

        private static Icon BuildFallbackIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(22, 26, 33)))
                using (GraphicsPath gp = RoundRect(new Rectangle(1, 1, 30, 30), 7))
                {
                    g.FillPath(bg, gp);
                }
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(94, 234, 212)))
                using (Font f = new Font("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("D", f, fg, new RectangleF(0, 0, 32, 32), sf);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>关于窗口：版本、适用端（Web 版，非 Desktop 端）、作者与仓库。</summary>
    internal sealed class AboutForm : Form
    {
        private Bitmap _logo;

        /// <summary>从嵌入资源取鲸鱼图标；失败再退回 exe 的 Win32 图标。</summary>
        private static Bitmap LoadLogo()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("whale.ico"))
                {
                    if (s != null) return new Icon(s, new Size(64, 64)).ToBitmap();
                }
            }
            catch { }
            try
            {
                Icon assoc = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (assoc != null) return assoc.ToBitmap();
            }
            catch { }
            return null;
        }

        /// <summary>直接画在窗体上，避免 PictureBox 在 DrawToBitmap 下不出的问题。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_logo != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(_logo, new Rectangle(20, 14, 64, 64));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _logo != null) { try { _logo.Dispose(); } catch { } }
            base.Dispose(disposing);
        }

        public AboutForm()
        {
            Text = "关于 DSH 启动器";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 440);
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = new Font("Microsoft YaHei UI", 9f);

            int y = 16;

            _logo = LoadLogo();
            DoubleBuffered = true;

            Label title = MakeLabel("DSH 启动器", 16f, FontStyle.Bold, Color.FromArgb(94, 234, 212));
            title.Location = new Point(96, y - 4);
            title.AutoSize = true;
            Controls.Add(title);

            Label ver = MakeLabel("v" + Program.VersionText + "   ·   Windows 托盘启动工具", 9f, FontStyle.Regular, Color.FromArgb(148, 163, 184));
            ver.Location = new Point(98, y + 26);
            ver.AutoSize = true;
            Controls.Add(ver);

            y += 82;

            Controls.Add(SectionTitle("它做什么", ref y));
            Controls.Add(BodyText("把 dsh 变成托盘常驻的后台服务：双击一次，服务静默运行；\n托盘图标右键即可打开界面、重启、停止、安装/修复 DSH、开机自启、看日志。\n支持 npm 全局安装版与本地 apps/cli 源码版两种启动模式。", ref y));

            y += 4;
            Controls.Add(SectionTitle("当前模式", ref y));
            Controls.Add(OkText(
                "LaunchMode=" + Settings.LaunchMode
                + "，Profile=" + Settings.Profile
                + (IsWebText() ? "\nWeb 界面：http://127.0.0.1:" + Settings.Port + "（端口可在配置中修改）" : "\n非 Web profile，无浏览器界面。"), ref y));
            Controls.Add(WarnText(
                "不适用于 DeepSeek Harness Desktop 桌面客户端。\n"
                + "桌面端为 Electron 应用，自带托盘、开机自启与窗口管理，\n"
                + "无需本工具；其 desktop profile 由客户端独占，CLI 也无法接管。", ref y));

            y += 4;
            Controls.Add(SectionTitle("作者与仓库", ref y));
            Controls.Add(BodyText("作者：liuxili", ref y));

            LinkLabel repo = new LinkLabel();
            repo.Text = "github.com/healy2016/dsh-launcher";
            repo.LinkColor = Color.FromArgb(94, 234, 212);
            repo.ActiveLinkColor = Color.White;
            repo.LinkBehavior = LinkBehavior.HoverUnderline;
            repo.Location = new Point(24, y);
            repo.AutoSize = true;
            repo.LinkClicked += delegate
            {
                try { Process.Start("https://github.com/healy2016/dsh-launcher"); } catch { }
            };
            Controls.Add(repo);

            Button ok = new Button();
            ok.Text = "确定";
            ok.Size = new Size(88, 30);
            ok.Location = new Point(ClientSize.Width - 108, ClientSize.Height - 44);
            ok.BackColor = Color.FromArgb(38, 44, 54);
            ok.ForeColor = Color.Gainsboro;
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
        }

        private static bool IsWebText()
        {
            return Settings.LaunchMode == "global" || string.Equals(Settings.Profile, "web", StringComparison.OrdinalIgnoreCase);
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", size, style);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            return l;
        }

        private static Label SectionTitle(string text, ref int y)
        {
            Label l = MakeLabel(text, 9.5f, FontStyle.Bold, Color.FromArgb(94, 234, 212));
            l.Location = new Point(20, y);
            l.AutoSize = true;
            y += 24;
            return l;
        }

        private static Label BodyText(string text, ref int y)
        {
            Label l = MakeLabel(text, 9f, FontStyle.Regular, Color.FromArgb(203, 213, 225));
            l.Location = new Point(24, y);
            l.AutoSize = true;
            y += l.PreferredHeight + 6;
            return l;
        }

        private static Label OkText(string text, ref int y)
        {
            Label l = MakeLabel(text, 9f, FontStyle.Regular, Color.FromArgb(134, 239, 172));
            l.Location = new Point(20, y);
            l.AutoSize = true;
            y += l.PreferredHeight + 6;
            return l;
        }

        private static Label WarnText(string text, ref int y)
        {
            Label l = MakeLabel(text, 9f, FontStyle.Regular, Color.FromArgb(250, 204, 21));
            l.Location = new Point(20, y);
            l.AutoSize = true;
            y += l.PreferredHeight + 6;
            return l;
        }
    }

    /// <summary>启动进度窗。</summary>
    internal sealed class SplashForm : Form
    {
        private ProgressBar _bar;
        private Label _status;
        private Label _detail;
        private Button _ok;
        private Button _cancel;
        private volatile bool _cancelled;

        public bool Cancelled { get { return _cancelled; } }

        public SplashForm()
        {
            Text = "DSH 启动器";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 170);
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = new Font("Microsoft YaHei UI", 9f);

            _status = new Label();
            _status.Text = "正在初始化...";
            _status.Location = new Point(20, 16);
            _status.Size = new Size(420, 22);
            _status.ForeColor = Color.FromArgb(226, 232, 240);
            Controls.Add(_status);

            _bar = new ProgressBar();
            _bar.Location = new Point(20, 44);
            _bar.Size = new Size(420, 20);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.Value = 0;
            Controls.Add(_bar);

            _detail = new Label();
            _detail.Text = "";
            _detail.Location = new Point(20, 72);
            _detail.Size = new Size(420, 36);
            _detail.ForeColor = Color.FromArgb(248, 113, 113);
            _detail.Visible = false;
            Controls.Add(_detail);

            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.Size = new Size(80, 28);
            _cancel.Location = new Point(360, 116);
            _cancel.BackColor = Color.FromArgb(38, 44, 54);
            _cancel.ForeColor = Color.Gainsboro;
            _cancel.FlatStyle = FlatStyle.Flat;
            _cancel.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            _cancel.Click += delegate { _cancelled = true; try { Close(); } catch { } };
            Controls.Add(_cancel);

            _ok = new Button();
            _ok.Text = "确定";
            _ok.Size = new Size(80, 28);
            _ok.Location = new Point(360, 116);
            _ok.BackColor = Color.FromArgb(38, 44, 54);
            _ok.ForeColor = Color.Gainsboro;
            _ok.FlatStyle = FlatStyle.Flat;
            _ok.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            _ok.Visible = false;
            _ok.Click += delegate { try { Close(); } catch { } };
            Controls.Add(_ok);
        }

        public void SetProgress(int pct, string text)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            try
            {
                if (_bar != null) _bar.Value = pct;
                if (_status != null) _status.Text = text;
            }
            catch { }
            Application.DoEvents();
        }

        public void SetError(string text, string detail)
        {
            try
            {
                if (_status != null) { _status.Text = text; _status.ForeColor = Color.FromArgb(248, 113, 113); }
                if (_bar != null) _bar.Value = 0;
                if (_detail != null) { _detail.Text = detail ?? ""; _detail.Visible = !string.IsNullOrEmpty(detail); }
                if (_cancel != null) _cancel.Visible = false;
                if (_ok != null) { _ok.Visible = true; _ok.Focus(); }
            }
            catch { }
        }
    }

    /// <summary>日志查看窗口。</summary>
    internal sealed class LogForm : Form
    {
        private string _path;
        private TextBox _text;

        public LogForm(string path)
        {
            _path = path;
            Text = "DSH 日志 - " + Path.GetFileName(path);
            ClientSize = new Size(960, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Color.FromArgb(226, 232, 240);

            _text = new TextBox();
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.Dock = DockStyle.Fill;
            _text.Font = new Font("Consolas", 10f);
            _text.BackColor = Color.FromArgb(16, 18, 23);
            _text.ForeColor = Color.FromArgb(203, 213, 225);
            _text.WordWrap = false;
            Controls.Add(_text);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 40;
            bottom.BackColor = Color.FromArgb(24, 27, 33);
            Controls.Add(bottom);

            Button refresh = new Button();
            refresh.Text = "刷新";
            refresh.Size = new Size(80, 28);
            refresh.Location = new Point(12, 6);
            refresh.BackColor = Color.FromArgb(38, 44, 54);
            refresh.ForeColor = Color.Gainsboro;
            refresh.FlatStyle = FlatStyle.Flat;
            refresh.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            refresh.Click += delegate { LoadLog(); };
            bottom.Controls.Add(refresh);

            Button openDir = new Button();
            openDir.Text = "打开目录";
            openDir.Size = new Size(80, 28);
            openDir.Location = new Point(104, 6);
            openDir.BackColor = Color.FromArgb(38, 44, 54);
            openDir.ForeColor = Color.Gainsboro;
            openDir.FlatStyle = FlatStyle.Flat;
            openDir.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            openDir.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(_path) + "\""); } catch { }
            };
            bottom.Controls.Add(openDir);

            LoadLog();
        }

        private void LoadLog()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _text.Text = "日志文件不存在: " + _path;
                    return;
                }
                _text.Text = File.ReadAllText(_path, Encoding.UTF8);
                _text.SelectionStart = _text.Text.Length;
                _text.ScrollToCaret();
            }
            catch (Exception ex)
            {
                _text.Text = "读取日志失败: " + ex.Message;
            }
        }
    }

    internal static class Program
    {
        /// <summary>程序版本，与 AssemblyVersion 同步。</summary>
        public const string VersionText = "1.3.0";

        /// <summary>根据 exe 文件名生成的应用标识。测试版 exe 名包含 Test 时使用 settings.test.ini。</summary>
        public static string AppId
        {
            get
            {
                try
                {
                    return Path.GetFileNameWithoutExtension(Application.ExecutablePath);
                }
                catch { return "DSHWebLauncher"; }
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // GUI 子系统程序没有控制台时 Console.OutputEncoding 是空操作，
            // 必须直接包装标准输出流才能保证重定向到文件时中文不乱码。
            try
            {
                StreamWriter utf8Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                utf8Out.AutoFlush = true;
                Console.SetOut(utf8Out);
            }
            catch { }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Settings.IniFileName = AppId.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0
                ? "settings.test.ini"
                : "settings.ini";
            Settings.Load();

            bool selftest = false;
            bool doEnableAuto = false;
            bool doDisableAuto = false;
            bool doAutoStatus = false;
            bool doStatus = false;
            bool doDetect = false;
            bool doInstallDsh = false;
            bool doAbout = false;
            string renderAbout = null;
            bool doSafeBoot = false;
            bool doExitSafeBoot = false;
            bool doSafeBootStatus = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--port") { int tmp; if (i + 1 < args.Length && int.TryParse(args[++i], out tmp)) Settings.Port = tmp; }
                else if (a == "--profile") { if (i + 1 < args.Length) Settings.Profile = args[++i]; }
                else if (a == "--workdir") { if (i + 1 < args.Length) Settings.WorkDir = args[++i]; }
                else if (a == "--mode") { if (i + 1 < args.Length) Settings.LaunchMode = args[++i].ToLowerInvariant(); }
                else if (a == "--launchargs") { if (i + 1 < args.Length) Settings.LaunchArgs = args[++i]; }
                else if (a == "--selftest") selftest = true;
                else if (a == "--enable-autostart") doEnableAuto = true;
                else if (a == "--disable-autostart") doDisableAuto = true;
                else if (a == "--autostart-status") doAutoStatus = true;
                else if (a == "--status") doStatus = true;
                else if (a == "--detect") doDetect = true;
                else if (a == "--install-dsh") doInstallDsh = true;
                else if (a == "--about") doAbout = true;
                else if (a == "--render-about") { if (i + 1 < args.Length) renderAbout = args[++i]; }
                else if (a == "--safe-boot") doSafeBoot = true;
                else if (a == "--exit-safe-boot") doExitSafeBoot = true;
                else if (a == "--safe-boot-status") doSafeBootStatus = true;
            }

            if (Settings.Port <= 0) Settings.Port = 3080;
            if (Settings.LaunchMode != "local") Settings.LaunchMode = "global";

            if (doDetect)
            {
                bool ok = Settings.ResolveRuntime(true);
                Settings.Save();
                Console.WriteLine("node=" + Settings.NodePath);
                Console.WriteLine("bin=" + Settings.DshBin);
                Console.WriteLine("runtime=" + (ok ? "ok" : "missing"));
                return ok ? 0 : 1;
            }

            if (doInstallDsh)
            {
                string logPath;
                string error;
                bool ok = DshSetup.InstallDsh(out logPath, out error);
                Console.WriteLine("install=" + (ok ? "ok" : "failed"));
                if (!string.IsNullOrEmpty(logPath)) Console.WriteLine("log=" + logPath);
                if (!string.IsNullOrEmpty(error)) Console.WriteLine("error=" + error);
                return ok ? 0 : 1;
            }

            Settings.Save();

            if (doSafeBootStatus)
            {
                Console.WriteLine("profile=" + Settings.Profile);
                Console.WriteLine("package_json=" + SafeBootManager.ProfilePackageJsonPath);
                Console.WriteLine("safe_boot=" + (SafeBootManager.IsSafeBootActive() ? "active" : "inactive"));
                return 0;
            }

            if (doSafeBoot)
            {
                SafeBootResult r = SafeBootManager.EnterSafeBoot();
                Console.WriteLine("ok=" + (r.Ok ? "true" : "false"));
                Console.WriteLine("message=" + r.Message);
                return r.Ok ? 0 : 1;
            }

            if (doExitSafeBoot)
            {
                SafeBootResult r = SafeBootManager.ExitSafeBoot();
                Console.WriteLine("ok=" + (r.Ok ? "true" : "false"));
                Console.WriteLine("message=" + r.Message);
                return r.Ok ? 0 : 1;
            }

            if (doEnableAuto) { Launcher.SetAutostart(true); Console.WriteLine("autostart=" + (Launcher.IsAutostartEnabled() ? "enabled" : "failed")); return 0; }
            if (doDisableAuto) { Launcher.SetAutostart(false); Console.WriteLine("autostart=" + (Launcher.IsAutostartEnabled() ? "failed" : "disabled")); return 0; }
            if (doAutoStatus) { Console.WriteLine("autostart=" + (Launcher.IsAutostartEnabled() ? "enabled" : "disabled")); return 0; }
            if (doStatus)
            {
                Console.WriteLine("mode=" + Settings.LaunchMode);
                Console.WriteLine("profile=" + Settings.Profile);
                Console.WriteLine("port=" + Settings.Port);
                Console.WriteLine("workdir=" + Settings.ResolvedWorkDir());
                Console.WriteLine("node=" + Settings.NodePath);
                Console.WriteLine("bin=" + Settings.DshBin);
                Console.WriteLine("runtime=" + (Settings.RuntimeReady ? "ok" : "missing"));
                if (Settings.LaunchMode == "local")
                {
                    string entry, error;
                    bool tsx;
                    bool ok = Settings.DetectLocalEntry(Settings.ResolvedWorkDir(), out entry, out tsx, out error);
                    Console.WriteLine("entry=" + (ok ? entry : error));
                }
                return 0;
            }
            if (doAbout)
            {
                Console.WriteLine(AboutText());
                return 0;
            }
            if (!string.IsNullOrEmpty(renderAbout))
            {
                return RenderAbout(renderAbout);
            }
            if (selftest) return SelfTest();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (Launcher launcher = new Launcher())
            {
                launcher.Start();
                Application.Run();
                return 0;
            }
        }

        /// <summary>把「关于」窗口渲染成 PNG（用于文档截图与视觉回归）。</summary>
        private static int RenderAbout(string path)
        {
            try
            {
                using (AboutForm f = new AboutForm())
                {
                    // DrawToBitmap 只能可靠地抓到"已显示"的控件，所以先把窗口挪到屏幕外显示出来
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);
                    f.Show();
                    Application.DoEvents();
                    Thread.Sleep(300);
                    Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    f.Hide();
                }
                Console.WriteLine("rendered=" + path);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + ex.Message);
                return 1;
            }
        }

        /// <summary>关于信息（控制台版，与托盘「关于」窗口同源）。</summary>
        public static string AboutText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DSH 启动器 v" + VersionText);
            sb.AppendLine("把 dsh 变成 Windows 托盘常驻的后台服务");
            sb.AppendLine();
            sb.AppendLine("当前模式");
            sb.AppendLine("  LaunchMode=" + Settings.LaunchMode);
            sb.AppendLine("  Profile=" + Settings.Profile);
            sb.AppendLine("  Port=" + Settings.Port);
            sb.AppendLine();
            sb.AppendLine("适用端");
            sb.AppendLine("  [是] Web 版：以 dsh web 启动，界面在浏览器中访问");
            sb.AppendLine("       http://127.0.0.1:" + Settings.Port + "（端口可改）");
            sb.AppendLine("  [否] DeepSeek Harness Desktop 桌面客户端：Electron 应用自带托盘");
            sb.AppendLine("       与开机自启，desktop profile 由客户端独占，CLI 无法接管");
            sb.AppendLine();
            sb.AppendLine("环境");
            sb.AppendLine("  自动识别 Node.js / DSH，并把 Node 目录与 npm 全局 bin 加入用户 PATH");
            sb.AppendLine("  托盘菜单「安装 / 修复 DSH」可调用 npm 安装或修复 DSH");
            sb.AppendLine();
            sb.AppendLine("作者  liuxili");
            sb.AppendLine("仓库  https://github.com/healy2016/dsh-launcher");
            sb.AppendLine();
            sb.Append("本工具不包含 DSH 本体，仅从外部调用 node 启动 dsh。");
            return sb.ToString();
        }

        /// <summary>同步自检：拉起 dsh，等端口/就绪信号，报告结果后清理退出。</summary>
        private static int SelfTest()
        {
            Settings.ResolveRuntime(false);
            Console.WriteLine("config: mode=" + Settings.LaunchMode);
            Console.WriteLine("        profile=" + Settings.Profile);
            Console.WriteLine("        port=" + Settings.Port);
            Console.WriteLine("        workdir=" + Settings.ResolvedWorkDir());

            string fileName, arguments, workDir, error;
            if (!Settings.BuildCommand(out fileName, out arguments, out workDir, out error))
            {
                Console.WriteLine("runtime=missing");
                Console.WriteLine("ERROR " + error);
                return 1;
            }
            Console.WriteLine("runtime=ok");

            string logPath = Path.Combine(Settings.LogDir, "selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            Process p = null;
            int exit = 1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = workDir;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                if (Settings.NodeNoWarnings)
                    psi.EnvironmentVariables["NODE_NO_WARNINGS"] = "1";

                p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppendLine(logPath, e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppendLine(logPath, e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                Console.WriteLine("spawned pid=" + p.Id);

                bool isWeb = Settings.LaunchMode == "global" || string.Equals(Settings.Profile, "web", StringComparison.OrdinalIgnoreCase);
                bool ready = false;
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    if (p.HasExited) { Console.WriteLine("process exited early, code=" + p.ExitCode); break; }
                    if (isWeb)
                    {
                        if (PortOpen(Settings.Port)) { ready = true; break; }
                    }
                    else if ((DateTime.UtcNow - deadline.AddSeconds(-90)).TotalSeconds >= 2.0)
                    {
                        ready = true;
                        break;
                    }
                    Thread.Sleep(700);
                }
                Console.WriteLine("ready=" + (ready ? "1" : "0"));
                exit = ready ? 0 : 1;
            }
            catch (Exception ex) { Console.WriteLine("ERROR " + ex.Message); }
            finally
            {
                if (p != null && !p.HasExited)
                {
                    try
                    {
                        ProcessStartInfo kpsi = new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F");
                        kpsi.CreateNoWindow = true;
                        kpsi.UseShellExecute = false;
                        Process k = Process.Start(kpsi);
                        k.WaitForExit(6000);
                    }
                    catch { }
                }
            }
            Console.WriteLine("log=" + logPath);
            try
            {
                string[] lines = File.ReadAllLines(logPath);
                int from = Math.Max(0, lines.Length - 12);
                for (int i = from; i < lines.Length; i++) Console.WriteLine("  | " + lines[i]);
            }
            catch { }
            return exit;
        }

        private static void AppendLine(string path, string line)
        {
            try { File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
