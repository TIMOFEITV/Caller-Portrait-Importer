using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace ScamWYF.TwitchControl
{
    internal sealed class Viewer
    {
        public string Id;
        public string Login;
        public string Name;
        public DateTime LastActiveUtc;
    }

    internal sealed class CodeEntry
    {
        public string Label;
        public string Value;
        public string Tag;
        public bool IsRevealed;
        public bool IsClaimed;
    }

    internal sealed class ControlledCall
    {
        public object Call;
        public Viewer Viewer;
        public string LastStreamerSpeech = "";
        public string Phase = "Incoming call";
        public readonly List<CodeEntry> Codes = new List<CodeEntry>();
        public readonly Queue<string> Messages = new Queue<string>();
        public float NextSpeakTime;
        public float LastViewerMessageTime;
        public float LastCodesRefresh;
        public bool RefreshedScamConfiguration;
        public bool Answered;
    }

    internal sealed class TwitchVictimController
    {
        private static TwitchVictimController current;
        private readonly Plugin host;
        private readonly Dictionary<string, Viewer> waiting = new Dictionary<string, Viewer>(StringComparer.Ordinal);
        private readonly System.Random random = new System.Random();
        private readonly Harmony harmony;
        private Type victimType;
        private Type networkCallType;
        private Type identitiesType;
        private ControlledCall active;
        private int totalSelected;
        private bool patchReady;

        public int WaitingCount { get { return waiting.Count; } }
        public int TotalSelected { get { return totalSelected; } }
        public string ActiveViewer { get { return active == null ? "None" : active.Viewer.Name; } }
        public bool PatchReady { get { return patchReady; } }

        public TwitchVictimController(Plugin owner)
        {
            host = owner;
            harmony = new Harmony("community.scamwithyourfriends.twitchcontrol.victims");
            current = this;
        }

        public void Initialize()
        {
            victimType = Reflect.Find("VictimCall");
            networkCallType = Reflect.Find("NetworkedCall");
            identitiesType = Reflect.Find("CallerIdentityLibrary");
            if (victimType == null || networkCallType == null || identitiesType == null)
            {
                host.LogError("Victim patch unavailable: game classes not found (game update?).");
                return;
            }
            try
            {
                Patch(victimType, "ServerPrepareCall", nameof(BeforePrepare), true);
                Patch(victimType, "ServerTryBuildDirectResponseAsync", nameof(PreventAi), true);
                Patch(networkCallType, "ServerAppendTranscript", nameof(AfterTranscript), false);
                patchReady = true;
                host.LogInfo("Victim patches installed: scheduling, AI suppression, and transcript capture.");
            }
            catch (Exception ex)
            {
                harmony.UnpatchSelf();
                patchReady = false;
                host.LogError("Victim patch setup failed; disabled entire victim subsystem: " + ex);
            }
        }

        private void Patch(Type type, string methodName, string patchName, bool prefix)
        {
            MethodInfo target = AccessTools.Method(type, methodName);
            MethodInfo replacement = AccessTools.Method(typeof(TwitchVictimController), patchName);
            if (target == null || replacement == null) throw new MissingMethodException(type.FullName, target == null ? methodName : patchName);
            harmony.Patch(target, prefix ? new HarmonyMethod(replacement) : null,
                prefix ? null : new HarmonyMethod(replacement));
        }

        private static void BeforePrepare(object __instance)
        {
            try { current?.Prepare(__instance); }
            catch (Exception ex) { current?.host.LogError("Twitch caller selection failed, game call left unchanged: " + ex); }
        }

        // The game's ServerQueuePrompt already adds the player's STT transcript before this
        // async response method runs. Replace ONLY the AI response task for our own call.
        private static bool PreventAi(object __instance, ref Task<string> __result)
        {
            if (current == null || !current.IsControlled(__instance)) return true;
            __result = Task.FromResult<string>(null);
            return false;
        }

        private static void AfterTranscript(object __instance, bool isPlayer, string text)
        {
            if (!isPlayer || current == null || current.active == null ||
                !ReferenceEquals(__instance, current.active.Call)) return;
            current.active.LastStreamerSpeech = Clip(text, 180);
        }

        private bool IsControlled(object call) { return patchReady && active != null && ReferenceEquals(active.Call, call); }

        private void Prepare(object call)
        {
            if (!patchReady || !host.VictimsEnabled.Value || active != null || host.CallerChance.Value <= 0) return;
            if (Reflect.Bool(Reflect.Get(call, "IsOutgoing"))) return;
            Type game = Reflect.Find("ScammerGameManager");
            object manager = Reflect.Get(game, "Instance");
            if (manager != null && Reflect.Bool(Reflect.Get(manager, "DayZeroTutorialActive"))) return;
            if (random.NextDouble() * 100.0 >= host.CallerChance.Value) return;
            Viewer selected = PickViewer();
            if (selected == null) return;

            object baseIdentity = Reflect.Get(call, "scheduledCallerIdentity");
            if (baseIdentity == null)
            {
                MethodInfo select = AccessTools.Method(victimType, "ServerSelectCallerIdentity");
                if (select == null) return;
                baseIdentity = select.Invoke(call, null);
            }
            if (baseIdentity == null) return;
            object temporary = Reflect.Clone(baseIdentity);
            if (!Reflect.Set(temporary, "displayName", Clip(selected.Name, 28))) return;
            // Preserve ID, portrait, voice, initial game trust and all scam definitions.
            Reflect.Call(call, "ServerSetScheduledIdentity", temporary);
            active = new ControlledCall
            {
                Call = call,
                Viewer = selected,
                LastViewerMessageTime = Time.realtimeSinceStartup
            };
            waiting.Remove(selected.Id);
            totalSelected++;
            host.LogInfo("Twitch victim scheduled: " + selected.Name + " (temporary identity, no files created)");
        }

        private Viewer PickViewer()
        {
            // Once a call ends, that viewer may explicitly !join again.
            // The waiting dictionary keeps at most one entry per Twitch user ID.
            List<Viewer> eligible = waiting.Values.ToList();
            if (eligible.Count == 0) return null;
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
            List<Viewer> recent = eligible.Where(v => v.LastActiveUtc >= cutoff).ToList();
            List<Viewer> pool = recent.Count > 0 ? recent : eligible;
            return pool[random.Next(pool.Count)];
        }

        public void OnChat(TwitchChatMessage item)
        {
            if (!patchReady || item == null || string.IsNullOrWhiteSpace(item.UserId)) return;
            string text = (item.Text ?? "").Trim();
            if (text.Equals("!join", StringComparison.OrdinalIgnoreCase))
            {
                // Do not let a viewer queue themselves while they are still the active victim.
                // After the call ends, a new !join places them back in the pool.
                if (active != null && active.Viewer.Id == item.UserId) return;
                Viewer viewer;
                if (!waiting.TryGetValue(item.UserId, out viewer))
                {
                    if (waiting.Count >= 10000) return; // memory cap; no caller instances created
                    viewer = new Viewer { Id = item.UserId, Login = item.Login };
                    waiting[item.UserId] = viewer;
                }
                viewer.Name = Clip(item.DisplayName, 28);
                viewer.LastActiveUtc = DateTime.UtcNow;
                return;
            }
            if (text.Equals("!leave", StringComparison.OrdinalIgnoreCase))
            {
                waiting.Remove(item.UserId);
                return;
            }
            Viewer currentViewer;
            if (waiting.TryGetValue(item.UserId, out currentViewer)) currentViewer.LastActiveUtc = DateTime.UtcNow;
            if (active == null || active.Viewer.Id != item.UserId) return;
            if (text.Equals("!hangup", StringComparison.OrdinalIgnoreCase))
            {
                HangUp();
                return;
            }
            if (text.StartsWith("!", StringComparison.Ordinal)) return;
            if (text.Length == 0 || !CallTalking()) return;
            if (active.Messages.Count >= 4) return; // anti-spam; game TTS must finish
            string cleaned = Clip(Sanitize(text), 190);
            if (cleaned.Length > 0)
            {
                active.Messages.Enqueue(cleaned);
                active.LastViewerMessageTime = Time.realtimeSinceStartup;
            }
        }

        private static string Sanitize(string text)
        {
            var b = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsControl(c) || c == '<' || c == '>') continue;
                b.Append(c);
            }
            return b.ToString().Trim();
        }
        private static string Clip(string value, int limit)
        {
            if (value == null) return "";
            return value.Length > limit ? value.Substring(0, limit) : value;
        }

        private bool CallTalking()
        {
            return active != null && String.Equals(Reflect.String(Reflect.Get(active.Call, "callState")), "Talking", StringComparison.Ordinal);
        }

        public void Tick()
        {
            if (active == null) return;
            UnityEngine.Object unityObject = active.Call as UnityEngine.Object;
            if ((active.Call is UnityEngine.Object && !((UnityEngine.Object)active.Call)) ||
                String.Equals(Reflect.String(Reflect.Get(active.Call, "callState")), "Ended", StringComparison.Ordinal))
            {
                active = null;
                return;
            }
            if (!CallTalking()) { active.Phase = "Incoming call — wait for the streamer to answer"; return; }
            float now = Time.realtimeSinceStartup;
            if (!active.Answered) { active.Answered = true; active.LastViewerMessageTime = now; }
            if (!active.RefreshedScamConfiguration)
            {
                active.RefreshedScamConfiguration = true;
                try { Reflect.Call(active.Call, "ServerRefreshUnlockedScams"); }
                catch (Exception ex) { host.LogWarning("Cannot refresh call scam configuration: " + ex.GetBaseException().Message); }
            }
            active.Phase = "LIVE — type in chat to speak as the victim";
            if (host.ViewerTimeout.Value > 0 && active.Messages.Count == 0 &&
                now >= active.NextSpeakTime &&
                now - active.LastViewerMessageTime > host.ViewerTimeout.Value)
            {
                host.LogInfo("Viewer response timeout; hanging up Twitch call.");
                HangUp();
                return;
            }
            if (now >= active.NextSpeakTime && active.Messages.Count > 0)
            {
                string spoken = active.Messages.Dequeue();
                try
                {
                    // Native NetworkedCall.SendTts() sends voice to the owning player over Mirror
                    // and appends the normal caller transcript. No separate client mod.
                    // The game uses sentinel-annotated <transcript> markup for clickable
                    // phone codes. Plain digit text is not enough. Only tag numbers that
                    // match the current game's authoritative, non-claimed sentinel state.
                    RefreshCodes();
                    List<string> disclosedTags;
                    string gameSpeech = PrepareViewerSpeech(spoken, out disclosedTags);
                    Reflect.Call(active.Call, "SendTts", gameSpeech);
                    // Human-controlled calls do not enter CallerDialogueSession.Commit().
                    // Record earned disclosure ONLY after the exact code was spoken, so
                    // the game's normal one-use verification can accept the value.
                    foreach (string tag in disclosedTags) RecordHumanDisclosure(tag);
                    if (disclosedTags.Count > 0) RefreshCodes();
                    // Preserve existing call-side response behavior.
                    Reflect.Call(active.Call, "ServerAfterResponseSent", gameSpeech);
                    active.NextSpeakTime = now + Mathf.Clamp(1.75f + spoken.Length / 15f, 2.5f, 16f);
                }
                catch (Exception ex) { host.LogError("Chat-to-victim TTS failed: " + ex.GetBaseException().Message); }
            }
            if (host.OverlayCodes.Value && now - active.LastCodesRefresh >= 2.0f)
            {
                active.LastCodesRefresh = now;
                try { RefreshCodes(); }
                catch (Exception e) { host.LogWarning("Code overlay refresh failed: " + e.GetBaseException().Message); }
            }
        }

        // Copies game values into the OBS view without prematurely *revealing* them.
        // The game's EnsureSpokenValue generates a shared choice-group value but also
        // switches IsRevealed on, so we restore that flag immediately for preview-only
        // values. A real viewer disclosure uses SentinelProcessor.ReplaceForTts later.
        private void RefreshCodes()
        {
            if (active == null || !CallTalking()) return;
            object sentinel = Reflect.Call(active.Call, "GetSentinelCallState");
            if (sentinel == null) return;

            IEnumerable tags = Reflect.Get(active.Call, "serverUnlockedSentinels") as IEnumerable;
            if (tags == null) return;
            var latest = new List<CodeEntry>();
            foreach (object item in tags)
            {
                string tag = Reflect.String(item);
                if (String.IsNullOrWhiteSpace(tag)) continue;
                object info = Reflect.Call(sentinel, "GetInfo", tag);
                string raw = Reflect.String(Reflect.Get(info, "RawValue"));
                if (String.IsNullOrWhiteSpace(raw))
                {
                    // Generate through this call's own SentinelCallState, preserving
                    // linked choice values. Never independently generate an overlay code.
                    bool wasRevealed = Reflect.Bool(Reflect.Get(info, "IsRevealed"));
                    Reflect.Call(sentinel, "EnsureSpokenValue", tag, "en");
                    info = Reflect.Call(sentinel, "GetInfo", tag);
                    if (info != null && !wasRevealed && !Reflect.Set(info, "IsRevealed", false))
                        throw new InvalidOperationException("Unable to preserve unrevealed sentinel state for " + tag);
                    raw = Reflect.String(Reflect.Get(info, "RawValue"));
                }
                if (String.IsNullOrWhiteSpace(raw)) continue;
                string label = CodeLabel(tag);
                latest.Add(new CodeEntry
                {
                    Tag = tag,
                    Label = label,
                    Value = Clip(raw, 110),
                    IsRevealed = Reflect.Bool(Reflect.Get(info, "IsRevealed")),
                    IsClaimed = Reflect.Bool(Reflect.Get(info, "IsClaimed"))
                });
                if (latest.Count >= 16) break;
            }
            active.Codes.Clear();
            active.Codes.AddRange(latest);
        }

        private static string CodeLabel(string tag)
        {
            switch (tag)
            {
                case "FICTIONAL_GIFT_CARD_CODE": return "Gift card code";
                case "FICTIONAL_CREDIT_CARD_NUMBER": return "Credit card number";
                case "FICTIONAL_CREDIT_CARD_EXPIRY": return "Expiration date";
                case "FICTIONAL_CREDIT_CARD_CVV": return "Security code";
                case "FICTIONAL_REMOTE_DESKTOP_CODE": return "Remote desktop code";
                case "FICTIONAL_SSN": return "Social security number";
                default: return tag.Replace("FICTIONAL_", "").Replace('_', ' ').ToLowerInvariant();
            }
        }

        // A viewer can type e.g. "Sure, it's 4821-7395" or "48217395".
        // Match only an EXACT code belonging to this call; other numbers remain speech.
        private string PrepareViewerSpeech(string viewerLine, out List<string> disclosedTags)
        {
            disclosedTags = new List<string>();
            if (active == null || active.Codes.Count == 0) return viewerLine;
            object sentinel = Reflect.Call(active.Call, "GetSentinelCallState");
            if (sentinel == null) return viewerLine;
            string tagged = viewerLine;
            // Longer values first, so partially similar codes cannot shadow another.
            foreach (CodeEntry code in active.Codes.OrderByDescending(x => x.Value.Length))
            {
                if (code.IsClaimed || disclosedTags.Contains(code.Tag)) continue;
                object info = Reflect.Call(sentinel, "GetInfo", code.Tag);
                string currentRaw = Reflect.String(Reflect.Get(info, "RawValue"));
                if (!String.Equals(currentRaw, code.Value, StringComparison.Ordinal) ||
                    Reflect.Bool(Reflect.Get(info, "IsClaimed"))) continue;
                string normalized = Regex.Replace(currentRaw, "[^A-Za-z0-9]", "");
                if (normalized.Length < 6 || !normalized.Any(char.IsDigit)) continue;
                // Accept spaces and hyphens between characters, but no extra digits.
                string pattern = @"(?<![A-Za-z0-9])" +
                    String.Join(@"[\s-]*", normalized.Select(c => Regex.Escape(c.ToString())).ToArray()) +
                    @"(?![A-Za-z0-9])";
                Regex regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!regex.IsMatch(tagged)) continue;
                tagged = regex.Replace(tagged, "[[" + code.Tag + "]]", 1);
                disclosedTags.Add(code.Tag);
            }
            if (disclosedTags.Count == 0) return viewerLine;
            // Use the SAME expansion/markup path as the game's original AI dialogue.
            // It produces <transcript sentinel="TAG" text="RAW">spoken digits</transcript>,
            // which the phone UI recognizes for click-to-copy/autofill.
            Type processor = Reflect.Find("SentinelProcessor");
            MethodInfo expand = processor == null ? null : AccessTools.Method(processor, "ReplaceForTts");
            if (expand == null) throw new MissingMethodException("SentinelProcessor.ReplaceForTts");
            object[] args = { tagged, sentinel, null, "en" };
            string prepared = expand.Invoke(null, args) as string;
            if (String.IsNullOrWhiteSpace(prepared) || prepared.IndexOf("<transcript sentinel=", StringComparison.Ordinal) < 0)
            {
                // Fail safely: never mark the code disclosed without game markup.
                disclosedTags.Clear();
                return viewerLine;
            }
            return prepared;
        }

        // The real AI normally marks earned disclosure through
        // CallerDialogueSession.Commit(). A human caller bypasses that method, so
        // record the matching disclosure in the game's existing session state.
        private void RecordHumanDisclosure(string tag)
        {
            string scam;
            switch (tag)
            {
                case "FICTIONAL_GIFT_CARD_CODE": scam = "gift-card"; break;
                case "FICTIONAL_CREDIT_CARD_NUMBER": scam = "credit-card"; break;
                case "FICTIONAL_SSN": scam = "social-security"; break;
                default: return;
            }
            object session = Reflect.Get(active.Call, "dialogueSession");
            var disclosed = Reflect.Get(session, "disclosed") as ICollection<string>;
            if (disclosed == null)
            {
                host.LogWarning("Human code spoken but game dialogue disclosure state was unavailable for " + scam);
                return;
            }
            disclosed.Add(scam);
            host.LogInfo("Human-controlled disclosure recorded: " + scam);
        }

        public void HangUp()
        {
            if (active == null) return;
            try { Reflect.Call(active.Call, "ServerHangUp", false); }
            catch (Exception ex) { host.LogWarning("Hangup not applied: " + ex.GetBaseException().Message); }
        }

        public string BuildOverlayJson()
        {
            if (active == null || !host.VictimsEnabled.Value) return "{\"active\":false}";
            var b = new StringBuilder();
            b.Append("{\"active\":true,\"viewer\":").Append(OverlayServer.EscapeJson(active.Viewer.Name));
            b.Append(",\"phase\":").Append(OverlayServer.EscapeJson(active.Phase));
            b.Append(",\"lastSpeech\":").Append(OverlayServer.EscapeJson(active.LastStreamerSpeech));
            b.Append(",\"codes\":[");
            for (int i = 0; host.OverlayCodes.Value && i < active.Codes.Count; ++i)
            {
                if (i > 0) b.Append(',');
                b.Append("{\"label\":").Append(OverlayServer.EscapeJson(active.Codes[i].Label));
                b.Append(",\"value\":").Append(OverlayServer.EscapeJson(active.Codes[i].Value));
                b.Append(",\"revealed\":").Append(active.Codes[i].IsRevealed ? "true" : "false");
                b.Append(",\"claimed\":").Append(active.Codes[i].IsClaimed ? "true" : "false").Append('}');
            }
            return b.Append("]}").ToString();
        }

        public void ResetPool() { waiting.Clear(); totalSelected = 0; }
        public void Dispose() { harmony.UnpatchSelf(); current = null; waiting.Clear(); active = null; }
    }
}
