using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Feature 4: Player habit exploitation.
    /// Tracks repeating player behavior sequences to detect exploitable habits.
    /// When a habit is confidently detected, outputs predictions for AI to counter.
    /// </summary>
    public sealed class PlayerHabitTracker
    {
        // ---- Configuration ----
        private const int DirectionBuckets = 8; // 8 compass directions for dash tracking
        private const int MaxSequenceLength = 16;
        private const float HabitDecayAlpha = 0.1f; // slow decay for habit detection
        private const float HabitConfidenceThreshold = 0.6f; // must be 60% consistent to count as habit
        private const float MinSamplesForHabit = 5f;

        // ---- Dash direction histogram ----
        // Track which direction the player dashes most often (in 8 compass directions)
        private readonly float[] _dashDirHistogram = new float[DirectionBuckets];
        private int _totalDashSamples;

        // ---- Post-action sequences ----
        // What does the player do after shooting? (retreat, strafe, advance, stay)
        private readonly float[] _postShootBehavior = new float[4]; // 0=retreat, 1=strafe, 2=advance, 3=stay
        private int _postShootSamples;
        private float _lastShootTime = -999f;
        private bool _awaitingPostShootSample;

        // ---- Reload timing ----
        // Track intervals between reload events to predict next reload
        private readonly List<float> _reloadIntervals = new List<float>();
        private float _lastReloadTime = -999f;
        private const int MaxReloadSamples = 16;

        // ---- HP threshold behavior ----
        // Track what player does at specific HP thresholds
        private int _fleeObservations;
        private float _fleeHpSum;

        // ---- Position repetition ----
        // Track frequently visited positions (grid-based)
        private readonly Dictionary<long, int> _positionVisits = new Dictionary<long, int>();
        private const float PositionGridSize = 3f; // 3m grid cells
        private const int MaxPositionEntries = 128;

        // ---- Outputs ----

        /// <summary>Most likely next dash direction (normalized). Zero if no bias detected.</summary>
        public Vector3 PredictedDashDirection { get; private set; }

        /// <summary>Confidence in dash direction prediction (0~1).</summary>
        public float DashDirectionConfidence { get; private set; }

        /// <summary>Expected player behavior after shooting. 0=retreat, 1=strafe, 2=advance, 3=stay.</summary>
        public int PredictedPostShootBehavior { get; private set; }

        /// <summary>Confidence in post-shoot prediction (0~1).</summary>
        public float PostShootConfidence { get; private set; }

        /// <summary>Predicted time until next reload (seconds). -1 if no prediction.</summary>
        public float PredictedTimeToReload { get; private set; } = -1f;

        /// <summary>Confidence in reload prediction (0~1).</summary>
        public float ReloadPredictionConfidence { get; private set; }

        /// <summary>HP ratio at which player tends to flee/retreat. -1 if not detected.</summary>
        public float DetectedFleeHpThreshold { get; private set; } = -1f;

        /// <summary>Most frequently visited position. Zero if none detected.</summary>
        public Vector3 FrequentPosition { get; private set; }

        /// <summary>Confidence in position prediction (0~1).</summary>
        public float PositionHabitConfidence { get; private set; }

        // ---- Data input ----

        public void RecordDash(Vector3 dashDirection)
        {
            if (dashDirection.sqrMagnitude < 0.001f) return;
            Vector3 dir = dashDirection.normalized;
            int bucket = DirectionToBucket(dir);
            _dashDirHistogram[bucket]++;
            _totalDashSamples++;

            UpdateDashPrediction();
        }

        public void RecordShot(Vector3 playerPosition)
        {
            _lastShootTime = Time.time;
            _awaitingPostShootSample = true;
        }

        /// <summary>Call every sample tick with player's current movement state.</summary>
        public void SampleMovement(Vector3 playerPosition, Vector3 moveDirection, float moveMagnitude, float dotToEnemy, float playerHpRatio)
        {
            // Post-shoot behavior detection
            if (_awaitingPostShootSample && Time.time - _lastShootTime >= 0.5f && Time.time - _lastShootTime <= 1.5f)
            {
                _awaitingPostShootSample = false;
                int behavior;
                if (moveMagnitude < 0.1f)
                    behavior = 3; // stay
                else if (dotToEnemy < -0.3f)
                    behavior = 0; // retreat
                else if (dotToEnemy > 0.3f)
                    behavior = 2; // advance
                else
                    behavior = 1; // strafe

                _postShootBehavior[behavior]++;
                _postShootSamples++;
                UpdatePostShootPrediction();
            }
            else if (_awaitingPostShootSample && Time.time - _lastShootTime > 1.5f)
            {
                _awaitingPostShootSample = false; // expired, no sample
            }

            // Position tracking
            RecordPosition(playerPosition);

            // HP threshold flee detection
            if (dotToEnemy < -0.4f && moveMagnitude > 0.4f && playerHpRatio < 0.5f)
            {
                // Player is retreating at below 50% HP
                _fleeHpSum += playerHpRatio;
                _fleeObservations++;
                if (_fleeObservations >= 3)
                {
                    DetectedFleeHpThreshold = _fleeHpSum / _fleeObservations;
                }
            }
        }

        public void RecordReload()
        {
            float now = Time.time;
            if (_lastReloadTime > 0f)
            {
                float interval = now - _lastReloadTime;
                if (interval > 1f && interval < 60f)
                {
                    _reloadIntervals.Add(interval);
                    if (_reloadIntervals.Count > MaxReloadSamples)
                        _reloadIntervals.RemoveAt(0);
                    UpdateReloadPrediction(now);
                }
            }
            _lastReloadTime = now;
        }

        // ---- Internal prediction updates ----

        private void UpdateDashPrediction()
        {
            if (_totalDashSamples < MinSamplesForHabit)
            {
                DashDirectionConfidence = 0f;
                PredictedDashDirection = Vector3.zero;
                return;
            }

            int maxBucket = 0;
            float maxCount = 0f;
            for (int i = 0; i < DirectionBuckets; i++)
            {
                if (_dashDirHistogram[i] > maxCount)
                {
                    maxCount = _dashDirHistogram[i];
                    maxBucket = i;
                }
            }

            float ratio = maxCount / _totalDashSamples;
            // Also consider adjacent buckets (player roughly dashes in that direction)
            int prev = (maxBucket - 1 + DirectionBuckets) % DirectionBuckets;
            int next = (maxBucket + 1) % DirectionBuckets;
            float adjacentRatio = (maxCount + _dashDirHistogram[prev] + _dashDirHistogram[next]) / _totalDashSamples;

            DashDirectionConfidence = Mathf.Clamp01(adjacentRatio - (1f / DirectionBuckets)); // subtract uniform expectation
            if (DashDirectionConfidence >= HabitConfidenceThreshold * 0.5f)
            {
                PredictedDashDirection = BucketToDirection(maxBucket);
            }
            else
            {
                PredictedDashDirection = Vector3.zero;
            }
        }

        private void UpdatePostShootPrediction()
        {
            if (_postShootSamples < MinSamplesForHabit)
            {
                PostShootConfidence = 0f;
                return;
            }

            int maxIdx = 0;
            float maxCount = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (_postShootBehavior[i] > maxCount)
                {
                    maxCount = _postShootBehavior[i];
                    maxIdx = i;
                }
            }

            PostShootConfidence = Mathf.Clamp01(maxCount / _postShootSamples - 0.25f); // subtract uniform expectation
            PredictedPostShootBehavior = maxIdx;
        }

        private void UpdateReloadPrediction(float now)
        {
            if (_reloadIntervals.Count < 3)
            {
                ReloadPredictionConfidence = 0f;
                PredictedTimeToReload = -1f;
                return;
            }

            // Calculate mean and standard deviation of reload intervals
            float sum = 0f;
            for (int i = 0; i < _reloadIntervals.Count; i++)
                sum += _reloadIntervals[i];
            float mean = sum / _reloadIntervals.Count;

            float varianceSum = 0f;
            for (int i = 0; i < _reloadIntervals.Count; i++)
            {
                float d = _reloadIntervals[i] - mean;
                varianceSum += d * d;
            }
            float stdDev = Mathf.Sqrt(varianceSum / _reloadIntervals.Count);
            float cv = mean > 0.1f ? stdDev / mean : 1f; // coefficient of variation

            // Low CV = consistent reload timing = high confidence
            ReloadPredictionConfidence = Mathf.Clamp01(1f - cv);
            float timeSinceLastReload = now - _lastReloadTime;
            PredictedTimeToReload = Mathf.Max(0f, mean - timeSinceLastReload);
        }

        private void RecordPosition(Vector3 pos)
        {
            long key = PositionToGridKey(pos);
            if (_positionVisits.ContainsKey(key))
                _positionVisits[key]++;
            else
            {
                if (_positionVisits.Count >= MaxPositionEntries)
                    PrunePositionVisits();
                _positionVisits[key] = 1;
            }

            // Update frequent position
            int maxVisits = 0;
            long maxKey = 0;
            foreach (var kv in _positionVisits)
            {
                if (kv.Value > maxVisits)
                {
                    maxVisits = kv.Value;
                    maxKey = kv.Key;
                }
            }

            int totalVisits = 0;
            foreach (var kv in _positionVisits)
                totalVisits += kv.Value;

            if (totalVisits > 0 && maxVisits >= MinSamplesForHabit)
            {
                float ratio = (float)maxVisits / totalVisits;
                PositionHabitConfidence = Mathf.Clamp01(ratio - 0.1f);
                if (PositionHabitConfidence > 0.1f)
                    FrequentPosition = GridKeyToPosition(maxKey);
                else
                    FrequentPosition = Vector3.zero;
            }
        }

        private void PrunePositionVisits()
        {
            // Remove entries with count = 1
            var toRemove = new List<long>();
            foreach (var kv in _positionVisits)
            {
                if (kv.Value <= 1) toRemove.Add(kv.Key);
            }
            foreach (var k in toRemove)
                _positionVisits.Remove(k);
            // If still too many, halve all counts
            if (_positionVisits.Count >= MaxPositionEntries)
            {
                var keys = new List<long>(_positionVisits.Keys);
                foreach (var k in keys)
                {
                    int v = _positionVisits[k] / 2;
                    if (v <= 0) _positionVisits.Remove(k);
                    else _positionVisits[k] = v;
                }
            }
        }

        // ---- Geometry helpers ----

        private static int DirectionToBucket(Vector3 dir)
        {
            float angle = Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            return Mathf.Clamp((int)(angle / 45f), 0, DirectionBuckets - 1);
        }

        private static Vector3 BucketToDirection(int bucket)
        {
            float angle = (bucket * 45f + 22.5f) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }

        private static long PositionToGridKey(Vector3 pos)
        {
            int gx = Mathf.FloorToInt(pos.x / PositionGridSize);
            int gz = Mathf.FloorToInt(pos.z / PositionGridSize);
            return ((long)gx << 32) | (uint)gz;
        }

        private static Vector3 GridKeyToPosition(long key)
        {
            int gx = (int)(key >> 32);
            int gz = (int)(key & 0xFFFFFFFF);
            return new Vector3((gx + 0.5f) * PositionGridSize, 0f, (gz + 0.5f) * PositionGridSize);
        }

        // ---- Save/Load helpers (persist key habit metrics across sessions) ----

        /// <summary>Export dash direction histogram for save.</summary>
        public float[] GetDashHistogramForSave()
        {
            var copy = new float[DirectionBuckets];
            System.Array.Copy(_dashDirHistogram, copy, DirectionBuckets);
            return copy;
        }

        /// <summary>Export post-shoot behavior histogram for save.</summary>
        public float[] GetPostShootBehaviorForSave()
        {
            var copy = new float[4];
            System.Array.Copy(_postShootBehavior, copy, 4);
            return copy;
        }

        public int TotalDashSamples => _totalDashSamples;
        public int PostShootSamples => _postShootSamples;
        public float FleeHpSum => _fleeHpSum;
        public int FleeObservations => _fleeObservations;

        /// <summary>Restore from saved data. Only restores aggregate habit metrics, not ephemeral event buffers.</summary>
        public void LoadFromSave(float[]? dashHistogram, int totalDashSamples,
            float[]? postShootBehavior, int postShootSamples,
            float fleeHpSum, int fleeObservations)
        {
            if (dashHistogram != null && dashHistogram.Length == DirectionBuckets)
            {
                System.Array.Copy(dashHistogram, _dashDirHistogram, DirectionBuckets);
                _totalDashSamples = totalDashSamples;
                UpdateDashPrediction();
            }
            if (postShootBehavior != null && postShootBehavior.Length == 4)
            {
                System.Array.Copy(postShootBehavior, _postShootBehavior, 4);
                _postShootSamples = postShootSamples;
                UpdatePostShootPrediction();
            }
            _fleeHpSum = fleeHpSum;
            _fleeObservations = fleeObservations;
            if (_fleeObservations >= 3)
                DetectedFleeHpThreshold = _fleeHpSum / _fleeObservations;
        }

        public void Reset()
        {
            for (int i = 0; i < DirectionBuckets; i++) _dashDirHistogram[i] = 0f;
            _totalDashSamples = 0;
            for (int i = 0; i < 4; i++) _postShootBehavior[i] = 0f;
            _postShootSamples = 0;
            _lastShootTime = -999f;
            _awaitingPostShootSample = false;
            _reloadIntervals.Clear();
            _lastReloadTime = -999f;
            _fleeObservations = 0;
            _fleeHpSum = 0f;
            _positionVisits.Clear();
            PredictedDashDirection = Vector3.zero;
            DashDirectionConfidence = 0f;
            PredictedPostShootBehavior = 0;
            PostShootConfidence = 0f;
            PredictedTimeToReload = -1f;
            ReloadPredictionConfidence = 0f;
            DetectedFleeHpThreshold = -1f;
            FrequentPosition = Vector3.zero;
            PositionHabitConfidence = 0f;
        }
    }
}
