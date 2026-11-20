using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NeoModLoader.api;
using NeoModLoader.api.attributes;
using UnityEngine;
using UnityEngine.Networking;

// ============================================================
// WBTestMod v0.8.7 (hot-reload ready)
//  - 四级通道降级: L1 ntfy -> L2 Gist -> L3 本地日志 -> L4 内存环形缓冲
//  - 第一手错误管道: Unity 日志/异常 -> 环形缓冲 -> 最佳通道回传
//  - 闪退自保护: 会话标记 + 崩溃计数 + 安全模式 (>=3 崩溃, CMD resetcrash 解除)
//  - 0.8.8: CMD lsmods + CMD reload 云端热重载触发
// ============================================================

public class Main : MonoBehaviour, IMod, IReloadable {
    // ---- 常量 ----
    public const string VERSION = "0.8.9";
    const string NTFY_URL = "https://ntfy.sh/wbmod-luojike-x9k2-085rep";
    const string CMD_URL = "https://ntfy.sh/wbmod-luojike-x9k2-085cmd";
    const string CMD_POLL = CMD_URL + "/json?poll=1";
    const string GIST_ID = "2821e66c82dd824e1afe722c0bedb0cf";
    const string GIST_TOKEN = "GHP_TOKEN_PLACEHOLDER";
    const string GIST_API = "https://api.github.com/gists/" + GIST_ID;
    const string GIST_RAW_CMD = "https://gist.githubusercontent.com/uuuu33ii/" + GIST_ID + "/raw/cmd.json";

    // ---- 状态 ----
    public static Main I;
    public static string LogPath;        // L3 本地日志（崩溃后存活）
    public static string MarkPath;       // 会话标记
    public static string CrashPath;      // 崩溃计数
    static string CrashPkgPath;   // 崩溃包(现场快照, 闪退后存活)
    static string lastId = "";
    static bool safeMode = false;
    static int crashCount = 0;
    static readonly Queue<string> pending = new Queue<string>();   // 未送出消息队列
    static readonly RingBuffer ring = new RingBuffer(300);         // 第一手错误环形缓冲

    // ---- 0.8.7: NML 热重载接入 (IMod + IReloadable) ----
    ModDeclare _declare;

    public ModDeclare GetDeclaration() { return _declare; }
    public GameObject GetGameObject() { return gameObject; }
    public string GetUrl() { return "https://github.com/uuuu33ii/wbmod-pipeline"; }

    // NML 加载顺序: OnLoad -> Awake -> OnEnable -> Start
    public void OnLoad(ModDeclare pModDecl, GameObject pGameObject) {
        _declare = pModDecl;
        AppendFile("OnLoad fired, declare=" + (pModDecl != null ? pModDecl.Name + " " + pModDecl.Version : "null") + "\n");
        // 已有实例 = 热重载路径会走 Reload(), 这里不做重复初始化
    }

    // NML ModReloadService.ReloadMod() 回调: 现场重编译+方法热替换后调用
    public void Reload() {
        try {
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            // 单实例守卫放行重载实例
            if (I == null) I = this;
            // 心跳/哨兵/轮询协程挂在各自 GameObject 上, 方法体已被 Detour 热替换, 无需重启
            AppendFile("=== HOT-RELOAD OK, version=" + VERSION + " ===\n");
            Report("[HOT-RELOAD] reloaded OK, version=" + VERSION + " ring=" + ring.Count);
        } catch (Exception e) {
            AppendFile("HOT-RELOAD err: " + e.Message + "\n" + e.StackTrace + "\n");
        }
    }

