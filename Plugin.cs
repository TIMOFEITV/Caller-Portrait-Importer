using System;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace ScamWYF.TwitchControl
{
    [BepInPlugin("community.scamwithyourfriends.twitchcontrol", "SWYF Twitch Control", "0.1.7")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal ConfigEntry<bool> VictimsEnabled;
        internal ConfigEntry<int> CallerChance;
        internal ConfigEntry<int> ShiftMinutes;
        internal ConfigEntry<int> ViewerTimeout;
        internal ConfigEntry<bool> OverlayCodes;
        private ConfigEntry<string> channel;
        private ConfigEntry<int> overlayPort;
        private TwitchIrcClient twitch;
        private TwitchDeviceAuth auth;
        // Mod author registers ONE Twitch public Device Code application and inserts its Client ID.
        // No client secret is ever needed; streamers only click Sign in with Twitch.
        private const string TwitchPublicClientId = "siols7296wsrf28fl0vh8cmrxt268w";
        private TwitchVictimController victims;
        private ShiftTimerController timer;
        private OverlayServer overlay;
        private readonly ConcurrentQueue<TwitchChatMessage> received = new ConcurrentQueue<TwitchChatMessage>();
        private int queued;
        private bool menuOpen;
        private bool menuAvailable;
        private System.Type mainMenuType;
        private float nextMenuPoll;
        private float nextMenuTypeRetry;
        private bool menuWasDetected;
        private bool menuLookupFailed;
        private float nextVictimTick;
        private float nextAuthTick;
        private int selectedTab;
        private Rect settingsRect = new Rect(90, 90, 580, 560);
        private GUIStyle titleStyle;
        private GUIStyle statusStyle;
        private GUIStyle windowStyle;
        private Texture2D panelTexture;
        private string overlayPortText = "8765";
        private string callerChanceText;
        private string viewerTimeoutText;
        private string shiftMinutesText;
        private float nextOverlayUpdate;
        private bool initialized;

        private void Awake()
        {
            VictimsEnabled = Config.Bind("Twitch Victims", "Enabled", true, "Use chat-controlled human callers.");
            CallerChance = Config.Bind("Twitch Victims", "CallerChancePercent", 45, new ConfigDescription("Chance an eligible incoming caller is a Twitch viewer.", new AcceptableValueRange<int>(0, 100)));
            ViewerTimeout = Config.Bind("Twitch Victims", "InactivityHangupSeconds", 120, new ConfigDescription("Hang up when selected victim hasn't typed. 0 = disable timeout.", new AcceptableValueRange<int>(0, 600)));
            OverlayCodes = Config.Bind("Twitch Victims", "ShowGameCodesOnOverlay", true, "Show the current call’s fictional codes without marking them spoken. Codes become disclosed only when the selected viewer says them.");
            ShiftMinutes = Config.Bind("Shift Length", "Minutes", 0, new ConfigDescription("0 = game default. Applies at next eligible shift if a safe network setter exists.", new AcceptableValueRange<int>(0, 480)));
            channel = Config.Bind("Twitch", "Channel", "", "Twitch channel without # or URL.");
            overlayPort = Config.Bind("Overlay", "LoopbackPort", 8765, new ConfigDescription("Only listens on 127.0.0.1.", new AcceptableValueRange<int>(1025, 65535)));
            overlayPortText = overlayPort.Value.ToString();
            callerChanceText = CallerChance.Value.ToString();
            viewerTimeoutText = ViewerTimeout.Value.ToString();
            shiftMinutesText = ShiftMinutes.Value.ToString();
            try
            {
                victims = new TwitchVictimController(this);
                victims.Initialize();
                timer = new ShiftTimerController(this);
                twitch = new TwitchIrcClient();
                twitch.Message += Enqueue;
                auth = new TwitchDeviceAuth(TwitchPublicClientId);
                auth.Resume();
                // Cache Harmony type lookups outside OnGUI; they can scan loaded assemblies.
                mainMenuType = Reflect.Find("MainMenuSingleton");
                if (mainMenuType == null) LogWarning("MainMenuSingleton not available during Awake; will retry lookup every 5 seconds.");
                overlay = new OverlayServer();
                overlay.Start(overlayPort.Value);
                initialized = true;
                LogInfo("Twitch Control initialized. Open the TWITCH CONTROL button in the main menu.");
            }
            catch (Exception e) { LogError("Startup failed: " + e); }
        }

        private void Enqueue(TwitchChatMessage message)
        {
            if (System.Threading.Interlocked.Increment(ref queued) > 3000)
            {
                System.Threading.Interlocked.Decrement(ref queued);
                return;
            }
            received.Enqueue(message);
        }

        private void Update()
        {
            if (!initialized) return;
            float now = Time.realtimeSinceStartup;

            // Main menu detection is polled only twice a second, NOT from OnGUI.
            // In particular, an absent game manager is NOT evidence of a main menu.
            if (now >= nextMenuPoll)
            {
                nextMenuPoll = now + 0.5f;
                menuAvailable = CheckMainMenu();
                if (!menuAvailable) menuOpen = false;
                if (menuAvailable && !menuWasDetected)
                {
                    LogInfo("Main menu detected; TWITCH CONTROL button available.");
                }
                menuWasDetected = menuAvailable;
            }

            if (now >= nextAuthTick)
            {
                nextAuthTick = now + 1f;
                auth?.Tick();
            }
            // Browser launch is executed on Unity's main thread, never the OAuth task thread.
            if (auth != null)
            {
                string open = auth.TakeBrowserUrl();
                if (!String.IsNullOrEmpty(open)) Application.OpenURL(open);
                TwitchDeviceAuth.Session session = auth.TakeSession();
                if (session != null)
                {
                    if (String.IsNullOrWhiteSpace(channel.Value)) channel.Value = session.Login;
                    twitch.Connect(channel.Value, session.Login, session.Token);
                }
            }

            TwitchChatMessage message;
            for (int i = 0; i < 120 && received.TryDequeue(out message); ++i)
            {
                System.Threading.Interlocked.Decrement(ref queued);
                victims.OnChat(message);
            }
            // The game-side call state is reflection-based; avoid querying it 60+ times/second.
            if (now >= nextVictimTick)
            {
                nextVictimTick = now + 0.10f;
                try { victims.Tick(); }
                catch (Exception ex) { LogWarning("Victim tick failed: " + ex.GetBaseException().Message); }
            }
            try { timer.Tick(); }
            catch (Exception ex) { LogWarning("Shift timer tick failed: " + ex.GetBaseException().Message); }
            if (now >= nextOverlayUpdate)
            {
                nextOverlayUpdate = now + 0.5f;
                overlay.UpdateState(menuAvailable ? "{\"active\":false}" : victims.BuildOverlayJson());
            }
        }

        private bool CheckMainMenu()
        {
            // Game's own AnalyticsCollector.ResolveState() identifies the menu by
            // MainMenuSingleton.Instance != null, ahead of its gameplay checks.
            // IsInitialized and ScammerGameStarted are NOT reliable menu gates.
            if (mainMenuType == null)
            {
                // Resolve again very infrequently in case the menu type became
                // available after the BepInEx plugin's Awake().
                if (Time.realtimeSinceStartup < nextMenuTypeRetry) return false;
                nextMenuTypeRetry = Time.realtimeSinceStartup + 5f;
                mainMenuType = Reflect.Find("MainMenuSingleton");
                if (mainMenuType == null) return false;
            }
            try
            {
                object mainMenu = Reflect.Get(mainMenuType, "Instance");
                if (mainMenu == null) return false;
                // Respect Unity's destroyed-object semantics.
                if (mainMenu is UnityEngine.Object && !((UnityEngine.Object)mainMenu)) return false;
                return true;
            }
            catch (Exception e)
            {
                // One warning from failure is sufficient; do not log on each poll.
                if (!menuLookupFailed)
                {
                    menuLookupFailed = true;
                    LogWarning("Main-menu lookup failed: " + e.GetBaseException().Message);
                }
                return false;
            }
        }

        private void OnGUI()
        {
            if (!initialized || !menuAvailable) return;
            // Only the real main menu has this button. Never paint it in a call/shift.
            if (GUI.Button(new Rect(Screen.width - 197, 15, 182, 39), "TWITCH CONTROL")) menuOpen = !menuOpen;
            if (!menuOpen) return;
            if (Event.current != null && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                menuOpen = false;
                Event.current.Use();
                return;
            }
            settingsRect.width = Mathf.Min(580, Screen.width);
            settingsRect.height = Mathf.Min(560, Screen.height);
            settingsRect.x = Mathf.Clamp(settingsRect.x, 0, Mathf.Max(0, Screen.width - settingsRect.width));
            settingsRect.y = Mathf.Clamp(settingsRect.y, 0, Mathf.Max(0, Screen.height - settingsRect.height));
            try { settingsRect = GUI.Window(643091, settingsRect, DrawWindow, "SWYF TWITCH CONTROL", windowStyle ?? GUI.skin.window); }
            catch (Exception ex) { LogWarning("Settings drawing failed: " + ex.GetBaseException().Message); menuOpen = false; }
        }

        private void DrawWindow(int id)
        {
            EnsureStyles();
            if (GUI.Button(new Rect(settingsRect.width - 43, 5, 30, 24), "X")) menuOpen = false;
            GUILayout.Space(7);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Twitch Victims", GUILayout.Height(36))) selectedTab = 0;
            if (GUILayout.Button("Shift Length", GUILayout.Height(36))) selectedTab = 1;
            if (GUILayout.Button("OBS Overlay", GUILayout.Height(36))) selectedTab = 2;
            GUILayout.EndHorizontal();
            GUILayout.Space(12);
            if (selectedTab == 0) VictimTab();
            else if (selectedTab == 1) ShiftTab();
            else OverlayTab();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Configure here before starting a shift. Settings are saved automatically.", statusStyle);
            GUILayout.Space(5);
            if (GUILayout.Button("Close", GUILayout.Height(32))) menuOpen = false;
            GUI.DragWindow(new Rect(0, 0, settingsRect.width, 34));
        }

        private void VictimTab()
        {
            bool victimsChoice = GUILayout.Toggle(VictimsEnabled.Value, "Enable Twitch-controlled victims", GUILayout.Height(25));
            if (victimsChoice != VictimsEnabled.Value) VictimsEnabled.Value = victimsChoice;
            GUILayout.Label("Twitch channel (leave blank to use your account)", titleStyle);
            string channelInput = GUILayout.TextField(channel.Value, 32, GUILayout.Height(28));
            if (!String.Equals(channelInput, channel.Value, StringComparison.Ordinal)) channel.Value = channelInput;
            GUILayout.Space(6);
            if (auth != null && auth.Configured)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Sign in with Twitch", GUILayout.Height(35))) auth.BeginLogin();
                if (GUILayout.Button("Disconnect", GUILayout.Height(35))) twitch.Disconnect();
                if (GUILayout.Button("Sign out", GUILayout.Height(35))) { twitch.Disconnect(); auth.SignOut(); }
                GUILayout.EndHorizontal();
                GUILayout.Label("Twitch authorization: " + auth.Status, statusStyle);
                if (!String.IsNullOrEmpty(auth.ActivationCode))
                    GUILayout.Label("Approval code: " + auth.ActivationCode + " (browser opens automatically)", titleStyle);
                if (!String.IsNullOrEmpty(auth.StorageStatus)) GUILayout.Label(auth.StorageStatus, statusStyle);
            }
            else
            {
                GUILayout.Label("Twitch sign-in is not configured in this development build.\n" +
                    "The MOD AUTHOR must register a Twitch PUBLIC Device Code application\n" +
                    "and set TwitchPublicClientId in Plugin.cs before publishing.", statusStyle);
            }
            GUILayout.Label("Chat: " + twitch.Status, statusStyle);
            GUILayout.Space(5);
            GUILayout.Label("Twitch caller chance: " + CallerChance.Value + "%", titleStyle);
            DrawBoundedNumber(CallerChance, ref callerChanceText, 0, 100);
            GUILayout.Label("Selected viewer inactivity timeout: " + (ViewerTimeout.Value == 0 ? "Off" : ViewerTimeout.Value + " s"), titleStyle);
            DrawBoundedNumber(ViewerTimeout, ref viewerTimeoutText, 0, 600);
            GUILayout.Label("Waiting: " + victims.WaitingCount + " | Calls selected this session: " + victims.TotalSelected +
                " | Currently controlled: " + victims.ActiveViewer, statusStyle);
            if (GUILayout.Button("Reset !join pool", GUILayout.Height(28))) victims.ResetPool();
            GUILayout.Label("Commands: !join / !leave / selected viewer: !hangup", statusStyle);
            GUILayout.Label("After a call, the same viewer can type !join again. No restart needed.", statusStyle);
        }

        private void ShiftTab()
        {
            GUILayout.Label("Shift length (server / host only)", titleStyle);
            GUILayout.Label("0 = original game duration. Applies at the start of the next shift.", statusStyle);
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Game default")) SetShiftMinutes(0);
            if (GUILayout.Button("30 min")) SetShiftMinutes(30);
            if (GUILayout.Button("1 hour")) SetShiftMinutes(60);
            if (GUILayout.Button("2 hours")) SetShiftMinutes(120);
            GUILayout.EndHorizontal();
            GUILayout.Space(12);
            GUILayout.Label(ShiftMinutes.Value == 0 ? "Game default" : ShiftMinutes.Value + " minutes", titleStyle);
            DrawBoundedNumber(ShiftMinutes, ref shiftMinutesText, 0, 480);
            GUILayout.Space(12);
            GUILayout.Label("Timer status: " + timer.Status, statusStyle);
            GUILayout.Space(12);
            GUILayout.Label("This build will NOT modify unsynchronized private timer fields.\n" +
                "If the game does not expose safe network-backed setters, the timer option\n" +
                "will remain disabled and report why here.", statusStyle);
        }

        private void OverlayTab()
        {
            GUILayout.Label("OBS Browser Source", titleStyle);
            GUILayout.TextField(overlay.Url, GUILayout.Height(28));
            GUILayout.Label("Add this localhost URL in OBS as a Browser Source. Resize and reposition the source in OBS; the webpage adapts to its dimensions.", statusStyle);
            GUILayout.Space(10);
            bool showCodes = GUILayout.Toggle(OverlayCodes.Value, "Show fictional scam codes to Twitch chat", GUILayout.Height(28));
            if (showCodes != OverlayCodes.Value) OverlayCodes.Value = showCodes;
            GUILayout.Label("Codes are shown to viewers but are NOT marked as spoken until the selected chatter actually says them. Streamers should avoid watching the overlay while scamming.", statusStyle);
            GUILayout.Space(12);
            GUILayout.Label("Local port", titleStyle);
            overlayPortText = GUILayout.TextField(overlayPortText, 5, GUILayout.Width(120));
            if (GUILayout.Button("Restart local overlay server", GUILayout.Height(30)))
            {
                int port;
                if (int.TryParse(overlayPortText, out port) && port >= 1025 && port <= 65535)
                {
                    overlayPort.Value = port;
                    overlay.Start(port);
                }
            }
            GUILayout.Label(overlay.Status, statusStyle);
        }

        // Unity 6's game assemblies do not expose GUILayout.HorizontalSlider or the
        // GUILayout.PasswordField max-length overload. Use GUI controls that we know
        // are available from the existing game compile, with editable numeric input.
        private void DrawBoundedNumber(ConfigEntry<int> config, ref string input, int min, int max)
        {
            GUILayout.BeginHorizontal();
            input = GUILayout.TextField(input ?? config.Value.ToString(), 4, GUILayout.Width(92), GUILayout.Height(27));
            GUILayout.Label("Enter " + min + "–" + max, statusStyle);
            GUILayout.EndHorizontal();
            int value;
            if (int.TryParse(input, out value) && value >= min && value <= max && config.Value != value)
                config.Value = value;
        }

        private void SetShiftMinutes(int minutes)
        {
            ShiftMinutes.Value = minutes;
            shiftMinutesText = minutes.ToString();
        }

        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            panelTexture.SetPixel(0, 0, new Color(.09f, .075f, .14f, .97f));
            panelTexture.Apply();
            windowStyle = new GUIStyle(GUI.skin.window);
            windowStyle.normal.background = panelTexture;
            windowStyle.normal.textColor = Color.white;
            windowStyle.fontStyle = FontStyle.Bold;
            windowStyle.padding = new RectOffset(17, 17, 21, 13);
            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor = Color.white;
            titleStyle.wordWrap = true;
            statusStyle = new GUIStyle(GUI.skin.label);
            statusStyle.normal.textColor = new Color(.82f, .76f, .94f, 1f);
            statusStyle.wordWrap = true;
        }

        internal void LogInfo(string value) { Logger.LogInfo(value); }
        internal void LogWarning(string value) { Logger.LogWarning(value); }
        internal void LogError(string value) { Logger.LogError(value); }

        private void OnDestroy()
        {
            twitch?.Dispose();
            auth?.Dispose();
            overlay?.Dispose();
            victims?.Dispose();
            if (panelTexture != null) Destroy(panelTexture);
        }
    }
}
