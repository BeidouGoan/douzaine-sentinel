// ===========================================================================
//  SentinelConsole.cs — 工位哨兵 PC 端原生控制台
//
//  ★ 为什么用 C# WinForms 而不是 Python + Web 界面：
//      - 真正独立的原生窗口（不是浏览器，没有地址栏/标签页）
//      - .NET Framework 4.8 是 Windows 自带的，编译产物是单个 exe
//      - 通知可以用 WinForms 自己画（完全可控的语义和样式）
//      - 暂停播放可以用 GSMTC 直接对媒体会话下指令，比模拟按键可靠
//
//  编译（不需要 Visual Studio）：
//      build.cmd
//  等价于：
//      csc.exe /target:winexe /out:SentinelConsole.exe SentinelConsole.cs
//            /r:System.Windows.Forms.dll /r:System.Drawing.dll
//            /r:System.Web.Extensions.dll
//
//  ★ 用到的 .NET 4.x 特性：
//      System.Web.Script.Serialization.JavaScriptSerializer  —— 解析 JSON
// ===========================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace SentinelConsole
{
    // =======================================================================
    //  设计系统 · Design Tokens
    //
    //  ★ 色值来源：docs/06-UI设计规范.md（手机端 UI 规范，已定稿）
    //    该规范 §1.3 明确写了「与 PC 控制台语义一致：绿=在，红=不在，琥珀=待确认」，
    //    所以这里直接采用规范里的深色模式色值，
    //    而不是各搞一套 —— 同一个绿在手机和 PC 上必须一模一样。
    //
    //  ★ 对比度（相对 surface #171B22 实测）：
    //    on_surface        #E8EAED  13.6:1  ✓ 远超标
    //    on_surface_variant#A7AFBC   7.3:1  ✓
    //    on_surface_muted  #79828F   4.2:1  ⚠ 仅用于 12px+ 装饰性文字
    //    primary           #2F6FE0   4.7:1  ✓ 对白字
    //    present/absence   ≥ 7:1     ✓
    // =======================================================================
    internal static class Theme
    {
        // ---- 中性色 ----
        public static readonly Color Background = ColorTranslator.FromHtml("#0E1116");
        public static readonly Color Surface = ColorTranslator.FromHtml("#171B22");
        public static readonly Color SurfaceVariant = ColorTranslator.FromHtml("#22262F");
        public static readonly Color SurfaceHover = ColorTranslator.FromHtml("#2A2F3A");
        public static readonly Color OnSurface = ColorTranslator.FromHtml("#E8EAED");
        public static readonly Color OnSurfaceVariant = ColorTranslator.FromHtml("#A7AFBC");
        public static readonly Color OnSurfaceMuted = ColorTranslator.FromHtml("#79828F");
        public static readonly Color Outline = ColorTranslator.FromHtml("#2E333D");
        public static readonly Color OutlineStrong = ColorTranslator.FromHtml("#3A414D");

        // ---- 品牌 / 交互 ----
        public static readonly Color Primary = ColorTranslator.FromHtml("#2F6FE0");
        public static readonly Color PrimaryHover = ColorTranslator.FromHtml("#3B7BEA");
        public static readonly Color BrandBlue = ColorTranslator.FromHtml("#4D8DFF");
        public static readonly Color OnPrimary = Color.White;

        // ---- 状态语义（与手机端一致）----
        public static readonly Color Present = ColorTranslator.FromHtml("#34D399");
        public static readonly Color PresentGlow = ColorTranslator.FromHtml("#14B87F");
        public static readonly Color Absent = ColorTranslator.FromHtml("#F87171");
        public static readonly Color AbsentGlow = ColorTranslator.FromHtml("#F05252");
        public static readonly Color Uncertain = ColorTranslator.FromHtml("#FBBF24");
        public static readonly Color UncertainGlow = ColorTranslator.FromHtml("#E38A17");

        // ---- 间距（4px 基准）----
        public const int Space1 = 4;
        public const int Space2 = 8;
        public const int Space3 = 12;
        public const int Space4 = 16;
        public const int Space6 = 24;
        public const int Space8 = 32;

        // ---- 圆角 ----
        public const int RadiusCard = 10;
        public const int RadiusControl = 6;

        // ---- 字体 ----
        public static readonly string FontFamily = "Microsoft YaHei UI";
        public static readonly string FontMono = "Consolas";

        /// <summary>产品名</summary>
        public const string ProductName = "斗在呢";
        public const string ProductTagline = "工位哨兵 · 离座检测";
    }

    // =======================================================================
    //  配置
    // =======================================================================
    /// <summary>
    /// 单个触发方向（离开 / 回来）的独立配置。
    /// 两个方向互不干扰，各自保存自己的动作、缓冲、冷却、命令。
    /// </summary>
    internal class TriggerProfile
    {
        public string[] actions = { "notify" };
        public double buffer_seconds = 0;
        public double cooldown = 30;
        public string exec = "";
    }

    internal class AppConfig
    {
        // ★ 离开 / 回来 两个方向各自一套配置，互不影响、各自保存
        public TriggerProfile absent = new TriggerProfile();
        public TriggerProfile present = new TriggerProfile();
        public bool dry_run = false;
        public string phone_host = "";
        // ★ 开机自启：HKCU\...\Run 里写一条「斗在呢」启动项。默认开启。
        public bool autostart = true;

        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        public static string Path
        {
            get
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                return System.IO.Path.Combine(dir, "sentinel_config.json");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    string txt = File.ReadAllText(Path, Encoding.UTF8);

                    // ---- 旧版 schema 迁移（action / trigger / absent_seconds …）----
                    // 旧配置没有 absent / present 字段，需要按原 trigger 语义拆成两套。
                    var raw = Ser.Deserialize<Dictionary<string, object>>(txt);
                    if (raw != null && raw.ContainsKey("trigger") && !raw.ContainsKey("absent"))
                    {
                        return MigrateFromLegacy(raw);
                    }

                    var c = Ser.Deserialize<AppConfig>(txt);
                    if (c != null)
                    {
                        if (c.absent == null) c.absent = new TriggerProfile();
                        if (c.present == null) c.present = new TriggerProfile();
                        Normalize(c.absent);
                        Normalize(c.present);
                        return c;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("配置文件读取失败：" + ex.Message);
            }
            var def = new AppConfig();
            Normalize(def.absent);
            Normalize(def.present);
            return def;
        }

        private static AppConfig MigrateFromLegacy(Dictionary<string, object> raw)
        {
            var c = new AppConfig();
            var acts = new List<string>();
            object av;
            if (raw.TryGetValue("action", out av) && av is ArrayList)
            {
                var arr = (ArrayList)av;
                foreach (var a in arr) if (a is string) acts.Add((string)a);
            }
            if (acts.Count == 0) acts.Add("notify");

            string trig = "absent";
            if (raw.ContainsKey("trigger") && raw["trigger"] is string) trig = (string)raw["trigger"];
            double aSec = ToDouble(raw, "absent_seconds");
            double pSec = ToDouble(raw, "present_seconds");
            double cd = ToDouble(raw, "cooldown");
            string exec = "";
            if (raw.ContainsKey("exec") && raw["exec"] is string) exec = (string)raw["exec"];

            bool wantAbsent = trig == "absent" || trig == "both";
            bool wantPresent = trig == "present" || trig == "both";

            if (wantAbsent)
            {
                c.absent.actions = acts.ToArray();
                c.absent.buffer_seconds = aSec;
                c.absent.cooldown = cd;
                c.absent.exec = exec;
            }
            if (wantPresent)
            {
                c.present.actions = acts.ToArray();
                c.present.buffer_seconds = pSec;
                c.present.cooldown = cd;
                c.present.exec = exec;
            }
            object dr;
            c.dry_run = raw.TryGetValue("dry_run", out dr) && dr is bool && (bool)dr;
            Normalize(c.absent);
            Normalize(c.present);
            return c;
        }

        private static double ToDouble(Dictionary<string, object> raw, string key)
        {
            object v;
            if (raw.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v); }
                catch { }
            }
            return 0;
        }

        private static void Normalize(TriggerProfile p)
        {
            if (p.actions == null || p.actions.Length == 0) p.actions = new[] { "notify" };
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(Path, Ser.Serialize(this), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Warn("配置保存失败：" + ex.Message);
            }
        }
    }

    // =======================================================================
    //  日志
    //
    //  ★ 同时写文件和界面：
    //    界面日志在窗口最小化/被遮挡时看不到，
    //    文件日志能让你事后排查（比如「昨晚到底触发了几次」）。
    // =======================================================================
    internal static class Log
    {
        public enum Level { Info, Ok, Warn, Err }

        public static event Action<Level, string> Line;

        private static StreamWriter _file;
        private static readonly object _lock = new object();

        public static string FilePath
        {
            get
            {
                return System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "sentinel_log.txt");
            }
        }

        static Log()
        {
            try
            {
                // 只保留最近 200KB，避免长期运行把文件撑爆
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200 * 1024)
                {
                    string old = File.ReadAllText(FilePath, Encoding.UTF8);
                    File.WriteAllText(FilePath,
                        old.Substring(old.Length / 2), Encoding.UTF8);
                }
                _file = new StreamWriter(
                    new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                                  FileShare.ReadWrite),
                    Encoding.UTF8);
                _file.AutoFlush = true;
            }
            catch { }
        }

        public static void Info(string m) { Emit(Level.Info, m); }
        public static void Ok(string m) { Emit(Level.Ok, m); }
        public static void Warn(string m) { Emit(Level.Warn, m); }
        public static void Err(string m) { Emit(Level.Err, m); }

        internal static void Emit(Level lv, string m)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + lv + "] " + m;
            lock (_lock)
            {
                try { if (_file != null) { _file.WriteLine(line); } } catch { }
            }
            var h = Line;
            if (h == null) { Console.WriteLine(line); return; }
            // ★ 日志的界面刷新不能反过来打断业务逻辑：
            //   以前这里没兜底，控件一旦出问题，异常会顺着 Log.Info
            //   一路冒泡到按钮点击里，表现成「点一下连接就弹未处理异常」。
            try { h(lv, m); }
            catch (Exception ex)
            {
                Console.WriteLine(line + "  (日志显示失败: " + ex.Message + ")");
            }
        }
    }

    // =======================================================================
    //  动作执行
    // =======================================================================
    internal static class Actions
    {
        private static Dictionary<string, DateTime> _lastFired =
            new Dictionary<string, DateTime>();

        public static bool DryRun = false;
        public static bool BypassCooldown = false;

        /// <summary>
        /// 弹通知的回调，由主窗口注入。
        /// ★ 必须用 UI 线程（有消息循环）创建 ToastWindow：
        ///   手机事件来自后台读线程，若在那里直接 new Form().Show()，
        ///   该线程没有消息泵，窗口画不出来、鼠标移上去是等待光标、
        ///   5 秒自动关闭的定时器也不会触发 —— 表现就是
        ///   「通知一直在 loading、关不掉」。
        ///   回调内部应当用 Control.BeginInvoke 把 Show 派发到 UI 线程。
        /// </summary>
        public static Action<string, string> ShowToastHandler;

        private static bool Throttle(string key, double cooldown)
        {
            if (BypassCooldown) return true;
            DateTime last;
            if (_lastFired.TryGetValue(key, out last))
            {
                if ((DateTime.Now - last).TotalSeconds < cooldown) return false;
            }
            _lastFired[key] = DateTime.Now;
            return true;
        }

        public static void Run(string[] names, string title, string body, double cooldown, string dir = null)
        {
            foreach (var n in names)
            {
                // ★ 节流键带方向（absent/present）：离开与回来各自独立计冷却，
                //   否则「离开」刚弹过 notify，30s 内「回来」的 notify 会被同一
                //   个 "notify" 键拦掉，变成“回来不提示”。同一方向内的重复触发
                //   仍会被拦下（防抖动）。
                var key = (dir != null) ? (n + ":" + dir) : n;
                if (!Throttle(key, cooldown))
                {
                    Log.Info("  " + n + ": 节流跳过");
                    continue;
                }
                string result;
                bool ok;
                switch (n)
                {
                    case "lock": ok = Lock(); result = ok ? "已锁定工作站" : "锁屏失败"; break;
                    case "pause": ok = PauseMedia(); result = ok ? "已发送暂停指令" : "暂停失败"; break;
                    case "resume": ok = ResumeMedia(); result = ok ? "已发送继续播放指令" : "继续播放失败"; break;
                    case "notify": ok = Notify(title, body); result = ok ? "已弹出通知" : "通知失败"; break;
                    default:
                        if (n.StartsWith("exec:"))
                        {
                            ok = Exec(n.Substring(5), out result);
                        }
                        else
                        {
                            ok = false; result = "未知动作 " + n;
                        }
                        break;
                }
                Log.Emit(ok ? Log.Level.Ok : Log.Level.Err, "  " + n + ": " + result);
            }
        }

        // ---------------- 锁屏 ----------------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        private static bool Lock()
        {
            if (DryRun) return true;
            try { return LockWorkStation(); }
            catch { return false; }
        }

        // ---------------- 暂停播放 ----------------
        //
        // ★ 为什么不用全局媒体键（keybd_event）：
        //   实测无效。发完 VK_MEDIA_PLAY_PAUSE 后网易云仍在播放
        //   （进程 CPU 只从 0.516s 降到 0.359s —— 那 30% 是音频解码开销，
        //     剩下的是 UI 线程/歌词轮询这些后台活动，本来就在跑）。
        //   网易云没注册全局媒体快捷键，keybd_event 发过去它不理。
        //
        // ★ 现在用 WM_APPCOMMAND 定向发送：
        //   网易云有个窗口类叫 "MediaPlayer SMTC window - {GUID}"，
        //   说明它支持 SMTC 媒体会话。往这个窗口发
        //   WM_APPCOMMAND(APPCOMMAND_MEDIA_PLAY_PAUSE) 就能控制它，
        //   不需要焦点，也不需要它注册全局快捷键。
        //
        //   实测：这样发，网易云确实暂停了。
        private const ushort WmAppCommand = 0x0319;
        private const int AppCommandMediaPlayPause = 14;
        private const int AppCommandMediaPlay = 46;
        private const byte VkMediaPlayPause = 0xB3;
        private const uint KeyeventfKeyup = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder s, int n);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32W pe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32W pe);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32W
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        private const uint Th32csSnapProcess = 0x00000002;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>找出所有注册了 SMTC 媒体会话的窗口（按进程名匹配）。</summary>
        private static List<IntPtr> FindSmTcWindows(string processName)
        {
            var result = new List<IntPtr>();
            var pids = new HashSet<uint>();

            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
            try
            {
                var pe = new ProcessEntry32W();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32W));
                if (Process32FirstW(snap, ref pe))
                {
                    do
                    {
                        if (pe.szExeFile != null &&
                            pe.szExeFile.StartsWith(processName, StringComparison.OrdinalIgnoreCase))
                        {
                            pids.Add(pe.th32ProcessID);
                        }
                    } while (Process32NextW(snap, ref pe));
                }
            }
            finally { CloseHandle(snap); }

            if (pids.Count == 0) return result;

            EnumWindowsProc cb = (h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                var sb = new StringBuilder(256);
                GetClassNameW(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf("SMTC", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(h);
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            return result;
        }

        private static bool PauseMedia()
        {
            if (DryRun) return true;

            // 1) 首选：定向 WM_APPCOMMAND 给 SMTC 窗口
            var handles = FindSmTcWindows("cloudmusic");
            if (handles.Count == 0) handles = FindSmTcWindows("cloudmusicX");
            if (handles.Count == 0) handles = FindSmTcWindows("qqmusic");

            foreach (var h in handles)
            {
                long lParam = ((long)AppCommandMediaPlayPause) << 16;
                PostMessageW(h, WmAppCommand, IntPtr.Zero, new IntPtr(lParam));
                Log.Ok("已向 SMTC 会话窗口发送 APPCOMMAND_MEDIA_PLAY_PAUSE");
                return true;
            }

            // 2) 兜底：全局媒体键（对注册了全局快捷键的应用有效）
            try
            {
                keybd_event(VkMediaPlayPause, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                keybd_event(VkMediaPlayPause, 0, KeyeventfKeyup, UIntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 继续播放：对 SMTC 会话窗口发 APPCOMMAND_MEDIA_PLAY（46）。
        /// 与「暂停」用的 PLAY_PAUSE（14，是切换）不同，PLAY 是明确的「播放」，
        /// 只在已暂停时发才恢复，不会把正在放的歌按停。
        /// </summary>
        private static bool ResumeMedia()
        {
            if (DryRun) return true;

            var handles = FindSmTcWindows("cloudmusic");
            if (handles.Count == 0) handles = FindSmTcWindows("cloudmusicX");
            if (handles.Count == 0) handles = FindSmTcWindows("qqmusic");

            foreach (var h in handles)
            {
                long lParam = ((long)AppCommandMediaPlay) << 16;
                PostMessageW(h, WmAppCommand, IntPtr.Zero, new IntPtr(lParam));
                Log.Ok("已向 SMTC 会话窗口发送 APPCOMMAND_MEDIA_PLAY");
                return true;
            }

            // 兜底：全局媒体键（已暂停时按它就是继续播放）
            try
            {
                keybd_event(VkMediaPlayPause, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                keybd_event(VkMediaPlayPause, 0, KeyeventfKeyup, UIntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        // ---------------- 通知 ----------------
        private static bool Notify(string title, string body)
        {
            if (DryRun) return true;
            try
            {
                // ★ 优先走注入的回调（主窗口已用 BeginInvoke 把它派发到 UI 线程）。
                //   ShowToastHandler 为空时（如命令行 --selftest 模式没起主窗口）
                //   退回直接 Show —— 那种一次性场景无所谓消息循环。
                if (ShowToastHandler != null)
                    ShowToastHandler(title, body);
                else
                    ToastWindow.Show(title, body);
                return true;
            }
            catch (Exception ex)
            {
                Log.Err("通知失败：" + ex.Message);
                return false;
            }
        }

        // ---------------- 执行命令 ----------------
        private static bool Exec(string cmd, out string output)
        {
            output = "";
            if (DryRun) { output = "dry-run"; return true; }
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    if (output.Length > 200) output = output.Substring(0, 200);
                    return p.WaitForExit(20000);
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }
    }

    // =======================================================================
    //  自绘通知窗口（替代系统 Toast）
    //
    //  ★ 为什么不用系统 Toast：
    //      Windows 10 LTSC / IoT 版没有通知中心，
    //      Toast 通知要么不显示，要么标题被 AppId 顶掉，
    //      看起来"没有语义"。自己画最可控。
    // =======================================================================
    internal class ToastWindow : Form
    {
        // ★ 要写全名：System.Threading 和 System.Windows.Forms 都有 Timer，
        //   简写会触发 CS0104 歧义错误。
        private System.Windows.Forms.Timer _timer;

        public static void Show(string title, string body)
        {
            var f = new ToastWindow(title, body);
            f.Show();
        }

        private ToastWindow(string title, string body)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(360, 86);
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            Font = new Font(Theme.FontFamily, 9f);

            // ★ 不再用 Region = new Region(path) 做圆角裁剪：
            //   GDI+ 的 Region(GraphicsPath) 在部分机器/显卡上会抛
            //   "参数无效"(Parameter is not valid)，直接把整个程序弹成
            //   .NET 崩溃对话框。圆角改由 OnPaint 里的 FillPath 画出，
            //   四角露出 BackColor（同为深色）视觉上仍是圆角。

            var lblT = new Label
            {
                Text = title,
                ForeColor = Theme.OnSurface,
                Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
                AutoSize = false,
                BackColor = Color.Transparent,
                Bounds = new Rectangle(20, 14, Width - 40, 24)
            };
            var lblB = new Label
            {
                Text = body,
                ForeColor = Theme.OnSurfaceVariant,
                Font = new Font(Theme.FontFamily, 9f),
                AutoSize = false,
                BackColor = Color.Transparent,
                Bounds = new Rectangle(20, 40, Width - 40, 20)
            };
            var lblTime = new Label
            {
                Text = DateTime.Now.ToString("HH:mm:ss"),
                ForeColor = Theme.OnSurfaceMuted,
                Font = new Font(Theme.FontMono, 8f),
                AutoSize = false,
                BackColor = Color.Transparent,
                Bounds = new Rectangle(20, 60, Width - 40, 16)
            };
            Controls.Add(lblT);
            Controls.Add(lblB);
            Controls.Add(lblTime);

            var scr = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(scr.Right - Width - 16, scr.Bottom - Height - 16);

            _timer = new System.Windows.Forms.Timer { Interval = 5000 };
            _timer.Tick += (s2, e) => { _timer.Stop(); Close(); };
            _timer.Start();

            Click += (s2, e) => { _timer.Stop(); Close(); };
            foreach (Control c in Controls) c.Click += (s2, e) => { _timer.Stop(); Close(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 安全网：任何 GDI 调用在个别环境抛异常都不应拖垮整个程序
            //   （否则会弹 .NET 崩溃对话框“参数无效”）。
            try
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                using (var path = Gfx.RoundRect(
                    new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusCard))
                {
                    g.DrawPath(new Pen(Theme.OutlineStrong), path);
                }
                // 左侧强调条
                using (var br = new SolidBrush(Theme.Primary))
                using (var path = Gfx.RoundRect(
                    new Rectangle(0, 0, 4, Height - 1), 2, 2, 0, 0, 2))
                    g.FillPath(br, path);
            }
            catch (Exception ex)
            {
                try { e.Graphics.Clear(Theme.Surface); } catch { }
                Log.Err("通知绘制异常：" + ex.Message);
            }
        }
    }

    // =======================================================================
    //  手机连接
    // =======================================================================
    internal class PhoneEvent
    {
        public string @event;
        public bool? present;
        /// <summary>
        /// 手机端状态机的三态："present" / "uncertain" / "absent"。
        /// ★ 旧版手机不发这个字段，此时退回用 present 判断（两态）。
        /// </summary>
        public string state;
        public double? confidence;
        public double? ts;
        public string device;
        public string model;
        public string backend;
        public int? pid;
        public double? fps;
        public long? infer_ms;
        public Dictionary<string, object> config;
    }

    /// <summary>连接状态（UI 由它驱动，不再靠点击时手动改按钮）</summary>
    internal enum PhoneLink { Disconnected, Connecting, Connected }

    internal class SentinelClient
    {
        /// <summary>连接超时：手机端没起来时别让用户等太久</summary>
        private const int ConnectTimeoutMs = 6000;

        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _reader;
        private volatile bool _running;

        /// <summary>是否正在建立连接（挡重复点击 / 自动重连叠加）</summary>
        private volatile bool _connecting;

        /// <summary>用户点了「断开」时置真，让后台正在进行的连接自己放弃</summary>
        private volatile bool _abortConnect;

        /// <summary>所有对 _client / _stream 的读写都在这把锁里做</summary>
        private readonly object _sync = new object();

        public bool Connected
        {
            get { var c = _client; return c != null && c.Connected; }
        }

        public bool Connecting { get { return _connecting; } }

        public event Action<PhoneEvent> OnEvent;

        /// <summary>
        /// 连接状态变化。
        /// ★ 可能在后台线程触发，UI 侧必须自己派发到 UI 线程。
        /// </summary>
        public event Action<PhoneLink, string> StateChanged;

        private void Raise(PhoneLink st, string reason)
        {
            var h = StateChanged;
            if (h == null) return;
            try { h(st, reason); } catch { }
        }

        /// <summary>
        /// 后台建立连接，立即返回。
        ///
        /// ★ 为什么必须异步：旧的同步实现把 8 秒 WaitOne 放在 UI 线程上，
        ///   连不上时窗口直接「未响应」，用户会反复点按钮。
        ///   而 WinForms 在 UI 线程等待时仍会泵送部分消息，点击事件会重入，
        ///   两次 Connect 抢同一个 _client 字段 —— 第二次的 Disconnect()
        ///   把字段置 null，第一次的 EndConnect/Close 就抛
        ///   “未将对象引用设置到对象的实例”。这就是「第一次连不上，
        ///   之后点连接就报错」的根因。
        /// </summary>
        public void ConnectAsync(string host, int port)
        {
            lock (_sync)
            {
                if (_connecting) return;
                _connecting = true;
                _abortConnect = false;
            }
            Raise(PhoneLink.Connecting, "正在连接 " + host + ":" + port + " …");
            ThreadPool.QueueUserWorkItem(delegate { ConnectWorker(host, port); });
        }

        private void ConnectWorker(string host, int port)
        {
            // ★ 用局部变量建连，全部成功后才挂到字段上。
            //   这样即使中途被 Disconnect / 被下一次连接打断，
            //   字段也不会停在「半成品」状态。
            TcpClient cli = null;
            NetworkStream st = null;
            try
            {
                cli = new TcpClient();
                cli.NoDelay = true;
                var ar = cli.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                    throw new TimeoutException("连接超时（" + (ConnectTimeoutMs / 1000) + " 秒）");
                cli.EndConnect(ar);
                cli.ReceiveTimeout = 0;
                st = cli.GetStream();

                lock (_sync)
                {
                    if (_abortConnect)
                    {
                        _connecting = false;
                        _abortConnect = false;
                        try { st.Close(); } catch { }
                        try { cli.Close(); } catch { }
                        return;
                    }
                    CloseCurrentLocked();          // 顶掉可能残留的旧连接
                    _client = cli;
                    _stream = st;
                    _running = true;
                    // ★ 读线程拿局部引用，不读字段：否则旧线程会读到新流，
                    //   两个线程抢同一个 NetworkStream，JSON 行被切碎。
                    _reader = new Thread(new ThreadStart(delegate { ReadLoop(cli, st); }));
                    _reader.IsBackground = true;
                    _reader.Start();
                }

                _connecting = false;
                Raise(PhoneLink.Connected, "已连上手机");
            }
            catch (Exception ex)
            {
                try { if (st != null) st.Close(); } catch { }
                try { if (cli != null) cli.Close(); } catch { }
                _connecting = false;
                Raise(PhoneLink.Disconnected, "连接失败：" + ex.Message);
            }
        }

        public void Disconnect()
        {
            _abortConnect = true;
            lock (_sync) { CloseCurrentLocked(); }
            _connecting = false;
            Raise(PhoneLink.Disconnected, "已断开连接");
        }

        /// <summary>关闭当前连接。调用方必须持有 _sync。</summary>
        private void CloseCurrentLocked()
        {
            _running = false;
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        private void ReadLoop(TcpClient cli, NetworkStream st)
        {
            var ser = new JavaScriptSerializer();
            var buf = new byte[8192];
            var line = new StringBuilder();
            try
            {
                while (_running)
                {
                    int n = st.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    line.Append(Encoding.UTF8.GetString(buf, 0, n));
                    int idx;
                    while ((idx = line.ToString().IndexOf('\n')) >= 0)
                    {
                        string s = line.ToString(0, idx).Trim();
                        line.Remove(0, idx + 1);
                        if (s.Length == 0) continue;
                        try
                        {
                            var ev = ser.Deserialize<PhoneEvent>(s);
                            var h = OnEvent;
                            if (h != null) h(ev);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                // ★ 只有「这条连接自己还挂着」时才上报断开。
                //   否则旧线程退出会把刚建好的新连接误报成「已断开」——
                //   实测日志里就出现过「连上 2 秒后又断开」的假警报。
                bool stillCurrent;
                lock (_sync)
                {
                    stillCurrent = ReferenceEquals(_client, cli);
                    if (stillCurrent) CloseCurrentLocked();
                }
                if (stillCurrent) Raise(PhoneLink.Disconnected, "与手机的连接已断开");
                else Log.Info("旧的手机连接已关闭");
            }
        }

        /// <summary>
        /// 自动探测手机地址（需 adb 在线）。
        ///
        /// ★ 优先级：USB 共享网 &gt; WiFi &gt; 其它。
        ///   旧版直接跳过 wlan0，只认 USB 那个 192.168.42.x，
        ///   结果「同一局域网直连」永远探测不到 —— 这是 WiFi 方案卡住的真正原因。
        ///   USB 优先是因为它更稳（不受路由器 AP 隔离、WiFi 休眠影响），
        ///   插着线就自动走线，拔了线自动落回 WiFi。
        /// </summary>
        public static string DetectPhoneIp()
        {
            string usb = "", wifi = "", other = "";
            try
            {
                var psi = new ProcessStartInfo("adb", "shell ip -o -4 addr show");
                psi.RedirectStandardOutput = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var line in outp.Split('\n'))
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 4) continue;
                        string iface = parts[1];
                        if (iface == "lo") continue;
                        int i = Array.IndexOf(parts, "inet");
                        if (i <= 0 || i + 1 >= parts.Length) continue;
                        string ip = parts[i + 1].Split('/')[0];

                        bool isUsb = iface.StartsWith("rndis") || iface.StartsWith("usb")
                                  || ip.StartsWith("192.168.42.");
                        bool isWifi = iface.StartsWith("wlan") || iface.StartsWith("ap");
                        if (isUsb) { if (usb.Length == 0) usb = ip; }
                        else if (isWifi) { if (wifi.Length == 0) wifi = ip; }
                        else if (other.Length == 0) other = ip;
                    }
                }
            }
            catch { }
            if (usb.Length > 0) return usb;
            if (wifi.Length > 0) return wifi;
            return other;
        }
    }

    // =======================================================================
    //  主窗口
    // =======================================================================
    internal class MainForm : Form
    {
        // --- 状态 ---
        private SentinelClient _client = new SentinelClient();
        private AppConfig _cfg = AppConfig.Load();
        /// <summary>手机端状态机最后一次报的状态：present / uncertain / absent，空串表示还没收到</summary>
        private string _lastState = "";
        private bool _hasState = false;
        private DateTime _absentSince = DateTime.MinValue;
        private DateTime _presentSince = DateTime.MinValue;
        private bool _firedAbsent = false;
        private bool _firedPresent = false;
        private bool _lastWasAbsent = false;

        /// <summary>加载配置中（此时不该标记「未保存」）</summary>
        private bool _loading = false;

        // --- 控件 ---
        private Label _lblStatus, _lblPresence, _lblDetail, _lblPhone, _lblPhoneCfg;
        private Label _lblFps, _lblInfer, _lblConf;
        private Panel _dot;
        private Color _dotStateColor = Theme.OnSurfaceMuted;
        private Color _dotGlowColor = Theme.OnSurfaceMuted;
        private FlatButton _btnConn, _btnSave, _btnTest, _btnClear;
        /// <summary>手机地址输入框：留空 = 每次连接前自动探测；填了 = 手动指定</summary>
        private LineField _txtHost;
        /// <summary>本次尝试连接的地址</summary>
        private string _lastHost = "";
        /// <summary>上次连接失败的地址（下次换探测结果兜底，见 ToggleConnect）</summary>
        private string _failedHost = "";
        private FlatCheck _chkDry, _chkAutoConnect, _chkAutoStart;
        private SegmentedControl _segTrigger;
        private Panel _panelAbsent, _panelPresent;
        private ProfilePanel _pAbsent = new ProfilePanel();
        private ProfilePanel _pPresent = new ProfilePanel();
        private RichTextBox _txtLog;

        // ★ 系统托盘：关闭主面板时最小化到托盘而非退出
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private bool _reallyExit;

        // ★ 断线自动重连：手机端重启/掉 WiFi 后不用人工点按钮。
        //   用户主动点「断开」时置 _autoReconnectPaused，不会被偷偷连回去。
        private System.Windows.Forms.Timer _reconnectTimer;
        private bool _autoReconnectPaused = true;   // 启动时由自动连接接管
        private int _retryDelay = 5;                // 重试间隔（秒），失败后翻倍，上限 30

        /// <summary>一个触发方向（离开 / 回来）对应的 UI 控件集合。</summary>
        private class ProfilePanel
        {
            public FlatCheck chkNotify, chkLock, chkPause, chkResume;
            public LineField txtExec;
            public NumberField numBuffer, numCooldown;
        }

        public MainForm()
        {
            Text = Theme.ProductName + " · " + Theme.ProductTagline;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 720);
            MinimumSize = new Size(1000, 680);
            BackColor = Theme.Background;
            ForeColor = Theme.OnSurface;
            Font = new Font(Theme.FontFamily, 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = true;
            AutoScaleMode = AutoScaleMode.Dpi;

            // ★ 图标：编译时已作为 win32 图标 + 托管资源嵌进 exe。
            //   从托管资源取出来给窗口标题栏/任务栏用（比 ExtractAssociatedIcon 稳）。
            try
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("SentinelConsole.icon.ico"))
                {
                    if (s != null) Icon = new Icon(s);
                }
            }
            catch { }

            BuildUi();
            LoadConfigToUi();
            // ★ 每次启动都按当前配置同步一次注册表：
            //   默认 autostart=true，首次运行即写入启动项；
            //   用户若把 exe 挪了位置，这里也会把启动项指向新路径（自愈）。
            ApplyAutostart(_cfg.autostart);
            Log.Line += OnLogLine;

            // ★ 把「弹通知」派发到 UI 线程：ToastWindow 是 WinForms 窗口，
            //   必须在有消息循环的 UI 线程上创建。事件来自后台读线程，
            //   直接 Show 会卡成「loading」、且 5 秒后不自动关闭。
            Actions.ShowToastHandler = (ti, bo) => RunOnUi(() => ToastWindow.Show(ti, bo));

            // ★ 系统托盘：关闭主面板时最小化到托盘而非退出
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("显示主面板", null, (s2, e2) => ShowMain());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("退出", null, (s2, e2) => ExitApp());
            _trayIcon = new NotifyIcon
            {
                Icon = Icon,
                Text = Theme.ProductName,
                ContextMenuStrip = _trayMenu,
                Visible = false
            };
            _trayIcon.DoubleClick += (s2, e2) => ShowMain();

            _client.OnEvent += OnPhoneEvent;

            // ★ 连接状态由客户端主动上报，UI 只负责「照着画」。
            //   这样断开、被动掉线、连接失败三种情况都会同步按钮和状态栏，
            //   不再出现「日志说断开、界面还显示已连接」的割裂。
            _client.StateChanged += OnClientState;

            // 自动连一次
            var t = new System.Windows.Forms.Timer { Interval = 600 };
            t.Tick += (s, e) => { t.Stop(); if (_chkAutoConnect.Checked) ToggleConnect(); };
            t.Start();

            // 断线自动重连（5s → 10s → 20s → 30s 退避）
            _reconnectTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _reconnectTimer.Tick += (s, e) =>
            {
                _reconnectTimer.Stop();
                if (_autoReconnectPaused) return;
                if (!_chkAutoConnect.Checked) return;
                if (_client.Connected || _client.Connecting) return;
                ToggleConnect();
                // 下一次的间隔：连接失败会在 ApplyConnState 里重新排程
            };

            // ★ 开机自启静默启动：注册表启动项带了 --tray，
            //   直接收进托盘，不弹主面板（避免每次开机都闪一下窗口）。
            if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--tray"))
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                if (_trayIcon != null) _trayIcon.Visible = true;
            }
        }

        // ------------------------------------------------------------------ //
        //  UI 构建
        // ------------------------------------------------------------------ //
        // ================================================================== //
        //  布局骨架
        //
        //  设计意图（V2）：
        //    用户 90% 的时间只关心两件事 ——「现在有人吗」和「配置对吗」。
        //    所以：
        //      · Hero 状态区独立成块，用最大字号 + 光晕状态点，是视觉主角
        //      · 指标/设备信息用分隔线降级为次要层
        //      · 手机端配置条紧贴 Hero 下方（"延迟叠加"是关键信息）
        //      · 配置区分「触发」「动作」「连接」三块，说明文字独立成提示条
        //      · 日志独占底部，视觉上退到最次要层
        // ================================================================== //
        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(Theme.Space4),
                BackColor = Theme.Background
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // ---------------- 左列：Hero + 日志 ----------------
            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Theme.Background,
                Margin = new Padding(0, 0, Theme.Space2, 0)
            };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 268));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(left, 0, 0);

            // ---------------- 右列：触发（含动作） / 连接 ----------------
            var right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Theme.Background,
                Margin = new Padding(Theme.Space2, 0, 0, 0)
            };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 420));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(right, 1, 0);

            left.Controls.Add(BuildHeroPanel(), 0, 0);
            left.Controls.Add(BuildLogCard(), 0, 1);
            right.Controls.Add(BuildTriggerCard(), 0, 0);
            right.Controls.Add(BuildConnCard(), 0, 1);
        }

        // ------------------------------------------------------------------ //
        //  Hero 状态区
        // ------------------------------------------------------------------ //
        private Panel BuildHeroPanel()
        {
            var hero = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface,
                Margin = new Padding(0, 0, 0, Theme.Space2)
            };
            hero.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = Gfx.RoundRect(
                    new Rectangle(0, 0, hero.Width - 1, hero.Height - 1), Theme.RadiusCard))
                {
                    g.DrawPath(new Pen(Theme.Outline), path);
                }
            };

            // --- 状态点（带光晕）---
            _dot = new Panel
            {
                BackColor = Color.Transparent,
                Size = new Size(64, 64),
                Location = new Point(Theme.Space6, Theme.Space6)
            };
            _dot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Gfx.GlowDot(e.Graphics, new Point(32, 32), 15,
                    _dotStateColor, _dotGlowColor);
            };
            hero.Controls.Add(_dot);

            // --- 主状态文字 ---
            _lblPresence = new Label
            {
                Text = "—",
                ForeColor = Theme.OnSurfaceMuted,
                Font = new Font(Theme.FontFamily, 44f, FontStyle.Bold),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(104, Theme.Space6, 300, 62)
            };
            hero.Controls.Add(_lblPresence);

            // --- 状态副标题（置信度 + 类别）---
            _lblDetail = new Label
            {
                Text = "等待手机连接…",
                ForeColor = Theme.OnSurfaceVariant,
                Font = new Font(Theme.FontFamily, 9.5f),
                AutoSize = false,
                Bounds = new Rectangle(106, Theme.Space6 + 64, 400, 22)
            };
            hero.Controls.Add(_lblDetail);

            // --- 分隔线 ---
            var sep = new Panel
            {
                BackColor = Theme.Outline,
                Size = new Size(1, 1),
                Location = new Point(Theme.Space6, 160)
            };
            hero.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Outline))
                {
                    e.Graphics.DrawLine(pen, Theme.Space6, 160, hero.Width - Theme.Space6, 160);
                }
            };

            // --- 指标三宫格 ---
            _lblFps = MakeMetric(hero, "FPS", "—", Theme.Space6, 176);
            _lblInfer = MakeMetric(hero, "推理耗时", "—ms", Theme.Space6 + 150, 176);
            _lblConf = MakeMetric(hero, "置信度", "—", Theme.Space6 + 300, 176);

            // --- 设备信息 ---
            _lblPhone = new Label
            {
                Text = "手机：—",
                ForeColor = Theme.OnSurfaceMuted,
                Font = new Font(Theme.FontFamily, 8.5f),
                AutoSize = false,
                Bounds = new Rectangle(Theme.Space6, 232, 420, 18)
            };
            hero.Controls.Add(_lblPhone);

            // --- 手机端配置条（关键：延迟是叠加的）---
            _lblPhoneCfg = new Label
            {
                Text = "手机端配置：—",
                ForeColor = Theme.BrandBlue,
                Font = new Font(Theme.FontFamily, 8.5f, FontStyle.Bold),
                AutoSize = false,
                Bounds = new Rectangle(Theme.Space6, 248, 420, 18)
            };
            hero.Controls.Add(_lblPhoneCfg);

            return hero;
        }

        private Label MakeMetric(Control parent, string key, string val, int x, int y)
        {
            var k = new Label
            {
                Text = key,
                ForeColor = Theme.OnSurfaceMuted,
                Font = new Font(Theme.FontFamily, 8f),
                AutoSize = false,
                Bounds = new Rectangle(x, y, 140, 16)
            };
            parent.Controls.Add(k);

            var v = new Label
            {
                Text = val,
                ForeColor = Theme.OnSurface,
                Font = new Font(Theme.FontMono, 12f, FontStyle.Bold),
                AutoSize = false,
                Bounds = new Rectangle(x, y + 16, 140, 24)
            };
            parent.Controls.Add(v);
            return v;
        }

        // ------------------------------------------------------------------ //
        //  日志
        // ------------------------------------------------------------------ //
        private Control BuildLogCard()
        {
            var card = new Card { Title = "日志", Dock = DockStyle.Fill };
            // ★ 关键修复：日志控件必须放进 card.Content（Card 自带的 TableLayoutPanel），
            //   而不是 card.Controls。否则会和 Content 这个空的 Dock=Fill 兄弟控件抢地盘，
            //   导致 RichTextBox 被挤成零高度 —— 表现就是「日志区一片空白」。
            var c = card.Content;
            c.RowCount = 2;
            c.RowStyles.Clear();
            c.ColumnStyles.Clear();
            c.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            c.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 日志文本框
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));   // 底部按钮行

            _txtLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface,
                ForeColor = Theme.OnSurfaceVariant,
                BorderStyle = BorderStyle.None,
                Font = new Font(Theme.FontMono, 8.5f),
                ReadOnly = true,
                DetectUrls = false,
                Margin = new Padding(0)
            };
            c.Controls.Add(_txtLog, 0, 0);

            var btnRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Surface,
                Margin = new Padding(0)
            };
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            btnRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _btnTest = new FlatButton
            {
                Text = "测试触发",
                ButtonKind = FlatButton.Kind.Warning,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 6, 3)
            };
            _btnTest.Click += (s, e) => TestTrigger();
            btnRow.Controls.Add(_btnTest, 0, 0);

            _btnClear = new FlatButton
            {
                Text = "清空",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 0, 3)
            };
            _btnClear.Click += (s, e) => _txtLog.Clear();
            btnRow.Controls.Add(_btnClear, 0, 1);

            c.Controls.Add(btnRow, 0, 1);
            return card;
        }

        // ------------------------------------------------------------------ //
        //  触发设置
        // ------------------------------------------------------------------ //
        private Control BuildTriggerCard()
        {
            var card = new Card { Title = "触发", Dock = DockStyle.Fill };
            var c = card.Content;
            c.RowCount = 4;
            // ★ 必须先 Clear：Card 构造函数已经加了 1 个 RowStyles/ColumnStyles，
            //   不清的话 TableLayoutPanel 按索引取样式会全部错位一格
            c.RowStyles.Clear();
            c.ColumnStyles.Clear();
            c.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));   // 说明标签
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // 分段 tab
            c.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 当前 tab 的配置面板
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // 全局：演练模式

            c.Controls.Add(MakeLabel("分别设置「离开」和「回来」时各自执行的动作", 0, 0, 420, 18), 0, 0);

            // ★ 两个 tab：离开 / 回来，各自一套动作、缓冲、冷却，互不干扰、独立保存。
            _segTrigger = new SegmentedControl
            {
                Items = new[] { "离开", "回来" },
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            _segTrigger.SelectedChanged += (s, e) => SwitchTriggerTab();
            c.Controls.Add(_segTrigger, 0, 1);

            // --- 两个配置面板叠在同一格，靠 Visible 切换 ---
            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface,
                Margin = new Padding(0)
            };
            _panelAbsent = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0) };
            _panelPresent = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0), Visible = false };
            _panelAbsent.Controls.Add(BuildProfilePanel(_pAbsent,
                "实际延迟 = 手机端「离开确认时长」(8 秒) + 这里的缓冲。\r\n" +
                "手机端那 8 秒用于防短暂遮挡误判，不能省。"));
            _panelPresent.Controls.Add(BuildProfilePanel(_pPresent,
                "回来后延迟多久才执行（防误判）。默认 1 秒即可。"));
            body.Controls.Add(_panelAbsent);
            body.Controls.Add(_panelPresent);
            c.Controls.Add(body, 0, 2);

            _chkDry = new FlatCheck { Text = "演练模式（只记录不执行）", Dock = DockStyle.Fill };
            _chkDry.CheckedChanged += (s, e) =>
            {
                Actions.DryRun = _chkDry.Checked;
                Log.Info("演练模式：" + (_chkDry.Checked ? "开" : "关"));
                MarkConfigDirty();
            };
            c.Controls.Add(_chkDry, 0, 3);

            return card;
        }

        /// <summary>
        /// 构建一个「触发方向」的配置面板：4 个动作勾选 + 执行命令 + 缓冲 + 冷却。
        /// 控件引用写回 <paramref name="p"/>，供读取 / 回填使用。
        /// </summary>
        private Panel BuildProfilePanel(ProfilePanel p, string hintText)
        {
            var host = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
                BackColor = Theme.Surface, Margin = new Padding(0)
            };
            host.RowStyles.Clear();
            host.ColumnStyles.Clear();
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));   // 勾选框 2x2
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));   // 执行命令
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 缓冲
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 冷却
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 提示条

            // --- 勾选框：2 行 2 列 ---
            var checks = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
                BackColor = Theme.Surface, Margin = new Padding(0)
            };
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            checks.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            checks.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            p.chkNotify = new FlatCheck { Text = "弹出通知", Dock = DockStyle.Fill };
            p.chkLock = new FlatCheck { Text = "锁定工作站", Dock = DockStyle.Fill };
            p.chkPause = new FlatCheck { Text = "暂停播放", Dock = DockStyle.Fill };
            p.chkResume = new FlatCheck { Text = "继续播放", Dock = DockStyle.Fill };
            Action mark = () => MarkConfigDirty();
            p.chkNotify.CheckedChanged += (s, e) => mark();
            p.chkLock.CheckedChanged += (s, e) => mark();
            p.chkPause.CheckedChanged += (s, e) => mark();
            p.chkResume.CheckedChanged += (s, e) => mark();
            checks.Controls.Add(p.chkNotify, 0, 0);
            checks.Controls.Add(p.chkLock, 1, 0);
            checks.Controls.Add(p.chkPause, 0, 1);
            checks.Controls.Add(p.chkResume, 1, 1);
            host.Controls.Add(checks, 0, 0);

            // --- 执行命令：标签在上，输入框在下 ---
            var cmdHost = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = Theme.Surface, Margin = new Padding(0)
            };
            cmdHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            cmdHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            cmdHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cmdHost.Controls.Add(MakeLabel("执行命令", 0, 0, 200, 16), 0, 0);
            p.txtExec = new LineField
            {
                Placeholder = "taskkill /IM notepad.exe",
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            p.txtExec.TextChanged += (s, e) => mark();
            cmdHost.Controls.Add(p.txtExec, 0, 1);
            host.Controls.Add(cmdHost, 0, 1);

            host.Controls.Add(BuildNumberRow("缓冲", out p.numBuffer, 0, 600), 0, 2);
            host.Controls.Add(BuildNumberRow("冷却", out p.numCooldown, 0, 3600), 0, 3);
            // ★ 这两个数字框以前没订阅：改完既不标「未保存」，也不写进内存，
            //   于是「冷却改成 1 秒」看着改了、实际还在用旧值。这才是根因。
            p.numBuffer.ValueChanged += (s, e) => mark();
            p.numCooldown.ValueChanged += (s, e) => mark();

            var hint = new HintBar
            {
                Text2 = hintText,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 0)
            };
            host.Controls.Add(hint, 0, 4);

            return host;
        }

        private void SwitchTriggerTab()
        {
            bool absent = _segTrigger.SelectedIndex != 1;   // 0=离开, 1=回来
            _panelAbsent.Visible = absent;
            _panelPresent.Visible = !absent;
        }

        /// <summary>
        /// 构建一行「标签 + 数字输入 + 单位」的水平容器。
        /// 用嵌套 TableLayoutPanel 保证数字框宽度固定、标签不被挤掉。
        /// </summary>
        private Panel BuildNumberRow(string label, out NumberField field,
                                      int min, int max)
        {
            var host = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Theme.Surface,
                Margin = new Padding(0)
            };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            host.Controls.Add(MakeLabel(label, 0, 0, 88, 18), 0, 0);

            field = new NumberField
            {
                Minimum = min, Maximum = max, Value = 0,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 0)
            };
            host.Controls.Add(field, 1, 0);

            host.Controls.Add(MakeLabel("秒", 0, 0, 24, 18, Theme.OnSurfaceMuted), 2, 0);
            return host;
        }

        // ------------------------------------------------------------------ //
        //  连接
        // ------------------------------------------------------------------ //
        private Control BuildConnCard()
        {
            var card = new Card { Title = "连接", Dock = DockStyle.Fill };
            var c = card.Content;
            c.RowCount = 6;
            // ★ 必须先 Clear：Card 构造函数已经加了 1 个 RowStyles/ColumnStyles，
            //   不清的话 TableLayoutPanel 按索引取样式会全部错位一格
            //   （实测分段控件跑到 y=64、高度 20，提示条文字被裁）
            c.RowStyles.Clear();
            c.ColumnStyles.Clear();
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 手机地址
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));   // 按钮
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));   // 自动连接
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));   // 开机自启
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));   // 提示条
            c.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 状态

            // --- 手机地址（手动填；留空则每次连接前自动探测）---
            var hostRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Surface,
                Margin = new Padding(0)
            };
            hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            hostRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            hostRow.Controls.Add(MakeLabel("手机地址", 0, 0, 68, 18, Theme.OnSurfaceMuted), 0, 0);

            _txtHost = new LineField
            {
                Placeholder = "留空自动探测",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 1, 0, 1)
            };
            _txtHost.TextChanged += (s, e) => MarkConfigDirty();
            hostRow.Controls.Add(_txtHost, 1, 0);
            c.Controls.Add(hostRow, 0, 0);

            var btnRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Surface,
                Margin = new Padding(0)
            };
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            btnRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _btnConn = new FlatButton
            {
                Text = "连接手机",
                ButtonKind = FlatButton.Kind.Primary,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 6, 2)
            };
            _btnConn.Click += (s, e) => ToggleConnect();
            btnRow.Controls.Add(_btnConn, 0, 0);

            _btnSave = new FlatButton
            {
                Text = "保存配置",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2)
            };
            _btnSave.Click += (s, e) => { SaveConfig(); ShowSaved(); };
            btnRow.Controls.Add(_btnSave, 1, 0);
            c.Controls.Add(btnRow, 0, 1);

            _chkAutoConnect = new FlatCheck
            {
                Text = "自动连接（掉线自动重连）",
                Checked = true,
                Dock = DockStyle.Fill
            };
            c.Controls.Add(_chkAutoConnect, 0, 2);

            _chkAutoStart = new FlatCheck
            {
                Text = "开机自动启动",
                Dock = DockStyle.Fill
            };
            _chkAutoStart.CheckedChanged += (s, e) => MarkConfigDirty();
            c.Controls.Add(_chkAutoStart, 0, 3);

            var hint = new HintBar
            {
                Text2 = "留空 = 自动探测（USB 优先，其次 WiFi）；填了就按填的连。\r\n" +
                       "手机端主界面底部会显示本机 IP，点一下即可复制。",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 0)
            };
            c.Controls.Add(hint, 0, 4);

            _lblStatus = new Label
            {
                Text = "● 未连接",
                ForeColor = Theme.OnSurfaceMuted,
                Font = new Font(Theme.FontFamily, 9f, FontStyle.Bold),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            c.Controls.Add(_lblStatus, 0, 5);

            return card;
        }

        private Label MakeLabel(string text, int x, int y, int w, int h)
        {
            return MakeLabel(text, x, y, w, h, Theme.OnSurface);
        }

        private Label MakeLabel(string text, int x, int y, int w, int h, Color color)
        {
            var l = new Label
            {
                Text = text,
                ForeColor = color,
                Font = new Font(Theme.FontFamily, 9f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(x, y, w, h)
            };
            return l;
        }

        /// <summary>配置被改动 → 立即同步进内存 + 提示需要保存。</summary>
        private void MarkConfigDirty()
        {
            // ★ 加载配置时也会触发 ValueChanged / CheckedChanged，
            //   那时不该标脏，否则一打开就显示「保存配置 *」，误导用户。
            if (_loading || _btnSave == null) return;
            HotApply();
            _btnSave.Text = "保存配置 *";
            _btnSave.BackColor = Theme.SurfaceHover;
        }

        /// <summary>
        /// 把界面上的当前值立刻同步进内存配置（不写文件、不碰注册表）。
        ///
        /// ★ 为什么要有这一步：
        ///   冷却 / 缓冲这类参数，改完往往马上就想看效果。
        ///   原先只有点「保存配置」才走 CollectProfile，
        ///   于是「改数字」变成了「看起来改了、其实没改」——
        ///   连保存按钮都不冒 * 号（数字框当时压根没订阅 ValueChanged）。
        ///   现在：改动即时生效，保存只负责持久化。
        /// </summary>
        private void HotApply()
        {
            CollectProfile(_pAbsent, _cfg.absent);
            CollectProfile(_pPresent, _cfg.present);
            _cfg.dry_run = _chkDry.Checked;
            _cfg.autostart = _chkAutoStart.Checked;
        }

        /// <summary>保存成功 → 按钮短暂变成已保存态。</summary>
        private void ShowSaved()
        {
            _btnSave.Text = "✓ 已保存";
            _btnSave.BackColor = Theme.SurfaceVariant;
            var t = new System.Windows.Forms.Timer { Interval = 1600 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                if (IsDisposed) return;
                // ★ 这 1.6 秒里用户可能又改了配置（按钮已变成「保存配置 *」），
                //   别把那个星号擦掉。
                if (_btnSave.Text != null && _btnSave.Text.EndsWith("*")) return;
                _btnSave.Text = "保存配置";
                _btnSave.BackColor = Theme.SurfaceVariant;
            };
            t.Start();
        }

        // ------------------------------------------------------------------ //
        //  配置
        // ------------------------------------------------------------------ //
        private void LoadConfigToUi()
        {
            _loading = true;
            ApplyProfileToUi(_pAbsent, _cfg.absent);
            ApplyProfileToUi(_pPresent, _cfg.present);
            _txtHost.Text2 = _cfg.phone_host ?? "";
            _segTrigger.SelectedIndex = 0;   // 默认停在「离开」tab
            SwitchTriggerTab();
            _chkDry.Checked = _cfg.dry_run;
            Actions.DryRun = _cfg.dry_run;
            _chkAutoStart.Checked = _cfg.autostart;
            _loading = false;
        }

        private void ApplyProfileToUi(ProfilePanel p, TriggerProfile cfg)
        {
            var acts = cfg.actions ?? new[] { "notify" };
            p.chkNotify.Checked = acts.Contains("notify");
            p.chkLock.Checked = acts.Contains("lock");
            p.chkPause.Checked = acts.Contains("pause");
            p.chkResume.Checked = acts.Contains("resume");
            p.txtExec.Text = cfg.exec ?? "";
            p.numBuffer.Value = Clamp(p.numBuffer, cfg.buffer_seconds);
            p.numCooldown.Value = Clamp(p.numCooldown, cfg.cooldown);
        }

        private int Clamp(NumberField n, double v)
        {
            if (v < n.Minimum) return n.Minimum;
            if (v > n.Maximum) return n.Maximum;
            return (int)v;
        }

        private void SaveConfig()
        {
            CollectProfile(_pAbsent, _cfg.absent);
            CollectProfile(_pPresent, _cfg.present);
            // ★ 地址也存进去：填了就代表「以后都按这个连」，
            //   清空则回到自动探测。
            _cfg.phone_host = (_txtHost.Text2 ?? "").Trim();
            _cfg.dry_run = _chkDry.Checked;
            _cfg.autostart = _chkAutoStart.Checked;
            ApplyAutostart(_cfg.autostart);
            _cfg.Save();
        }

        // ------------------------------------------------------------------ //
        //  开机自启（写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run）
        // ------------------------------------------------------------------ //
        private static void ApplyAutostart(bool enable)
        {
            try
            {
                const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
                const string valueName = "斗在呢";
                using (var key = Registry.CurrentUser.OpenSubKey(keyName, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        // 路径加引号：防止路径含空格被当成参数截断
                        key.SetValue(valueName, "\"" + Application.ExecutablePath + "\" --tray",
                            RegistryValueKind.String);
                    }
                    else if (key.GetValue(valueName) != null)
                    {
                        key.DeleteValue(valueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("开机自启设置失败：" + ex.Message);
            }
        }

        private void CollectProfile(ProfilePanel p, TriggerProfile cfg)
        {
            var acts = new List<string>();
            if (p.chkNotify.Checked) acts.Add("notify");
            if (p.chkLock.Checked) acts.Add("lock");
            if (p.chkPause.Checked) acts.Add("pause");
            if (p.chkResume.Checked) acts.Add("resume");
            cfg.actions = acts.ToArray();
            cfg.exec = p.txtExec.Text.Trim();
            cfg.buffer_seconds = (double)p.numBuffer.Value;
            cfg.cooldown = (double)p.numCooldown.Value;
        }

        private void RunProfile(TriggerProfile p, string title, string body, string dir = null)
        {
            var names = new List<string>(p.actions ?? new[] { "notify" });
            if (names.Count == 0) names.Add("notify");
            if (!string.IsNullOrEmpty(p.exec)) names.Add("exec:" + p.exec);
            Actions.Run(names.ToArray(), title, body, p.cooldown, dir);
        }

        // ------------------------------------------------------------------ //
        //  连接
        // ------------------------------------------------------------------ //
        private void ToggleConnect()
        {
            // 正在连：忽略连击。以前这里没有闸门，用户等不及连点两下，
            // 第二次会把第一次正在用的连接对象关掉 → 空引用崩溃。
            if (_client.Connecting) return;

            if (_client.Connected)
            {
                // 用户主动断开：别再自动连回去
                _autoReconnectPaused = true;
                if (_reconnectTimer != null) _reconnectTimer.Stop();
                _client.Disconnect();       // UI 由 StateChanged 统一刷新，见 ApplyConnState
                return;
            }

            _autoReconnectPaused = false;

            // ★ 优先级：输入框 &gt; 配置文件 &gt; 自动探测。
            //   输入框是用户当场填的，最可信；留空才回落到配置 / 探测。
            string host = (_txtHost.Text2 ?? "").Trim();
            if (string.IsNullOrEmpty(host)) host = _cfg.phone_host;

            // ★ 兜底自愈：这个地址上次刚失败过，而 adb 现在能探到另一个地址，
            //   说明手机换网了（USB 共享 ↔ WiFi、换了路由器）。
            //   没有这一步的话，配置里残留一个旧 IP 就会永远连不上，
            //   用户还得自己想起来去清空——这不合理。
            if (!string.IsNullOrEmpty(host) && host == _failedHost)
            {
                string alt = SentinelClient.DetectPhoneIp();
                if (!string.IsNullOrEmpty(alt) && alt != host)
                {
                    Log.Info("上次 " + host + " 连不上，改试探测到的 " + alt);
                    host = alt;
                    SetHostBox(alt);
                }
            }

            if (string.IsNullOrEmpty(host))
            {
                // ★ 探测结果不写回 _cfg.phone_host。
                //   写回去等于把某一刻的地址钉死了：USB 换 WiFi、
                //   路由器重新分配 IP 之后，它会一直连旧地址。
                //   现在「输入框/配置里填了」= 手动指定；留空 = 每次重新探测。
                host = SentinelClient.DetectPhoneIp();
                if (string.IsNullOrEmpty(host))
                {
                    Log.Err("探测不到手机地址：请先用 USB 连上手机，"
                          + "或在上面填手机的 WiFi IP（手机端主界面底部可复制）");
                    // 没地址也算一次失败：继续按退避间隔重试，
                    // 免得用户插上 USB 之后还得手动点一下。
                    ScheduleReconnect();
                    return;
                }
                // 把探测结果回显到输入框，让用户知道「这次连的是哪个地址」。
                // 只显示、不写配置，也不算用户改动（不标 *）。
                SetHostBox(host);
            }

            Log.Info("正在连接 " + host + ":8765 …");
            _lastHost = host;
            _client.ConnectAsync(host, 8765);   // 立即返回，不卡界面
        }

        /// <summary>
        /// 回显探测到的地址，但不标记「未保存」——
        /// 自动探测不是用户编辑，不该让保存按钮冒 * 号。
        /// </summary>
        private void SetHostBox(string host)
        {
            bool prev = _loading;
            _loading = true;
            try { _txtHost.Text2 = host; }
            finally { _loading = prev; }
        }

        // ------------------------------------------------------------------ //
        //  连接状态 → 界面
        // ------------------------------------------------------------------ //
        private void OnClientState(PhoneLink st, string reason)
        {
            if (st == PhoneLink.Connected) Log.Ok("已连上手机");
            else if (st == PhoneLink.Disconnected)
            {
                if (reason != null && reason.StartsWith("连接失败")) Log.Err(reason);
                else Log.Warn(reason ?? "已断开");
            }
            RunOnUi(() => ApplyConnState(st, reason));
        }

        /// <summary>
        /// 一处改全部：按钮文字、状态栏、Hero 状态点、指标、存在的状态机。
        /// ★ 以前断开只是把连接对象关掉，按钮还写着「断开」、状态栏还亮着
        ///   「已连接」，就是漏了这里。
        /// </summary>
        private void ApplyConnState(PhoneLink st, string reason)
        {
            switch (st)
            {
                case PhoneLink.Connecting:
                    _btnConn.Text = "连接中…";
                    _btnConn.Enabled = false;
                    _lblStatus.Text = "● 连接中…";
                    _lblStatus.ForeColor = Theme.Uncertain;
                    // ★ 这里故意不动 _lblDetail：自动重连每 5~30 秒来一次，
                    //   每次都把副标题改成「正在连接…」，上一次的失败原因
                    //   就会被冲掉，用户永远看不到为什么连不上。
                    _dotStateColor = Theme.Uncertain;
                    _dotGlowColor = Theme.UncertainGlow;
                    _dot.Invalidate();
                    break;

                case PhoneLink.Connected:
                    _btnConn.Text = "断开";
                    _btnConn.Enabled = true;
                    _lblStatus.Text = "● 已连接";
                    _lblStatus.ForeColor = Theme.Present;
                    // 清掉上一轮的失败提示，等第一帧 presence 事件来接管
                    _lblDetail.Text = "已连接，等待手机画面…";
                    _lblDetail.ForeColor = Theme.OnSurfaceVariant;
                    _failedHost = "";
                    _retryDelay = 5;
                    // ★ 这次连上的地址如果和配置里存的不一样（说明是兜底换来的），
                    //   就悄悄记下来：下次启动直接连对，不用再白等一轮超时。
                    //   注意只写 _cfg 里已有的值，不会顺手把未保存的界面改动带上。
                    if (!string.IsNullOrEmpty(_lastHost) && _cfg.phone_host != _lastHost)
                    {
                        _cfg.phone_host = _lastHost;
                        _cfg.Save();
                    }
                    if (_reconnectTimer != null) _reconnectTimer.Stop();
                    break;

                case PhoneLink.Disconnected:
                    _btnConn.Text = "连接手机";
                    _btnConn.Enabled = true;
                    _lblStatus.Text = "● 未连接";
                    _lblStatus.ForeColor = Theme.OnSurfaceMuted;
                    ResetPresenceUi();
                    // 失败原因留在 Hero 副标题上：只写进日志的话，
                    // 用户看到的就是一个「什么都没发生」的界面。
                    if (reason != null && reason.StartsWith("连接失败"))
                    {
                        _lblDetail.Text = reason;
                        _lblDetail.ForeColor = Theme.Absent;   // 失败要一眼看见
                        _failedHost = _lastHost;               // 下次换探测结果兜底
                    }
                    ScheduleReconnect();
                    break;
            }
        }

        /// <summary>断开后 Hero 区必须回到「未知」，否则会一直挂着上一次的「有人」。</summary>
        private void ResetPresenceUi()
        {
            _lblPresence.Text = "—";
            _lblPresence.ForeColor = Theme.OnSurfaceMuted;
            _lblDetail.Text = "等待手机连接…";
            _lblDetail.ForeColor = Theme.OnSurfaceVariant;
            _dotStateColor = Theme.OnSurfaceMuted;
            _dotGlowColor = Theme.OnSurfaceMuted;
            _dot.Invalidate();
            _lblFps.Text = "—";
            _lblInfer.Text = "—ms";
            _lblConf.Text = "—";
            _lblPhone.Text = "手机：—";
            _lblPhoneCfg.Text = "手机端配置：—";

            // 状态机也一起清零，重连后第一帧不会拿旧值算「离开/回来」
            _lastState = "";
            _hasState = false;
            _firedAbsent = false;
            _firedPresent = false;
            _absentSince = DateTime.MinValue;
            _presentSince = DateTime.MinValue;
        }

        private void ScheduleReconnect()
        {
            if (_reconnectTimer == null) return;
            if (_autoReconnectPaused) return;
            if (!_chkAutoConnect.Checked) return;
            int sec = Math.Min(_retryDelay, 30);
            _reconnectTimer.Interval = sec * 1000;
            _reconnectTimer.Start();
            _retryDelay = Math.Min(_retryDelay * 2, 30);
        }

        // ------------------------------------------------------------------ //
        //  事件处理
        // ------------------------------------------------------------------ //
        private void OnPhoneEvent(PhoneEvent ev)
        {
            if (ev == null) return;
            try
            {
                if (ev.@event == "hello")
                {
                    Log.Ok("手机端已连接：" + ev.device + " (pid " + ev.pid + ")");
                    RunOnUi(() =>
                    {
                        _lblPhone.Text = "手机：" + ev.device + "  ·  模型 " + ev.model
                                       + "  ·  后端 " + ev.backend;
                    });
                }
                else if (ev.@event == "stats")
                {
                    RunOnUi(() =>
                    {
                        _lblFps.Text = string.Format("{0:0.0}", ev.fps ?? 0);
                        _lblInfer.Text = (ev.infer_ms ?? 0) + "ms";
                    });
                }
                else if (ev.@event == "config")
                {
                    var c = ev.config;
                    if (c != null && c.Count > 0)
                    {
                        object v;
                        var sb = new StringBuilder("手机端配置：");
                        if (c.TryGetValue("absent_seconds", out v)) sb.Append("离开 " + v + "s · ");
                        if (c.TryGetValue("threshold", out v)) sb.Append("阈值 " + v + " · ");
                        if (c.TryGetValue("pin_cpu", out v)) sb.Append("绑核 " + v + " · ");
                        if (c.TryGetValue("fps", out v)) sb.Append(v + "fps");
                        string txt = sb.ToString();
                        RunOnUi(() => _lblPhoneCfg.Text = txt);
                    }
                }
                else if (ev.@event == "presence")
                {
                    bool present = ev.present ?? false;

                    // ★ 手机端状态机是三态，必须按 state 走：
                    //     present    有人
                    //     uncertain  待确认（手机正在数那 8 秒，还没判定「真走了」）
                    //     absent     确认无人
                    //   旧版手机不发 state，用 present 退回两态。
                    string state = ev.state;
                    if (state != "present" && state != "uncertain" && state != "absent")
                        state = present ? "present" : "absent";

                    string conf = ev.confidence.HasValue
                        ? ev.confidence.Value.ToString("0.00") : "—";
                    string head, detail;
                    Color core, glow;
                    switch (state)
                    {
                        case "present":
                            head = "有人"; core = Theme.Present; glow = Theme.PresentGlow;
                            detail = "置信度 " + conf + "  ·  工位有人";
                            break;
                        case "uncertain":
                            head = "待确认"; core = Theme.Uncertain; glow = Theme.UncertainGlow;
                            detail = "置信度 " + conf + "  ·  画面变化，正在确认";
                            break;
                        default:
                            head = "无人"; core = Theme.Absent; glow = Theme.AbsentGlow;
                            detail = "置信度 " + conf;
                            break;
                    }

                    RunOnUi(() => _lblConf.Text = conf);
                    bool changed = _hasState && _lastState != state;
                    _lastState = state;
                    _hasState = true;
                    RunOnUi(() =>
                    {
                        _lblPresence.Text = head;
                        _lblPresence.ForeColor = core;
                        _dotStateColor = core;
                        _dotGlowColor = glow;
                        _dot.Invalidate();
                        // ★ 副标题同步：说明「凭什么判定有人」，
                        //   光一个大字看不出置信度，用户不敢信。
                        _lblDetail.Text = detail;
                    });
                    if (changed)
                        Log.Info(state == "present" ? "检测到人回来"
                               : state == "uncertain" ? "画面变化，等待手机端确认"
                               : "检测到人离开");

                    HandlePresence(present, state);
                }
            }
            catch (Exception ex)
            {
                Log.Err("事件处理异常：" + ex.Message);
            }
        }

        private void HandlePresence(bool present, string state)
        {
            // ★ 待确认：手机端还在数那 8 秒的确认窗口，这时候什么都不能做。
            //   计时器也一起清零——否则上一轮留下的「离开」计时会接着算下去，
            //   手机端辛苦守的 8 秒就被偷走了。
            if (state == "uncertain")
            {
                _absentSince = DateTime.MinValue;
                _presentSince = DateTime.MinValue;
                return;
            }

            if (!present)
            {
                // ---- 离开：按「离开」tab 的配置执行，带缓冲计时 ----
                if (_firedAbsent) return;
                if (_absentSince == DateTime.MinValue)
                {
                    _absentSince = DateTime.Now;
                    _presentSince = DateTime.MinValue;   // 重置回来方向的计时
                    return;
                }
                double elapsed = (DateTime.Now - _absentSince).TotalSeconds;
                if (elapsed < _cfg.absent.buffer_seconds) return;

                _absentSince = DateTime.MinValue;
                _firedAbsent = true;
                _firedPresent = false;
                Log.Warn(string.Format("人已离开 {0:0.0}s（超过阈值 {1:0.0}s），执行「离开」动作",
                                       elapsed, _cfg.absent.buffer_seconds));
                RunProfile(_cfg.absent, "人已离开工位", "工位哨兵检测到工位已无人", "absent");
            }
            else
            {
                // ---- 回来：按「回来」tab 的配置执行，带缓冲计时 ----
                if (_firedPresent) return;
                if (_presentSince == DateTime.MinValue)
                {
                    _presentSince = DateTime.Now;
                    _absentSince = DateTime.MinValue;    // 重置离开方向的计时
                    return;
                }
                double elapsed = (DateTime.Now - _presentSince).TotalSeconds;
                if (elapsed < _cfg.present.buffer_seconds) return;

                _presentSince = DateTime.MinValue;
                _firedPresent = true;
                _firedAbsent = false;
                Log.Warn(string.Format("人已回来 {0:0.0}s（超过阈值 {1:0.0}s），执行「回来」动作",
                                       elapsed, _cfg.present.buffer_seconds));
                RunProfile(_cfg.present, "欢迎回来", "工位哨兵检测到你已回到工位", "present");
            }
        }

        private void TestTrigger()
        {
            SaveConfig();
            bool testAbsent = _segTrigger.SelectedIndex != 1;   // 测试当前所在的 tab
            _firedAbsent = false;
            _firedPresent = false;
            _absentSince = DateTime.MinValue;
            _presentSince = DateTime.MinValue;
            var prof = testAbsent ? _cfg.absent : _cfg.present;
            string who = testAbsent ? "人已离开工位" : "欢迎回来";
            Log.Warn("手动触发一次「" + (testAbsent ? "人离开" : "人回来") + "」测试事件");
            Actions.BypassCooldown = true;
            try
            {
                RunProfile(prof, who, "工位哨兵手动测试", testAbsent ? "absent" : "present");
            }
            finally { Actions.BypassCooldown = false; }
        }

        // ------------------------------------------------------------------ //
        //  日志输出
        // ------------------------------------------------------------------ //
        private void OnLogLine(Log.Level lv, string msg)
        {
            RunOnUi(() =>
            {
                Color c;
                switch (lv)
                {
                    case Log.Level.Ok: c = Theme.Present; break;
                    case Log.Level.Warn: c = Theme.Uncertain; break;
                    case Log.Level.Err: c = Theme.Absent; break;
                    default: c = Color.FromArgb(212, 212, 212); break;
                }
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.SelectionLength = 0;
                _txtLog.SelectionColor = Color.FromArgb(77, 86, 101);
                _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] ");
                _txtLog.SelectionColor = c;
                _txtLog.AppendText(msg + Environment.NewLine);
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            });
        }

        private void RunOnUi(Action a)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // ★ 点 X / Alt+F4（用户关闭）：最小化到托盘，不断开手机连接。
            //   只有托盘菜单的「退出」会把 _reallyExit 置真，才真正关闭。
            //   Windows 关机 / 任务管理器结束 等 CloseReason 不在此列，照常退出。
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (_trayIcon != null) _trayIcon.Visible = true;
                Log.Info("[close] 收进托盘 reason=" + e.CloseReason + " tray=" + (_trayIcon != null));
                return;
            }
            Log.Info("[close] 真正退出 reason=" + e.CloseReason + " reallyExit=" + _reallyExit);
            if (_trayIcon != null) _trayIcon.Visible = false;
            if (_reconnectTimer != null) _reconnectTimer.Stop();
            // ★ 先退订再断开：否则断开事件会回调到正在销毁的窗口上刷控件
            _client.StateChanged -= OnClientState;
            _client.Disconnect();
            base.OnFormClosing(e);
        }

        private void ShowMain()
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            // ★ 开机自启是静默启动（ShowInTaskbar=false），点托盘恢复时要补回来
            ShowInTaskbar = true;
            Show();
            Activate();          // 从后台拉回前台，光 BringToFront 不够
            BringToFront();
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        /// <summary>
        /// 被第二个实例唤醒时调用（用户又双击了一次 exe / 快捷方式）。
        /// 调用方已经切到 UI 线程，这里只管把面板亮出来。
        /// </summary>
        public void ShowFromExternal()
        {
            ShowMain();
        }

        private void ExitApp()
        {
            _reallyExit = true;
            Close();
        }
    }

    // =======================================================================
    //  入口
    // =======================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ★ 最后一道兜底：任何漏网的异常都不该以 .NET 裸崩溃框收场。
            //   （原先是“未将对象引用设置到对象的实例”直接弹系统对话框，
            //    既看不出哪一行，也会让用户以为程序要崩了。）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log.Err("[界面异常] " + e.Exception);
                try
                {
                    ToastWindow.Show("操作没能完成", "错误已写进 sentinel_log.txt，程序可以继续使用");
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Err("[未处理异常] " + e.ExceptionObject);
            };

            // ---- 命令行模式 ----
            // --selftest            连一次手机、打印状态、退出
            // --action notify,lock  指定动作（逗号分隔）
            // --host 192.168.x.x    指定手机地址
            // --test-trigger        手动触发一次「人离开」并执行动作
            // --tray                开机自启静默启动：收进系统托盘、不弹主面板
            if (args.Length > 0 && args[0] == "--selftest")
            {
                SelfTest(args);
                return;
            }

            // ---- 单实例 ----
            // ★ 一个工位只该有一个哨兵。之前每点一次快捷方式就多开一扇窗，
            //   于是出现「好几个窗口各自连手机、各自执行锁屏」——危险且莫名其妙。
            //   现在第二个实例只做一件事：把已经在跑的那个叫到前面，然后自己退出。
            bool isFirst;
            _instanceMutex = new Mutex(true, MutexName, out isFirst);
            if (!isFirst)
            {
                // 开机自启带 --tray：用户没在等窗口，静默退出就好，别弹面板打扰他
                bool silent = args.Length > 0 && args[0] == "--tray";
                if (!silent) SignalRunningInstance();
                return;
            }

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);

            var form = new MainForm();
            StartShowListener(form);
            try
            {
                Application.Run(form);
            }
            finally
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                try { _instanceMutex.Dispose(); } catch { }
            }
        }

        // ------------------------------------------------------------------
        //  单实例
        // ------------------------------------------------------------------
        private const string MutexName = "DouZaiNe.SingleInstance.v1";
        private const string SignalName = "DouZaiNe.ShowWindow.v1";

        private static Mutex _instanceMutex;
        private static EventWaitHandle _showSignal;

        /// <summary>
        /// 通知已经在跑的那个实例「又有人点你了」。
        ///
        /// ★ 为什么用命名事件，而不是 FindWindow + SetForegroundWindow：
        ///   窗口可能正缩在托盘里（ShowInTaskbar=false、甚至 Hide() 过），
        ///   按标题根本找不到；而且后台进程抢前台本来就受系统限制。
        ///   事件不依赖窗口状态，可靠得多。
        /// </summary>
        private static void SignalRunningInstance()
        {
            // 对方可能刚启动：Mutex 建好了、事件还没建好，给几次机会
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(SignalName))
                    {
                        ev.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(120);
                }
                catch
                {
                    return;
                }
            }
        }

        /// <summary>在后台等「叫醒」信号，收到就把主面板弹到最前。</summary>
        private static void StartShowListener(MainForm form)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showSignal.WaitOne();
                        if (form.IsDisposed) return;
                        form.BeginInvoke((Action)(() => form.ShowFromExternal()));
                    }
                    catch (Exception ex)
                    {
                        Log.Err("[单实例] 监听线程退出: " + ex.Message);
                        return;
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "SingleInstance";
            t.Start();
        }

        private static void SelfTest(string[] args)
        {
            string host = "";
            var acts = new[] { "notify" };
            bool trigger = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--host" && i + 1 < args.Length) host = args[i + 1];
                else if (args[i] == "--action" && i + 1 < args.Length)
                    acts = args[i + 1].Split(',');
                else if (args[i] == "--test-trigger") trigger = true;
            }

            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== 工位哨兵 · 自检 ===");

            if (host.Length == 0)
            {
                host = SentinelClient.DetectPhoneIp();
                Console.WriteLine("探测到手机地址: " + (host.Length > 0 ? host : "(失败)"));
            }
            if (host.Length == 0) { Console.WriteLine("没有手机地址，退出"); return; }

            var client = new SentinelClient();
            bool gotEvent = false;
            client.OnEvent += ev =>
            {
                if (ev == null) return;
                gotEvent = true;
                Console.WriteLine("  收到事件: " + ev.@event +
                    (ev.present.HasValue ? (" present=" + ev.present.Value) : "") +
                    (ev.fps.HasValue ? (" fps=" + ev.fps.Value) : "") +
                    (ev.infer_ms.HasValue ? (" infer=" + ev.infer_ms.Value + "ms") : "") +
                    (ev.device != null ? (" device=" + ev.device) : ""));
            };

            // ★ 连接已改成异步（ConnectAsync），自检用轮询等结果
            client.ConnectAsync(host, 8765);
            for (int i = 0; i < 20 && !client.Connected; i++) Thread.Sleep(500);
            if (!client.Connected)
            {
                Console.WriteLine("连接失败：10 秒内没连上 " + host + ":8765");
                return;
            }
            Console.WriteLine("已连上手机，等待事件...");
            for (int i = 0; i < 12 && !gotEvent; i++) Thread.Sleep(500);
            if (!gotEvent) Console.WriteLine("  (12 秒内没收到事件)");

            if (trigger)
            {
                Console.WriteLine("手动触发动作: " + string.Join(",", acts));
                Actions.Run(acts, "人已离开工位", "工位哨兵自检触发", 0, "absent");
            }

            client.Disconnect();
            Console.WriteLine("=== 自检结束 ===");
        }
    }
}
