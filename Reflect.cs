using System;
using System.Reflection;
using HarmonyLib;

namespace ScamWYF.TwitchControl
{
    internal static class Reflect
    {
        internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        internal static object Get(object source, string name)
        {
            if (source == null) return null;
            Type t = source as Type ?? source.GetType();
            object receiver = source is Type ? null : source;
            FieldInfo f = t.GetField(name, All);
            if (f != null) return f.GetValue(receiver);
            PropertyInfo p = t.GetProperty(name, All);
            return p == null ? null : p.GetValue(receiver, null);
        }

        internal static bool Set(object source, string name, object value)
        {
            if (source == null) return false;
            Type t = source as Type ?? source.GetType();
            object receiver = source is Type ? null : source;
            FieldInfo f = t.GetField(name, All);
            if (f != null) { f.SetValue(receiver, value); return true; }
            PropertyInfo p = t.GetProperty(name, All);
            if (p != null && p.GetSetMethod(true) != null) { p.SetValue(receiver, value, null); return true; }
            return false;
        }

        internal static object Call(object source, string method, params object[] parameters)
        {
            if (source == null) return null;
            Type t = source as Type ?? source.GetType();
            MethodInfo m = AccessTools.Method(t, method);
            if (m == null) throw new MissingMethodException(t.FullName, method);
            return m.Invoke(source is Type ? null : source, parameters);
        }

        internal static string String(object value) { return value == null ? "" : Convert.ToString(value); }
        internal static bool Bool(object value) { return value != null && Convert.ToBoolean(value); }
        internal static float Float(object value) { return value == null ? 0f : Convert.ToSingle(value); }
        internal static int Int(object value) { return value == null ? 0 : Convert.ToInt32(value); }
        internal static Type Find(string shortName) { return AccessTools.TypeByName(shortName); }
        internal static object Clone(object source)
        {
            if (source == null) return null;
            return typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, null);
        }
    }
}
