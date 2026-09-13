using System.Runtime.CompilerServices;
using UnityEngine;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Feature 2: Success/failure feedback loop.
    /// Per-AI tracker that records tactic → outcome pairs and computes per-tactic success rates.
    /// AI uses success rates to weight future tactic selections.
    /// </summary>
    public sealed class TacticOutcomeTracker
    {
        /// <summary>Tactic categories tracked for success/failure.</summary>
        public enum TacticType
        {
            Dodge,      // dodge attempt → did AI avoid damage in next 1.5s?
            Approach,   // move toward player → did AI deal damage in next 2s?
            Cover,      // use cover → did AI avoid damage while behind cover?
            Flank,      // side/behind approach → did AI deal damage from off-angle?
            Push,       // rush during player vulnerability → did AI deal damage?
            Retreat,    // back away → did AI avoid damage in next 2s?
            Count       // sentinel
        }

        private const int TypeCount = (int)TacticType.Count;
        private const float DecayAlpha = 0.2f; // EMA alpha for outcome blending
        private const float DefaultSuccessRate = 0.5f;

        // ---- Per-AI state (stored via ConditionalWeakTable in patches) ----
        private readonly float[] _successRate = new float[TypeCount];
        private readonly int[] _attempts = new int[TypeCount];
        private readonly int[] _successes = new int[TypeCount];

        // Pending outcome: when an action starts, record type + time; check outcome later
        private TacticType _pendingTactic = TacticType.Count;
        private float _pendingStartTime = -999f;
        private float _pendingPlayerHpAtStart;
        private float _pendingAiHpAtStart;
        private const float OutcomeWindowSeconds = 2f;

        public TacticOutcomeTracker()
        {
            for (int i = 0; i < TypeCount; i++)
                _successRate[i] = DefaultSuccessRate;
        }

        /// <summary>Success rate for a given tactic (0~1). 0.5 = neutral, >0.5 = working, <0.5 = failing.</summary>
        public float GetSuccessRate(TacticType type)
        {
            int idx = (int)type;
            return idx >= 0 && idx < TypeCount ? _successRate[idx] : DefaultSuccessRate;
        }

        /// <summary>Weight multiplier for a tactic based on its success rate. Range: 0.6~1.4.</summary>
        public float GetTacticWeight(TacticType type)
        {
            float rate = GetSuccessRate(type);
            // Map 0~1 success rate to 0.6~1.4 weight
            return Mathf.Lerp(0.6f, 1.4f, rate);
        }

        /// <summary>Record that this AI started a tactic. Call when the action begins.</summary>
        public void RecordTacticStart(TacticType type, float aiHpRatio, float playerHpRatio)
        {
            // If there's a pending tactic that wasn't resolved, treat it as neutral
            if (_pendingTactic != TacticType.Count)
                ResolvePending(false, false);

            _pendingTactic = type;
            _pendingStartTime = Time.time;
            _pendingAiHpAtStart = aiHpRatio;
            _pendingPlayerHpAtStart = playerHpRatio;
        }

        /// <summary>
        /// Call periodically (every 0.1-0.5s) to check if pending tactic outcome window has elapsed.
        /// Pass current HP ratios to determine success.
        /// </summary>
        public void UpdatePendingOutcome(float aiHpRatio, float playerHpRatio)
        {
            if (_pendingTactic == TacticType.Count) return;
            if (Time.time - _pendingStartTime < OutcomeWindowSeconds) return;

            bool aiAvoided = aiHpRatio >= _pendingAiHpAtStart - 0.01f; // AI didn't take significant damage
            bool aiDealt = playerHpRatio < _pendingPlayerHpAtStart - 0.01f; // AI dealt damage to player

            bool success;
            switch (_pendingTactic)
            {
                case TacticType.Dodge:
                case TacticType.Cover:
                case TacticType.Retreat:
                    success = aiAvoided; // defensive tactics succeed if AI avoided damage
                    break;
                case TacticType.Approach:
                case TacticType.Flank:
                case TacticType.Push:
                    success = aiDealt; // offensive tactics succeed if AI dealt damage
                    break;
                default:
                    success = false;
                    break;
            }

            ResolvePending(success, true);
        }

        /// <summary>Force-resolve pending tactic (e.g., AI died → all pending = failure).</summary>
        public void ForceResolvePending(bool success)
        {
            if (_pendingTactic != TacticType.Count)
                ResolvePending(success, true);
        }

        private void ResolvePending(bool success, bool counted)
        {
            if (_pendingTactic == TacticType.Count) return;
            int idx = (int)_pendingTactic;
            if (idx >= 0 && idx < TypeCount && counted)
            {
                _attempts[idx]++;
                if (success) _successes[idx]++;
                // EMA blend
                float sample = success ? 1f : 0f;
                _successRate[idx] = DecayAlpha * sample + (1f - DecayAlpha) * _successRate[idx];
            }
            _pendingTactic = TacticType.Count;
            _pendingStartTime = -999f;
        }

        /// <summary>
        /// Overall AI effectiveness (0~1). Average of all tactic success rates weighted by attempt count.
        /// Used to scale global difficulty: low effectiveness → AI tries harder, high → AI relaxes slightly.
        /// </summary>
        public float GetOverallEffectiveness()
        {
            float weightedSum = 0f;
            float totalWeight = 0f;
            for (int i = 0; i < TypeCount; i++)
            {
                float w = Mathf.Max(1f, _attempts[i]);
                weightedSum += _successRate[i] * w;
                totalWeight += w;
            }
            return totalWeight > 0f ? weightedSum / totalWeight : DefaultSuccessRate;
        }

        // ---- Per-AI storage via ConditionalWeakTable ----

        private static readonly ConditionalWeakTable<global::AICharacterController, TacticOutcomeTracker> _trackers
            = new ConditionalWeakTable<global::AICharacterController, TacticOutcomeTracker>();

        public static TacticOutcomeTracker GetFor(global::AICharacterController ai)
        {
            return _trackers.GetOrCreateValue(ai);
        }

        public static bool TryGetFor(global::AICharacterController ai, out TacticOutcomeTracker tracker)
        {
            return _trackers.TryGetValue(ai, out tracker);
        }
    }
}
