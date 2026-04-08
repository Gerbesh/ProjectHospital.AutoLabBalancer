using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class FramePacingService
    {
        private static bool _captured;
        private static bool _applied;
        private static int _originalTargetFrameRate;
        private static int _originalVSyncCount;
        private static float _originalMaximumDeltaTime;
        private static int _lastTargetFrameRate = int.MinValue;
        private static int _lastVSyncCount = int.MinValue;
        private static float _lastMaximumDeltaTime = -1f;
        private static int _lastMonitorRefreshRate = int.MinValue;

        public static string Summary
        {
            get
            {
                return ModText.F("FramePacingLine",
                    Application.targetFrameRate,
                    _lastMonitorRefreshRate > 0 ? _lastMonitorRefreshRate.ToString() : "-",
                    QualitySettings.vSyncCount,
                    Time.maximumDeltaTime.ToString("0.000"));
            }
        }

        public static void Tick()
        {
            if (RuntimeSettings.Config == null)
            {
                return;
            }

            CaptureOriginals();
            if (!RuntimeSettings.Config.Enabled.Value || !RuntimeSettings.Config.EnableFramePacing.Value)
            {
                RestoreOriginals();
                return;
            }

            var targetFrameRate = GetTargetFrameRate();
            var vSyncCount = RuntimeSettings.Config.FramePacingDisableVSync.Value ? 0 : 1;
            var maximumDeltaTime = Mathf.Clamp(RuntimeSettings.Config.FramePacingMaximumDeltaTime.Value, 0.016f, 0.25f);

            if (!_applied || targetFrameRate != _lastTargetFrameRate)
            {
                Application.targetFrameRate = targetFrameRate;
                _lastTargetFrameRate = targetFrameRate;
            }

            if (!_applied || vSyncCount != _lastVSyncCount)
            {
                QualitySettings.vSyncCount = vSyncCount;
                _lastVSyncCount = vSyncCount;
            }

            if (!_applied || Mathf.Abs(maximumDeltaTime - _lastMaximumDeltaTime) > 0.0001f)
            {
                Time.maximumDeltaTime = maximumDeltaTime;
                _lastMaximumDeltaTime = maximumDeltaTime;
            }

            _applied = true;
        }

        private static void CaptureOriginals()
        {
            if (_captured)
            {
                return;
            }

            _originalTargetFrameRate = Application.targetFrameRate;
            _originalVSyncCount = QualitySettings.vSyncCount;
            _originalMaximumDeltaTime = Time.maximumDeltaTime;
            _captured = true;
        }

        private static void RestoreOriginals()
        {
            if (!_captured || !_applied)
            {
                return;
            }

            Application.targetFrameRate = _originalTargetFrameRate;
            QualitySettings.vSyncCount = _originalVSyncCount;
            Time.maximumDeltaTime = _originalMaximumDeltaTime;
            _lastTargetFrameRate = int.MinValue;
            _lastVSyncCount = int.MinValue;
            _lastMaximumDeltaTime = -1f;
            _lastMonitorRefreshRate = int.MinValue;
            _applied = false;
        }

        private static int GetTargetFrameRate()
        {
            var configured = RuntimeSettings.Config.FramePacingTargetFrameRate.Value;
            if (RuntimeSettings.Config.FramePacingUseMonitorRefreshRate.Value)
            {
                var refreshRate = Screen.currentResolution.refreshRate;
                if (refreshRate > 0)
                {
                    _lastMonitorRefreshRate = refreshRate;
                    return Mathf.Clamp(refreshRate, 30, 240);
                }
            }

            _lastMonitorRefreshRate = int.MinValue;
            return Mathf.Clamp(configured, 30, 240);
        }
    }
}
