using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using ZhaDai.Automation;

namespace ZhaDai.Runtime
{
    /// <summary>
    /// Owns the run: finds the game, reads the work order, and steps the executor once per frame.
    ///
    /// The front end drives it through files under <c>&lt;game&gt;\ZhaDai\</c>, so starting, stopping
    /// and watching a run needs no input hooks and no in-game UI:
    ///
    ///   run.cfg     written by the front end; <c>enabled=1</c> starts, <c>enabled=0</c> stops
    ///   *.zplan     the work order, written by the planner
    ///   status.txt  written by this class every second, read by the front end
    ///   runtime.log append-only, includes the reflection self check
    ///
    /// Every entry point swallows its own exceptions: a bug in the automation must never take the
    /// game down. The first failure disables the run and says so in the log.
    /// </summary>
    internal static class Host
    {
        private const int PollIntervalFrames = 30;

        private static readonly object Gate = new object();

        private static bool initialised;
        private static bool disabled;
        private static string directory;
        private static string logPath;
        private static string configPath;
        private static string statusPath;
        private static GameReflection reflection;
        private static TerrariaBridge bridge;
        private static BlastExecutor executor;
        private static ExecutionPlan plan;
        private static string planPath;
        private static long planStamp;
        private static DateTime configStamp;
        private static long lastPoll;
        private static long lastStatus;
        private static int loggedLines;

        private static bool enabled;

        /// <summary>Key name from run.cfg that starts and stops the run, e.g. F10.</summary>
        private static string hotkey = "F10";

        private static bool hostKeyDownLastFrame;

        /// <summary>True once run.cfg has supplied a hotkey; from then on the key owns the on/off switch.</summary>
        private static bool hotkeyConfigured;

        /// <summary>The config's own enabled= is applied once, so it cannot fight the hotkey every second.</summary>
        private static bool configEnabledApplied;
        private static bool allowExplosives;
        private static int maxDeaths = 30;
        private static double hostileDistance = 14d;

        /// <summary>Called once per frame, before the local player is updated.</summary>
        internal static void Frame()
        {
            lock (Gate)
            {
                try
                {
                    if (disabled)
                    {
                        return;
                    }

                    if (!initialised)
                    {
                        Initialise();
                    }

                    if (disabled || reflection == null || !reflection.IsUsable)
                    {
                        return;
                    }

                    bridge.Refresh();

                    // The hotkey is what actually starts a run when the player is driving: the config only
                    // loads the plan, and a key press in game flips the switch. Toggling with one key keeps
                    // the player in control: press again and the executor stops where it stands.
                    bool hotkeyDown = HotkeyDown();
                    if (hotkeyDown && !hostKeyDownLastFrame)
                    {
                        ToggleByHotkey();
                    }

                    hostKeyDownLastFrame = hotkeyDown;

                    if (bridge.Tick - lastPoll >= PollIntervalFrames)
                    {
                        lastPoll = bridge.Tick;
                        Poll();
                    }

                    if (enabled && executor != null)
                    {
                        executor.Step(bridge);
                    }

                    if (bridge.Tick - lastStatus >= 60)
                    {
                        lastStatus = bridge.Tick;
                        FlushStatus();
                        FlushLog();
                    }
                }
                catch (Exception exception)
                {
                    Fail("运行时异常，已停止接管（游戏不受影响）：" + exception);
                }
            }
        }

        /// <summary>Called once per frame, on the way out of the player update.</summary>
        internal static void EndFrame()
        {
            lock (Gate)
            {
                if (bridge != null)
                {
                    bridge.AdvanceFrame();
                }
            }
        }

