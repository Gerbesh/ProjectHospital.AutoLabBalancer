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
            Reset();
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
            foreach (var method in new[]
            {
                AccessTools.Method(AccessTools.TypeByName("Lopital.ProcedureManager"), "Update", new[] { typeof(int) }),
                AccessTools.Method(AccessTools.TypeByName("Lopital.WalkComponent"), "MultiUpdate", new[] { typeof(int), typeof(float) }),
                AccessTools.Method(AccessTools.TypeByName("Lopital.HospitalizationComponent"), "Update", new[] { typeof(float) }),
                AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorDoctor"), "Update", new[] { typeof(float) }),
                AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorNurse"), "Update", new[] { typeof(float) }),
                AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorJanitor"), "Update", new[] { typeof(float) }),
                AccessTools.Method(AccessTools.TypeByName("Lopital.BehaviorLabSpecialist"), "Update", new[] { typeof(float) }),
                AccessTools.Method(typeof(BottleneckOverlayService), "CreateSnapshot", Type.EmptyTypes),
                AccessTools.Method(typeof(ProductivityTweaksService), "Tick", new[] { typeof(float) })
            })
            {
                if (method != null)
                {
                    yield return method;
                }
            }

            foreach (var method in GetStateMachineMethods("Lopital.BehaviorNurse"))
            {
                yield return method;
            }

            foreach (var method in GetStateMachineMethods("Lopital.BehaviorDoctor"))
            {
                yield return method;
            }

            foreach (var method in GetStateMachineMethods("Lopital.HospitalizationComponent"))
            {
                yield return method;
            }

            foreach (var method in GetStateMachineMethods("Lopital.ProcedureComponent"))
            {
                yield return method;
            }
        }

        private static IEnumerable<MethodBase> GetStateMachineMethods(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                yield break;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters)
                {
                    continue;
                }

                if (method.Name.StartsWith("UpdateState", StringComparison.Ordinal)
                    || method.Name == "SelectNextAction"
                    || method.Name == "SelectNextStep"
                    || method.Name == "IsHospitalizationOver"
                    || method.Name == "ReleaseFromObservation")
                {
                    yield return method;
                }
            }
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

            var declaringType = __originalMethod.DeclaringType == null ? "Unknown" : __originalMethod.DeclaringType.Name;
            PerformanceProfiler.Stop(declaringType + "." + __originalMethod.Name, __state);
        }
    }
}
