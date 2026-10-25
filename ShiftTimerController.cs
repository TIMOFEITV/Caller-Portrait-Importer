using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ScamWYF.TwitchControl
{
    // Runtime-guarded adapter. The export did not contain ScammerGameManager itself,
    // so modifying unsynchronized private fields would be reckless in multiplayer.
    internal sealed class ShiftTimerController
    {
        private readonly Plugin host;
        private Type managerType;
        private PropertyInfo lengthSetter;
        private PropertyInfo remainingSetter;
        private int appliedDay = -1;
        private bool inspected;
        private float nextTick;
        public string Status { get; private set; } = "Waiting for shift";

        public ShiftTimerController(Plugin owner) { host = owner; }

        private void Inspect(object manager)
        {
            inspected = true;
            Type t = manager.GetType();
            managerType = t;
            // Only choose network-backed setters. Mirror generates Network... properties
            // for SyncVars, ensuring dirty bits/client replication when written on server.
            lengthSetter = NetworkSetter(t, "DayLengthSeconds");
            remainingSetter = NetworkSetter(t, "DayTimeRemaining");
            if (lengthSetter == null || remainingSetter == null)
            {
                Status = "Unsupported timer state: no verified network-backed setters";
                host.LogWarning("Shift timer safely disabled: " + Status +
                    ". Need ScammerGameManager decompilation for a precise patch.");
            }
            else
            {
                Status = "Authoritative timer setters detected; waiting for new shift";
                host.LogInfo("Shift timer found network setters: " + lengthSetter.Name + ", " + remainingSetter.Name);
            }
        }

        private static PropertyInfo NetworkSetter(Type type, string name)
        {
            foreach (PropertyInfo p in type.GetProperties(Reflect.All))
            {
                if (!p.Name.StartsWith("Network", StringComparison.OrdinalIgnoreCase)) continue;
                if (!p.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.GetIndexParameters().Length > 0 || p.GetSetMethod(true) == null) continue;
                if (p.PropertyType != typeof(float) && p.PropertyType != typeof(double) && p.PropertyType != typeof(int)) continue;
                return p;
            }
            return null;
        }

        public void Tick()
        {
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + 0.5f;
            if (host.ShiftMinutes.Value == 0) { Status = "Game default"; return; }
            Type mirror = Reflect.Find("Mirror.NetworkServer");
            if (mirror == null || !Reflect.Bool(Reflect.Get(mirror, "active")))
            {
                Status = "Host-only: waiting for server";
                return;
            }
            Type t = Reflect.Find("ScammerGameManager");
            if (t == null) { Status = "ScammerGameManager type not found"; return; }
            object manager = Reflect.Get(t, "Instance");
            if (manager == null) { Status = "Waiting for manager"; return; }
            if (!inspected || t != managerType) Inspect(manager);
            if (remainingSetter == null || lengthSetter == null) return;
            bool started = Reflect.Bool(Reflect.Get(manager, "ScammerGameStarted"));
            bool review = Reflect.Bool(Reflect.Get(manager, "IsReviewPhase"));
            if (!started || review)
            {
                appliedDay = -1;
                Status = "Waiting for next shift";
                return;
            }
            int day = Reflect.Int(Reflect.Get(manager, "CurrentDay"));
            float remaining = Reflect.Float(Reflect.Get(manager, "DayTimeRemaining"));
            float configured = Reflect.Float(Reflect.Get(manager, "DayLengthSeconds"));
            if (day < 1 || remaining <= 0 || configured <= 0) return;
            if (appliedDay == day) return;
            float requested = host.ShiftMinutes.Value * 60f;
            float elapsed = Mathf.Max(0, configured - remaining);
            float newRemaining = Mathf.Max(5f, requested - elapsed);
            try
            {
                object secondsValue = Convert.ChangeType(requested, lengthSetter.PropertyType);
                object remainingValue = Convert.ChangeType(newRemaining, remainingSetter.PropertyType);
                lengthSetter.SetValue(manager, secondsValue, null);
                remainingSetter.SetValue(manager, remainingValue, null);
                // Read back the public getters: don't claim success based on silent setter failures.
                float readLength = Reflect.Float(Reflect.Get(manager, "DayLengthSeconds"));
                float readRemaining = Reflect.Float(Reflect.Get(manager, "DayTimeRemaining"));
                if (Math.Abs(readLength - requested) > 2f || Math.Abs(readRemaining - newRemaining) > 2f)
                {
                    Status = "Timer setter verification failed; no additional writes";
                    host.LogWarning("Shift timer did not reflect requested values after network setters. " + Status);
                    appliedDay = day;
                    return;
                }
                appliedDay = day;
                Status = "Applied " + host.ShiftMinutes.Value + " minute shift on day " + day;
                host.LogInfo(Status);
            }
            catch (Exception ex)
            {
                appliedDay = day;
                Status = "Timer update failed; see log";
                host.LogWarning("Shift timer update aborted: " + ex.GetBaseException().Message);
            }
        }
    }
}