        /// <summary>
        /// Reads whether the configured key is down right now. Terraria publishes the raw keyboard state in
        /// Main.keyState (Main.cs:973), and FocusHelper only fills it while the game window has focus, so an
        /// unfocused window simply never reports a press -- which is the behaviour we want.
        /// </summary>
        private static bool HotkeyDown()
        {
            if (reflection == null || reflection.MainKeyState == null)
            {
                return false;
            }

            object state = reflection.MainKeyState.GetValue(null);
            if (state == null || reflection.KeyboardStatePressedKeys == null)
            {
                return false;
            }

            object[] pressed = reflection.KeyboardStatePressedKeys.Invoke(state, null) as object[];
            if (pressed == null)
            {
                return false;
            }

            for (int i = 0; i < pressed.Length; i++)
            {
                if (pressed[i] != null &&
                    string.Equals(pressed[i].ToString(), hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>One key flips the run on and off, so the player keeps the switch in game.</summary>
        private static void ToggleByHotkey()
        {
            // Read the config once here so a key press works even if it lands before the first poll: the
            // plan has to be loaded before the executor can be built.
            configEnabledApplied = true;
            Poll();

            if (enabled)
            {
                enabled = false;
                Log("按下 " + hotkey + "：停止接管。");
                FlushStatus();
                FlushLog();
                return;
            }

            if (plan == null)
            {
                Log("按下 " + hotkey + "：还没有可用的施工文件，先在 run.cfg 里写 plan=...。");
                FlushLog();
                return;
            }

            enabled = true;
            Log("按下 " + hotkey + "：开始接管，共 " + plan.Charges.Count + " 发雷管。");
            FlushStatus();
            FlushLog();
        }

        private static void Initialise()
        {
            initialised = true;

            Assembly game = FindGameAssembly();
            if (game == null)
            {
                disabled = true;
                return;
            }

            string gameDirectory = Path.GetDirectoryName(game.Location);
            directory = Path.Combine(gameDirectory ?? ".", "ZhaDai");
            logPath = Path.Combine(directory, "runtime.log");
            configPath = Path.Combine(directory, "run.cfg");
            statusPath = Path.Combine(directory, "status.txt");

            reflection = GameReflection.Resolve(game);
            bridge = new TerrariaBridge(reflection);

            Log("ZhaDai.Runtime 已注入，游戏目录 " + gameDirectory);
            foreach (string problem in reflection.Problems)
            {
                Log("缺少必需成员：" + problem);
            }

            foreach (string note in reflection.Notes)
            {
                Log("可选成员缺失：" + note);
            }

            if (!reflection.IsUsable)
            {
                Log("反射自检未通过，接管功能保持关闭。游戏本身不受影响。");
                disabled = true;
                return;
            }

            Log("反射自检通过，等待 " + Path.Combine("ZhaDai", "run.cfg") + " 里的 enabled=1。");
            Poll();
        }

        /// <summary>
        /// Finds the game assembly. Injected code runs inside Terraria.exe, so the entry assembly is
        /// normally it; the scan is the fallback for hosts that load us differently.
        /// </summary>
        private static Assembly FindGameAssembly()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            if (entry != null && entry.GetType("Terraria.Main", false) != null)
            {
                return entry;
            }

            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                if (loaded[i].GetType("Terraria.Main", false) != null)
                {
                    return loaded[i];
                }
            }

            return null;
        }

        private static void Poll()
        {
            if (!File.Exists(configPath))
            {
                if (enabled)
                {
                    enabled = false;
                    Log("run.cfg 不在了，停止接管。");
                }

                return;
            }

            DateTime stamp = File.GetLastWriteTimeUtc(configPath);
            bool changed = stamp != configStamp;
            if (!changed)
            {
                return;
            }

            configStamp = stamp;
            string[] lines = File.ReadAllLines(configPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim().ToLowerInvariant();
                string value = line.Substring(split + 1).Trim();

                switch (key)
                {
                    case "enabled":
                        bool wanted = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        if (hotkeyConfigured)
                        {
                            // With a hotkey in play the file only gets to say it once; otherwise the
                            // per-second re-read would switch the run straight back off after a key press.
                            if (!configEnabledApplied)
                            {
                                enabled = wanted;
                                configEnabledApplied = true;
                            }
                        }
                        else
                        {
                            enabled = wanted;
                        }

                        break;
                    case "plan":
                        OpenPlan(value);
                        break;
                    case "allowexplosives":
                        allowExplosives = value == "1";
                        break;
                    case "hotkey":
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            hotkey = value.Trim().ToUpperInvariant();
                            hotkeyConfigured = true;
                        }

                        break;
                    case "maxdeaths":
                        maxDeaths = ParseInt(value, maxDeaths);
                        break;
                    case "hostiledistance":
                        hostileDistance = ParseDouble(value, hostileDistance);
                        break;
                    default:
                        break;
                }
            }

            if (enabled && plan == null)
            {
                Log("run.cfg 要求开始，但没有可用的施工文件，保持待命。");
                enabled = false;
            }

            if (enabled && executor == null)
            {
                ExecutorOptions options = new ExecutorOptions
                {
                    AllowExplosives = allowExplosives,
                    MaxDeaths = maxDeaths,
                    HostileSafeDistance = hostileDistance,
                };

                executor = new BlastExecutor(plan, options);
                Log("开始接管，施工文件 " + planPath + "，共 " + plan.Charges.Count + " 发雷管。");
            }
            else if (!enabled && executor != null)
            {
                executor.Stop();
                Log("收到停止指令。");
            }
        }

        private static void OpenPlan(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            string full = Path.IsPathRooted(value) ? value : Path.Combine(directory, value);
            if (!File.Exists(full))
            {
                Log("找不到施工文件：" + full);
                return;
            }

            long stamp = File.GetLastWriteTimeUtc(full).Ticks;
            if (planPath == full && planStamp == stamp)
            {
                return;
            }

            try
            {
                plan = ExecutionPlan.Load(full);
                planPath = full;
                planStamp = stamp;
                executor = null;
                Log("读取施工文件成功：" + full + "（" + plan.Charges.Count + " 发，引信 " +
                    plan.FuseTicks.ToString(CultureInfo.InvariantCulture) + " tick，撤离 " +
                    plan.RetreatTiles.ToString(CultureInfo.InvariantCulture) + " 格）");
            }
            catch (Exception exception)
            {
                Log("施工文件读不了：" + exception.Message);
                plan = null;
                executor = null;
            }
        }

        private static void FlushStatus()
        {
            try
            {
                Directory.CreateDirectory(directory);
                List<string> lines = new List<string>();
                lines.Add("enabled=" + (enabled ? "1" : "0"));
                lines.Add("reflection=" + (reflection != null && reflection.IsUsable ? "ok" : "failed"));
                lines.Add("world=" + (bridge.TileWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                                      bridge.TileHeight.ToString(CultureInfo.InvariantCulture)));
                lines.Add("plan=" + (planPath ?? ""));

                if (executor != null)
                {
                    ExecutorStatus status = executor.Status;
                    lines.Add("state=" + status.State);
                    lines.Add("charge=" + status.ChargeIndex.ToString(CultureInfo.InvariantCulture) + "/" +
                              status.ChargeCount.ToString(CultureInfo.InvariantCulture));
                    lines.Add("fired=" + status.Fired.ToString(CultureInfo.InvariantCulture));
                    lines.Add("skipped=" + status.Skipped.ToString(CultureInfo.InvariantCulture));
                    lines.Add("deaths=" + status.Deaths.ToString(CultureInfo.InvariantCulture));
                    lines.Add("blastHits=" + status.BlastHits.ToString(CultureInfo.InvariantCulture));
                    lines.Add("gravestones=" + status.GravestonesDug.ToString(CultureInfo.InvariantCulture));
                    lines.Add("lastSkip=" + status.LastSkip);
                    lines.Add("message=" + status.Message);
                    lines.Add("hazardWaits=" + status.HazardWaits.ToString(CultureInfo.InvariantCulture));
                    lines.Add("routes=" + status.RoutesPlanned.ToString(CultureInfo.InvariantCulture));
                    lines.Add("routeRestarts=" + status.RouteRestarts.ToString(CultureInfo.InvariantCulture));
                    lines.Add("routeDigs=" + status.RouteDigs.ToString(CultureInfo.InvariantCulture));
                    lines.Add("routeNote=" + status.LastRouteNote);
                    lines.Add("digsDone=" + status.DigsDone.ToString(CultureInfo.InvariantCulture) + "/" +
                              status.DigsSkipped.ToString(CultureInfo.InvariantCulture));
                    lines.Add("plugs=" + status.PlugsDone.ToString(CultureInfo.InvariantCulture) + "/" +
                              status.PlugsSkipped.ToString(CultureInfo.InvariantCulture) + "/" +
                              status.PlugsAlreadySolid.ToString(CultureInfo.InvariantCulture));
                    lines.Add("retries=" + status.Retries.ToString(CultureInfo.InvariantCulture));
                    lines.Add("ticks=" + status.Ticks.ToString(CultureInfo.InvariantCulture));

                    foreach (KeyValuePair<SkipReason, int> pair in executor.SkipCounts)
                    {
                        lines.Add("skip." + pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture));
                    }
                }

                lines.Add("life=" + bridge.PlayerLife.ToString(CultureInfo.InvariantCulture) + "/" +
                          bridge.PlayerLifeMax.ToString(CultureInfo.InvariantCulture));
                lines.Add("position=" + bridge.PlayerX.ToString("0.0", CultureInfo.InvariantCulture) + "," +
                          bridge.PlayerY.ToString("0.0", CultureInfo.InvariantCulture));
                lines.Add("timestamp=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

                File.WriteAllLines(statusPath, lines.ToArray());
            }
            catch (Exception exception)
            {
                Log("状态文件写不了：" + exception.Message);
            }
        }

        private static void FlushLog()
        {
            if (bridge == null)
            {
                return;
            }

            IList<string> lines = bridge.Messages;
            while (loggedLines < lines.Count)
            {
                Log(lines[loggedLines]);
                loggedLines++;
            }
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "  " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // A log that cannot be written must never break the game.
            }
        }

        private static void Fail(string message)
        {
            disabled = true;
            enabled = false;
            if (executor != null)
            {
                executor.Stop();
            }

            Log(message);
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static double ParseDouble(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }
}
