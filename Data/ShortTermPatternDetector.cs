using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Feature 3: Short-term adaptation layer.
    /// Maintains a fast-reacting sliding window (3-5s) to detect sudden player behavior changes
    /// and output immediate tactical modifiers on top of the slower EMA-based system.
    /// </summary>
    public sealed class ShortTermPatternDetector
    {
        // ---- Configuration ----
        private const float WindowSeconds = 4f;
        private const int MaxEvents = 64;
        private const float FastEmaAlpha = 0.4f; // much faster than main EMA (0.15)

        // ---- Event ring buffers ----
        private readonly float[] _dashTimes = new float[MaxEvents];
        private readonly Vector3[] _dashDirs = new Vector3[MaxEvents];
        private int _dashWrite;
        private int _dashCount;

        private readonly float[] _shotTimes = new float[MaxEvents];
        private int _shotWrite;
        private int _shotCount;

        private readonly float[] _moveTimes = new float[MaxEvents];
        private readonly Vector3[] _moveDirs = new Vector3[MaxEvents];
        private int _moveWrite;
        private int _moveCount;

        // ---- Fast EMA state ----
        private float _fastMoveStrength;
        private float _fastDashRate;
        private float _fastFireRate;
        private float _fastMoveDirDot; // approach/retreat dot
        private float _prevMoveStrength;
        private float _prevDashRate;
        private float _prevFireRate;

        // ---- Output cache ----
        private float _cachedTime = -999f;
        private const float CacheInterval = 0.15f;

        /// <summary>How much faster AI should react right now (0~0.5). High = sudden behavior spike.</summary>
        public float ReactivityBoost { get; private set; }

        /// <summary>Multiply dodge chance by this (0.7~1.4). High = player suddenly aggressive.</summary>
        public float DodgeUrgencyMult { get; private set; } = 1f;

        /// <summary>If player is currently vulnerable (reloading, retreating, low fire rate), AI should push.</summary>
        public bool PushWindowActive { get; private set; }

        /// <summary>Immediate aggression correction (-0.3~+0.3) from short-term behavior spike.</summary>
        public float ImmediateAggressionCorrection { get; private set; }

        /// <summary>How fast the player's behavior is changing right now (0~1). Used to scale all short-term effects.</summary>
        public float BehaviorVolatility { get; private set; }

        /// <summary>Detected burst fire → AI should dodge more during burst. 0~1.</summary>
        public float BurstFireIntensity { get; private set; }

        /// <summary>Direction player is consistently moving toward in the last few seconds.</summary>
        public Vector3 RecentMoveDirection { get; private set; }

        // ---- Data input methods ----

        public void RecordDash(Vector3 direction)
        {
            float now = Time.time;
            _dashTimes[_dashWrite] = now;
            _dashDirs[_dashWrite] = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.zero;
            _dashWrite = (_dashWrite + 1) % MaxEvents;
            if (_dashCount < MaxEvents) _dashCount++;
        }

        public void RecordShot()
        {
            float now = Time.time;
            _shotTimes[_shotWrite] = now;
            _shotWrite = (_shotWrite + 1) % MaxEvents;
            if (_shotCount < MaxEvents) _shotCount++;
        }

        public void RecordMovement(Vector3 moveDir, float magnitude, float dotToEnemy)
        {
            float now = Time.time;
            _moveTimes[_moveWrite] = now;
            _moveDirs[_moveWrite] = moveDir;
            _moveWrite = (_moveWrite + 1) % MaxEvents;
            if (_moveCount < MaxEvents) _moveCount++;

            // Fast EMA update
            _fastMoveStrength = FastEmaAlpha * magnitude + (1f - FastEmaAlpha) * _fastMoveStrength;
            _fastMoveDirDot = FastEmaAlpha * dotToEnemy + (1f - FastEmaAlpha) * _fastMoveDirDot;
        }

        /// <summary>Call every sample tick (0.1s) to update derived values.</summary>
        public void Tick()
        {
            float now = Time.time;
            if (now - _cachedTime < CacheInterval) return;
            _cachedTime = now;

            // Count events in window
            float dashRate = CountInWindow(_dashTimes, _dashCount, _dashWrite, now, WindowSeconds) / WindowSeconds * 60f;
            float fireRate = CountInWindow(_shotTimes, _shotCount, _shotWrite, now, WindowSeconds) / WindowSeconds * 60f;

            // Fast EMA for rates
            _fastDashRate = FastEmaAlpha * dashRate + (1f - FastEmaAlpha) * _fastDashRate;
            _fastFireRate = FastEmaAlpha * fireRate + (1f - FastEmaAlpha) * _fastFireRate;

            // Behavior volatility: how much are the fast EMAs changing vs previous tick
            float moveDelta = Mathf.Abs(_fastMoveStrength - _prevMoveStrength);
            float dashDelta = Mathf.Abs(_fastDashRate - _prevDashRate) / 12f; // normalize: 12 dashes/min = max
            float fireDelta = Mathf.Abs(_fastFireRate - _prevFireRate) / 120f; // normalize: 120 shots/min = max
            BehaviorVolatility = Mathf.Clamp01((moveDelta + dashDelta + fireDelta) / 3f * 4f);

            _prevMoveStrength = _fastMoveStrength;
            _prevDashRate = _fastDashRate;
            _prevFireRate = _fastFireRate;

            // Reactivity boost: spike when player behavior is volatile
            ReactivityBoost = Mathf.Clamp(BehaviorVolatility * 0.5f, 0f, 0.5f);

            // Dodge urgency: high when player is actively firing a lot (burst)
            BurstFireIntensity = DetectBurstFire(now);
            DodgeUrgencyMult = Mathf.Clamp(1f + BurstFireIntensity * 0.3f + (dashRate > 8f ? 0.1f : 0f), 0.7f, 1.4f);

            // Push window: player retreating or low fire rate after being active
            bool playerRetreating = _fastMoveDirDot < -0.3f && _fastMoveStrength > 0.3f;
            bool fireLull = _prevFireRate > 30f && fireRate < 10f; // was shooting, now stopped
            PushWindowActive = playerRetreating || fireLull;

            // Immediate aggression correction
            float dashSpike = Mathf.Clamp01((dashRate - 4f) / 8f) * 0.15f; // lots of dashes = player aggressive
            float fireSpike = Mathf.Clamp01((fireRate - 40f) / 60f) * 0.1f; // lots of fire = player aggressive
            float retreatSignal = playerRetreating ? -0.1f : 0f;
            ImmediateAggressionCorrection = Mathf.Clamp(dashSpike + fireSpike + retreatSignal, -0.3f, 0.3f);

            // Recent dominant move direction
            RecentMoveDirection = ComputeRecentMoveDirection(now);
        }

        // ---- Internal helpers ----

        private int CountInWindow(float[] times, int count, int writePos, float now, float window)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                int idx = (writePos - 1 - i + MaxEvents * 2) % MaxEvents;
                if (now - times[idx] <= window) n++;
                else break; // ring buffer is chronological, older entries beyond window
            }
            return n;
        }

        private float DetectBurstFire(float now)
        {
            // Count shots in last 1 second
            int shotsInLastSecond = 0;
            float shortWindow = 1f;
            for (int i = 0; i < _shotCount; i++)
            {
                int idx = (_shotWrite - 1 - i + MaxEvents * 2) % MaxEvents;
                if (now - _shotTimes[idx] <= shortWindow) shotsInLastSecond++;
                else break;
            }
            // 5+ shots in 1 second = intense burst
            return Mathf.Clamp01((shotsInLastSecond - 2f) / 5f);
        }

        private Vector3 ComputeRecentMoveDirection(float now)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            float shortWindow = 2f;
            for (int i = 0; i < _moveCount && n < 20; i++)
            {
                int idx = (_moveWrite - 1 - i + MaxEvents * 2) % MaxEvents;
                if (now - _moveTimes[idx] > shortWindow) break;
                sum += _moveDirs[idx];
                n++;
            }
            return n > 0 ? (sum / n).normalized : Vector3.zero;
        }

        public void Reset()
        {
            _dashCount = 0; _dashWrite = 0;
            _shotCount = 0; _shotWrite = 0;
            _moveCount = 0; _moveWrite = 0;
            _fastMoveStrength = 0.5f;
            _fastDashRate = 0f;
            _fastFireRate = 0f;
            _fastMoveDirDot = 0f;
            _prevMoveStrength = 0.5f;
            _prevDashRate = 0f;
            _prevFireRate = 0f;
            ReactivityBoost = 0f;
            DodgeUrgencyMult = 1f;
            PushWindowActive = false;
            ImmediateAggressionCorrection = 0f;
            BehaviorVolatility = 0f;
            BurstFireIntensity = 0f;
            RecentMoveDirection = Vector3.zero;
            _cachedTime = -999f;
        }
    }
}