    void Awake() {
        if (I != null) {   // 0.8.3 单实例守卫: 已有实例则自禁用
            enabled = false;
            try { GameObject.Destroy(gameObject); } catch {}
            return;
        }
        I = this;
        string pd = Application.persistentDataPath;
        LogPath = pd + "/wbmod_log.txt";
        MarkPath = pd + "/wbmod_session.mark";
        CrashPath = pd + "/wbmod_crash.count";
        CrashPkgPath = pd + "/wbmod_crashpack.txt";   // 0.8.7 修复: 0.8.6 起漏赋值, 崩溃包静默丢失

        // ---- 闪退自保护: 检查上次会话标记 ----
        string prevMark = ReadFile(MarkPath);
        if (prevMark == "RUNNING") {
            crashCount = ParseInt(ReadFile(CrashPath)) + 1;
            WriteFile(CrashPath, crashCount.ToString());
            if (crashCount >= 3) safeMode = true;
        }
        WriteFile(MarkPath, "RUNNING");

        string header = "=== WBTestMod v" + VERSION + " session " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + " crashCount=" + crashCount + " safeMode=" + safeMode + " ===\n";
        AppendFile(header);

        // 上次异常退出: 先补传崩溃前的本地日志（第一手证据，优先级最高）
        if (prevMark == "RUNNING") {
            pending.Enqueue("[CRASH-RECOVERY] 上次会话异常退出, 崩溃计数=" + crashCount);
            pending.Enqueue("[CRASH-LOG]\n" + TailFile(LogPath, 4000));
            string pkg = ReadFile(CrashPkgPath);
            if (!string.IsNullOrEmpty(pkg)) { pending.Enqueue("[CRASH-PACK]\n" + pkg.Substring(0, Math.Min(pkg.Length, 3000))); WriteFile(CrashPkgPath, ""); }
        }

        Application.logMessageReceived += OnLog;
        var go = new GameObject("WBTestModReporter");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<Reporter>();
        var sentryGo = new GameObject("WBTestModSentry");
        UnityEngine.Object.DontDestroyOnLoad(sentryGo);
        sentryGo.AddComponent<Sentry>();
        var pollerGo = new GameObject("WBTestModPoller");
        UnityEngine.Object.DontDestroyOnLoad(pollerGo);
        var poller = pollerGo.AddComponent<Poller>();
        if (safeMode) poller.interval = 30f;   // 安全模式: 低频轮询, 保住远程复位通道
        Report("Main.Awake fired, version=" + VERSION + ", safeMode=" + safeMode);
    }

    void OnApplicationQuit() {
        // 优雅退出: 清标记 + 清崩溃计数
        WriteFile(MarkPath, "EXIT");
        WriteFile(CrashPath, "0");
    }

    // ---- 第一手错误管道 ----
    static void OnLog(string cond, string stack, LogType type) {
        if (type == LogType.Exception || type == LogType.Assert || type == LogType.Error) {
            string entry = "[" + type + "] " + cond + "\n" + stack;
            ring.Add(entry);
            Report(entry.Length > 1500 ? entry.Substring(0, 1500) : entry);
            // 0.8.6: 异常现场立即落盘崩溃包(含最近行为环), 闪退后下次会话上报
            try { WriteFile(CrashPkgPath, DateTime.Now + "\n" + entry + "\n---- ring ----\n" + ring.Dump(3000)); } catch {}
        }
    }

    // ---- 统一发送入口: 走通道降级 ----
    public static void Report(string msg) {
        AppendFile(DateTime.Now.ToString("HH:mm:ss ") + msg + "\n");
        if (safeMode) return;   // 安全模式: 只落本地, 不发网络
        pending.Enqueue(msg);
        if (I != null) I.StartCoroutine(ChannelHub.Flush());
    }

    // ============================================================
    // 通道中枢: L1 ntfy -> L2 Gist -> L3 本地日志(已写) -> L4 环形缓冲
    // ============================================================
    public static class ChannelHub {
        // 连续失败阈值: 达到即降级; 成功即回升
        const int FAIL_THRESHOLD = 3;
        public static int ntfyFails = 0, gistFails = 0;
        public static bool flushing = false;

        public static int BestLevel() {
            if (ntfyFails < FAIL_THRESHOLD) return 1;
            if (gistFails < FAIL_THRESHOLD) return 2;
            return 3; // L3 本地日志在 Report 时已必然落盘
        }

        public static IEnumerator Flush() {
            if (flushing || pending.Count == 0) yield break;
            flushing = true;
            try {
                while (pending.Count > 0) {
                    string msg = pending.Peek();
                    int level = BestLevel();
                    bool ok = false;
                    if (level == 1) { yield return NtfyPost(msg, r => ok = r); }
                    else if (level == 2) { yield return GistWrite(msg, r => ok = r); }
                    if (ok) { pending.Dequeue(); }
                    else yield break;   // 失败留在队列, 降级由下次 Flush 重试
                }
            } finally { flushing = false; }
        }

