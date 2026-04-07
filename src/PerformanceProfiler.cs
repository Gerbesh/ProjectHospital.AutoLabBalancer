using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class PerformanceSample
    {
        public string Name;
        public long Calls;
        public double TotalMs;
        public double MaxMs;
        public long SlowCalls;

        public double AverageMs
        {
            get { return Calls <= 0 ? 0.0 : TotalMs / Calls; }
        }

        public PerformanceSample Clone()
        {
            return new PerformanceSample
            {
                Name = Name,
                Calls = Calls,
                TotalMs = TotalMs,
                MaxMs = MaxMs,
                SlowCalls = SlowCalls
            };
        }
    }

    internal static class PerformanceProfiler
    {
        private static readonly Dictionary<string, PerformanceSample> Samples = new Dictionary<string, PerformanceSample>();
        private static readonly object Sync = new object();
        private static double _tickToMs = 1000.0 / Stopwatch.Frequency;
        private static float _nextLogAt;

        public static bool Enabled
        {
            get
            {
                return RuntimeSettings.Config != null
                    && RuntimeSettings.Config.Enabled.Value
                    && RuntimeSettings.Config.EnablePerformanceProfiler.Value;
            }
        }

        public static long Start()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void Stop(string name, long startTicks)
        {
            if (startTicks <= 0L || !Enabled)
            {
                return;
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * _tickToMs;
            lock (Sync)
            {
                PerformanceSample sample;
                if (!Samples.TryGetValue(name, out sample))
                {
                    sample = new PerformanceSample { Name = name };
                    Samples[name] = sample;
                }

                sample.Calls++;
                sample.TotalMs += elapsedMs;
                if (elapsedMs > sample.MaxMs)
                {
                    sample.MaxMs = elapsedMs;
                }

                if (elapsedMs >= RuntimeSettings.Config.ProfilerSlowCallMs.Value)
                {
                    sample.SlowCalls++;
                }
            }
        }

        public static void Tick(float now)
        {
            if (!Enabled || RuntimeSettings.Logger == null || now < _nextLogAt)
            {
                return;
            }

            _nextLogAt = now + Mathf.Max(5f, RuntimeSettings.Config.ProfilerSampleIntervalSeconds.Value);
            var samples = GetTopSamples(RuntimeSettings.Config.ProfilerTopN.Value);
            if (samples.Count == 0)
            {
                return;
            }

            RuntimeSettings.Logger.LogInfo(ModText.T("PerfTag") + " " + string.Join(" | ", samples.Select(FormatSample).ToArray()));
            if (RuntimeSettings.Config.ProfilerAutoResetAfterLog.Value)
            {
                Reset();
            }
        }

        public static List<PerformanceSample> GetTopSamples(int limit)
        {
            lock (Sync)
            {
                return Samples.Values
                    .OrderByDescending(sample => sample.TotalMs)
                    .Take(Mathf.Max(1, limit))
                    .Select(sample => sample.Clone())
                    .ToList();
            }
        }

        public static void Reset()
        {
            lock (Sync)
            {
                Samples.Clear();
            }
        }

        public static string FormatSample(PerformanceSample sample)
        {
            if (sample == null)
            {
                return string.Empty;
            }

            return sample.Name
                + " calls=" + sample.Calls
                + " totalMs=" + sample.TotalMs.ToString("0.00")
                + " avgMs=" + sample.AverageMs.ToString("0.000")
                + " maxMs=" + sample.MaxMs.ToString("0.00")
                + " slow=" + sample.SlowCalls;
        }
    }

    [HarmonyPatch]
    internal static class PerformanceProfilerPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = new List<MethodBase>();
            AddMethod(methods, AccessTools.Method(AccessTools.TypeByName("Lopital.ProcedureManager"), "Update", new[] { typeof(int) }));
            AddMethod(methods, AccessTools.Method(AccessTools.TypeByName("Lopital.WalkComponent"), "MultiUpdate", new[] { typeof(int), typeof(float) }));
            AddMethod(methods, AccessTools.Method(AccessTools.TypeByName("Lopital.HospitalizationComponent"), "Update", new[] { typeof(float) }));
            AddMethod(methods, AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorDoctor"), "Update", new[] { typeof(float) }));
            AddMethod(methods, AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorNurse"), "Update", new[] { typeof(float) }));
            AddMethod(methods, AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorJanitor"), "Update", new[] { typeof(float) }));
            AddMethod(methods, AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorLabSpecialist"), "Update", new[] { typeof(float) }));
            AddMethod(methods, AccessTools.Method(typeof(BottleneckOverlayService), "CreateSnapshot", Type.EmptyTypes));
            AddMethod(methods, AccessTools.Method(typeof(ProductivityTweaksService), "Tick", new[] { typeof(float) }));

            foreach (var typeName in new[]
            {
                "Lopital.BehaviorNurse",
                "Lopital.BehaviorDoctor",
                "Lopital.BehaviorJanitor",
                "Lopital.BehaviorLabSpecialist",
                "Lopital.BehaviorPatient",
                "Lopital.HospitalizationComponent",
                "Lopital.ProcedureComponent",
                "Lopital.ProcedureManager",
                "Lopital.ProcedureQueue",
                "Lopital.ProcedureScene",
                "Lopital.ProcedureSceneFactory",
                "Lopital.Department",
                "Lopital.MapScriptInterface",
                "Lopital.LabProcedureManager"
            })
            {
                AddDetailedMethods(methods, typeName);
            }

            return methods;
        }

        private static void AddDetailedMethods(List<MethodBase> methods, string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                return;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (ShouldProfileDetailedMethod(method))
                {
                    AddMethod(methods, method);
                }
            }
        }

        private static bool ShouldProfileDetailedMethod(MethodInfo method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.IsConstructor)
            {
                return false;
            }

            var name = method.Name;
            if (name.StartsWith("get_", StringComparison.Ordinal)
                || name.StartsWith("set_", StringComparison.Ordinal)
                || name.StartsWith("add_", StringComparison.Ordinal)
                || name.StartsWith("remove_", StringComparison.Ordinal)
                || name.StartsWith("DEBUG_", StringComparison.Ordinal))
            {
                return false;
            }

            return name.StartsWith("UpdateState", StringComparison.Ordinal)
                || name.StartsWith("Find", StringComparison.Ordinal)
                || name.StartsWith("GetClosest", StringComparison.Ordinal)
                || name.StartsWith("GetRandom", StringComparison.Ordinal)
                || name.StartsWith("Has", StringComparison.Ordinal)
                || name.StartsWith("Is", StringComparison.Ordinal)
                || name.StartsWith("Can", StringComparison.Ordinal)
                || name.StartsWith("Select", StringComparison.Ordinal)
                || name.StartsWith("Check", StringComparison.Ordinal)
                || name.StartsWith("Try", StringComparison.Ordinal)
                || name.StartsWith("Plan", StringComparison.Ordinal)
                || name.StartsWith("Reserve", StringComparison.Ordinal)
                || name.StartsWith("Release", StringComparison.Ordinal)
                || name.StartsWith("Send", StringComparison.Ordinal)
                || name.StartsWith("Clean", StringComparison.Ordinal);
        }

        private static void AddMethod(List<MethodBase> methods, MethodBase method)
        {
            if (method == null || methods.Contains(method))
            {
                return;
            }

            methods.Add(method);
        }

        private static void Prefix(MethodBase __originalMethod, ref long __state)
        {
            __state = PerformanceProfiler.Start();
        }

        private static void Postfix(MethodBase __originalMethod, long __state)
        {
            if (__originalMethod == null)
            {
                return;
            }

            PerformanceProfiler.Stop(FormatMethodName(__originalMethod), __state);
        }

        private static string FormatMethodName(MethodBase method)
        {
            var declaringType = method.DeclaringType == null ? "Unknown" : method.DeclaringType.Name;
            return declaringType + "." + method.Name + "(" + method.GetParameters().Length + ")";
        }
    }
}