        static IEnumerator NtfyPost(string msg, Action<bool> done) {
            using (var req = new UnityWebRequest(NTFY_URL, "POST")) {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(msg));
                req.SetRequestHeader("Content-Type", "text/plain");
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (!req.isNetworkError && !req.isHttpError) ntfyFails = 0;
                else { ntfyFails++; Main.AppendFile("SEND FAIL http=" + req.responseCode + " netErr=" + req.isNetworkError + " " + msg.Substring(0, System.Math.Min(60, msg.Length)) + "\n"); }
                done(!req.isNetworkError && !req.isHttpError);
            }
        }

        public static IEnumerator GistWrite(string msg, Action<bool> done) {
            string body = "{\"files\":{\"log_tail.txt\":{\"content\":\"" + JsonEsc(msg) + "\"}}}";
            using (var req = new UnityWebRequest(GIST_API, "PATCH")) {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + GIST_TOKEN);
                req.SetRequestHeader("User-Agent", "WBTestMod");
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 20;
                yield return req.SendWebRequest();
                if (!req.isNetworkError && !req.isHttpError) gistFails = 0;
                else gistFails++;
                done(!req.isNetworkError && !req.isHttpError);
            }
        }

        public static string JsonEsc(string s) {
            var sb = new StringBuilder();
            foreach (char c in s) {
                switch (c) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if ((int)c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // ---- 下行指令轮询 ----
    class Poller : MonoBehaviour {
        public float interval = 5f;
        static bool polling = false;
        void Start() { StartCoroutine(PollLoop()); }
        IEnumerator PollLoop() {
            yield return new WaitForSeconds(3f);
            while (true) {
                if (!polling) { polling = true; yield return PollOnce(); polling = false; }
                yield return new WaitForSeconds(interval);
            }
        }
        IEnumerator PollOnce() {
            // L1: ntfy 轮询
            string url = CMD_POLL + "&since=all";
            if (!string.IsNullOrEmpty(lastId)) url = CMD_POLL + "&after=" + lastId;
            string body = null;
            using (var req = new UnityWebRequest(url, "GET")) {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (!req.isNetworkError && !req.isHttpError) body = req.downloadHandler.text;
            }
            if (body != null) {
                ChannelHub.ntfyFails = 0;
                foreach (var kv in NtfyJson.Parse(body)) {
                    if (Main.seenIds.Contains(kv.Key)) continue;
                    Main.seenIds.Add(kv.Key);
                    lastId = kv.Key;
                    HandleCommand(kv.Value);
                }
            } else {
                // L2: Gist 取指令
                string cmd = null;
                using (var req = new UnityWebRequest(GIST_RAW_CMD + "?t=" + DateTime.Now.Ticks, "GET")) {
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.timeout = 15;
                    req.SetRequestHeader("User-Agent", "WBTestMod");
                    yield return req.SendWebRequest();
                    if (!req.isNetworkError && !req.isHttpError) cmd = req.downloadHandler.text;
                }
                if (cmd != null) HandleCommand(cmd);
                else ChannelHub.ntfyFails++;
            }
        }
        void HandleCommand(string msg) {
            if (string.IsNullOrEmpty(msg)) return;
            string m = msg.Trim();
            if (m.StartsWith("WBTestMod@")) return;   // 自己的回声
            Main.AppendFile("CMD: " + m + "\n");
            if (m.StartsWith("CMD ping")) { Main.Report("pong " + VERSION + " safeMode=" + safeMode + " ntfyFails=" + ChannelHub.ntfyFails + " gistFails=" + ChannelHub.gistFails); return; }
            if (m.StartsWith("CMD stat")) {
                Main.Report("stat: safeMode=" + safeMode + " crashCount=" + crashCount
                    + " pending=" + pending.Count + " ring=" + ring.Count
                    + " ntfyFails=" + ChannelHub.ntfyFails + " gistFails=" + ChannelHub.gistFails + " lastId=" + lastId);
                return;
            }
            if (m.StartsWith("CMD log")) { Main.Report("[LOGTAIL]\n" + TailFile(LogPath, 3000)); return; }
            if (m.StartsWith("CMD prevlog")) { Main.Report("[PREVLOG]\n" + TailFile(PersistentPath("wbmod08_mirror.txt"), 3000)); return; }
            if (m.StartsWith("CMD crashlog")) {
                string prev = ReadFile(CrashPath);
                Main.Report("crashCount=" + prev + " ring:\n" + ring.Dump(2000));
                return;
            }
            if (m.StartsWith("CMD crashpack")) { string pkg2 = ReadFile(CrashPkgPath); Main.Report(string.IsNullOrEmpty(pkg2) ? "crashpack: empty" : "[CRASH-PACK]\n" + pkg2.Substring(0, Math.Min(pkg2.Length, 3000))); return; }
            if (m.StartsWith("CMD resetcrash")) {
                WriteFile(CrashPath, "0");
                crashCount = 0;
                if (safeMode) { safeMode = false; Main.Report("SAFE MODE OFF by remote command"); }
                else Main.Report("crash counter reset");
                return;
            }
            if (m.StartsWith("CMD lsmods")) {   // 0.8.8: 云端清点 mod
                try {
                    var sb3 = new System.Text.StringBuilder();
                    sb3.Append("loaded mods: ").Append(NeoModLoader.WorldBoxMod.LoadedMods.Count).Append('\n');
                    foreach (var md in NeoModLoader.WorldBoxMod.LoadedMods) {
                        var d3 = md.GetDeclaration();
                        sb3.Append("- ").Append(d3.Name).Append(' ').Append(d3.Version)
                           .Append(" | folder=").Append(d3.FolderPath)
                           .Append(" | canReload=").Append(NeoModLoader.services.ModReloadService.CanReload(d3))
                           .Append('\n');
                    }
                    Main.Report("[LSMODS]\n" + sb3.ToString());
                } catch (Exception e3) { Main.Report("lsmods err: " + e3.Message); }
                return;
            }
            if (m.StartsWith("CMD reload")) {   // 0.8.8: 云端触发热重载(重编译源码+方法热替换)
                try {
                    bool ok = false; string detail = "";
                    foreach (var md in NeoModLoader.WorldBoxMod.LoadedMods) {
                        var d4 = md.GetDeclaration();
                        if (d4.Name != "WBTestMod") continue;
                        if (!NeoModLoader.services.ModReloadService.CanReload(d4)) { detail = "canReload=false (need IReloadable)"; break; }
                        ok = NeoModLoader.services.ModReloadService.ReloadMod(d4);
                        detail = ok ? "recompile+hotpatch+reload OK" : "ReloadMod returned false (see log)";
                        break;
                    }
                    Main.Report(ok ? "[RELOAD] " + detail : "[RELOAD] FAILED: " + (string.IsNullOrEmpty(detail) ? "WBTestMod not found in LoadedMods" : detail));
                } catch (Exception e4) { Main.Report("reload err: " + e4.Message + "\n" + e4.StackTrace); }
                return;
            }
            if (m.StartsWith("CMD update")) { StartCoroutine(CoUpdate()); return; }   // 0.8.9: 云端拉新源码落盘+自热重载
            // 0.8.3: 不复述陌生消息(回声风暴根治), 静默丢弃
        }
        IEnumerator CoUpdate() {
            string url = "https://gist.githubusercontent.com/uuuu33ii/" + GIST_ID + "/raw/code_cs.txt";
            string src = null;
            using (var req = new UnityEngine.Networking.UnityWebRequest(url)) {
                req.timeout = 20;
                req.SetRequestHeader("User-Agent", "WBTestMod");
                yield return req.SendWebRequest();
                if (!req.isNetworkError && !req.isHttpError) src = req.downloadHandler.text;
            }
            if (src == null || src.Length < 500 || !src.StartsWith("/*WBCODE*/")) {
                Main.Report("[UPDATE] FAILED: bad source" + (src == null ? " (fetch err)" : " (len=" + src.Length + ", marker miss)"));
                yield break;
            }
            string folder = null;
            foreach (var md in NeoModLoader.WorldBoxMod.LoadedMods) {
                var d = md.GetDeclaration();
                if (d.Name == "WBTestMod") { folder = d.FolderPath; break; }
            }
            if (string.IsNullOrEmpty(folder)) { Main.Report("[UPDATE] FAILED: WBTestMod folder not found"); yield break; }
            string target = System.IO.Path.Combine(folder, "Code/Main.cs");
            try {
                if (System.IO.File.Exists(target)) System.IO.File.Copy(target, target + ".bak", true);
                System.IO.File.WriteAllText(target, src);
            } catch (Exception e5) { Main.Report("[UPDATE] FAILED write: " + e5.Message); yield break; }
            Main.Report("[UPDATE] written " + src.Length + " bytes -> " + target + ", reloading...");
            yield return new WaitForSeconds(1f);
            foreach (var md in NeoModLoader.WorldBoxMod.LoadedMods) {
                var d6 = md.GetDeclaration();
                if (d6.Name != "WBTestMod") continue;
                bool ok6 = NeoModLoader.services.ModReloadService.ReloadMod(d6);
                Main.Report(ok6 ? "[RELOAD] recompile+hotpatch+reload OK (post-update)" : "[RELOAD] FAILED after update (see log)");
                break;
            }
        }
    }

    static HashSet<string> seenIds = new HashSet<string>();

    // ============================================================
    // 本地文件工具 (L3 兜底层)
    // ============================================================
    public static void AppendFile(string s) { try { System.IO.File.AppendAllText(LogPath, s); } catch { ring.AddRaw(s); } }
    public static string PersistentPath(string n) { return Application.persistentDataPath + "/" + n; }
    public static void AppendFile2(string name, string s) { try { System.IO.File.AppendAllText(PersistentPath(name), s); } catch { } }
    public static string ReadFile(string p) { try { return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : ""; } catch { return ""; } }
    public static void WriteFile(string p, string s) { try { System.IO.File.WriteAllText(p, s); } catch { } }
    public static string TailFile(string p, int max) {
        try {
            if (!System.IO.File.Exists(p)) return "(no file)";
            string all = System.IO.File.ReadAllText(p);
            return all.Length <= max ? all : all.Substring(all.Length - max);
        } catch (Exception e) { return "(tail err " + e.Message + ")"; }
    }
    static int ParseInt(string s) { int v = 0; int.TryParse(s == null ? "" : s.Trim(), out v); return v; }

    // ---- 最小 JSON 扫描 (ntfy /json 流) ----
    public static class NtfyJson {
        public static List<KeyValuePair<string, string>> Parse(string body) {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(body)) return list;
            foreach (string line in body.Split('\n')) {
                if (!line.Contains("\"event\":\"message\"")) continue;
                string id = Extract(line, "\"id\":\"", "\"");
                string msg = Extract(line, "\"message\":\"", "\"}");
                if (id != null && msg != null) list.Add(new KeyValuePair<string, string>(id, msg));
            }
            return list;
        }
        static string Extract(string s, string open, string close) {
            int a = s.IndexOf(open); if (a < 0) return null;
            a += open.Length;
            string rest = s.Substring(a);
            var sb = new StringBuilder();
            for (int i = 0; i < rest.Length; i++) {
                char c = rest[i];
                if (c == '\\' && i + 1 < rest.Length) {
                    char n = rest[i + 1];
                    if (n == '"') { sb.Append('"'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                }
                if (rest.Substring(i).StartsWith(close)) return sb.ToString();
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

// ---- L4 环形缓冲: 内存兜底 + 错误暂存 ----
public class RingBuffer {
    readonly string[] buf; int head = 0, count = 0;
    public RingBuffer(int cap) { buf = new string[cap]; }
    public int Count { get { return count; } }
    public void Add(string s) { AddRaw("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s); }
    public void AddRaw(string s) { buf[head] = s; head = (head + 1) % buf.Length; if (count < buf.Length) count++; }
    public string Dump(int max) {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) {
            string line = buf[(head - count + i + buf.Length) % buf.Length];
            if (sb.Length + (line == null ? 0 : line.Length) > max) break;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }
}


// ============ 哨兵: Player.log 增量追赶 + 本地队列 + 镜像存档 (0.8.1) ============
public class Sentry : MonoBehaviour {
    string logPath = "";
    long pos = 0;
    float timer = 2f;
    const string QueueName = "wbmod08_queue.txt";
    const string MirrorName = "wbmod08_mirror.txt";
    const string PosName = "wbmod08_logpos.txt";
    const long MirrorCap = 300000;

    void Start() {
        logPath = ProbeLog();
        Main.Report("sentry init, log=" + (logPath ?? "NONE(logcat-only)") + " pendingQ=" + QueueLines());
        Catchup();
    }
    string P(string n) { return Application.persistentDataPath + "/" + n; }
    void Update() {
        timer += Time.unscaledDeltaTime;
        if (timer < 15f) return;
        timer = 0f;
        Tick();
    }
    string ProbeLog() {
        // Android 上 consoleLogPath 可能为空; 探测候选文件, 找到就追, 找不到则待机(错误仍由 logMessageReceived 抓)
        string[] cands = new string[0];
        try { string c = Application.consoleLogPath; if (!string.IsNullOrEmpty(c)) cands = new string[] { c }; } catch {}
        try {
            string pd = Application.persistentDataPath;
            cands = (cands.Length == 0) ? new string[] { pd + "/Player.log", pd + "/output_log.txt", pd + "/console.log" }
                                        : new string[] { cands[0], pd + "/Player.log" };
        } catch {}
        foreach (string c in cands) { try { if (System.IO.File.Exists(c)) return c; } catch {} }
        return null;
    }
    public int QueueLines() {
        try { var t = System.IO.File.ReadAllText(P(QueueName)); if (t.Length == 0) return 0; return t.Split('\n').Length; }
        catch { return 0; }
    }
    void Catchup() {
        // 上一会话遗留的队列内容先补传
        try {
            string q = System.IO.File.ReadAllText(P(QueueName));
            if (!string.IsNullOrEmpty(q)) Main.Report("[PREV-QUEUE]\n" + q);
        } catch {}
        ClearQueue();
    }
    void ClearQueue() { try { System.IO.File.WriteAllText(P(QueueName), ""); } catch {} }
    void SavePos() { try { System.IO.File.WriteAllText(P(PosName), pos.ToString()); } catch {} }
    void Tick() {
        if (string.IsNullOrEmpty(logPath)) return;
        try {
            var fi = new System.IO.FileInfo(logPath);
            if (!fi.Exists) return;
            if (fi.Length < pos) { pos = 0; Main.AppendFile("SENTRY: Player.log rotated, restart tail\n"); }
            if (fi.Length == pos) { Pump(); return; }
            string add = "";
            using (var fs = fi.Open(System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite)) {
                fs.Seek(pos, System.IO.SeekOrigin.Begin);
                using (var sr = new System.IO.StreamReader(fs)) { add = sr.ReadToEnd(); }
            }
            pos = fi.Length;
            SavePos();
            if (add.Length == 0) { Pump(); return; }
            // 镜像存档（全量历史, 限 300KB）
            try {
                string mir = System.IO.File.Exists(P(MirrorName)) ? System.IO.File.ReadAllText(P(MirrorName)) : "";
                mir += add;
                if (mir.Length > MirrorCap) mir = mir.Substring((int)(mir.Length - MirrorCap));
                System.IO.File.WriteAllText(P(MirrorName), mir);
            } catch {}
            // 错误行入本地队列
            int qn = 0;
            foreach (string raw in add.Split('\n')) {
                if (Sentry.IsError(raw)) { Main.AppendFile2(QueueName, raw + "\n"); qn++; }
            }
            if (qn > 0) Main.Report("sentry queued " + qn + " error line(s)");
            Pump();
        } catch (Exception e) { Main.AppendFile("SENTRY tick err: " + e.Message + "\n"); }
    }
    // 队列泵: 通道健康(L1/L2)才外送, 否则留在本地
    void Pump() {
        if (Main.ChannelHub.BestLevel() > 2) return;
        try {
            string q = System.IO.File.ReadAllText(P(QueueName));
            if (string.IsNullOrEmpty(q)) return;
            string payload = q;
            if (payload.Length > 2500) payload = payload.Substring(payload.Length - 2500);
            Main.Report("[SENTRY]\n" + payload);
            ClearQueue();
        } catch {}
    }
    public static bool IsError(string line) {
        if (string.IsNullOrEmpty(line)) return false;
        return line.Contains("Exception") || line.Contains("Error") || line.Contains("ERROR")
            || line.Contains("FATAL") || line.Contains("SIGSEGV") || line.Contains("Assert")
            || line.Contains("Crash") || line.Contains("crash");
    }
}

public class Reporter : MonoBehaviour {
    void Start() { StartCoroutine(Ping()); }
    IEnumerator Ping() {
        yield return new WaitForSeconds(3f);
        Main.Report("heartbeat 3s OK");
        for (int i = 1; i <= 20; i++) {
            yield return new WaitForSeconds(120f);
            if (Main.ChannelHub.BestLevel() <= 2) Main.Report("heartbeat " + (i * 133) + "s alive, level=L" + Main.ChannelHub.BestLevel());
        }
    }
}
