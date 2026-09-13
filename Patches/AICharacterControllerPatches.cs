using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Duckov;
using Duckov.Utilities;
using Duckov.Scenes;
using AdaptiveEnemyAI.Data;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Settings;
using ItemStatsSystem.Stats;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// Harmony patches that apply adaptive parameters to AICharacterController.
    /// After the game sets reactionTime etc., Postfix overwrites with loadout-profile-based correction.
    /// </summary>
    public static class AICharacterControllerPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.AICharacterController";
        private static Harmony? _harmony;
        private static bool _applied;

        /// <summary>Reaction time multiplier when loadout not collected (1f = keep original).</summary>
        private const float DefaultReactionMultiplier = 1f;

        /// <summary>Reaction time correction strength (aggression * this value applied to reaction time).</summary>
        private const float ReactionAggressionStrength = 0.35f;

        /// <summary>combatMoveRange / forceTrace correction strength.</summary>
        private const float MoveAggressionStrength = 0.4f;

        /// <summary>Scatter (accuracy) correction strength. At low aggression (cautious) scatter multiplier decreases (accuracy up). agg * this becomes PercentageMultiply Value.</summary>
        private const float ScatterAggressionStrength = 0.25f;

        /// <summary>Fire rate correction strength. At high aggression fire interval·delay decrease (fire more). Time scale = 1 - agg * this.</summary>
        private const float FireRateAggressionStrength = 0.2f;

        /// <summary>Combat move time range correction. High aggression = larger range (move longer), low = smaller.</summary>
        private const float CombatMoveTimeRangeAggressionStrength = 0.25f;
        /// <summary>Combat aim turn speed correction. Increases at high aggression.</summary>
        private const float CombatTurnSpeedAggressionStrength = 0.2f;
        /// <summary>Scatter multiplier (target running / off-screen) correction. High aggression = lower TargetRunning, low = higher OffScreen.</summary>
        private const float ScatterMultiAggressionStrength = 0.15f;
        /// <summary>Trace chance correction. Increases at high aggression.</summary>
        private const float TraceTargetChanceAggressionStrength = 0.2f;
        /// <summary>Forget time correction. At low aggression increases (chase less long).</summary>
        private const float ForgetTimeAggressionStrength = 0.15f;
        /// <summary>Skill cooldown correction. At high aggression cooldown slightly reduced (use more often).</summary>
        private const float SkillCoolTimeRangeAggressionStrength = 0.15f;
        /// <summary>Item skill use chance correction. Slight increase at high aggression.</summary>
        private const float ItemSkillChanceAggressionStrength = 0.2f;
        /// <summary>Item skill cooldown correction. Slight decrease at high aggression (use more often).</summary>
        private const float ItemSkillCoolTimeAggressionStrength = 0.15f;

        /// <summary>Per-gun-type correction strength multiplier. null/Common=1f. Key: GunTypeTag, inner key: Strength key. Unset=1f.</summary>
        private static readonly Dictionary<string, Dictionary<string, float>> _gunTypeStrengthMultipliers = new Dictionary<string, Dictionary<string, float>>
        {
            { "Tag_GunType_PST", new Dictionary<string, float> { { "Move", 1.1f }, { "TraceTargetChance", 1.1f }, { "ForgetTime", 0.9f } } },
            { "Tag_GunType_SMG", new Dictionary<string, float> { { "Reaction", 1.1f }, { "Move", 1.15f }, { "Scatter", 0.9f }, { "FireRate", 1.15f }, { "TraceTargetChance", 1.1f }, { "ForgetTime", 0.9f } } },
            { "Tag_GunType_SNP", new Dictionary<string, float> { { "Reaction", 1.25f }, { "Move", 0.9f }, { "Scatter", 0.85f }, { "FireRate", 0.9f }, { "TraceTargetChance", 0.9f }, { "ForgetTime", 1.1f }, { "Dodge", 0.9f }, { "DodgeDistCloseRatio", 0.2f }, { "DodgeDistFarRatio", 0.5f }, { "DodgeDistMultAtClose", 1.3f }, { "DodgeDistMultAtFar", 0.7f } } },
            { "Tag_GunType_SHT", new Dictionary<string, float> { { "Reaction", 1.1f }, { "Move", 1.2f }, { "Scatter", 0.9f }, { "FireRate", 1.1f }, { "CombatTurnSpeed", 1.15f }, { "TraceTargetChance", 1.15f }, { "ForgetTime", 0.9f } } },
            { "Tag_GunType_MAG", new Dictionary<string, float> { { "Move", 1.1f }, { "FireRate", 1.05f }, { "TraceTargetChance", 1.05f } } },
            { "Tag_GunType_Rocket", new Dictionary<string, float> { { "Reaction", 1.2f }, { "Move", 0.95f }, { "Scatter", 0.8f }, { "TraceTargetChance", 0.95f }, { "ForgetTime", 1.05f }, { "Dodge", 1.25f } } },
            { "Melee", new Dictionary<string, float> { { "Reaction", 1.15f }, { "Move", 1.2f }, { "CombatTurnSpeed", 1.15f }, { "TraceTargetChance", 1.2f }, { "ForgetTime", 0.85f }, { "Dodge", 1.1f } } },
        };

        /// <summary>Apply correction strength by gun type. baseStrength * (per-type multiplier, or 1f if none) * GlobalAIScale.</summary>
        private static float GetEffectiveStrength(string? gunTypeTag, float baseStrength, string strengthKey)
        {
            float result = baseStrength;
            if (!string.IsNullOrEmpty(gunTypeTag) && _gunTypeStrengthMultipliers.TryGetValue(gunTypeTag, out var inner) && inner != null)
                result = baseStrength * (inner.TryGetValue(strengthKey, out var mult) ? mult : 1f);
            return result * AdaptiveAISettings.GlobalAIScale;
        }

        private static string? GetCurrentGunTypeTag() => PlayerLoadoutService.IsValid ? PlayerLoadoutService.Current.GunTypeTag : null;

        /// <summary>Dodge multiplier by distance. By distance/player range ratio: close (dodge up) / far (dodge down). Melee or range 0 = 1f.</summary>
        private static float GetDodgeDistanceMultiplier(global::AICharacterController c, string? gunTypeTag, float weaponRange)
        {
            if (!AdaptiveAISettings.DodgeDistanceCorrectionEnabled || c?.CharacterMainControl == null) return 1f;
            var main = CharacterMainControl.Main;
            if (main == null || weaponRange < 1f) return 1f;

            float dist = (c.transform.position - main.transform.position).magnitude;
            float ratio = dist / weaponRange;

            float closeRatio = AdaptiveAISettings.DodgeDistCloseRatioDefault;
            float farRatio = AdaptiveAISettings.DodgeDistFarRatioDefault;
            float multClose = AdaptiveAISettings.DodgeDistMultAtCloseDefault;
            float multFar = AdaptiveAISettings.DodgeDistMultAtFarDefault;
            if (!string.IsNullOrEmpty(gunTypeTag) && _gunTypeStrengthMultipliers.TryGetValue(gunTypeTag, out var inner) && inner != null)
            {
                if (inner.TryGetValue("DodgeDistCloseRatio", out var cr)) closeRatio = cr;
                if (inner.TryGetValue("DodgeDistFarRatio", out var fr)) farRatio = fr;
                if (inner.TryGetValue("DodgeDistMultAtClose", out var mc)) multClose = mc;
                if (inner.TryGetValue("DodgeDistMultAtFar", out var mf)) multFar = mf;
            }

            if (ratio <= closeRatio) return multClose;
            if (ratio >= farRatio) return multFar;
            float t = (ratio - closeRatio) / Mathf.Max(0.01f, farRatio - closeRatio);
            return Mathf.Lerp(multClose, multFar, t);
        }

        // LoadoutWeight is configured in AdaptiveAISettings.LoadoutWeight.

        /// <summary>Behavior learning reflection strength (for keeping lower bound).</summary>
        private const float BehaviorBlendStrength = 0.35f;

        /// <summary>For testing: if non-zero, force this value as aggression (F10=0.6, F11=-0.6, F12=clear).</summary>
        internal static float DebugAggressionOverride;

        /// <summary>If true, output debug log for projectile dodge/dash logic (toggleable in debug overlay).</summary>
        internal static bool DebugLogIncomingDodge = false;

        /// <summary>Per-instance Preset original value cache (stored once after Init, used for correction).</summary>
        private static ConditionalWeakTable<global::AICharacterController, BaseValues> _baseValues = new ConditionalWeakTable<global::AICharacterController, BaseValues>();
        /// <summary>Last approach dash attempt time (Time.time). BT may not call Dash(), so mod checks cooldown when trying periodically.</summary>
        private static ConditionalWeakTable<global::AICharacterController, ApproachDashTimeHolder> _lastApproachDashTime = new ConditionalWeakTable<global::AICharacterController, ApproachDashTimeHolder>();
        private sealed class ApproachDashTimeHolder { internal float Value = -999f; }
        /// <summary>Last dash (dodge/approach/tactical) time. For tactical dash cooldown check.</summary>
        private static ConditionalWeakTable<global::AICharacterController, ApproachDashTimeHolder> _lastAnyDodgeTime = new ConditionalWeakTable<global::AICharacterController, ApproachDashTimeHolder>();

        /// <summary>Per-AI aggro order list. Order changes only on hit (OnHurt); then that attacker is moved to front (priority 1).</summary>
        private static ConditionalWeakTable<global::AICharacterController, AggroListHolder> _aggroListByAi = new ConditionalWeakTable<global::AICharacterController, AggroListHolder>();
        private const int AggroListMaxCount = 24;
        private sealed class AggroListHolder
        {
            internal readonly List<DamageReceiver> List = new List<DamageReceiver>(AggroListMaxCount);
        }

        /// <summary>Whether DamageReceiver is valid and alive. false if null, destroyed, or dead.</summary>
        internal static bool IsDamageReceiverAlive(DamageReceiver dr)
        {
            if (dr == null || (dr as UnityEngine.Object) == null) return false;
            return !dr.IsDead;
        }

        /// <summary>Aggro order changes only on hit. Only the attacker is moved to front (priority 1). Called from OnHurt only.</summary>
        internal static void AddToAggroList(global::AICharacterController ai, DamageReceiver receiver)
        {
            if (ai == null || receiver == null) return;
            var aggroHolder = _aggroListByAi.GetOrCreateValue(ai);
            var list = aggroHolder.List;
            list.Remove(receiver);
            list.Insert(0, receiver);
            while (list.Count > AggroListMaxCount)
                list.RemoveAt(list.Count - 1);
        }

        /// <summary>Remove dead/invalid from aggro list, return first valid enemy. null if none.</summary>
        internal static DamageReceiver GetNextAggroTarget(global::AICharacterController ai)
        {
            if (ai == null || !_aggroListByAi.TryGetValue(ai, out var aggroHolder)) return null;
            var list = aggroHolder.List;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!IsDamageReceiverAlive(list[i]))
                    list.RemoveAt(i);
            }
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>For post-dash pause pattern: last dash start time (Time.time). -999f if no dash yet.</summary>
        internal static float GetLastAnyDodgeTime(global::AICharacterController c)
        {
            if (c == null) return -999f;
            var h = _lastAnyDodgeTime.GetOrCreateValue(c);
            return h.Value;
        }
        /// <summary>Issue3: last time noticed==true (Time.time). Used to allow search grace after combat when retreating.</summary>
        private static ConditionalWeakTable<global::AICharacterController, LastNoticedTimeHolder> _lastNoticedTimeByAi = new ConditionalWeakTable<global::AICharacterController, LastNoticedTimeHolder>();
        private sealed class LastNoticedTimeHolder { internal float Value = -999f; }

        /// <summary>Used for approach/charge dash cooldown multiplier. If |aggression| ≤ this, cooldown 1x.</summary>
        private const float DashNeutralZone = 0.1f;
        /// <summary>Dash cooldown multiplier lower bound at extreme aggression (0.9). Lower = dash more often.</summary>
        private const float DashCoolMultAtExtreme = 0.75f;
        /// <summary>Approach dash attempt chance (per 1s tick). BT may not call Dash(), so mod tries directly.</summary>
        private const float ApproachDashTryChance = 0.35f;

        // IncomingDodgeAggressionThreshold → AdaptiveAISettings (for probability curve, no skip by absolute value)
        // IncomingDodgeChanceMin/Max → moved to AdaptiveAISettings.IncomingDodgeChanceMin/Max
        // Dodge cooldown 4~3s and judgment correction → AdaptiveAISettings.IncomingDodgeCooldownMax/Min and GetEffectiveDodgeCooldownFor
        /// <summary>Projectile dodge dash chance when reloading. Movement tries to dodge as much as possible.</summary>
        private const float IncomingDodgeChanceWhenReloading = 0.72f;
        /// <summary>If this many dodge dashes occur within this time (seconds), treat as "burst" and apply recovery cooldown.</summary>
        private const float IncomingDodgeBurstWindow = 4f;
        private const int IncomingDodgeBurstCount = 2;
        /// <summary>Recovery cooldown (seconds) after burst before next dodge dash. 2~4s range.</summary>
        private const float IncomingDodgeRecoveryCooldown = 3f;
        /// <summary>Minimum dot between projectile direction and AI direction. Uses setting (default 0.7 = mainly hit trajectory).</summary>
        private static float GetIncomingDodgeDotMin() => Mathf.Clamp(AdaptiveAISettings.IncomingDodgeDotMin, 0.3f, 1f);

        // Per-entity offset: AdaptiveAISettings.PerEntityAggressionOffsetMin/Max. Aggression 0~2 scale (1=aggressive, 2=cap).
        /// <summary>Final aggression clamp max. 0~2 scale (1=aggressive; &gt;1 applies stronger than before).</summary>
        private const float AggressionClampMax = 2f;
        /// <summary>Final aggression clamp min. 0~1 scale (0=cautious).</summary>
        private const float AggressionClampMin = 0f;
        /// <summary>Aggression lower bound when holding melee weapon. Same in 0~1.</summary>
        private const float MeleeAggressionMin = 0f;

        /// <summary>Modifier source for scatter correction (for RemoveAllModifiersFromSource).</summary>
        internal static readonly object ScatterModifierSource = new object();

        /// <summary>Per-instance scatter correction Modifier we added (update Value only).</summary>
        private static ConditionalWeakTable<global::AICharacterController, Modifier> _scatterModifiers = new ConditionalWeakTable<global::AICharacterController, Modifier>();
        /// <summary>Per-CharacterItem GunScatterMultiplier Stat cache. Reduces GetStat from N per frame to 1 per item.</summary>
        private static ConditionalWeakTable<object, object> _scatterStatCache = new ConditionalWeakTable<object, object>();
        /// <summary>Last projectile dodge dash attempt time (Time.time). For burst limit, time of previous attempt.</summary>
        private static ConditionalWeakTable<global::AICharacterController, LastIncomingDodgeTime> _lastIncomingDodgeTime = new ConditionalWeakTable<global::AICharacterController, LastIncomingDodgeTime>();

        private sealed class LastIncomingDodgeTime
        {
            public float Time;
            /// <summary>Time of the dodge dash just before that. 0 if not recorded.</summary>
            public float TimePrev;
        }
        /// <summary>For dodge reaction delay: when unnoticed = first threat detection time (Time), when noticed = TimeNoticed. Reset that side to 0 on state change.</summary>
        private static ConditionalWeakTable<global::AICharacterController, FirstThreatTimeState> _firstThreatTimeState = new ConditionalWeakTable<global::AICharacterController, FirstThreatTimeState>();
        private sealed class FirstThreatTimeState
        {
            public float Time;       // When unnoticed: time threat was first detected
            public float TimeNoticed; // When noticed: time threat was first detected
            /// <summary>When noticed: reaction delay start time (set once, for display/judgment match). 0 if not set.</summary>
            public float ReactionDelayStartWhenNoticed;
            /// <summary>Whether first-threat dodge scale was applied this engagement. true after first apply; reset when noticed→false.</summary>
            public bool FirstThreatDodgeScaled;
        }

        // ---- Cover exit aim delay: track cover→exposed transition per AI ----
        private static ConditionalWeakTable<global::AICharacterController, CoverExitState> _coverExitState = new ConditionalWeakTable<global::AICharacterController, CoverExitState>();
        private sealed class CoverExitState
        {
            /// <summary>Whether AI was in cover last tick.</summary>
            public bool WasInCover;
            /// <summary>Time.time when AI last left cover (hasObsticleToTarget went true→false).</summary>
            public float LastCoverExitTime;
        }

        private sealed class BaseValues
        {
            public float BaseReactionTime;
            public float CombatMoveRange;
            public float ForceTracePlayerDistance;
            public float BaseSightDistance;
            public float BaseSightAngle;
            public bool BaseCanDash;
            public Vector2 BaseDashCoolTimeRange;
            public float ShootDelay;
            public Vector2 ShootTimeRange;
            public Vector2 ShootTimeSpaceRange;
            public Vector2 CombatMoveTimeRange;
            public float CombatTurnSpeed;
            public float ScatterMultiIfTargetRunning;
            public float ScatterMultiIfOffScreen;
            public float TraceTargetChance;
            public float ForgetTime;
            public Vector2 SkillCoolTimeRange;
            public float ItemSkillChance;
            public float ItemSkillCoolTime;
            public bool Set;
            /// <summary>This enemy's aggression offset (−0.3~+0.3). Fixed once then kept.</summary>
            public float AggressionOffset;
            public bool AggressionOffsetInitialized;
            /// <summary>Aggression applied once on spawn (behavior pattern only). Reused for projectile dodge, reload block, etc.</summary>
            public float CachedAggression;
            /// <summary>Tactical factor relative to weapon/range. Used for canDash, combat move range, etc.</summary>
            public float CachedLoadoutRangeFactor;
            /// <summary>Whether parameter correction was already applied on spawn.</summary>
            public bool AggressionAppliedAtSpawn;
        }

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                // Attack-related Harmony patches (Attack/StartAction/CA_Attack etc.) were removed. PatchAll only registers currently applied patches.
                _harmony.PatchAll(typeof(AICharacterControllerPatches).Assembly);
                _applied = true;
            }
            catch (System.Exception)
            {
                // Ignore patch errors in release.
            }
        }

        public static void RemovePatches()
        {
            if (!_applied || _harmony == null) return;
            try
            {
                _harmony.UnpatchAll(HarmonyId);
                _applied = false;
            }
            catch (System.Exception)
            {
                // Ignore unpatch errors in release.
            }
        }

        /// <summary>Computed once per frame and reused (only when no enemy context).</summary>
        private static float _cachedBaseAggression;
        private static int _cachedBaseAggressionFrame = -1;

        /// <summary>Base aggression from player loadout+behavior (no per-entity offset). If c is null: absolute value + cache (debug). If c present: relative to that enemy.</summary>
        private static float GetBaseAggressionFor(global::AICharacterController? c)
        {
#if DEBUG
            if (DebugAggressionOverride != 0f)
                return DebugAggressionOverride;
#endif
            float loadoutAgg;
            if (c == null)
            {
                int frame = Time.frameCount;
                if (frame == _cachedBaseAggressionFrame)
                    return _cachedBaseAggression;
                loadoutAgg = PlayerLoadoutService.IsValid ? PlayerLoadoutService.Current.GetAggressionFactor() : 0f;
            }
            else
            {
                var enemy = GetEnemyLoadout(c);
                loadoutAgg = PlayerLoadoutService.IsValid
                    ? PlayerLoadoutService.Current.GetAggressionFactorRelativeTo(enemy.WeaponRange, enemy.IsMelee, enemy.TotalArmor, enemy.GunTypeTag)
                    : 0f;
                // Per-weapon preferred range correction: inside preferred band = aggression +, far = - (encourage approach)
                var main = CharacterMainControl.Main;
                if (main != null && c.CharacterMainControl != null)
                {
                    Vector3 toPlayer = main.transform.position - c.CharacterMainControl.transform.position;
                    toPlayer.y = 0f;
                    float dist = toPlayer.magnitude;
                    float distMod = WeaponPreferredRange.GetDistanceAggressionModifier(enemy.GunTypeTag, dist);
                    float baseRange = AdaptiveAISettings.BaseAggressionRange;
                    loadoutAgg = Mathf.Clamp(loadoutAgg + distMod, -baseRange, baseRange);
                }
            }

            // When player advantage: raise more; when disadvantage: raise moderately; both contribute positively to judgment
            float loadoutForBlend = loadoutAgg;
            float range = AdaptiveAISettings.BaseAggressionRange;
            if (c != null && AdaptiveAISettings.JudgmentRisesWhenPlayerAdvantage)
            {
                if (loadoutAgg < 0f)
                    loadoutForBlend = Mathf.Min(-loadoutAgg * Mathf.Max(1f, AdaptiveAISettings.PlayerAdvantageJudgmentMultiplier), range);
                else if (loadoutAgg > 0f)
                    loadoutForBlend = Mathf.Min(loadoutAgg * Mathf.Clamp01(AdaptiveAISettings.PlayerDisadvantageJudgmentScale), range);
                else
                    loadoutForBlend = 0f;
            }

            float behaviorAgg = 0f;
            var behaviorSummary = PlayerBehaviorCollector.GetCurrentSummary();
            if (behaviorSummary != null && behaviorSummary.AimSampleCount >= 5)
                behaviorAgg = behaviorSummary.GetBehaviorAggressionFactor();
            float w = AdaptiveAISettings.LoadoutWeight;
            float result = loadoutForBlend * w + behaviorAgg * (1f - w);

            // Reduce clustering near 0: remap exponent (<1) pushes small absolute values outward
            float exp = Mathf.Clamp(AdaptiveAISettings.AggressionRemapExponent, 0.2f, 2f);
            if (exp != 1f && range > 0.001f)
            {
                float t = Mathf.Clamp01(Mathf.Abs(result) / range);
                float tNew = Mathf.Pow(t, exp);
                result = Mathf.Sign(result) * tNew * range;
            }

            // Minimum absolute value: remove excessive neutrality band (optional)
            float minAbs = AdaptiveAISettings.MinAbsAggression;
            if (minAbs > 0f && Mathf.Abs(result) < minAbs && result != 0f)
                result = Mathf.Sign(result) * minAbs;

            if (c == null)
            {
                _cachedBaseAggressionFrame = Time.frameCount;
                _cachedBaseAggression = result;
            }
            return result;
        }

        /// <summary>Base aggression 0~1 from behavior pattern only (no loadout/range). After remap and min-absolute, converted to 0~1.</summary>
        private static float GetBehaviorBaseAggressionFor(global::AICharacterController? c)
        {
#if DEBUG
            if (DebugAggressionOverride != 0f)
            {
                if (DebugAggressionOverride >= 0f && DebugAggressionOverride <= 1f)
                    return Mathf.Clamp01(DebugAggressionOverride);
                return Mathf.Clamp01((DebugAggressionOverride + 1f) * 0.5f);
            }
#endif
            float behaviorAgg = 0f;
            var behaviorSummary = PlayerBehaviorCollector.GetCurrentSummary();
            if (behaviorSummary != null && behaviorSummary.AimSampleCount >= 5)
                behaviorAgg = behaviorSummary.GetBehaviorAggressionFactor();
            float range = AdaptiveAISettings.BehaviorAggressionRange;
            if (range < 0.001f) range = 0.5f;
            float result = Mathf.Clamp(behaviorAgg, -range, range);

            float exp = Mathf.Clamp(AdaptiveAISettings.AggressionRemapExponent, 0.2f, 2f);
            if (exp != 1f && range > 0.001f)
            {
                float t = Mathf.Clamp01(Mathf.Abs(result) / range);
                float tNew = Mathf.Pow(t, exp);
                result = Mathf.Sign(result) * tNew * range;
            }
            float minAbs = AdaptiveAISettings.MinAbsAggression;
            if (minAbs > 0f && Mathf.Abs(result) < minAbs && result != 0f)
                result = Mathf.Sign(result) * minAbs;
            // -range~+range → 0~1 (0=cautious, 1=aggressive)
            return Mathf.Clamp01((result + range) / (2f * range));
        }

        /// <summary>Tactical factor from weapon/range matchup only (no behavior pattern). For combatMoveRange, forceTrace, canDash.</summary>
        private static float GetLoadoutRangeFactor(global::AICharacterController c)
        {
            if (c == null) return 0f;
            var enemy = GetEnemyLoadout(c);
            float loadoutAgg = PlayerLoadoutService.IsValid
                ? PlayerLoadoutService.Current.GetAggressionFactorRelativeTo(enemy.WeaponRange, enemy.IsMelee, enemy.TotalArmor, enemy.GunTypeTag)
                : 0f;
            var main = CharacterMainControl.Main;
            if (main != null && c.CharacterMainControl != null)
            {
                Vector3 toPlayer = main.transform.position - c.CharacterMainControl.transform.position;
                toPlayer.y = 0f;
                float dist = toPlayer.magnitude;
                float distMod = WeaponPreferredRange.GetDistanceAggressionModifier(enemy.GunTypeTag, dist);
                float baseRange = AdaptiveAISettings.BaseAggressionRange;
                loadoutAgg = Mathf.Clamp(loadoutAgg + distMod, -baseRange, baseRange);
            }
            return loadoutAgg;
        }

        /// <summary>Aggression 0~1 from behavior pattern only (no loadout/range). Used for reaction time, scatter, dodge chance, etc.</summary>
        internal static float GetBehaviorAggressionFor(global::AICharacterController c)
        {
            var baseVal = _baseValues.GetOrCreateValue(c);
            if (!baseVal.AggressionOffsetInitialized)
            {
                float offsetMin = AdaptiveAISettings.PerEntityAggressionOffsetMin;
                float offsetMax = AdaptiveAISettings.PerEntityAggressionOffsetMax;
                baseVal.AggressionOffset = UnityEngine.Random.Range(offsetMin, offsetMax);
                baseVal.AggressionOffsetInitialized = true;
            }
            float baseAgg = GetBehaviorBaseAggressionFor(c);
            // Offset reflects old -0.3~0.3 range as 0~1 scale (×0.5)
            float agg = Mathf.Clamp(baseAgg + baseVal.AggressionOffset * 0.5f, AggressionClampMin, AggressionClampMax);
            if (EnemyHasMeleeWeapon(c))
                agg = Mathf.Clamp(agg, MeleeAggressionMin, AggressionClampMax);
            return agg;
        }

        /// <summary>Whether this enemy is holding a melee weapon. True if GetMeleeWeapon() exists or no gun and preset is melee type (wolf/animal/Scav_Melee etc.).</summary>
        private static bool EnemyHasMeleeWeapon(global::AICharacterController c)
        {
            var main = c?.CharacterMainControl;
            bool hasMelee = main != null && (main.GetMeleeWeapon() != null || (main.GetGun() == null && IsMeleePreset(main)));
            if (AdaptiveAISettings.DebugLogMeleeWeapon && main != null && c != null)
            {
                if (!_meleeDebugLogThrottle.TryGetValue(main, out float last) || Time.time - last >= 2f)
                {
                    _meleeDebugLogThrottle[main] = Time.time;
                    var gun = main.GetGun();
                    var melee = main.GetMeleeWeapon();
                    Debug.Log($"[AdaptiveEnemyAI] EnemyHasMeleeWeapon: AI={c.GetInstanceID()} preset=\"{main.characterPreset?.name ?? "null"}\" GetMeleeWeapon()={((object)melee) != null} GetGun()={((object)gun) != null} CurrentHold={main.CurrentHoldItemAgent?.name ?? "null"} → hasMelee={hasMelee}");
                }
            }
            return hasMelee;
        }

        internal static readonly Dictionary<CharacterMainControl, float> _meleeDebugLogThrottle = new Dictionary<CharacterMainControl, float>();

        /// <summary>True if boss or elite preset. Used for 4s dodge cooldown. (Includes in-game spelling "Elete")</summary>
        private static bool IsBossOrElitePreset(CharacterMainControl? cmc)
        {
            string? name = cmc?.characterPreset?.name;
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Boss", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Elete", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("LittleBoss", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Whether preset name indicates melee type (wolf, animal, Scav_Melee etc.). Used to identify enemies that attack with natural melee (bite/claw) even when GetMeleeWeapon() is null.</summary>
        internal static bool IsMeleePreset(CharacterMainControl? main)
        {
            if (main == null) return false;
            string? name = main.characterPreset?.name;
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Wolf", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Animal_", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("_Melee", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.Equals("EnemyPreset_Scav_Melee", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Summary of this enemy (AI) loadout and armor. For matchup, movement correction, weapon matchup.</summary>
        internal readonly struct EnemyLoadoutSnapshot
        {
            public readonly float WeaponRange;
            public readonly bool IsMelee;
            public readonly float TotalArmor;
            /// <summary>GunType tag of weapon enemy holds. "Melee" for melee; null if none or unknown.</summary>
            public readonly string? GunTypeTag;

            public EnemyLoadoutSnapshot(float weaponRange, bool isMelee, float totalArmor, string? gunTypeTag = null)
            {
                WeaponRange = weaponRange;
                IsMelee = isMelee;
                TotalArmor = totalArmor;
                GunTypeTag = gunTypeTag;
            }
        }

        /// <summary>Finds AICharacterController linked to CharacterMainControl. Checks self, parent, and children (some presets attach AI only on child).</summary>
        internal static global::AICharacterController GetAIForCharacter(CharacterMainControl c)
        {
            if (c == null) return null;
            var ai = c.GetComponent<global::AICharacterController>();
            if (ai != null) return ai;
            ai = c.GetComponentInParent<global::AICharacterController>();
            if (ai != null) return ai;
            return c.GetComponentInChildren<global::AICharacterController>();
        }

        /// <summary>True only when damage source is a real character (AI or player). Prevents aggro from proxy/dummy fromCharacter (explosion, trap, etc.) causing attacks at nothing.</summary>
        internal static bool IsValidAggroDamageSource(CharacterMainControl fromCharacter)
        {
            if (fromCharacter == null) return false;
            if (fromCharacter == CharacterMainControl.Main) return true;
            return GetAIForCharacter(fromCharacter) != null;
        }

        internal static EnemyLoadoutSnapshot GetEnemyLoadout(global::AICharacterController c)
        {
            var main = c?.CharacterMainControl;
            if (main == null)
                return new EnemyLoadoutSnapshot(0f, false, 0f, null);

            float weaponRange = 0f;
            bool isMelee = false;
            string? gunTypeTag = null;
            var gun = main.GetGun();
            if (gun != null)
            {
                weaponRange = gun.BulletDistance;
                isMelee = false;
                gunTypeTag = PlayerLoadoutService.GetGunTypeTagFromGun(gun);
            }
            else if (main.GetMeleeWeapon() != null || IsMeleePreset(main))
            {
                weaponRange = 0f;
                isMelee = true;
                gunTypeTag = "Melee";
            }
            // 터렛: GetGun()이 null이거나 사거리가 0이면 공격/사격 판정이 거의 불가능해져서 매우 가까워야만 공격함. 인지와 동일한 폴백 사거리 적용.
            if (CharacterMainControlDashPatches.IsDashBlockedForPreset(main) && weaponRange < 1f)
            {
                float fallback = AdaptiveAISettings.TurretNoticeMinRangeFallback > 0f ? AdaptiveAISettings.TurretNoticeMinRangeFallback : 18f;
                weaponRange = fallback;
            }

            float totalArmor = 0f;
            if (main.Health != null)
                totalArmor = main.Health.BodyArmor + main.Health.HeadArmor;

            return new EnemyLoadoutSnapshot(weaponRange, isMelee, totalArmor, gunTypeTag);
        }

        /// <summary>Final aggression 0~1 to apply to this enemy (loadout+behavior blend, for debug). GetBehaviorAggressionFor returns behavior-only 0~1.</summary>
        internal static float GetEffectiveAggressionFor(global::AICharacterController c)
        {
            var baseVal = _baseValues.GetOrCreateValue(c);
            if (!baseVal.AggressionOffsetInitialized)
            {
                float offsetMin = AdaptiveAISettings.PerEntityAggressionOffsetMin;
                float offsetMax = AdaptiveAISettings.PerEntityAggressionOffsetMax;
                baseVal.AggressionOffset = UnityEngine.Random.Range(offsetMin, offsetMax);
                baseVal.AggressionOffsetInitialized = true;
            }
            float baseAgg = GetBaseAggressionFor(c);
            float range = AdaptiveAISettings.BaseAggressionRange;
            float baseAgg01 = Mathf.Clamp01((baseAgg + range) / (2f * range));
            float agg = Mathf.Clamp(baseAgg01 + baseVal.AggressionOffset * 0.5f, AggressionClampMin, AggressionClampMax);
            if (EnemyHasMeleeWeapon(c))
                agg = Mathf.Clamp(agg, MeleeAggressionMin, AggressionClampMax);
            return agg;
        }

        /// <summary>For debug overlay: base aggression 0~1 from player perspective (no per-entity offset).</summary>
        internal static float GetDebugEffectiveAggression()
        {
            float raw = GetBaseAggressionFor(null);
            float range = AdaptiveAISettings.BaseAggressionRange;
            return Mathf.Clamp01((raw + range) / (2f * range));
        }

        /// <summary>Reaction time from loadout+behavior profile. By aggression 0~1; higher = faster reaction. If LoadoutAffectsBehaviorParams, uses cached loadout+behavior blend.</summary>
        internal static float GetReactionTimeWithMultiplier(global::AICharacterController c, float? aggression = null)
        {
            if (c == null) return 0.2f;
            float baseRt = c.baseReactionTime;
            bool atNight = TimeOfDayController.Instance != null && TimeOfDayController.Instance.AtNight;
            bool isInDoor = MultiSceneCore.Instance != null && MultiSceneCore.Instance.GetSubSceneInfo().IsInDoor;
            float nightFactor = (atNight && !isInDoor) ? c.nightReactionTimeFactor : 1f;
            float agg = aggression ?? GetCachedAggressionFor(c);
            float strength = GetEffectiveStrength(GetCurrentGunTypeTag(), ReactionAggressionStrength, "Reaction");
            float mult = Mathf.Clamp(1f - (agg - 0.5f) * strength * 2f, 0.6f, 1.4f);
            float result = baseRt * nightFactor * mult;

            // Feature 5: Multi-axis reaction time override (Reactivity axis)
            if (AdaptiveAISettings.MultiAxisEnabled)
            {
                var multiAxis = PlayerBehaviorCollector.GetMultiAxisProfile();
                if (multiAxis != null && multiAxis.IsValid)
                {
                    float axisReactionMult = multiAxis.GetReactionTimeMult();
                    float s = Mathf.Clamp01(AdaptiveAISettings.MultiAxisOverrideStrength);
                    result *= Mathf.Lerp(1f, axisReactionMult, s);
                }
            }

            // Feature 3: Short-term reactivity boost (faster reaction during behavior spikes)
            if (AdaptiveAISettings.ShortTermPatternEnabled)
            {
                var detector = PlayerBehaviorCollector.GetShortTermDetector();
                if (detector != null)
                {
                    float boost = Mathf.Clamp(detector.ReactivityBoost, 0f, AdaptiveAISettings.ShortTermReactivityBoostCap);
                    result *= Mathf.Max(0.5f, 1f - boost);
                }
            }

            // Global floor: prevent unrealistically fast reaction from multiplicative stacking
            result = Mathf.Max(result, AdaptiveAISettings.ReactionTimeGlobalFloor);

            return result;
        }

        /// <summary>After per-instance original cache, apply behavior aggression and tactical (range/weapon) separately. If aggression provided, skip recalculating behavior aggression.</summary>
        internal static void ApplyLoadoutToMoveAndTrace(global::AICharacterController c, float? aggression = null)
        {
            if (c == null) return;
            // Do not apply adaptive params to player team (companion, horse, etc.); keep original behavior (e.g. follow)
            if (c.CharacterMainControl != null && c.CharacterMainControl.Team == Teams.player)
                return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c.CharacterMainControl))
                return;
            // Melee original behavior: do not modify any field for melee AI (combatMoveRange, forceTrace, canDash etc. all stay preset).
            if (AdaptiveAISettings.MeleeUseOriginalBehavior && EnemyHasMeleeWeapon(c))
                return;
            // Non-aggressive (forceTrace≤0.5): prevent combatMoveRange etc. from being overwritten when mob only registered by spawn patch gets ApplyLoadoutToMoveAndTrace on tick. Fixes follow bug from CharacterSpawnerRoot_AddCreatedCharacter_Patch.
            if (c.forceTracePlayerDistance <= NonAggroForceTraceThreshold)
                return;
            var baseVal = _baseValues.GetOrCreateValue(c);
            if (!baseVal.Set)
            {
                baseVal.BaseReactionTime = c.baseReactionTime;
                baseVal.CombatMoveRange = c.combatMoveRange;
                // Non-aggressive (forceTrace≤0.5): fix cache to 0. Prevents follow bug when default (positive) is read before preset and then scaled.
                baseVal.ForceTracePlayerDistance = c.forceTracePlayerDistance <= NonAggroForceTraceThreshold ? 0f : c.forceTracePlayerDistance;
                baseVal.BaseCanDash = c.canDash;
                baseVal.BaseDashCoolTimeRange = c.dashCoolTimeRange;
                baseVal.ShootDelay = c.shootDelay;
                baseVal.ShootTimeRange = c.shootTimeRange;
                baseVal.ShootTimeSpaceRange = c.shootTimeSpaceRange;
                baseVal.CombatMoveTimeRange = c.combatMoveTimeRange;
                baseVal.CombatTurnSpeed = c.combatTurnSpeed;
                baseVal.ScatterMultiIfTargetRunning = c.scatterMultiIfTargetRunning;
                baseVal.ScatterMultiIfOffScreen = c.scatterMultiIfOffScreen;
                baseVal.TraceTargetChance = c.traceTargetChance;
                baseVal.ForgetTime = c.forgetTime;
                baseVal.BaseSightDistance = c.sightDistance;
                baseVal.BaseSightAngle = c.sightAngle;
                baseVal.SkillCoolTimeRange = c.skillCoolTimeRange;
                baseVal.ItemSkillChance = c.itemSkillChance;
                baseVal.ItemSkillCoolTime = c.itemSkillCoolTime;
                baseVal.Set = true;
            }
            // Sight distance and angle correction: apply scale and minimum so enemy has cone/range similar to player
            float sightDist = baseVal.BaseSightDistance * Mathf.Max(0.5f, AdaptiveAISettings.SightDistanceMultiplier);
            if (AdaptiveAISettings.SightDistanceMinimumM > 0f && sightDist < AdaptiveAISettings.SightDistanceMinimumM)
                sightDist = AdaptiveAISettings.SightDistanceMinimumM;
            c.sightDistance = Mathf.Max(5f, sightDist);
            float sightAng = baseVal.BaseSightAngle * Mathf.Max(0.5f, AdaptiveAISettings.SightAngleMultiplier);
            if (AdaptiveAISettings.SightAngleMinimumDeg > 0f && sightAng < AdaptiveAISettings.SightAngleMinimumDeg)
                sightAng = AdaptiveAISettings.SightAngleMinimumDeg;
            c.sightAngle = Mathf.Clamp(sightAng, 30f, 360f);
            // Aggression for behavior: if LoadoutAffectsBehaviorParams use loadout+behavior blend, else behavior only. Used for reaction, scatter, fire rate, dodge, etc.
            float aggForBehavior = aggression ?? (AdaptiveAISettings.LoadoutAffectsBehaviorParams ? GetEffectiveAggressionFor(c) : GetBehaviorAggressionFor(c));
            // Map familiarity: apply offset so AI follows player play style (behavior profile), not just raising aggression. Aggressive player → AI more aggressive; defensive player → AI more cautious.
            if (AdaptiveAISettings.MapFamiliarityEnabled)
            {
                try
                {
                    string mapId = SceneManager.GetActiveScene().name;
                    float familiarity = PlayerBehaviorCollector.GetMapFamiliarity(mapId);
                    var summary = PlayerBehaviorCollector.GetCurrentSummary();
                    float behaviorFactor = (summary != null && summary.AimSampleCount >= 5) ? summary.GetBehaviorAggressionFactor() : 0f;
                    float range = Mathf.Max(0.01f, AdaptiveAISettings.BehaviorAggressionRange);
                    float normalizedFactor = Mathf.Clamp(behaviorFactor / range, -1f, 1f);
                    float styleOffset = familiarity * AdaptiveAISettings.MapFamiliarityStrength * normalizedFactor * AdaptiveAISettings.MapFamiliarityOffsetCap;
                    styleOffset = Mathf.Clamp(styleOffset, -AdaptiveAISettings.MapFamiliarityOffsetCap, AdaptiveAISettings.MapFamiliarityOffsetCap);
                    aggForBehavior = Mathf.Clamp(aggForBehavior + styleOffset, AggressionClampMin, AggressionClampMax);
                }
                catch { /* Keep original on scene/map familiarity or behavior profile lookup failure */ }
            }
            float loadoutFactor = GetLoadoutRangeFactor(c);
            baseVal.CachedAggression = aggForBehavior;
            baseVal.CachedLoadoutRangeFactor = loadoutFactor;

            string? gunTag = GetCurrentGunTypeTag();
            float moveStrength = GetEffectiveStrength(gunTag, MoveAggressionStrength, "Move");
            float moveMult = Mathf.Clamp(1f + loadoutFactor * moveStrength, 0.5f, 1.5f);
            c.combatMoveRange = Mathf.Max(0.1f, baseVal.CombatMoveRange * moveMult);
            // Non-aggressive (current forceTrace≤0.5): keep 0 so we do not overwrite with our cached default when preset sets it to 0 later.
            if (c.forceTracePlayerDistance <= NonAggroForceTraceThreshold)
                c.forceTracePlayerDistance = 0f;
            else
            {
                float traceDist = baseVal.ForceTracePlayerDistance * moveMult;
                c.forceTracePlayerDistance = baseVal.ForceTracePlayerDistance > 0.5f
                    ? Mathf.Max(0.51f, traceDist)
                    : Mathf.Max(0f, traceDist);
            }

            // canDash: only turret false; rest always true (no toggling).
            bool blockedByPreset = CharacterMainControlDashPatches.IsDashBlockedForPreset(c.CharacterMainControl);
            c.canDash = !blockedByPreset;
            float distToPlayer = -1f;
            if (global::CharacterMainControl.Main != null)
            {
                float distSq = (c.transform.position - global::CharacterMainControl.Main.transform.position).sqrMagnitude;
                distToPlayer = Mathf.Sqrt(distSq);
            }
            if (AdaptiveAISettings.DebugLogDash && blockedByPreset)
                Debug.Log($"[AdaptiveEnemyAI] canDash=false(ApplyLoadout): AI={c.GetInstanceID()} blockedByPreset turret preset=\"{c.CharacterMainControl?.characterPreset?.name ?? "null"}\"");

            float coolMult = 1f;
            if (c.canDash)
            {
                float t = Mathf.Clamp01((aggForBehavior - DashNeutralZone) / (AggressionClampMax - DashNeutralZone));
                coolMult = Mathf.Lerp(1f, DashCoolMultAtExtreme, t);
            }
            c.dashCoolTimeRange = new Vector2(
                baseVal.BaseDashCoolTimeRange.x * coolMult,
                baseVal.BaseDashCoolTimeRange.y * coolMult);

            // Behavior aggression 0~1 (when loadout reflected, blend by LoadoutWeight): scatter, fire rate, combat move time, aim turn, trace/forget time, skill/item cooldown (0.5=neutral)
            ApplyScatterFromAggression(c, aggForBehavior, gunTag);
            ApplyFireRateFromAggression(c, aggForBehavior, baseVal, gunTag);

            float moveTimeStrength = GetEffectiveStrength(gunTag, CombatMoveTimeRangeAggressionStrength, "CombatMoveTimeRange");
            float moveTimeMult = Mathf.Clamp(1f + (aggForBehavior - 0.5f) * 2f * moveTimeStrength, 0.5f, 1.5f);
            c.combatMoveTimeRange = new Vector2(
                Mathf.Max(0.2f, baseVal.CombatMoveTimeRange.x * moveTimeMult),
                Mathf.Max(0.5f, baseVal.CombatMoveTimeRange.y * moveTimeMult));

            float turnStrength = GetEffectiveStrength(gunTag, CombatTurnSpeedAggressionStrength, "CombatTurnSpeed");
            float turnMult = Mathf.Clamp(1f + (aggForBehavior - 0.5f) * 2f * turnStrength, 0.7f, 1.3f);
            c.combatTurnSpeed = Mathf.Max(50f, baseVal.CombatTurnSpeed * turnMult);

            float scatterMultiStrength = GetEffectiveStrength(gunTag, ScatterMultiAggressionStrength, "ScatterMulti");
            float scatterMult = Mathf.Clamp(1f - (aggForBehavior - 0.5f) * 2f * scatterMultiStrength, 0.6f, 1.4f);
            c.scatterMultiIfTargetRunning = Mathf.Max(0.5f, baseVal.ScatterMultiIfTargetRunning * scatterMult);
            c.scatterMultiIfOffScreen = Mathf.Max(0.5f, baseVal.ScatterMultiIfOffScreen * scatterMult);

            float traceStrength = GetEffectiveStrength(gunTag, TraceTargetChanceAggressionStrength, "TraceTargetChance");
            float traceMult = Mathf.Clamp(1f + (aggForBehavior - 0.5f) * 2f * traceStrength, 0.5f, 1.5f);
            c.traceTargetChance = Mathf.Clamp01(baseVal.TraceTargetChance * traceMult);

            float forgetStrength = GetEffectiveStrength(gunTag, ForgetTimeAggressionStrength, "ForgetTime");
            float forgetMult = Mathf.Clamp(1f - (aggForBehavior - 0.5f) * 2f * forgetStrength, 0.7f, 1.3f);
            // Feature 5: Multi-axis forget time override (Precision + Aggression)
            if (AdaptiveAISettings.MultiAxisEnabled)
            {
                var multiAxis = PlayerBehaviorCollector.GetMultiAxisProfile();
                if (multiAxis != null && multiAxis.IsValid)
                {
                    float axisForgetMult = multiAxis.GetForgetTimeMult();
                    float s = Mathf.Clamp01(AdaptiveAISettings.MultiAxisOverrideStrength);
                    forgetMult *= Mathf.Lerp(1f, axisForgetMult, s);
                }
            }
            c.forgetTime = Mathf.Max(1f, baseVal.ForgetTime * forgetMult);

            float skillCoolStrength = GetEffectiveStrength(gunTag, SkillCoolTimeRangeAggressionStrength, "SkillCoolTimeRange");
            float skillCoolMult = Mathf.Clamp(1f - (aggForBehavior - 0.5f) * 2f * skillCoolStrength, 0.7f, 1.2f);
            c.skillCoolTimeRange = new Vector2(
                Mathf.Max(0.5f, baseVal.SkillCoolTimeRange.x * skillCoolMult),
                Mathf.Max(1f, baseVal.SkillCoolTimeRange.y * skillCoolMult));
            float itemChanceStrength = GetEffectiveStrength(gunTag, ItemSkillChanceAggressionStrength, "ItemSkillChance");
            float itemChanceMult = Mathf.Clamp(1f + (aggForBehavior - 0.5f) * 2f * itemChanceStrength, 0.5f, 1.5f);
            c.itemSkillChance = Mathf.Clamp01(baseVal.ItemSkillChance * itemChanceMult);
            float itemCoolStrength = GetEffectiveStrength(gunTag, ItemSkillCoolTimeAggressionStrength, "ItemSkillCoolTime");
            float itemCoolMult = Mathf.Clamp(1f - (aggForBehavior - 0.5f) * 2f * itemCoolStrength, 0.7f, 1.2f);
            c.itemSkillCoolTime = Mathf.Max(1f, baseVal.ItemSkillCoolTime * itemCoolMult);

        }

        /// <summary>Melee: if MeleeDriveAttackOurselves, keep combat_Attack_Tree null so BT does not call Attack(). Else only set null when out of range if MeleeDisableAttackTreeWhenOutOfRange.</summary>
        private static FieldInfo? _combatAttackTreeField;
        private static readonly Dictionary<global::AICharacterController, object?> _originalCombatAttackTree = new Dictionary<global::AICharacterController, object?>();

        internal static void ApplyMeleeAttackTreeByRange(global::AICharacterController ai)
        {
            if (ai == null) return;
            if (PerAIPatchControl.IsExcludedFromAdaptivePatches(ai)) return;
            var c = ai.CharacterMainControl;
            if (c == null || c == CharacterMainControl.Main) return;
            var melee = c.GetMeleeWeapon();
            if (melee == null) return;

            bool keepNull = AdaptiveAISettings.MeleeDriveAttackOurselves;
            if (!keepNull && AdaptiveAISettings.MeleeDisableAttackTreeWhenOutOfRange)
            {
                var main = CharacterMainControl.Main;
                Vector3? tpos = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackTargetPosition(c, ai, main);
                if (!tpos.HasValue) keepNull = true;
                else
                {
                    Vector3 toT = tpos.Value - c.transform.position;
                    toT.y = 0f;
                    float maxDist = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackMaxDistance(melee);
                    keepNull = maxDist <= 0f || toT.magnitude > maxDist;
                }
            }

            if (!keepNull)
            {
                if (_originalCombatAttackTree.TryGetValue(ai, out object? original) && original != null)
                {
                    if (_combatAttackTreeField == null)
                        _combatAttackTreeField = AccessTools.Field(typeof(global::AICharacterController), "combat_Attack_Tree");
                    try { _combatAttackTreeField?.SetValue(ai, original); } catch { }
                    _originalCombatAttackTree.Remove(ai);
                }
                return;
            }

            if (_combatAttackTreeField == null)
                _combatAttackTreeField = AccessTools.Field(typeof(global::AICharacterController), "combat_Attack_Tree");
            if (_combatAttackTreeField == null) return;
            object? current = _combatAttackTreeField.GetValue(ai);
            if (current == null) return;
            try
            {
                if (!_originalCombatAttackTree.ContainsKey(ai))
                    _originalCombatAttackTree[ai] = current;
                _combatAttackTreeField.SetValue(ai, null);
            }
            catch (System.Exception) { }
        }

        /// <summary>Melee: if out of range but attack action is running, force stop. During CA_Attack CanRun() is false so speed drops; stopping restores running.</summary>
        internal static void StopMeleeAttackWhenOutOfRange(global::AICharacterController ai)
        {
            if (ai == null || AdaptiveAISettings.MeleeUseOriginalBehavior) return;
            if (PerAIPatchControl.IsExcludedFromAdaptivePatches(ai)) return;
            var c = ai.CharacterMainControl;
            if (c == null || c == CharacterMainControl.Main || c.attackAction == null) return;
            if (!c.attackAction.Running) return;
            var melee = c.GetMeleeWeapon();
            if (melee == null) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            Vector3? tpos = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackTargetPosition(c, ai, main);
            if (!tpos.HasValue) { try { c.attackAction.StopAction(); } catch { } return; }
            Vector3 toT = tpos.Value - c.transform.position;
            toT.y = 0f;
            float maxDist = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackMaxDistance(melee);
            if (maxDist > 0f && toT.magnitude > maxDist)
                try { c.attackAction.StopAction(); } catch { }
        }

        /// <summary>Melee: when MeleeDriveAttackOurselves, call Attack() if in range. Mod controls attack timing instead of BT. Called every 0.1s.</summary>
        internal static void TryMeleeAttackWhenInRange(global::AICharacterController ai)
        {
            if (ai == null || !AdaptiveAISettings.MeleeDriveAttackOurselves || !AdaptiveAISettings.MeleeAttackTriggerEnabled) return;
            if (PerAIPatchControl.IsExcludedFromAdaptivePatches(ai)) return;
            var c = ai.CharacterMainControl;
            if (c == null || c == CharacterMainControl.Main) return;
            var melee = c.GetMeleeWeapon();
            if (melee == null) return;
            if (c.attackAction == null || c.attackAction.Running) return;
            var main = CharacterMainControl.Main;
            // 시야 게이트: 타겟이 플레이어인데 벽 너머면 휘두르지 않는다(사거리만으로 벽 관통 공격 방지).
            if (main != null && IsTargetingPlayer(ai, main) && !CanEngagePlayer(ai, main)) return;
            Vector3? tpos = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackTargetPosition(c, ai, main);
            if (!tpos.HasValue) return;
            Vector3 toT = tpos.Value - c.transform.position;
            toT.y = 0f;
            float dist = toT.magnitude;
            float maxDist = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackMaxDistance(melee);
            if (maxDist <= 0f || dist > maxDist) return;
            if (!melee.AttackableTargetInRange() && dist > (melee.AttackRange + 0.05f) * 1.15f) return;
            try { c.Attack(); } catch { }
        }

        /// <summary>Melee: when out of range set shootDelay very high. (This game BT is not controlled by shootDelay so no effect. Prefer MeleeDisableAttackTreeWhenOutOfRange.)</summary>
        internal static void ApplyMeleeShootDelayByRange(global::AICharacterController ai)
        {
            if (ai == null || !AdaptiveAISettings.MeleeSuppressAttackCallWhenOutOfRange) return;
            if (PerAIPatchControl.IsExcludedFromAdaptivePatches(ai)) return;
            var c = ai.CharacterMainControl;
            if (c == null || c == CharacterMainControl.Main) return;
            var melee = c.GetMeleeWeapon();
            if (melee == null) return;
            if (!AdaptiveAISettings.MeleeAttackTriggerEnabled) { ai.shootDelay = AdaptiveAISettings.MeleeCooldownWhenOutOfRange; return; }
            var main = CharacterMainControl.Main;
            Vector3? tpos = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackTargetPosition(c, ai, main);
            if (!tpos.HasValue) { ai.shootDelay = AdaptiveAISettings.MeleeCooldownWhenOutOfRange; return; }
            Vector3 toT = tpos.Value - c.transform.position;
            toT.y = 0f;
            float dist = toT.magnitude;
            float maxDist = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackMaxDistance(melee);
            if (maxDist <= 0f) return;
            var baseVal = _baseValues.GetOrCreateValue(ai);
            if (dist > maxDist)
                ai.shootDelay = AdaptiveAISettings.MeleeCooldownWhenOutOfRange;
            else
                ai.shootDelay = baseVal.Set ? baseVal.ShootDelay : 0f;
        }

        /// <summary>Refresh only canDash and dashCoolTimeRange every 1s. canDash from tactical (range/weapon); cooldown scale from cached behavior aggression.</summary>
        internal static void RefreshCanDashFromDistance(global::AICharacterController c)
        {
            if (c == null) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c.CharacterMainControl))
                return;
            var baseVal = _baseValues.GetOrCreateValue(c);
            bool blockedByPreset = CharacterMainControlDashPatches.IsDashBlockedForPreset(c.CharacterMainControl);
            if (!baseVal.Set)
            {
                c.canDash = !blockedByPreset;
                if (AdaptiveAISettings.DebugLogDash && blockedByPreset)
                    Debug.Log($"[AdaptiveEnemyAI] RefreshCanDash(baseVal unset): AI={c.GetInstanceID()} canDash=false(turret) preset=\"{c.CharacterMainControl?.characterPreset?.name ?? "null"}\"");
                return;
            }
            float loadoutFactor = GetLoadoutRangeFactor(c);
            baseVal.CachedLoadoutRangeFactor = loadoutFactor;
            // canDash: only turret false, rest always true.
            c.canDash = !blockedByPreset;
            float distToPlayer = -1f;
            if (global::CharacterMainControl.Main != null)
            {
                float distSq = (c.transform.position - global::CharacterMainControl.Main.transform.position).sqrMagnitude;
                distToPlayer = Mathf.Sqrt(distSq);
            }
            if (AdaptiveAISettings.DebugLogDash && blockedByPreset)
                Debug.Log($"[AdaptiveEnemyAI] canDash=false(RefreshCanDash): AI={c.GetInstanceID()} turret preset=\"{c.CharacterMainControl?.characterPreset?.name ?? "null"}\"");
            float coolMult = 1f;
            if (c.canDash)
            {
                float agg = GetCachedAggressionFor(c);
                float t = Mathf.Clamp01((agg - DashNeutralZone) / (AggressionClampMax - DashNeutralZone));
                coolMult = Mathf.Lerp(1f, DashCoolMultAtExtreme, t);
            }
            c.dashCoolTimeRange = new Vector2(
                baseVal.BaseDashCoolTimeRange.x * coolMult,
                baseVal.BaseDashCoolTimeRange.y * coolMult);
        }

        /// <summary>Tries approach/charge dash once by cooldown and probability. Game BT may not call Dash(), so mod calls it directly.
        /// When no movement input, CA_Dash.OnStart reads MoveInput and produces in-place dash; set MoveInput toward player just before calling.</summary>
        internal static void TryApproachDash(global::AICharacterController c)
        {
            if (c == null || c.CharacterMainControl == null) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c.CharacterMainControl))
                return;
            // Approach dash only when noticed (combat/tracking). When unnoticed (patrol) prevent charge toward player. Melee also uses dash-roll-in pattern toward player.
            if (!c.noticed) return;
            // Only try approach dash while actually moving toward player (avoids excessive dashing).
            if (AdaptiveAISettings.ApproachDashOnlyWhenApproaching && !CharacterMainControlSetMoveInputPatches.IsApproachingPlayer(c))
                return;
            // Skip inactive objects to avoid "gameObject is not active" from CA_Dash/AudioManager when character is dead or pooled.
            if (!c.gameObject.activeInHierarchy || !c.CharacterMainControl.gameObject.activeInHierarchy) return;
            if (!c.canDash) return;
            if (CharacterMainControlDashPatches.DashIsIncomingDodge) return;
            float now = Time.time;
            var holder = _lastApproachDashTime.GetOrCreateValue(c);
            float minCool = Mathf.Max(0.5f, c.dashCoolTimeRange.x);
            if (now - holder.Value < minCool) return;

            // Feature 3: Increase approach dash chance when push window is active
            float approachChance = ApproachDashTryChance;
            if (AdaptiveAISettings.ShortTermPushWindowEnabled && AdaptiveAISettings.ShortTermPatternEnabled)
            {
                var detector = PlayerBehaviorCollector.GetShortTermDetector();
                if (detector != null && detector.PushWindowActive)
                    approachChance = Mathf.Min(1f, approachChance + AdaptiveAISettings.ShortTermPushWindowApproachBoost);
            }
            // Feature 4: Increase approach dash chance when reload prediction is confident (player about to reload)
            if (AdaptiveAISettings.HabitExploitationEnabled)
            {
                var habits = PlayerBehaviorCollector.GetHabitTracker();
                if (habits != null && habits.ReloadPredictionConfidence >= AdaptiveAISettings.HabitConfidenceMin
                    && habits.PredictedTimeToReload >= 0f && habits.PredictedTimeToReload < 1.5f)
                {
                    approachChance = Mathf.Min(1f, approachChance + AdaptiveAISettings.HabitReloadPushStrength);
                }
            }

            if (UnityEngine.Random.value > approachChance) return;

            var main = CharacterMainControl.Main;
            if (main != null)
            {
                Vector3 aiPos = c.CharacterMainControl.transform.position;
                Vector3 toPlayer = main.transform.position - aiPos;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude >= 0.01f)
                {
                    toPlayer.Normalize();
                    Vector3 dashDir = toPlayer;
                    float judgmentNorm = Mathf.Clamp01(GetCachedAggressionFor(c) / 2f);
                    bool tryBackDash = AdaptiveAISettings.ApproachDashToBackChance > 0f
                        && judgmentNorm >= Mathf.Clamp01(AdaptiveAISettings.ApproachDashToBackJudgmentMin)
                        && UnityEngine.Random.value < Mathf.Clamp01(AdaptiveAISettings.ApproachDashToBackChance)
                        && !CharacterMainControlSetMoveInputPatches.IsAIBehindPlayerForDash(main, aiPos);
                    if (tryBackDash && CharacterMainControlSetMoveInputPatches.GetDirectionTowardPlayerBackForDash(main, aiPos, out Vector3 dirToBack))
                        dashDir = dirToBack;
                    else if (AdaptiveAISettings.DashPreferOutOfPlayerViewEnabled && AdaptiveAISettings.ApproachDashOutOfViewBlend > 0.0001f)
                    {
                        Vector3 outOfView = GetOutOfPlayerViewDirection(c);
                        if (outOfView.sqrMagnitude > 0.01f)
                            dashDir = Vector3.Slerp(toPlayer, outOfView, Mathf.Clamp01(AdaptiveAISettings.ApproachDashOutOfViewBlend)).normalized;
                    }
                    c.CharacterMainControl.SetMoveInput(dashDir);
                }
            }
            c.CharacterMainControl.Dash();
            holder.Value = now;
            var anyHolder = _lastAnyDodgeTime.GetOrCreateValue(c);
            anyHolder.Value = now;

            // Feature 2: Record approach tactic start
            if (AdaptiveAISettings.TacticOutcomeTrackingEnabled)
            {
                var tracker = TacticOutcomeTracker.GetFor(c);
                float aiHp = (c.CharacterMainControl?.Health != null && c.CharacterMainControl.Health.MaxHealth > 0f)
                    ? Mathf.Clamp01(c.CharacterMainControl.Health.CurrentHealth / c.CharacterMainControl.Health.MaxHealth) : 1f;
                float playerHp = (CharacterMainControl.Main?.Health != null && CharacterMainControl.Main.Health.MaxHealth > 0f)
                    ? Mathf.Clamp01(CharacterMainControl.Main.Health.CurrentHealth / CharacterMainControl.Main.Health.MaxHealth) : 1f;
                tracker.RecordTacticStart(TacticOutcomeTracker.TacticType.Approach, aiHp, playerHp);
            }
        }

        /// <summary>When blending out of cover toward player (right after ApplyLeaveCoverToEngage), dash out once. Shares cooldown with dodge/approach dash.</summary>
        internal static void TryLeaveCoverDash(global::AICharacterController c)
        {
            if (c == null || c.CharacterMainControl == null) return;
            if (!AdaptiveAISettings.LeaveCoverDashEnabled) return;
            if (!CharacterMainControlSetMoveInputPatches.WasLeaveCoverEngageRecently(c, 0.4f)) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c.CharacterMainControl)) return;
            if (!c.noticed || !IsAggroOnPlayer(c)) return;
            if (!c.gameObject.activeInHierarchy || !c.CharacterMainControl.gameObject.activeInHierarchy) return;
            if (!c.canDash) return;
            if (c.CharacterMainControl.dashAction != null && c.CharacterMainControl.dashAction.Running) return;
            if (CharacterMainControlDashPatches.IsCharacterReloading(c.CharacterMainControl)) return;
            if (TryGetIncomingProjectileThreat(c, out _, out _)) return;

            float now = Time.time;
            var anyHolder = _lastAnyDodgeTime.GetOrCreateValue(c);
            float minCool = Mathf.Max(0.5f, c.dashCoolTimeRange.x);
            if (now - anyHolder.Value < minCool) return;

            var main = CharacterMainControl.Main;
            if (main != null)
            {
                Vector3 toPlayer = main.transform.position - c.transform.position;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude >= 0.01f)
                {
                    toPlayer.Normalize();
                    Vector3 dashDir = toPlayer;
                    if (AdaptiveAISettings.DashPreferOutOfPlayerViewEnabled && AdaptiveAISettings.ApproachDashOutOfViewBlend > 0.0001f)
                    {
                        Vector3 outOfView = GetOutOfPlayerViewDirection(c);
                        if (outOfView.sqrMagnitude > 0.01f)
                            dashDir = Vector3.Slerp(toPlayer, outOfView, Mathf.Clamp01(AdaptiveAISettings.ApproachDashOutOfViewBlend)).normalized;
                    }
                    c.CharacterMainControl.SetMoveInput(dashDir);
                }
            }
            c.CharacterMainControl.Dash();
            anyHolder.Value = now;
            var approachHolder = _lastApproachDashTime.GetOrCreateValue(c);
            approachHolder.Value = now;
        }

        /// <summary>Set only baseReactionTime by behavior aggression 0~1. Higher = faster reaction. If LoadoutAffectsBehaviorParams use cached loadout+behavior blend.</summary>
        internal static void ApplyBaseReactionTimeForAdaptive(global::AICharacterController ai, float? aggression = null)
        {
            if (ai == null) return;
            var baseVal = _baseValues.GetOrCreateValue(ai);
            if (!baseVal.Set) return;
            float agg = aggression ?? GetCachedAggressionFor(ai);
            float strength = GetEffectiveStrength(GetCurrentGunTypeTag(), ReactionAggressionStrength, "Reaction");
            float mult = Mathf.Clamp(1f - (agg - 0.5f) * strength * 2f, 0.6f, 1.4f);
            ai.baseReactionTime = Mathf.Max(0.05f, baseVal.BaseReactionTime * mult);
        }

        /// <summary>Called once on spawn. Applies adaptive params: reaction time, move/trace/scatter, etc. CachedAggression and CachedLoadoutRangeFactor set in ApplyLoadoutToMoveAndTrace.</summary>
        internal static void ApplyAdaptiveParamsOnce(global::AICharacterController ai)
        {
            if (ai == null) return;
            if (ai.CharacterMainControl != null && ai.CharacterMainControl.Team == Teams.player)
                return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(ai.CharacterMainControl))
                return;
            // Melee original: do not overwrite shootCanMove (keep preset). Otherwise melee would stop and swing with no movement.
            if (!AdaptiveAISettings.MeleeUseOriginalBehavior && EnemyHasMeleeWeapon(ai))
                ai.shootCanMove = false; // Melee: no move while attacking → stop near player to swing
            else if (AdaptiveAISettings.MoveWhileFiringEnabled)
                ai.shootCanMove = true;
            ApplyLoadoutToMoveAndTrace(ai, null);
            ApplyBaseReactionTimeForAdaptive(ai, null);
            var baseVal = _baseValues.GetOrCreateValue(ai);
            baseVal.AggressionAppliedAtSpawn = true;
        }

        /// <summary>Apply once if not yet applied. Called from AdaptiveAIValueRunner (every 0.1s). Mod does not change attack timing (shootDelay etc.).</summary>
        internal static void EnsureAdaptiveParamsApplied(global::AICharacterController ai)
        {
            if (ai == null) return;
            if (ai.CharacterMainControl != null && ai.CharacterMainControl.Team == Teams.player)
                return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(ai.CharacterMainControl))
                return;
            var baseVal = _baseValues.GetOrCreateValue(ai);
            if (!baseVal.AggressionAppliedAtSpawn)
            {
                ApplyAdaptiveParamsOnce(ai);
                return;
            }
        }

        /// <summary>Called only for currently spawned enemies on loadout change. Recompute behavior aggression and tactical factor then update params and cache.</summary>
        internal static void ReapplyParamsAndCacheForLoadoutChange(global::AICharacterController ai)
        {
            if (ai == null) return;
            if (ai.CharacterMainControl != null && ai.CharacterMainControl.Team == Teams.player)
                return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(ai.CharacterMainControl))
                return;
            var baseVal = _baseValues.GetOrCreateValue(ai);
            if (!baseVal.AggressionAppliedAtSpawn) return;
            if (!AdaptiveAISettings.MeleeUseOriginalBehavior && EnemyHasMeleeWeapon(ai))
                ai.shootCanMove = false;
            else if (AdaptiveAISettings.MoveWhileFiringEnabled)
                ai.shootCanMove = true;
            ApplyLoadoutToMoveAndTrace(ai, null);
            ApplyBaseReactionTimeForAdaptive(ai, null);
        }

        /// <summary>Cached aggression on spawn (when loadout reflected, loadout+behavior blend per LoadoutAffectsBehaviorParams). Used for projectile dodge, reaction time, etc. For excluded enemies returns computed value depending on loadout reflection.
        /// Enhanced: applies short-term pattern correction (Feature 3), tactic effectiveness boost (Feature 2), and multi-axis blending (Feature 5).</summary>
        internal static float GetCachedAggressionFor(global::AICharacterController ai)
        {
            if (ai == null) return 0f;
            var baseVal = _baseValues.GetOrCreateValue(ai);
            float agg;
            if (!baseVal.Set)
                agg = AdaptiveAISettings.LoadoutAffectsBehaviorParams ? GetEffectiveAggressionFor(ai) : GetBehaviorAggressionFor(ai);
            else
                agg = baseVal.CachedAggression;

            // Feature 5: Multi-axis override (blend legacy with multi-axis computed value)
            if (AdaptiveAISettings.MultiAxisEnabled)
            {
                var multiAxis = PlayerBehaviorCollector.GetMultiAxisProfile();
                if (multiAxis != null && multiAxis.IsValid)
                {
                    float multiAgg = multiAxis.GetLegacyAggression();
                    float strength = Mathf.Clamp01(AdaptiveAISettings.MultiAxisOverrideStrength);
                    agg = Mathf.Lerp(agg, multiAgg, strength);
                }
            }

            // Feature 3: Short-term immediate aggression correction
            if (AdaptiveAISettings.ShortTermPatternEnabled)
            {
                var detector = PlayerBehaviorCollector.GetShortTermDetector();
                if (detector != null)
                {
                    float cap = Mathf.Clamp(AdaptiveAISettings.ShortTermImmediateAggressionCap, 0f, 0.3f);
                    float correction = Mathf.Clamp(detector.ImmediateAggressionCorrection, -cap, cap);
                    agg = Mathf.Clamp(agg + correction, AggressionClampMin, AggressionClampMax);
                }
            }

            // Feature 2: Tactic effectiveness boost (when AI is failing, try harder)
            if (AdaptiveAISettings.TacticOutcomeTrackingEnabled)
            {
                var tracker = TacticOutcomeTracker.GetFor(ai);
                float effectiveness = tracker.GetOverallEffectiveness();
                if (effectiveness < AdaptiveAISettings.TacticEffectivenessBoostThreshold)
                {
                    float deficit = AdaptiveAISettings.TacticEffectivenessBoostThreshold - effectiveness;
                    float boost = deficit * AdaptiveAISettings.TacticEffectivenessBoostStrength;
                    agg = Mathf.Clamp(agg + boost, AggressionClampMin, AggressionClampMax);
                }
            }

            return agg;
        }

        /// <summary>Get cached GunScatterMultiplier Stat (used when removing Modifier in OnDestroy etc.).</summary>
        internal static bool TryGetCachedScatterStat(object characterItem, out ItemStatsSystem.Stat stat)
        {
            stat = null;
            if (characterItem == null) return false;
            if (!_scatterStatCache.TryGetValue(characterItem, out var statObj)) return false;
            stat = statObj as ItemStatsSystem.Stat;
            return stat != null;
        }

        /// <summary>Apply correction Modifier to GunScatterMultiplier Stat by aggression and judgment. Low aggression (cautious) = less scatter. Higher judgment = extra scatter reduction (accuracy up). Stat queried and cached once per item.</summary>
        private static void ApplyScatterFromAggression(global::AICharacterController c, float agg, string? gunTag = null)
        {
            if (c == null) return;
            var cmc = c.CharacterMainControl;
            var item = cmc?.CharacterItem;
            if (item == null) return;

            if (!_scatterStatCache.TryGetValue(item, out var statObj))
            {
                var stat = item.GetStat("GunScatterMultiplier");
                if (stat == null) return;
                _scatterStatCache.Add(item, stat);
                statObj = stat;
            }
            var cachedStat = (ItemStatsSystem.Stat)statObj;
            if (cachedStat == null) return;

            float strength = GetEffectiveStrength(gunTag ?? GetCurrentGunTypeTag(), ScatterAggressionStrength, "Scatter");
            // Aggression 0~1: 0.5=neutral, lower=less scatter (accurate), higher=more scatter
            float value = (agg - 0.5f) * strength * 2f;
            // Higher judgment = extra scatter reduction (accuracy up)
            if (AdaptiveAISettings.ScatterScaleByJudgmentEnabled)
            {
                float judgmentNorm = GetDodgeJudgmentNorm(c);
                float judgmentStrength = Mathf.Clamp01(AdaptiveAISettings.ScatterJudgmentStrength);
                value -= judgmentNorm * judgmentStrength;
            }

            // Feature 5: Multi-axis precision override
            if (AdaptiveAISettings.MultiAxisEnabled)
            {
                var multiAxis = PlayerBehaviorCollector.GetMultiAxisProfile();
                if (multiAxis != null && multiAxis.IsValid)
                {
                    float axisMult = multiAxis.GetScatterMult();
                    float s = Mathf.Clamp01(AdaptiveAISettings.MultiAxisOverrideStrength);
                    // axisMult < 1 = more accurate (less scatter), blend with existing value
                    value *= Mathf.Lerp(1f, axisMult, s);
                }
            }

            // Cover scatter penalty: AI in cover shoots less accurately (no instant headshots from cover).
            // Applied as a floor on value so that judgment/aggression reductions cannot fully cancel it.
            if (AdaptiveAISettings.CoverScatterPenaltyEnabled && c.hasObsticleToTarget)
            {
                float coverFloor = Mathf.Clamp01(AdaptiveAISettings.CoverScatterPenaltyStrength);
                value = Mathf.Max(value, coverFloor);
            }

            // Cover exit aim delay: temporary scatter boost right after leaving cover (settling time)
            if (AdaptiveAISettings.CoverExitAimDelayEnabled)
            {
                var exitState = _coverExitState.GetOrCreateValue(c);
                bool inCover = c.hasObsticleToTarget;
                if (exitState.WasInCover && !inCover)
                    exitState.LastCoverExitTime = Time.time;
                exitState.WasInCover = inCover;

                if (!inCover && exitState.LastCoverExitTime > 0f)
                {
                    float elapsed = Time.time - exitState.LastCoverExitTime;
                    float duration = Mathf.Max(0.1f, AdaptiveAISettings.CoverExitAimDelayDuration);
                    if (elapsed < duration)
                    {
                        // Scatter boost that decays linearly over the delay duration.
                        // Use floor so the exit penalty is not cancelled by negative base values.
                        float t = 1f - (elapsed / duration);
                        float exitScatter = t * Mathf.Clamp01(AdaptiveAISettings.CoverExitAimDelayScatterBoost);
                        value = Mathf.Max(value, exitScatter);
                    }
                }
            }

            value = Mathf.Clamp(value, -0.65f, 0.5f);

            if (!_scatterModifiers.TryGetValue(c, out var mod))
            {
                mod = new Modifier(ModifierType.PercentageMultiply, value, ScatterModifierSource);
                cachedStat.AddModifier(mod);
                _scatterModifiers.Add(c, mod);
            }
            else if (Mathf.Abs(mod.Value - value) > 0.0001f)
            {
                mod.Value = value;
            }
        }

        /// <summary>Mod does not touch attack timing or shootDelay. Keeps original (preset) values.</summary>
        private static void ApplyFireRateFromAggression(global::AICharacterController c, float agg, BaseValues baseVal, string? gunTag = null)
        {
            if (c == null || baseVal == null) return;
            // No change to shootDelay, shootTimeRange, shootTimeSpaceRange — same as original.
        }

        /// <summary>Snapshot of player-team projectiles only. Used for gun dodge check.</summary>
        private static readonly List<global::Projectile> _playerProjectilesSnapshot = new List<global::Projectile>();
        private static int _projectileSnapshotFrame = -1;

        /// <summary>Dodge detection range (meters) by aggression (0~1). Higher aggression = narrower range (dodge only when close, fewer false dodges); lower = react from wider range.</summary>
        private static float GetIncomingDodgeDetectionRange(float aggression)
        {
            float minR = AdaptiveAISettings.IncomingDodgeDetectionRangeMin;
            float maxR = AdaptiveAISettings.IncomingDodgeDetectionRangeMax;
            float t = Mathf.Clamp01((aggression - AggressionClampMin) / (AggressionClampMax - AggressionClampMin));
            return Mathf.Lerp(maxR, minR, t);
        }

        /// <summary>Closest approach distance from point (aiPos) to line (projPos + t*projDir). toAi = normalized (aiPos-projPos), dist = |aiPos-projPos|.</summary>
        private static float GetClosestApproachDistance(float dist, Vector3 toAi, Vector3 projDir)
        {
            if (dist < 0.001f) return 0f;
            float dot = Vector3.Dot(projDir, toAi);
            if (dot <= 0f) return dist; // Projectile moving away
            return dist * Mathf.Sqrt(Mathf.Max(0f, 1f - dot * dot));
        }

        /// <summary>Effective dodge chance cap (0~1). When DodgeAndAimScaleByPlayerFamiliarity, lerps from relaxed default to AtMaxFamiliarity by map familiarity.</summary>
        private static float GetEffectiveIncomingDodgeChanceCap()
        {
            if (!AdaptiveAISettings.DodgeAndAimScaleByPlayerFamiliarity)
                return Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceCap);
            float fam = PlayerBehaviorCollector.GetCurrentMapFamiliarity();
            return Mathf.Lerp(
                Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceCap),
                Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceCapAtMaxFamiliarity),
                fam);
        }

        /// <summary>Dodge chance cap (0~1) by player hit rate pattern. No samples or low hit rate = CapMin; high = CapMax. For relaxing dodge on first shot. CapMax is lerped by map familiarity when DodgeAndAimScaleByPlayerFamiliarity.</summary>
        private static float GetPlayerPatternDodgeCap()
        {
            var summary = PlayerBehaviorCollector.GetCurrentSummary();
            float capMin = Mathf.Clamp01(AdaptiveAISettings.DodgeChanceCapMin);
            float capMax = Mathf.Clamp01(AdaptiveAISettings.DodgeChanceCapMax);
            if (AdaptiveAISettings.DodgeAndAimScaleByPlayerFamiliarity)
            {
                float fam = PlayerBehaviorCollector.GetCurrentMapFamiliarity();
                capMax = Mathf.Lerp(capMax, Mathf.Clamp01(AdaptiveAISettings.DodgeChanceCapMaxAtMaxFamiliarity), fam);
            }
            if (summary == null || !summary.HasCombatSamplesP2E)
                return capMin;
            float shoots = Mathf.Max(summary.ShootCountPerMin * 0.5f, 1f);
            float hitRate = Mathf.Clamp01(summary.PlayerToEnemyHitsPerMin / shoots);
            return Mathf.Lerp(capMin, capMax, hitRate);
        }

        /// <summary>Dodge chance multiplier by player pattern (hit rate, fire style). Aim/high hit = toward cap; spray/low hit = toward floor. Keep cap/floor, only shift curve.</summary>
        private static float GetPlayerPatternDodgeScale()
        {
            var summary = PlayerBehaviorCollector.GetCurrentSummary();
            if (summary == null || !summary.HasCombatSamplesP2E)
                return 1f;
            float shoots = Mathf.Max(summary.ShootCountPerMin * 0.5f, 1f);
            float hitRate = Mathf.Clamp01(summary.PlayerToEnemyHitsPerMin / shoots);
            float shootsNorm = Mathf.Clamp01(summary.ShootCountPerMin / Mathf.Max(1f, AdaptiveAISettings.ShootCountPerMinRef));
            // High hit + low fire = aim type (threat up) → toward 1; low hit + high fire = spray → toward 0
            float patternScore = hitRate * (1f - 0.4f * shootsNorm);
            float lateral = Mathf.Clamp01(summary.LateralMoveRatioEma);
            patternScore = Mathf.Clamp01(patternScore + 0.05f * lateral);
            if (AdaptiveAISettings.DodgeChancePatternUseDashAim)
            {
                float dashNorm = Mathf.Clamp01(summary.DashCountPerMin / 6f);
                float aimNorm = Mathf.Clamp01(summary.AimChangeVariance / Mathf.Max(0.01f, AdaptiveAISettings.AimVarianceRef));
                patternScore = Mathf.Clamp01(patternScore + 0.04f * (dashNorm - 0.5f) + 0.03f * (aimNorm - 0.5f));
            }
            float minS = Mathf.Clamp(AdaptiveAISettings.DodgeChancePatternScaleMin, 0.5f, 1f);
            float maxS = Mathf.Clamp(AdaptiveAISettings.DodgeChancePatternScaleMax, 1f, 1.5f);
            return Mathf.Lerp(minS, maxS, patternScore);
        }

        private static FieldInfo? _noticeTimeMarkerField;
        /// <summary>Dodge chance cap (0~1) when combat ramp applied. Before/just after combat 50%; rises per second during combat. RampMax and global cap lerped by map familiarity when DodgeAndAimScaleByPlayerFamiliarity.</summary>
        private static float GetCombatRampDodgeCap(global::AICharacterController c)
        {
            if (c == null || !AdaptiveAISettings.DodgeChanceCombatRampEnabled)
                return GetEffectiveIncomingDodgeChanceCap();
            float preCap = Mathf.Clamp01(AdaptiveAISettings.DodgeChancePreCombatCap);
            float rampMax = Mathf.Clamp01(AdaptiveAISettings.DodgeChanceCombatRampMax);
            if (AdaptiveAISettings.DodgeAndAimScaleByPlayerFamiliarity)
            {
                float fam = PlayerBehaviorCollector.GetCurrentMapFamiliarity();
                rampMax = Mathf.Lerp(rampMax, Mathf.Clamp01(AdaptiveAISettings.DodgeChanceCombatRampMaxAtMaxFamiliarity), fam);
            }
            float perSec = Mathf.Max(0f, AdaptiveAISettings.DodgeChanceCombatRampPerSecond);
            if (!c.noticed)
                return preCap;
            if (_noticeTimeMarkerField == null)
                _noticeTimeMarkerField = AccessTools.Field(typeof(global::AICharacterController), "noticeTimeMarker");
            float noticeTime = 0f;
            if (_noticeTimeMarkerField != null)
            {
                try { noticeTime = (float)(_noticeTimeMarkerField.GetValue(c) ?? 0f); }
                catch { }
            }
            float elapsed = Mathf.Max(0f, Time.time - noticeTime);
            float ramp = Mathf.Min(elapsed * perSec, rampMax - preCap);
            return Mathf.Min(preCap + ramp, rampMax);
        }

        /// <summary>Dodge multiplier by player fire pattern (burst vs single). Below 1 only for single shot / no pattern. If DodgeChanceScaleByShootPattern off, 1f.</summary>
        private static float GetShootPatternDodgeMultiplier()
        {
            if (!AdaptiveAISettings.DodgeChanceScaleByShootPattern)
                return 1f;
            if (!PlayerBehaviorCollector.GetRecentShootPattern(out _, out bool inBurst, out _))
                return Mathf.Clamp01(AdaptiveAISettings.DodgeChanceMultWhenSingleShot);
            if (inBurst)
                return 1f;
            return 1f;
        }

        /// <summary>When called on projectile spawn (e.g. gun fire), requests dodge check. AdaptiveAIValueRunner subscribes.</summary>
        internal static event System.Action? OnProjectileSpawned;
        internal static void NotifyProjectileSpawned() => OnProjectileSpawned?.Invoke();

        /// <summary>If incoming hostile projectile (non-friendly team) would hit this AI, try dodge dash by condition and probability. If aggression provided, TryIncomingProjectileDodge skips recompute. LOS not required; bullet detection counts as threat awareness and allows dodge.</summary>
        internal static void CheckIncomingProjectileAndTryDodge(global::AICharacterController c, float? aggression = null)
        {
            if (c?.CharacterMainControl == null)
                return;
            if (!c.gameObject.activeInHierarchy || !c.CharacterMainControl.gameObject.activeInHierarchy)
                return;
            if (c.CharacterMainControl.Team == Teams.player)
                return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c.CharacterMainControl))
                return;
            if (PlayerBehaviorCollector.IsInBase())
                return;
            if (!AdaptiveAISettings.IncomingDodgePlayerProjectilesOnly)
                return;
            // Non-aggressive: do not perform dodge action until player is noticed (or recently hurt by player). Prevents walking toward player after dodge.
            if (IsNonAggro(c) && !c.noticed && !IsRecentlyHurtByPlayer(c))
                return;
            // For reaction delay: on state change reset first threat time for that side. When switching to unnoticed, also clear noticed cache.
            var firstThreatState = _firstThreatTimeState.GetValue(c, _ => new FirstThreatTimeState());
            if (c.noticed)
            {
                firstThreatState.Time = 0f;
            }
            else
            {
                firstThreatState.TimeNoticed = 0f;
                firstThreatState.ReactionDelayStartWhenNoticed = 0f;
                firstThreatState.FirstThreatDodgeScaled = false;
            }

            int frame = Time.frameCount;
            if (frame != _projectileSnapshotFrame)
            {
                _playerProjectilesSnapshot.Clear();
                lock (ProjectilePatches.ProjectilesLock)
                {
                    foreach (var p in ProjectilePatches.PlayerTeamProjectiles)
                    {
                        if (p == null || !p.gameObject.activeInHierarchy) continue;
                        if (!_playerProjectilesSnapshot.Contains(p))
                            _playerProjectilesSnapshot.Add(p);
                    }
                }
                _projectileSnapshotFrame = frame;
            }

            float agg = aggression ?? GetCachedAggressionFor(c);
            Vector3 aiPos = c.transform.position;
            float detectionRange = GetIncomingDodgeDetectionRange(agg);
            float closestApproachThreshold = Mathf.Max(0.01f, AdaptiveAISettings.IncomingDodgeClosestApproachMeters);

            bool debugLoggedThisAi = false;
            foreach (var p in _playerProjectilesSnapshot)
            {
                Vector3 projPos = p.transform.position;
                Vector3 projDir = p.transform.forward;
                float speed = p.context.speed;
                if (speed < 1f) continue;

                Vector3 toAi = aiPos - projPos;
                float dist = toAi.magnitude;
                if (dist < 0.01f) continue;
                toAi /= dist;

                if (dist > detectionRange)
                    continue;
                float closestApproach = GetClosestApproachDistance(dist, toAi, projDir);
                // Close range (round already near): only slight relaxation by closest approach. Dodge only rounds that would actually hit; no relaxation beyond 1.5m.
                float threshold = closestApproachThreshold;
                if (dist < 5f)
                    threshold = Mathf.Max(threshold, Mathf.Min(1.5f, dist * 0.35f));
                if (closestApproach > threshold)
                {
                    if (DebugLogIncomingDodge && !debugLoggedThisAi && Time.frameCount % 30 == 0)
                    {
                        debugLoggedThisAi = true;
                        Debug.Log($"[AdaptiveEnemyAI] Dodge skip(closest approach): dist={dist:F1}m range={detectionRange:F0}m closest={closestApproach:F2}m threshold={threshold:F2}m");
                    }
                    continue;
                }
                float dotMin = GetIncomingDodgeDotMin();
                float dot = Vector3.Dot(projDir, toAi);
                if (dot < dotMin)
                {
                    if (DebugLogIncomingDodge && !debugLoggedThisAi && Time.frameCount % 30 == 0)
                    {
                        debugLoggedThisAi = true;
                        Debug.Log($"[AdaptiveEnemyAI] Dodge skip(dot): dist={dist:F1}m dot={dot:F2} min={dotMin:F2}");
                    }
                    continue;
                }

                if (DebugLogIncomingDodge)
                    Debug.Log($"[AdaptiveEnemyAI] Threat detected(player projectile) → dodge attempt: AI={c.GetInstanceID()} dist={dist:F1}m closest={closestApproach:F2}m");
                // Reaction delay: record time threat was first detected (once per unnoticed/noticed).
                var threatState = _firstThreatTimeState.GetValue(c, _ => new FirstThreatTimeState());
                if (!c.noticed && AdaptiveAISettings.DodgeReactionDelayWhenNotNoticed > 0f && threatState.Time <= 0f)
                    threatState.Time = Time.time;
                if (c.noticed && AdaptiveAISettings.DodgeReactionDelayWhenNoticedMax > 0f && threatState.TimeNoticed <= 0f)
                    threatState.TimeNoticed = Time.time;
                TryIncomingProjectileDodge(c, aggression, projDir, toAi, closestApproachMeters: closestApproach);
                return;
            }
        }

        /// <summary>True if there is a threat projectile about to reach this AI, and returns its direction (projDir, toAi). For movement pattern (zigzag/diamond) blend when dash not possible.</summary>
        internal static bool TryGetIncomingProjectileThreat(global::AICharacterController c, out Vector3 projDir, out Vector3 toAi)
        {
            projDir = default;
            toAi = default;
            if (c?.CharacterMainControl == null) return false;
            if (c.CharacterMainControl.Team == Teams.player) return false;
            if (!AdaptiveAISettings.IncomingDodgePlayerProjectilesOnly) return false;

            float agg = GetCachedAggressionFor(c);
            Vector3 aiPos = c.transform.position;
            float detectionRange = GetIncomingDodgeDetectionRange(agg);
            float closestThreshold = Mathf.Max(0.01f, AdaptiveAISettings.IncomingDodgeClosestApproachMeters);
            lock (ProjectilePatches.ProjectilesLock)
            {
                foreach (var p in ProjectilePatches.PlayerTeamProjectiles)
                {
                    if (p == null || !p.gameObject.activeInHierarchy) continue;
                    Vector3 projPos = p.transform.position;
                    Vector3 dir = p.transform.forward;
                    if (p.context.speed < 1f) continue;
                    Vector3 toAiVec = aiPos - projPos;
                    float dist = toAiVec.magnitude;
                    if (dist < 0.01f) continue;
                    toAiVec /= dist;
                    if (dist > detectionRange) continue;
                    if (GetClosestApproachDistance(dist, toAiVec, dir) > closestThreshold) continue;
                    if (Vector3.Dot(dir, toAiVec) < GetIncomingDodgeDotMin()) continue;
                    projDir = dir;
                    toAi = toAiVec;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Dodge against melee swing. (No projectile; treat attacker→AI direction as threat and call TryIncomingProjectileDodge)</summary>
        internal static void CheckIncomingMeleeAndTryDodge(global::AICharacterController c, float? aggression, Vector3 attackerOrigin)
        {
            if (c?.CharacterMainControl == null) return;
            if (!c.gameObject.activeInHierarchy || !c.CharacterMainControl.gameObject.activeInHierarchy) return;
            if (c.CharacterMainControl.Team == Teams.player) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            // Non-aggressive: do not perform dodge until player noticed (or recently hurt by player).
            if (IsNonAggro(c) && !c.noticed && !IsRecentlyHurtByPlayer(c)) return;

            Vector3 aiPos = c.transform.position;
            Vector3 toAi = aiPos - attackerOrigin;
            float dist = toAi.magnitude;
            if (dist < 0.1f) return;
            toAi /= dist;

            // For melee, dodge direction is toward player (attacker) (isMelee: true)
            TryIncomingProjectileDodge(c, aggression, toAi, toAi, isMelee: true);
        }

        /// <summary>Dodge dash direction for incoming projectile. When reloading (gun only): retreat or left/right (no forward); else: perpendicular left/right to travel. If melee (isMelee): toward attacker.</summary>
        private static Vector3 GetIncomingDodgeDirection(Vector3 projDir, Vector3 toAi, bool reloading, bool isMelee = false)
        {
            Vector3 projFlat = new Vector3(projDir.x, 0f, projDir.z);
            if (projFlat.sqrMagnitude < 0.01f) return Vector3.forward;
            projFlat.Normalize();

            if (isMelee)
            {
                // Melee: dodge toward player (attacker) — projDir is attacker→AI so opposite = AI→attacker
                return -projFlat;
            }

            if (reloading)
            {
                // Reloading + gun (projectile): choose only retreat or left/right so we do not go forward (bullet direction)
                Vector3 retreat = -projFlat;
                Vector3 right = Vector3.Cross(Vector3.up, projFlat);
                if (right.sqrMagnitude < 0.01f) return retreat;
                right.Normalize();
                float roll = UnityEngine.Random.value;
                if (roll < 0.33f) return retreat;
                return roll < 0.66f ? right : -right;
            }

            // General: one of left/right perpendicular to travel direction
            Vector3 rightGeneral = Vector3.Cross(Vector3.up, projFlat);
            if (rightGeneral.sqrMagnitude < 0.01f) return Vector3.forward;
            rightGeneral.Normalize();
            return UnityEngine.Random.value >= 0.5f ? rightGeneral : -rightGeneral;
        }

        /// <summary>Dodge reaction delay (seconds) for this AI. Delay decreases with judgment (map familiarity) - aggression. When unnoticed 0.5~2s; when noticed 0.12~0.25s.</summary>
        internal static float GetEffectiveDodgeReactionDelayFor(global::AICharacterController c)
        {
            float baseDelayNoticed = AdaptiveAISettings.DodgeReactionDelayWhenNotNoticed;
            float minDelayUnnoticed = AdaptiveAISettings.DodgeReactionDelayWhenNotNoticedMin;
            if (c != null && c.noticed && AdaptiveAISettings.DodgeReactionDelayWhenNoticedMax <= 0f)
                return 0f;
            if (!AdaptiveAISettings.DodgeReactionDelayScaleByJudgmentEnabled || c == null)
                return c != null && !c.noticed ? minDelayUnnoticed : baseDelayNoticed;
            float judgment = 0f;
            try
            {
                string mapId = SceneManager.GetActiveScene().name;
                if (AdaptiveAISettings.MapFamiliarityEnabled && !string.IsNullOrEmpty(mapId))
                {
                    float familiarity = PlayerBehaviorCollector.GetMapFamiliarity(mapId);
                    var summary = PlayerBehaviorCollector.GetCurrentSummary();
                    float behaviorFactor = (summary != null && summary.AimSampleCount >= 5) ? summary.GetBehaviorAggressionFactor() : 0f;
                    float range = Mathf.Max(0.01f, AdaptiveAISettings.BehaviorAggressionRange);
                    float normalizedStyle = Mathf.Clamp((behaviorFactor / range + 1f) * 0.5f, 0f, 1f);
                    judgment = familiarity * normalizedStyle;
                }
            }
            catch { /* Keep 0 when map familiarity not used */ }
            float agg = GetCachedAggressionFor(c);
            float t = (judgment - agg + 1f) * 0.5f;
            t = Mathf.Clamp01(t);
            bool unnoticed = c != null && !c.noticed;
            float minDelay = unnoticed ? minDelayUnnoticed : baseDelayNoticed;
            float maxDelay = Mathf.Max(minDelay, unnoticed ? AdaptiveAISettings.DodgeReactionDelayWhenNotNoticedMax : AdaptiveAISettings.DodgeReactionDelayWhenNoticedMax);
            return Mathf.Clamp(Mathf.Lerp(maxDelay, minDelay, t), minDelay, maxDelay);
        }

        /// <summary>Start time for dodge reaction delay. Unnoticed = first threat detection time; noticed = cached notice time (set once to reduce display).</summary>
        private static float GetReactionDelayStartTime(global::AICharacterController c)
        {
            if (c == null) return 0f;
            if (!_firstThreatTimeState.TryGetValue(c, out var state))
                return 0f;
            if (c.noticed)
            {
                if (state.ReactionDelayStartWhenNoticed > 0f)
                    return state.ReactionDelayStartWhenNoticed;
                float noticeTime = 0f;
                if (_noticeTimeMarkerField == null)
                    _noticeTimeMarkerField = AccessTools.Field(typeof(global::AICharacterController), "noticeTimeMarker");
                if (_noticeTimeMarkerField != null)
                {
                    try { noticeTime = (float)(_noticeTimeMarkerField.GetValue(c) ?? 0f); }
                    catch { }
                }
                if (noticeTime <= 0f && state.TimeNoticed > 0f)
                    noticeTime = state.TimeNoticed;
                if (noticeTime <= 0f)
                    noticeTime = Time.time;
                state.ReactionDelayStartWhenNoticed = noticeTime;
                return noticeTime;
            }
            return state.Time;
        }

        /// <summary>Remaining dodge reaction delay (seconds) for this AI. When noticed from notice time; when unnoticed from first threat detection. For debug overlay.</summary>
        internal static bool TryGetRemainingDodgeReactionDelay(global::AICharacterController c, out float remainingSeconds, out float totalDelaySeconds)
        {
            remainingSeconds = 0f;
            totalDelaySeconds = 0f;
            if (c == null) return false;
            float effectiveDelay = GetEffectiveDodgeReactionDelayFor(c);
            if (effectiveDelay <= 0f) return false;
            float firstTime = GetReactionDelayStartTime(c);
            if (firstTime <= 0f) return false;
            float elapsed = Time.time - firstTime;
            if (elapsed >= effectiveDelay) return false;
            totalDelaySeconds = effectiveDelay;
            remainingSeconds = Mathf.Max(0f, effectiveDelay - elapsed);
            return true;
        }

        /// <summary>Judgment (0~2) for dodge cooldown/success chance. From player loadout+behavior (incl. map familiarity) = GetCachedAggressionFor. Not a fixed value.</summary>
        private static float GetDodgeJudgment(global::AICharacterController c)
        {
            if (c == null) return 1f;
            return Mathf.Clamp(GetCachedAggressionFor(c), AggressionClampMin, AggressionClampMax);
        }

        /// <summary>Normalize judgment 0~2 to 0~1 for Lerp.</summary>
        private static float GetDodgeJudgmentNorm(global::AICharacterController c)
        {
            float j = GetDodgeJudgment(c);
            return Mathf.Clamp01(j / AggressionClampMax);
        }

        /// <summary>Dodge cooldown (seconds) by judgment. Lerped by map familiarity when DodgeAndAimScaleByPlayerFamiliarity (low fam = longer cooldown, high fam = shorter up to AtMaxFamiliarity).</summary>
        private static float GetEffectiveDodgeCooldownFor(global::AICharacterController c, bool reloading, bool bossOrElite)
        {
            float fam = AdaptiveAISettings.DodgeAndAimScaleByPlayerFamiliarity ? PlayerBehaviorCollector.GetCurrentMapFamiliarity() : 0f;
            float cooldownMax, cooldownMin;
            if (reloading)
            {
                if (bossOrElite)
                {
                    cooldownMax = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingBossEliteMax, AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingBossEliteMaxAtMaxFamiliarity, fam);
                    cooldownMin = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingBossEliteMin, AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingBossEliteMinAtMaxFamiliarity, fam);
                }
                else
                {
                    cooldownMax = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingMax, AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingMaxAtMaxFamiliarity, fam);
                    cooldownMin = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingMin, AdaptiveAISettings.IncomingDodgeCooldownWhenReloadingMinAtMaxFamiliarity, fam);
                }
            }
            else if (bossOrElite)
            {
                cooldownMax = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownBossEliteMax, AdaptiveAISettings.IncomingDodgeCooldownBossEliteMaxAtMaxFamiliarity, fam);
                cooldownMin = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownBossEliteMin, AdaptiveAISettings.IncomingDodgeCooldownBossEliteMinAtMaxFamiliarity, fam);
            }
            else
            {
                cooldownMax = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownMax, AdaptiveAISettings.IncomingDodgeCooldownMaxAtMaxFamiliarity, fam);
                cooldownMin = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeCooldownMin, AdaptiveAISettings.IncomingDodgeCooldownMinAtMaxFamiliarity, fam);
            }
            float t = AdaptiveAISettings.IncomingDodgeCooldownScaleByJudgmentEnabled ? GetDodgeJudgmentNorm(c) : 0f;
            return Mathf.Lerp(cooldownMax, cooldownMin, t);
        }

        /// <summary>Dodge success chance (0~1) by judgment. Used at second gate after attempt. Floor/Cap ensure min/max.</summary>
        private static float GetEffectiveDodgeSuccessChance(global::AICharacterController c)
        {
            float floor = Mathf.Clamp01(AdaptiveAISettings.DodgeSuccessChanceFloor);
            float cap = Mathf.Clamp01(AdaptiveAISettings.DodgeSuccessChanceCap);
            if (cap < floor) cap = floor;
            float raw;
            if (!AdaptiveAISettings.DodgeSuccessChanceScaleByJudgmentEnabled || c == null)
                raw = Mathf.Clamp01(AdaptiveAISettings.DodgeSuccessChance);
            else
            {
                float t = GetDodgeJudgmentNorm(c);
                raw = Mathf.Lerp(
                    AdaptiveAISettings.DodgeSuccessChanceAtLowJudgment,
                    AdaptiveAISettings.DodgeSuccessChanceAtHighJudgment,
                    t);
            }
            return Mathf.Clamp(raw, floor, cap);
        }

        /// <summary>Aggression 0~1 (assertiveness). Dodge chance = detection chance (single roll): on pass, second gate (success chance) then dash. Cooldown, burst etc. applied separately. If isMelee, dodge direction toward attacker. If closestApproachMeters set, use closest-approach-based probability (converges to 100% as approach→0).</summary>
        private static void TryIncomingProjectileDodge(global::AICharacterController c, float? aggression, Vector3 projDir, Vector3 toAi, bool isMelee = false, float? closestApproachMeters = null)
        {
            if (c?.CharacterMainControl == null) return;
            if (CharacterMainControlDashPatches.IsDashBlockedForPreset(c.CharacterMainControl))
            {
                if (DebugLogIncomingDodge)
                    Debug.Log($"[AdaptiveEnemyAI] Dodge skip(preset dash disabled): AI={c.GetInstanceID()} preset=\"{c.CharacterMainControl?.characterPreset?.name ?? "null"}\"");
                return;
            }

            if (DebugLogIncomingDodge)
                Debug.Log($"[AdaptiveEnemyAI] Dodge attempt enter: AI={c.GetInstanceID()} canDash={c.canDash}");
            // Only refresh aggression/detection etc. for dodge check. canDash set temporarily true just before dodge dash to avoid BT etc. blocking.
            EnsureAdaptiveParamsApplied(c);
            float aggForParams = aggression ?? GetCachedAggressionFor(c);
            ApplyLoadoutToMoveAndTrace(c, aggForParams);

            bool reloading = CharacterMainControlDashPatches.IsCharacterReloading(c.CharacterMainControl);

            // TryIncomingProjectileDodge is only called after threat (projectile/melee) is already detected. So even if aggressive+unnoticed,
            // "no incoming bullet situation" does not apply — here we are in incoming bullet/melee situation, so do not skip dodge.

            // Dodge reaction delay: when unnoticed, N seconds after first threat detection; when noticed, N seconds after notice time (noticeTimeMarker, damage/sound/dash etc.) before attempting dodge.
            float effectiveDelay = GetEffectiveDodgeReactionDelayFor(c);
            if (effectiveDelay > 0f)
            {
                float firstTime = GetReactionDelayStartTime(c);
                if (firstTime > 0f)
                {
                    float elapsed = Time.time - firstTime;
                    if (elapsed < effectiveDelay)
                    {
                        if (DebugLogIncomingDodge)
                            Debug.Log($"[AdaptiveEnemyAI] Dodge skip(reaction delay): AI={c.GetInstanceID()} noticed={c.noticed} elapsed={elapsed:F2}s delay={effectiveDelay:F2}s");
                        return;
                    }
                }
            }

            // Dodge dash calls Dash() directly without canDash. Separate from BT's canDash; can trigger projectile dodge even when canDash=false.

            // Prevent infinite retreat: do not run dodge dash if player–enemy distance too far
            float maxDodgeDist = AdaptiveAISettings.MaxIncomingDodgeDistance;
            if (maxDodgeDist > 0f && CharacterMainControl.Main != null)
            {
                float distToPlayer = (c.transform.position - CharacterMainControl.Main.transform.position).magnitude;
                if (distToPlayer > maxDodgeDist)
                {
                    if (DebugLogIncomingDodge)
                        Debug.Log($"[AdaptiveEnemyAI] Dodge skip(distance over): AI={c.GetInstanceID()} distToPlayer={distToPlayer:F1}m max={maxDodgeDist:F0}m");
                    return;
                }
            }

            var state = _lastIncomingDodgeTime.GetValue(c, _ => new LastIncomingDodgeTime());
            bool bossOrElite = IsBossOrElitePreset(c.CharacterMainControl);
            float cooldown = GetEffectiveDodgeCooldownFor(c, reloading, bossOrElite);
            if (Time.time - state.Time < cooldown)
            {
                if (DebugLogIncomingDodge)
                    Debug.Log($"[AdaptiveEnemyAI] Dodge skip(cooldown): AI={c.GetInstanceID()} elapsed={Time.time - state.Time:F1}s cooldown={cooldown:F1}s");
                return;
            }

            // Burst limit: if multiple dodges in short time, apply recovery cooldown (prevent infinite dash)
            if (state.TimePrev > 0f &&
                Time.time - state.TimePrev < IncomingDodgeBurstWindow &&
                Time.time - state.Time < IncomingDodgeRecoveryCooldown)
            {
                if (DebugLogIncomingDodge)
                    Debug.Log($"[AdaptiveEnemyAI] Dodge skip(burst limit): AI={c.GetInstanceID()}");
                return;
            }

            float chance;
            float aggForDodge = aggression ?? GetCachedAggressionFor(c);
            float thresholdM = Mathf.Max(0.01f, AdaptiveAISettings.IncomingDodgeClosestApproachMeters);

            if (closestApproachMeters.HasValue)
            {
                // Closest-approach based: converges to 100% as approach→0; at threshold use BaseWhenClosest (default high)
                float t = Mathf.Clamp01(closestApproachMeters.Value / thresholdM);
                chance = Mathf.Lerp(1f, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceBaseWhenClosest), t);
                chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
                chance = Mathf.Min(chance, 1f);
                if (AdaptiveAISettings.DodgeChanceScaleByPlayerPattern)
                    chance *= GetPlayerPatternDodgeScale();
                // Player pattern (hit rate) based cap: relax dodge on first shot. Higher hit rate = higher cap.
                if (AdaptiveAISettings.DodgeChanceCapByPlayerHitRate)
                {
                    float cap = GetPlayerPatternDodgeCap();
                    chance = Mathf.Min(chance, cap);
                }
                // Combat ramp: 50% cap before/just after combat; +5% per second up to 70% (prevent over-dodge on first shot)
                chance = Mathf.Min(chance, GetCombatRampDodgeCap(c));
            }
            else
            {
                if (reloading)
                    chance = IncomingDodgeChanceWhenReloading;
                else
                {
                    float aggThreshold = Mathf.Max(0f, AdaptiveAISettings.IncomingDodgeAggressionThreshold);
                    float denom = Mathf.Max(0.01f, AggressionClampMax - aggThreshold);
                    float t = Mathf.Clamp01((aggForDodge - aggThreshold) / denom);
                    chance = Mathf.Lerp(AdaptiveAISettings.IncomingDodgeChanceMin, AdaptiveAISettings.IncomingDodgeChanceMax, t);
                }

                // Per-GunType dodge multiplier: applied by assertiveness (0~1).
                float dodgeBase = GetEffectiveStrength(GetCurrentGunTypeTag(), 1f, "Dodge");
                float aggThresholdDodge = Mathf.Max(0f, AdaptiveAISettings.IncomingDodgeAggressionThreshold);
                float denomDodge = Mathf.Max(0.01f, AggressionClampMax - aggThresholdDodge);
                float dodgeT = Mathf.Clamp01((aggForDodge - aggThresholdDodge) / denomDodge);
                float dodgeMult = Mathf.Lerp(1f, dodgeBase, dodgeT);
                chance = Mathf.Min(chance * dodgeMult, 1f);

                // Distance-based dodge correction: e.g. sniper=less dodge at far, more at close (per-weapon curve)
                float playerRange = PlayerLoadoutService.IsValid ? PlayerLoadoutService.Current.WeaponRange : 0f;
                chance *= GetDodgeDistanceMultiplier(c, GetCurrentGunTypeTag(), playerRange);
                chance = Mathf.Min(chance, 1f);

                // Unspotted enemy: if assertive (>=0.5) ensure Floor; if cautious apply scale to prevent over-dodge.
                if (!c.noticed)
                {
                    float aggVal = aggression ?? GetCachedAggressionFor(c);
                    if (aggVal >= 0.5f)
                        chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
                    else
                    {
                        float t = Mathf.Clamp01((0.5f - aggVal) / 0.5f);
                        float scale = Mathf.Lerp(Mathf.Clamp01(AdaptiveAISettings.DodgeChanceScaleWhenPlayerNotNoticed), 1f, t);
                        chance *= scale;
                    }
                    float maxWhenNotNoticed = Mathf.Clamp01(AdaptiveAISettings.DodgeChanceMaxWhenPlayerNotNoticed);
                    if (maxWhenNotNoticed >= 0f) chance = Mathf.Min(chance, maxWhenNotNoticed);
                }

                // Suppression/cover correction: adjust dodge chance (hurtTimeMarker, attackAction.Running, hasObsticleToTarget)
                var dmgInfo = default(DamageInfo);
                if (c.IsHurt(2f, 0, ref dmgInfo) && dmgInfo.fromCharacter == CharacterMainControl.Main)
                    chance *= AdaptiveAISettings.DodgeChanceMultWhenSuppressed;
                if (c.CharacterMainControl?.attackAction != null && c.CharacterMainControl.attackAction.Running)
                    chance *= AdaptiveAISettings.DodgeChanceMultWhenShooting;
                if (AdaptiveAISettings.DodgeChanceScaleByPlayerPattern)
                    chance *= GetPlayerPatternDodgeScale();
                chance = Mathf.Min(chance, GetEffectiveIncomingDodgeChanceCap());
                chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
            }
            // Extra dodge reduction only when unnoticed (less reduction at higher aggression)
            if (AdaptiveAISettings.DodgeScaleWhenNotNoticedEnabled && !c.noticed)
            {
                float aggNorm = Mathf.Clamp01((aggForDodge - AggressionClampMin) / Mathf.Max(0.01f, AggressionClampMax - AggressionClampMin));
                float scale = Mathf.Lerp(Mathf.Clamp01(AdaptiveAISettings.DodgeScaleWhenNotNoticedMin), Mathf.Clamp01(AdaptiveAISettings.DodgeScaleWhenNotNoticedMax), aggNorm);
                chance *= scale;
            }
            // Cover (obstacle between player and AI): by player shooting (hold hide/allow peek) or by aggression
            if (c.hasObsticleToTarget)
            {
                if (AdaptiveAISettings.CoverDodgeByPlayerShootingEnabled)
                {
                    bool playerShooting = CharacterMainControl.Main != null && CharacterMainControl.Main.attackAction != null && CharacterMainControl.Main.attackAction.Running;
                    if (playerShooting)
                    {
                        chance *= Mathf.Clamp01(AdaptiveAISettings.InCoverDodgeMultWhenPlayerShooting);
                        chance = Mathf.Min(chance, Mathf.Clamp01(AdaptiveAISettings.InCoverDodgeCapWhenPlayerShooting));
                    }
                    else
                    {
                        chance *= AdaptiveAISettings.DodgeChanceMultWhenInCover;
                        if (AdaptiveAISettings.InCoverDodgeCapWhenPlayerNotShooting > 0f)
                            chance = Mathf.Min(chance, Mathf.Clamp01(AdaptiveAISettings.InCoverDodgeCapWhenPlayerNotShooting));
                    }
                }
                else if (AdaptiveAISettings.InCoverDodgeBlockEnabled)
                {
                    if (aggForDodge >= Mathf.Clamp01(AdaptiveAISettings.InCoverDodgeAggressionThreshold))
                        chance = Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor);
                    else
                        chance *= Mathf.Clamp01(AdaptiveAISettings.InCoverDodgeChanceMultWhenLowAgg);
                }
                else
                    chance *= AdaptiveAISettings.DodgeChanceMultWhenInCover;
            }
            chance = Mathf.Min(chance, GetEffectiveIncomingDodgeChanceCap());
            chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
            // Combat ramp: 50% cap before/just after combat; +per second up to rampMax (prevent over-dodge on first shot)
            chance = Mathf.Min(chance, GetCombatRampDodgeCap(c));
            // Final multiplier for unnoticed enemy dodge chance: heavily reduce dodge for completely unspotted (multiply once more then ensure floor)
            if (!c.noticed)
            {
                chance *= Mathf.Clamp01(AdaptiveAISettings.DodgeChanceMultWhenPlayerNotNoticed);
                chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
            }
            // Fire pattern link: relax dodge when single shot/no pattern (less reaction to player first/single shot)
            chance *= GetShootPatternDodgeMultiplier();
            chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
            // First threat in this engagement for this AI: reduce dodge multiplier on first shot only, then normal
            if (AdaptiveAISettings.DodgeFirstThreatScaleEnabled)
            {
                var threatState = _firstThreatTimeState.GetValue(c, _ => new FirstThreatTimeState());
                if (!threatState.FirstThreatDodgeScaled)
                {
                    chance *= Mathf.Clamp01(AdaptiveAISettings.DodgeFirstThreatScale);
                    threatState.FirstThreatDodgeScaled = true;
                }
            }
            chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));

            // Feature 3: Short-term dodge urgency multiplier (burst fire detection)
            if (AdaptiveAISettings.ShortTermPatternEnabled)
            {
                var detector = PlayerBehaviorCollector.GetShortTermDetector();
                if (detector != null)
                {
                    float urgencyMult = Mathf.Clamp(detector.DodgeUrgencyMult, 0.7f, AdaptiveAISettings.ShortTermDodgeUrgencyMultCap);
                    chance *= urgencyMult;
                    chance = Mathf.Min(chance, GetEffectiveIncomingDodgeChanceCap());
                    chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
                }
            }

            // Feature 5: Multi-axis dodge multiplier (Reactivity axis)
            if (AdaptiveAISettings.MultiAxisEnabled)
            {
                var multiAxis = PlayerBehaviorCollector.GetMultiAxisProfile();
                if (multiAxis != null && multiAxis.IsValid)
                {
                    float axisDodgeMult = multiAxis.GetDodgeChanceMult();
                    float s = Mathf.Clamp01(AdaptiveAISettings.MultiAxisOverrideStrength);
                    chance *= Mathf.Lerp(1f, axisDodgeMult, s);
                    chance = Mathf.Min(chance, GetEffectiveIncomingDodgeChanceCap());
                    chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
                }
            }

            // Feature 2: Weight by dodge tactic success rate
            if (AdaptiveAISettings.TacticOutcomeTrackingEnabled)
            {
                var tracker = TacticOutcomeTracker.GetFor(c);
                float dodgeWeight = tracker.GetTacticWeight(TacticOutcomeTracker.TacticType.Dodge);
                dodgeWeight = Mathf.Clamp(dodgeWeight, AdaptiveAISettings.TacticWeightMin, AdaptiveAISettings.TacticWeightMax);
                chance *= dodgeWeight;
                chance = Mathf.Min(chance, GetEffectiveIncomingDodgeChanceCap());
                chance = Mathf.Max(chance, Mathf.Clamp01(AdaptiveAISettings.IncomingDodgeChanceFloor));
            }

            float roll = UnityEngine.Random.value;
            if (roll > chance)
            {
                if (DebugLogIncomingDodge)
                    Debug.Log($"[AdaptiveEnemyAI] Dodge skip(probability): AI={c.GetInstanceID()} roll={roll:F2} chance={chance:F2}");
                return;
            }

            // Attempt committed → consume cooldown. If noticed, skip stage 2 (success chance) and dash; if unnoticed, dash after passing stage 2.
            state.TimePrev = state.Time;
            state.Time = Time.time;
            if (!c.noticed)
            {
                float effectiveSuccess = GetEffectiveDodgeSuccessChance(c);
                if (UnityEngine.Random.value >= effectiveSuccess)
                {
                    if (DebugLogIncomingDodge)
                        Debug.Log($"[AdaptiveEnemyAI] Dodge skip(success chance fail): AI={c.GetInstanceID()} effectiveSuccess={effectiveSuccess:F2}");
                    return;
                }
            }

            Vector3 dodgeDir = GetIncomingDodgeDirection(projDir, toAi, reloading, isMelee);

            // Enemy with melee weapon always dashes toward player on dodge (direction within configured angle). Skip when using original behavior.
            if (!AdaptiveAISettings.MeleeUseOriginalBehavior && EnemyHasMeleeWeapon(c) && CharacterMainControl.Main != null)
            {
                Vector3 toPlayer = CharacterMainControl.Main.transform.position - c.transform.position;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude >= 0.01f)
                {
                    toPlayer.Normalize();
                    float maxAngle = Mathf.Clamp(AdaptiveAISettings.MeleeDodgeTowardPlayerMaxAngleDeg, 0f, 90f);
                    float angleDeg = UnityEngine.Random.Range(-maxAngle, maxAngle);
                    dodgeDir = Quaternion.AngleAxis(angleDeg, Vector3.up) * toPlayer;
                }
            }

            // Blend dodge dash direction toward out of player view (side/behind) for better use
            if (AdaptiveAISettings.DashPreferOutOfPlayerViewEnabled && dodgeDir.sqrMagnitude > 0.01f)
            {
                Vector3 outOfView = GetOutOfPlayerViewDirection(c);
                if (outOfView.sqrMagnitude > 0.01f)
                {
                    float blend = Mathf.Clamp01(AdaptiveAISettings.DodgeDashOutOfViewBlend);
                    dodgeDir = Vector3.Slerp(dodgeDir, outOfView, blend).normalized;
                }
            }

            bool savedCanDash = c.canDash;
            if (DebugLogIncomingDodge)
                Debug.Log($"[AdaptiveEnemyAI] Dodge dash execute: AI={c.GetInstanceID()} canDash={savedCanDash} dir=({dodgeDir.x:F2},{dodgeDir.z:F2}) reloading={reloading}");
            // Set canDash temporarily true so dodge dash works even when canDash is false (BT or other path may read canDash)
            c.canDash = true;
            // Dodge dash direction: if melee toward attacker; if reloading retreat; else perpendicular left/right.
            // CA_Dash.OnStart reads MoveInput; BT may overwrite it same frame and cause in-place roll.
            // Cache direction and force-apply in CA_Dash OnStart Postfix.
            CharacterMainControlDashPatches.DashIsIncomingDodge = true;
            CharacterMainControlDashPatches.IncomingDodgeDirection = dodgeDir;
            try
            {
                c.CharacterMainControl.SetMoveInput(dodgeDir);
                c.CharacterMainControl.Dash();
            }
            finally
            {
                CharacterMainControlDashPatches.DashIsIncomingDodge = false;
                c.canDash = savedCanDash;
                var anyHolder = _lastAnyDodgeTime.GetOrCreateValue(c);
                anyHolder.Value = Time.time;
            }

            // Feature 2: Record dodge tactic start for outcome tracking
            if (AdaptiveAISettings.TacticOutcomeTrackingEnabled)
            {
                var tracker = TacticOutcomeTracker.GetFor(c);
                float aiHp = (c.CharacterMainControl?.Health != null && c.CharacterMainControl.Health.MaxHealth > 0f)
                    ? Mathf.Clamp01(c.CharacterMainControl.Health.CurrentHealth / c.CharacterMainControl.Health.MaxHealth) : 1f;
                float playerHp = (CharacterMainControl.Main?.Health != null && CharacterMainControl.Main.Health.MaxHealth > 0f)
                    ? Mathf.Clamp01(CharacterMainControl.Main.Health.CurrentHealth / CharacterMainControl.Main.Health.MaxHealth) : 1f;
                tracker.RecordTacticStart(TacticOutcomeTracker.TacticType.Dodge, aiHp, playerHp);
            }
        }

        /// <summary>Tactical dash to disorient player during movement (not bullet dodge). Probability by player pattern (aim stability, hit rate). Shares cooldown with dodge/approach dash.</summary>
        internal static void TryTacticalDisorientationDodge(global::AICharacterController c)
        {
            if (c == null || c.CharacterMainControl == null) return;
            if (!AdaptiveAISettings.TacticalDashEnabled) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c.CharacterMainControl))
                return;
            if (CharacterMainControlDashPatches.IsDashBlockedForPreset(c.CharacterMainControl))
                return;
            if (!c.noticed || !IsAggroOnPlayer(c)) return;
            if (!c.gameObject.activeInHierarchy || !c.CharacterMainControl.gameObject.activeInHierarchy) return;
            if (c.CharacterMainControl.dashAction != null && c.CharacterMainControl.dashAction.Running) return;
            if (CharacterMainControlDashPatches.IsCharacterReloading(c.CharacterMainControl)) return;
            if (TryGetIncomingProjectileThreat(c, out _, out _)) return;

            // Try tactical dash only when approaching or leaving cover (prevent frequent dash)
            if (AdaptiveAISettings.TacticalDashOnlyWhenApproachingOrLeavingCover)
            {
                if (!CharacterMainControlSetMoveInputPatches.IsApproachingPlayer(c)
                    && !CharacterMainControlSetMoveInputPatches.WasLeaveCoverEngageRecently(c))
                    return;
            }

            float now = Time.time;
            var anyHolder = _lastAnyDodgeTime.GetOrCreateValue(c);
            if (now - anyHolder.Value < AdaptiveAISettings.TacticalDashCooldownAfterDodge)
                return;

            var summary = PlayerBehaviorCollector.GetCurrentSummary();
            float chance = Mathf.Clamp01(AdaptiveAISettings.TacticalDashChanceBase);
            if (summary != null)
            {
                if (AdaptiveAISettings.TacticalDashScaleByAimStability)
                {
                    float aimVarRef = Mathf.Max(0.01f, AdaptiveAISettings.AimVarianceRef);
                    float aimNorm = Mathf.Clamp01(summary.AimChangeVariance / aimVarRef);
                    float mult = Mathf.Lerp(AdaptiveAISettings.TacticalDashMaxMultWhenAimStable, 1f, aimNorm);
                    chance *= mult;
                }
                if (AdaptiveAISettings.TacticalDashScaleByPlayerHitRate && summary.HasCombatSamplesP2E)
                {
                    float shoots = Mathf.Max(summary.ShootCountPerMin * 0.5f, 1f);
                    float hitRate = Mathf.Clamp01(summary.PlayerToEnemyHitsPerMin / shoots);
                    float mult = Mathf.Lerp(1f, AdaptiveAISettings.TacticalDashMaxMultWhenHighHitRate, hitRate);
                    chance *= mult;
                }
                if (AdaptiveAISettings.TacticalModeTierEnabled)
                {
                    int mode = Data.BehaviorProfileSummary.GetTacticalModeIndex(Mathf.Clamp01(GetCachedAggressionFor(c) / 2f), summary.GetDefensiveStanceFactor());
                    chance *= AdaptiveAISettings.GetTacticalModeApproachDashFreqMult(mode);
                }
            }
            chance = Mathf.Clamp01(chance);
            if (UnityEngine.Random.value > chance) return;

            Vector3 dodgeDir = GetTacticalDodgeDirection(c);
            if (dodgeDir.sqrMagnitude < 0.01f) return;

            bool savedCanDash = c.canDash;
            c.canDash = true;
            CharacterMainControlDashPatches.DashIsIncomingDodge = true;
            CharacterMainControlDashPatches.IncomingDodgeDirection = dodgeDir;
            try
            {
                c.CharacterMainControl.SetMoveInput(dodgeDir);
                c.CharacterMainControl.Dash();
                anyHolder.Value = now;
            }
            finally
            {
                CharacterMainControlDashPatches.DashIsIncomingDodge = false;
                c.canDash = savedCanDash;
            }
        }

        /// <summary>Tactical dash direction: one of left/right relative to player view (horizontal). Side dash to disorient aim.</summary>
        private static Vector3 GetTacticalDodgeDirection(global::AICharacterController c)
        {
            var main = CharacterMainControl.Main;
            if (main == null) return Vector3.forward;
            Vector3 playerForward = main.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f) return Vector3.forward;
            playerForward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, playerForward);
            if (right.sqrMagnitude < 0.01f) return Vector3.forward;
            right.Normalize();
            return UnityEngine.Random.value >= 0.5f ? right : -right;
        }

        /// <summary>Direction out of player view (side/behind). Blending dash toward this gives out-of-view dash. Zero on failure.</summary>
        private static Vector3 GetOutOfPlayerViewDirection(global::AICharacterController c)
        {
            var main = CharacterMainControl.Main;
            if (main == null || c == null) return Vector3.zero;
            Vector3 playerForward = main.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f) return Vector3.zero;
            playerForward.Normalize();
            Vector3 playerToAi = c.transform.position - main.transform.position;
            playerToAi.y = 0f;
            if (playerToAi.sqrMagnitude < 0.01f) return Vector3.zero;
            playerToAi.Normalize();
            Vector3 outOfView = playerToAi - playerForward * Vector3.Dot(playerToAi, playerForward);
            if (outOfView.sqrMagnitude < 0.01f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, playerForward);
                if (right.sqrMagnitude > 0.01f) return right.normalized;
                return Vector3.zero;
            }
            return outOfView.normalized;
        }

        /// <summary>Called when noticed==true to refresh last notice time. For issue 3 search grace.</summary>
        internal static void UpdateLastNoticedTime(global::AICharacterController ai)
        {
            if (ai != null && ai.noticed)
                _lastNoticedTimeByAi.GetOrCreateValue(ai).Value = Time.time;
        }

        private static FieldInfo? _noticeFromCharacterFieldForForce;
        private static FieldInfo? _noticeFromPosFieldForForce;
        private static FieldInfo? _noticeFromDirectionFieldForForce;

        /// <summary>noticeTimeMarker·noticeFromCharacter·noticeFromPos·noticeFromDirection 리플렉션 핸들 준비.</summary>
        private static void EnsureNoticeFields()
        {
            if (_noticeTimeMarkerField == null)
                _noticeTimeMarkerField = AccessTools.Field(typeof(global::AICharacterController), "noticeTimeMarker");
            if (_noticeFromCharacterFieldForForce == null)
                _noticeFromCharacterFieldForForce = AccessTools.Field(typeof(global::AICharacterController), "noticeFromCharacter");
            if (_noticeFromPosFieldForForce == null)
                _noticeFromPosFieldForForce = AccessTools.Field(typeof(global::AICharacterController), "noticeFromPos");
            if (_noticeFromDirectionFieldForForce == null)
                _noticeFromDirectionFieldForForce = AccessTools.Field(typeof(global::AICharacterController), "noticeFromDirection");
        }

        // ===== Line of sight =====
        // 원작 정리: AICharacterController.noticed는 "봤다"가 아니라 "소리를 들었다/맞았다"(OnSound·OnHurt에서만 true).
        // 실제 시야 탐지는 BT의 SearchEnemyAround(useSight)+CheckObsticle이 담당하고, 그 결과가 searchedEnemy/hasObsticleToTarget에 들어온다.
        // 또 원작 Update는 forceTracePlayerDistance 안이면 시야와 무관하게 searchedEnemy=플레이어로 둔다(거리만 검사).
        // 따라서 noticed·searchedEnemy·소리 반경만 보고 "인지했다"로 취급하면 벽 너머 어그로가 생기므로, 여기서 직접 LOS를 검사한다.

        private sealed class SightStateHolder
        {
            internal float CheckedAt = -999f;        // 마지막으로 레이를 쏜 시각
            internal bool RawVisible;                // 거리·각도·레이 원본 판정(지연 없음)
            internal bool RawPeripheral;             // 주변시 대역에서 보이는 중인지
            internal float RawVisibleSince = -999f;  // 연속으로 RawVisible이 된 시각(사격 지연 기준)
            internal float LastSeenAt = -999f;       // 마지막으로 "보였던"(raw) 시각
            internal Vector3 LastSeenPos;            // 그때의 플레이어 위치
            internal bool HasLastSeen;
            internal float LastEngagedAt = -999f;    // 마지막으로 교전 허가(확정 시야)가 난 시각
            internal bool SearchHandedOff;           // 이번 상실에 대해 수색 지점을 이미 넘겼는지
            internal float SearchStartedAt = -999f;
            internal float LastSoundAt = -999f;      // 플레이어 소리를 마지막으로 들은 시각
            internal Vector3 LastSoundPos;           // 그 소리가 난 지점
            internal bool HasSound;
        }
        private static ConditionalWeakTable<global::AICharacterController, SightStateHolder> _sightStateByAi = new ConditionalWeakTable<global::AICharacterController, SightStateHolder>();

        /// <summary>시야를 막는 레이어 마스크. 기본은 벽만(낮은 엄폐물 너머로는 보임). SightBlockedByHalfObstacle면 반높이 장애물도 포함.</summary>
        private static bool TryGetSightBlockMask(out int layerMask)
        {
            layerMask = 0;
            try
            {
                var layers = GameplayDataSettings.Layers;
                layerMask = (int)layers.wallLayerMask;
                if (AdaptiveAISettings.SightBlockedByHalfObstacle)
                    layerMask |= (int)layers.halfObsticleLayer;
                return layerMask != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 원본 시야 판정 — 지연 없음. 거리·시야각·벽 레이만 보고, 결과는 SightCheckIntervalSeconds 동안 캐시한다.
        /// v1.3.9: 타겟 유지·추적은 이 값을 쓴다. 지연이 걸린 값(HasConfirmedSightToPlayer)으로 타겟을 지우면
        /// 지연 동안 BT의 타겟을 매 프레임 부수게 되고, 지연이 끝나도 BT가 SearchEnemyAround부터 다시 돌아
        /// 첫 사격이 눈에 띄게 늦어진다. 그래서 "보이면 즉시 타겟, 사격만 지연"으로 나눴다.
        /// </summary>
        internal static bool HasRawSightToPlayer(global::AICharacterController ai, CharacterMainControl main)
        {
            if (ai == null || main == null) return false;
            var c = ai.CharacterMainControl;
            if (c == null) return false;

            var st = _sightStateByAi.GetOrCreateValue(ai);
            float now = Time.time;
            float interval = Mathf.Max(0f, AdaptiveAISettings.SightCheckIntervalSeconds);
            if (interval <= 0f || now - st.CheckedAt >= interval)
            {
                bool wasVisible = st.RawVisible;
                st.CheckedAt = now;
                bool raw = ComputeRawSight(ai, c, main, out bool peripheral);
                if (raw)
                {
                    // 캐시 간격 보정: 실제로는 지난 검사와 이번 검사 사이 어딘가에서 보이기 시작했다.
                    // 그대로 now를 쓰면 매번 간격의 절반만큼 늦게 반응한다.
                    if (!wasVisible || st.RawVisibleSince < 0f)
                        st.RawVisibleSince = now - interval * 0.5f;
                }
                else st.RawVisibleSince = -999f;
                st.RawVisible = raw;
                st.RawPeripheral = peripheral;

                if (raw)
                {
                    st.LastSeenAt = now;
                    st.LastSeenPos = main.transform.position;
                    st.HasLastSeen = true;
                    st.SearchHandedOff = false;
                    st.SearchStartedAt = -999f;
                }
            }
            return st.RawVisible;
        }

        /// <summary>
        /// 교전(조준 고정·사격) 허가. 원본 시야가 서 있고, 그 상태가 필요한 지연만큼 유지됐을 때만 true.
        /// 지연 = max(깜빡임 방지, 최초 발견 반응시간). v1.3.9 이전에는 둘을 더해서 체감상 너무 느렸다.
        /// </summary>
        internal static bool HasConfirmedSightToPlayer(global::AICharacterController ai, CharacterMainControl main)
        {
            if (!HasRawSightToPlayer(ai, main)) return false;
            var st = _sightStateByAi.GetOrCreateValue(ai);
            float now = Time.time;

            // 이미 교전 중이던 상대를 다시 잡는 경우(재인지)는 반응시간을 다시 물리지 않는다.
            bool cold = (now - st.LastEngagedAt) > Mathf.Max(0f, AdaptiveAISettings.SightMemorySeconds);
            float acquire = Mathf.Max(0f, AdaptiveAISettings.SightAcquireDelaySeconds);
            if (st.RawPeripheral) acquire *= Mathf.Max(1f, AdaptiveAISettings.SightPeripheralAcquireMultiplier);
            float need = acquire;
            if (cold && AdaptiveAISettings.SightUseReactionDelayOnFirstSight)
                need = Mathf.Max(need, GetSightReactionDelay(ai)); // 합이 아니라 큰 쪽
            if (now - st.RawVisibleSince < need) return false;

            bool firstEngage = cold;
            st.LastEngagedAt = now;
            if (AdaptiveAISettings.DebugLogSight && firstEngage)
                Debug.Log($"[AdaptiveEnemyAI] ENGAGE: AI={ai.GetInstanceID()} dist={Vector3.Distance(ai.CharacterMainControl.transform.position, main.transform.position):F1}m peripheral={st.RawPeripheral} delay={need:F2}s");
            return true;
        }

        /// <summary>
        /// 최초 발견 시 멈칫하는 시간(초). ai.reactionTime이 기준인데, 이 값은 게임 Update가 매 프레임
        /// baseReactionTime(모드가 적응형 공격성으로 조정) × 야간계수로 다시 계산하므로 적응형 수치가 이미 반영돼 있다.
        /// SightReactionUsesAdaptiveValue면 여기에 다축 프로파일(Reactivity)·단기 패턴 보정까지 추가로 얹는다
        /// — 기존 GetReactionTimeWithMultiplier에만 있고 실제로는 어디에도 적용되지 않던 항목들이다.
        /// </summary>
        internal static float GetSightReactionDelay(global::AICharacterController ai)
        {
            if (ai == null) return AdaptiveAISettings.SightReactionDelayMin;
            float rt = Mathf.Max(0.01f, ai.reactionTime);

            if (AdaptiveAISettings.SightReactionUsesAdaptiveValue)
            {
                if (AdaptiveAISettings.MultiAxisEnabled)
                {
                    var multiAxis = PlayerBehaviorCollector.GetMultiAxisProfile();
                    if (multiAxis != null && multiAxis.IsValid)
                    {
                        float axisMult = multiAxis.GetReactionTimeMult();
                        float s = Mathf.Clamp01(AdaptiveAISettings.MultiAxisOverrideStrength);
                        rt *= Mathf.Lerp(1f, axisMult, s);
                    }
                }
                if (AdaptiveAISettings.ShortTermPatternEnabled)
                {
                    var detector = PlayerBehaviorCollector.GetShortTermDetector();
                    if (detector != null)
                    {
                        float boost = Mathf.Clamp(detector.ReactivityBoost, 0f, AdaptiveAISettings.ShortTermReactivityBoostCap);
                        rt *= Mathf.Max(0.5f, 1f - boost);
                    }
                }
            }

            rt *= Mathf.Max(0f, AdaptiveAISettings.SightReactionDelayScale);
            float min = Mathf.Max(0f, AdaptiveAISettings.SightReactionDelayMin);
            float max = Mathf.Max(min, AdaptiveAISettings.SightReactionDelayMax);
            return Mathf.Clamp(rt, min, max);
        }

        /// <summary>원본 시야 판정: 사거리 · 시야각(주변시 포함) · 눈높이 직선에 벽이 없는지.</summary>
        private static bool ComputeRawSight(global::AICharacterController ai, CharacterMainControl c, CharacterMainControl main, out bool peripheral)
        {
            peripheral = false;
            Vector3 from = c.transform.position + Vector3.up * AdaptiveAISettings.SightEyeHeight;
            Vector3 to = main.transform.position + Vector3.up * AdaptiveAISettings.SightTargetHeight;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.01f) return true;

            // ai.sightDistance는 이미 SightDistanceMultiplier/SightDistanceMinimumM이 반영된 값이다(ApplySightValues). 여기서 다시 곱하지 않는다.
            float sightRange = ai.sightDistance > 0.01f ? ai.sightDistance : 20f;
            sightRange *= Mathf.Max(0.01f, AdaptiveAISettings.SightLosRangeMultiplier);
            if (dist > sightRange) return false;

            // 시야각: 원작 SearchEnemyAround와 같은 기준(CurrentAimDirection + sightAngle 전체 각).
            if (AdaptiveAISettings.SightAngleEnabled)
            {
                Vector3 forward = c.CurrentAimDirection;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward = c.transform.forward;
                    forward.y = 0f;
                }
                Vector3 flat = to - from;
                flat.y = 0f;
                if (forward.sqrMagnitude > 0.0001f && flat.sqrMagnitude > 0.0001f)
                {
                    float ang = Vector3.Angle(forward.normalized, flat.normalized);
                    float half = Mathf.Clamp(ai.sightAngle, 30f, 360f) * 0.5f;
                    float halfPeripheral = half + Mathf.Max(0f, AdaptiveAISettings.SightPeripheralExtraDeg);
                    if (ang > halfPeripheral) return false;
                    peripheral = ang > half;
                }
            }

            // 벽 레이어만 검사한다. DefaultRaycastLayers를 쓰면 플레이어 무기·소품에 레이가 막혀 오탐이 난다(v1.3.1 이슈).
            if (!TryGetSightBlockMask(out int mask)) return true; // 게임 로드 전 등: 판정 불가 시 기존 동작 유지
            dir /= dist;
            return !Physics.Raycast(from, dir, dist - 0.2f, mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>마지막으로 플레이어를 확정 목격한 이후 경과 시간(초). 한 번도 못 봤으면 매우 큰 값.</summary>
        internal static float GetTimeSincePlayerSeen(global::AICharacterController ai)
        {
            if (ai == null) return 999f;
            if (!_sightStateByAi.TryGetValue(ai, out var st) || st == null || !st.HasLastSeen) return 999f;
            return Time.time - st.LastSeenAt;
        }

        /// <summary>플레이어가 낸 소리를 이 AI가 들었을 때 호출. 지점·시각만 기록한다 — 타겟도, 조준도 걸지 않는다.</summary>
        internal static void RecordHeardPlayerSound(global::AICharacterController ai, Vector3 soundPos)
        {
            if (ai == null || !AdaptiveAISettings.SoundTrackingEnabled) return;
            var st = _sightStateByAi.GetOrCreateValue(ai);
            float now = Time.time;
            st.LastSoundAt = now;
            st.LastSoundPos = soundPos;
            st.HasSound = true;
            // 이미 한 지점을 향해 수색 중이면 방향을 바꾸지 않는다. 총성마다 지점을 갱신하면
            // "소리가 난 곳"이 곧 플레이어 현재 위치가 되어, 수색이 아니라 추적이 된다(v1.3.9 댓글: 소리나면 무조건 돌격).
            // 단서만 기억해 두고, 이 수색이 끝날 때 TickSightMemory가 더 새로운 소리로 다음 수색을 이어간다.
            float cooldown = Mathf.Max(0f, AdaptiveAISettings.SoundSearchRetargetCooldownSeconds);
            if (st.SearchHandedOff && now - st.SearchStartedAt < cooldown)
            {
                if (AdaptiveAISettings.DebugLogSight)
                    Debug.Log($"[AdaptiveEnemyAI] 소리 기록만(수색 중 {now - st.SearchStartedAt:F1}s): AI={ai.GetInstanceID()} pos={soundPos}");
                return;
            }
            // 수색 중이 아니거나 쿨다운이 지났다 — 새 단서로 다시 가본다.
            st.SearchHandedOff = false;
            st.SearchStartedAt = -999f;
        }

        /// <summary>마지막으로 확정 목격한 위치. 없으면 false.</summary>
        internal static bool TryGetLastSeenPlayerPos(global::AICharacterController ai, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (ai == null) return false;
            if (!_sightStateByAi.TryGetValue(ai, out var st) || st == null || !st.HasLastSeen) return false;
            pos = st.LastSeenPos;
            return true;
        }

        /// <summary>
        /// 시야를 잃었을 때 마지막 목격 지점으로 수색을 넘긴다(④). 원작 BT의 경계 분기(MoveToRandomPos·SetAimToRandomDirection)가
        /// noticeFromPos·patrolPosition을 쓰므로, 새 트리를 짜지 않고 그 자산을 그대로 재사용한다.
        /// AICharacterController.Update Prefix에서 매 프레임 호출.
        /// </summary>
        internal static void TickSightMemory(global::AICharacterController ai, CharacterMainControl main)
        {
            if (!AdaptiveAISettings.RequireLineOfSightForAggro) return;
            if (ai == null || main == null) return;
            var c = ai.CharacterMainControl;
            if (c == null || c == main) return;
            if (!Team.IsEnemy(c.Team, Teams.player)) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c)) return;
            if (!_sightStateByAi.TryGetValue(ai, out var st) || st == null) return;

            // 아직 인지 중(보이거나 최근 피격)이면 수색 상태가 아니다.
            if (CanSensePlayer(ai, main)) return;
            // 다른 적과 교전 중이면 개입하지 않는다.
            if (ai.searchedEnemy != null && !IsTargetingPlayer(ai, main)) return;

            float now = Time.time;

            // 수색 지점 선택: 실제로 본 위치가 우선, 없거나 더 오래됐으면 들은 위치.
            bool sightUsable = AdaptiveAISettings.SightSearchLastSeenEnabled && st.HasLastSeen
                && now - st.LastSeenAt >= Mathf.Max(0f, AdaptiveAISettings.SightMemorySeconds);
            bool soundUsable = AdaptiveAISettings.SoundTrackingEnabled && st.HasSound
                && now - st.LastSoundAt <= Mathf.Max(0f, AdaptiveAISettings.SoundTrackMemorySeconds);
            if (!sightUsable && !soundUsable) return;

            bool useSound = soundUsable && (!sightUsable || st.LastSoundAt > st.LastSeenAt);
            Vector3 searchPos = useSound ? st.LastSoundPos : st.LastSeenPos;

            if (!st.SearchHandedOff)
            {
                st.SearchHandedOff = true;
                st.SearchStartedAt = now;
                HandOffSearchToPoint(ai, c, searchPos, useSound ? "소리" : "목격");
                return;
            }

            float duration = Mathf.Max(0f, AdaptiveAISettings.SightSearchDurationSeconds);
            if (duration > 0f && now - st.SearchStartedAt >= duration)
            {
                // 수색 중에 들린 더 새로운 소리가 아직 유효하면, 포기하지 않고 그 지점으로 다음 수색을 간다.
                // 한 번에 한 지점씩 — 플레이어가 계속 쏘면 결국 도달하지만, 실시간 위치를 따라붙지는 않는다.
                if (soundUsable && st.LastSoundAt > st.SearchStartedAt)
                {
                    st.SearchHandedOff = false;
                    st.SearchStartedAt = -999f;
                    if (AdaptiveAISettings.DebugLogSight)
                        Debug.Log($"[AdaptiveEnemyAI] search leg done → 다음 소리 지점으로: AI={ai.GetInstanceID()} pos={st.LastSoundPos}");
                    return;
                }
                st.HasLastSeen = false;
                st.HasSound = false;
                ai.noticed = false;
                ai.alert = false;
                if (AdaptiveAISettings.DebugLogSight)
                    Debug.Log($"[AdaptiveEnemyAI] search gave up: AI={ai.GetInstanceID()} → 패트롤 복귀");
            }
        }

        /// <summary>단서 지점을 "저기서 뭔가 있었다" 상태로 게임에 넘긴다. 타겟은 지우고 인지 지점만 남긴다(조준·사격 없음).</summary>
        private static void HandOffSearchToPoint(global::AICharacterController ai, CharacterMainControl c, Vector3 lastSeenPos, string reason)
        {
            EnsureNoticeFields();
            ai.searchedEnemy = null;
            ai.aimTarget = null;
            ai.noticed = true;
            ai.alert = true;
            try
            {
                // noticeFromCharacter는 비운다 — "플레이어를 노리는 중"이 아니라 "저 지점이 수상하다"여야 한다.
                _noticeFromCharacterFieldForForce?.SetValue(ai, null);
                _noticeFromPosFieldForForce?.SetValue(ai, lastSeenPos);
                Vector3 dir = lastSeenPos - c.transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f) dir.Normalize();
                else dir = c.transform.forward;
                _noticeFromDirectionFieldForForce?.SetValue(ai, dir);
                _noticeTimeMarkerField?.SetValue(ai, Time.time);
            }
            catch { }
            ai.patrolPosition = lastSeenPos;
            try { ai.MoveToPos(lastSeenPos); } catch { }
            if (AdaptiveAISettings.DebugLogSight)
                Debug.Log($"[AdaptiveEnemyAI] → 수색 이동({reason}): AI={ai.GetInstanceID()} pos={lastSeenPos}");
        }

        /// <summary>
        /// 이 AI가 플레이어를 "인지해도 되는" 상태인지. 지금 보이거나, 방금 전까지 보였거나(SightMemorySeconds),
        /// 플레이어에게 최근 피격됐으면 true. RequireLineOfSightForAggro가 false면 항상 true(기존 동작).
        /// </summary>
        internal static bool CanSensePlayer(global::AICharacterController ai, CharacterMainControl main)
        {
            if (!AdaptiveAISettings.RequireLineOfSightForAggro) return true;
            if (ai == null || main == null) return false;
            if (IsRecentlyHurtByPlayer(ai)) return true;               // 맞았으면 원작처럼 반격 허용
            if (HasRawSightToPlayer(ai, main)) return true;            // 지연 없는 원본 판정 — 보이면 즉시 타겟
            float memory = Mathf.Max(0f, AdaptiveAISettings.SightMemorySeconds);
            return memory > 0f && GetTimeSincePlayerSeen(ai) <= memory; // 모퉁이 뒤로 숨어도 잠깐은 추적
        }

        /// <summary>
        /// 교전(조준 고정·사격·근접 휘두름) 허가. "지금 보이는가"만 본다 — 기억(SightMemorySeconds)도, 최근 피격 예외도 없다.
        /// 맞았다는 사실은 쫓아올 이유는 되어도 벽 너머로 휘두르거나 쏠 이유는 아니므로, 여기서는 실제 시야만 허용.
        /// </summary>
        internal static bool CanEngagePlayer(global::AICharacterController ai, CharacterMainControl main)
        {
            if (!AdaptiveAISettings.RequireLineOfSightForAggro) return true;
            if (ai == null || main == null) return false;
            return HasConfirmedSightToPlayer(ai, main);
        }

        /// <summary>이 AI가 플레이어를 타겟(aimTarget/searchedEnemy)으로 두고 있는지.</summary>
        internal static bool IsTargetingPlayer(global::AICharacterController ai, CharacterMainControl main)
        {
            var receiver = GetPlayerDamageReceiver(main);
            if (ai == null || receiver == null) return false;
            if (ai.aimTarget != null && ai.aimTarget.gameObject == receiver.gameObject) return true;
            return ai.searchedEnemy != null && ai.searchedEnemy.gameObject == receiver.gameObject;
        }

        /// <summary>플레이어(CharacterMainControl)의 DamageReceiver. 1.3.5 등에서 mainDamageReceiver 필드가 null일 수 있어, GetComponent 폴백 사용.</summary>
        internal static DamageReceiver GetPlayerDamageReceiver(CharacterMainControl main)
        {
            if (main == null) return null;
            if (main.mainDamageReceiver != null) return main.mainDamageReceiver;
            var dr = main.GetComponent<DamageReceiver>();
            if (dr != null) return dr;
            return main.GetComponentInChildren<DamageReceiver>();
        }

        /// <summary>When game targets player (e.g. SetTarget(player)), force noticed state. Sets noticed, alert, searchedEnemy, aimTarget, noticeTimeMarker etc. so mod tracking/approach/attack logic runs. Also sets aimTarget so when only player is target from start, approach and attack pattern matches post-combat switch from other mob.</summary>
        internal static bool ForceNoticedState(global::AICharacterController ai, CharacterMainControl main)
        {
            if (ai == null || main == null) return false;
            var receiver = GetPlayerDamageReceiver(main);
            if (receiver == null) return false;
            // 시야 게이트: 벽 너머에서는 강제 인지하지 않는다. 소리(총성)·소리 반경 인지로 여기 들어온 경우도 여기서 막힌다.
            if (!CanSensePlayer(ai, main))
            {
                if (AdaptiveAISettings.DebugLogSight)
                    Debug.Log($"[AdaptiveEnemyAI] force-notice rejected (no sight): AI={ai.GetInstanceID()}");
                return false;
            }
            ai.noticed = true;
            ai.alert = true;
            ai.searchedEnemy = receiver;
            ai.aimTarget = receiver.transform;
            UpdateLastNoticedTime(ai);

            EnsureNoticeFields();

            try
            {
                // 인지 시점을 과거로 둬서 게임이 reactionTime만큼 또 기다리지 않게 한다.
                // v1.3.9: 반응 지연은 교전 허가 단계(HasConfirmedSightToPlayer)가 담당하므로 여기서 또 기다리면 안 된다.
                float reaction = Mathf.Max(0.05f, ai.reactionTime);
                _noticeTimeMarkerField?.SetValue(ai, Time.time - reaction - 0.05f);
                _noticeFromCharacterFieldForForce?.SetValue(ai, main);
                _noticeFromPosFieldForForce?.SetValue(ai, main.transform.position);
                Vector3 dir = (main.transform.position - (ai.CharacterMainControl != null ? ai.CharacterMainControl.transform.position : ai.transform.position));
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f) dir.Normalize();
                else dir = Vector3.forward;
                _noticeFromDirectionFieldForForce?.SetValue(ai, dir);
            }
            catch { }
            return true;
        }

        /// <summary>현재 미인지지만 최근 교전 유예(RecentCombatGraceSeconds) 안이면 true. 이슈3.</summary>
        internal static bool IsWithinRecentCombatGrace(global::AICharacterController ai)
        {
            if (ai == null || ai.noticed) return false;
            float grace = AdaptiveAISettings.RecentCombatGraceSeconds;
            if (grace <= 0f) return false;
            if (!_lastNoticedTimeByAi.TryGetValue(ai, out var holder) || holder == null) return false;
            return (Time.time - holder.Value) <= grace;
        }

        /// <summary>이 AI가 최근 N초 이내에 플레이어에게 피격당했으면 true. 공격 인지 시 접근·조준 허용용.</summary>
        internal static bool IsRecentlyHurtByPlayer(global::AICharacterController ai)
        {
            if (ai == null) return false;
            float grace = AdaptiveAISettings.RecentHurtByPlayerGraceSeconds;
            if (grace <= 0f) return false;
            var dmgInfo = default(DamageInfo);
            return ai.IsHurt(grace, 0, ref dmgInfo) && dmgInfo.fromCharacter == CharacterMainControl.Main;
        }

        /// <summary>이 AI가 최근 N초 이내에 플레이어가 아닌 다른 캐릭터(다른 AI 등)에게 피격당했으면 true. 어그로를 공격자 쪽으로 유지할 때 사용.</summary>
        internal static bool IsRecentlyHurtByNonPlayer(global::AICharacterController ai)
        {
            if (ai == null) return false;
            float grace = AdaptiveAISettings.RecentHurtByPlayerGraceSeconds;
            if (grace <= 0f) return false;
            var dmgInfo = default(DamageInfo);
            return ai.IsHurt(grace, 0, ref dmgInfo) && dmgInfo.fromCharacter != null && dmgInfo.fromCharacter != CharacterMainControl.Main;
        }

        /// <summary>현재 어그로(공격 대상)가 플레이어인지. true일 때만 플레이어 추적·에임 고정·이동 보정 적용. 다른 AI에게 피격된 경우 false. 1.3.5 대응: GetPlayerDamageReceiver 폴백 사용.</summary>
        internal static bool IsAggroOnPlayer(global::AICharacterController ai)
        {
            if (ai == null) return false;
            var main = CharacterMainControl.Main;
            var playerReceiver = GetPlayerDamageReceiver(main);
            if (main == null || playerReceiver == null) return false;
            if (IsRecentlyHurtByPlayer(ai)) return true;
            if (IsRecentlyHurtByNonPlayer(ai)) return false;
            // 시야 게이트: 소리(OnSound)로 noticed가 되었거나 forceTracePlayerDistance/소리 반경으로 타겟만 잡힌 경우처럼
            // "보이지 않는데 타겟만 플레이어"인 상태는 어그로로 보지 않는다. → 에임 고정·접근 이동·사격이 붙지 않음.
            if (!CanSensePlayer(ai, main)) return false;
            // 인지(noticed) 상태에서 인지한 대상이 플레이어면 추적·발사 적용. BT가 아직 searchedEnemy/aimTarget을 넣기 전에도 동작하도록.
            if (ai.noticed && ai.NoticeFromCharacter == main) return true;
            if (ai.searchedEnemy != null && ai.searchedEnemy.gameObject == playerReceiver.gameObject) return true;
            if (ai.aimTarget != null && ai.aimTarget.gameObject == playerReceiver.gameObject) return true;
            // 인지만 되었고(소리·수색 인계 등) 아무도 타겟하지 않은 상태. 게임 BT가 플레이어를 잡았으면 위에서 이미 true다.
            // 여기서는 확정 시야(반응 지연 통과)를 요구한다 — 주변시에 스친 정도로 접근 이동까지 붙이면,
            // 소리 지점으로 걸어가던 적이 모퉁이에서 플레이어를 흘끗 본 순간 돌격병이 된다. 방아쇠 게이트와 같은 기준.
            if (ai.noticed)
            {
                if (ai.searchedEnemy != null && ai.searchedEnemy.gameObject != playerReceiver.gameObject) return false;
                if (ai.aimTarget != null && ai.aimTarget.gameObject != playerReceiver.gameObject) return false;
                return !AdaptiveAISettings.RequireLineOfSightForAggro || HasConfirmedSightToPlayer(ai, main);
            }
            return false;
        }

        /// <summary>비선공몹 여부. forceTracePlayerDistance가 이 값 이하면 선공하지 않는 몹(CA_DashPatches와 동일 기준).</summary>
        private const float NonAggroForceTraceThreshold = 0.5f;
        /// <summary>비선공몹이면 true. 비선공몹은 플레이어 인지 전까지 모드 무빙/접근 등 적용 대상에서 제외할 때 사용.</summary>
        internal static bool IsNonAggro(global::AICharacterController ai)
        {
            return ai != null && ai.forceTracePlayerDistance <= NonAggroForceTraceThreshold;
        }

        /// <summary>피격 시에만 어그로 허용 프리셋(예: EnemyPreset_Melee_UltraMan 라이트맨). 소리·시야·거리로는 인지/타겟 안 하고, 플레이어에게 피격된 경우에만 타겟·공격·접근 허용.</summary>
        internal static readonly string PresetNameAggroOnlyWhenHurt = "EnemyPreset_Melee_UltraMan";
        /// <summary>해당 AI가 "피격 시에만 어그로" 프리셋이면 true.</summary>
        internal static bool IsAggroOnlyWhenHurtPreset(global::AICharacterController ai)
        {
            if (ai?.CharacterMainControl?.characterPreset == null) return false;
            return string.Equals(ai.CharacterMainControl.characterPreset.name, PresetNameAggroOnlyWhenHurt, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>터렛 정면(앞쪽 반구)에 플레이어가 있는지. dot(전방, 터렛→플레이어) > 0이면 true. 뒤에서는 인지 안 함.</summary>
        internal static bool IsPlayerInTurretFrontCone(global::AICharacterController ai, CharacterMainControl main)
        {
            if (ai == null || main == null) return false;
            Transform turretT = ai.CharacterMainControl != null ? ai.CharacterMainControl.transform : ai.transform;
            Vector3 forward = turretT.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) return true;
            forward.Normalize();
            Vector3 toPlayer = main.transform.position - turretT.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f) return true;
            toPlayer.Normalize();
            return Vector3.Dot(forward, toPlayer) > 0f;
        }
    }

    /// <summary>
    /// AI 파괴 시 우리가 추가한 산탄 보정 Modifier 제거.
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), "OnDestroy")]
    public static class AICharacterControllerOnDestroyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(global::AICharacterController __instance)
        {
            if (__instance == null) return;
            // 탈출/씬 언로드 시: 수십~수백 명 OnDestroy가 연달아 호출되며 Modifier 제거가 누적되어 3~5분 멈춤 유발. 씬 언로드 중에는 스킵.
            if (!__instance.gameObject.scene.isLoaded) return;
            if (PlayerBehaviorCollector.IsInBase()) return;

            // Feature 2: Resolve pending tactic as failure on AI death — "this tactic got me killed"
            if (Data.TacticOutcomeTracker.TryGetFor(__instance, out var tracker))
                tracker.ForceResolvePending(success: false);

            PlayerBehaviorCollector.UnregisterSpawnedEnemyAI(__instance);

            if (__instance.CharacterMainControl?.CharacterItem == null) return;
            var item = __instance.CharacterMainControl.CharacterItem;
            if (AICharacterControllerPatches.TryGetCachedScatterStat(item, out var stat))
                stat.RemoveAllModifiersFromSource(AICharacterControllerPatches.ScatterModifierSource);
            else
            {
                var s = item.GetStat("GunScatterMultiplier");
                s?.RemoveAllModifiersFromSource(AICharacterControllerPatches.ScatterModifierSource);
            }
        }
    }

    /// <summary>
    /// 펫 전용: 타겟(searchedEnemy)이 죽었거나 무효면 초기화하고 리더 쪽으로 즉시 이동 요청. 적 처치 후 펫이 가만히 있는 버그 방지.
    /// 추가: 타겟이 없을 때 경로가 없거나 끝에 도달했으면 리더 쪽으로 이동 요청(게임이 searchedEnemy만 null로 둔 경우 대비).
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), "Update")]
    public static class AICharacterController_Update_PetClearDeadTargetPrefix
    {
        [HarmonyPrefix]
        public static void Prefix(global::AICharacterController __instance)
        {
            if (__instance == null || __instance.CharacterMainControl == null) return;
            if (LevelManager.Instance?.PetCharacter != __instance.CharacterMainControl) return;
            var leader = __instance.leader;
            if (leader == null || (leader as UnityEngine.Object) == null) return;

            var se = __instance.searchedEnemy;
            if (se != null)
            {
                if ((se as UnityEngine.Object) == null) { ClearPetTargetAndResumeFollow(__instance, leader); return; }
                if (se.IsDead) { ClearPetTargetAndResumeFollow(__instance, leader); return; }
                // 전투 중에도: 경로 없음/끝 도달/리더 최근 피격 시 리더 쪽 이동 요청 — 적에게 맞았을 때·공격했을 때 펫이 제자리 고정되는 버그 방지.
            }

            // 타겟 없거나 전투 중: 경로가 없거나 이미 끝에 도달했으면 리더 쪽으로 이동 요청.
            // 플레이어(리더)가 최근 피격됐으면 경로를 매번 갱신 — 넉백/스태거로 위치가 바뀌어도 펫이 제자리 고정되지 않도록
            float lastPlayerHit = PlayerBehaviorCollector.GetLastEnemyToPlayerHitTime();
            bool leaderRecentlyHurt = leader == CharacterMainControl.Main && (Time.time - lastPlayerHit) <= 1.5f;
            if (!__instance.HasPath() || __instance.ReachedEndOfPath() || leaderRecentlyHurt)
            {
                __instance.patrolPosition = leader.transform.position;
                __instance.MoveToPos(leader.transform.position);
            }
        }

        private static void ClearPetTargetAndResumeFollow(global::AICharacterController ai, CharacterMainControl leader)
        {
            ai.searchedEnemy = null;
            ai.aimTarget = null;
            ai.noticed = false;
            ai.alert = false;
            ai.patrolPosition = leader.transform.position;
            ai.StopMove();
            ai.MoveToPos(leader.transform.position);
        }
    }

    /// <summary>
    /// 플레이어를 인식하지 않은 적은 aimTarget(플레이어) 해제 → 발사 불가. 단, 게임이 이미 플레이어를 타겟으로 둔 경우(재인지) 강제 인지 후 추적 허용.
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), "Update")]
    public static class AICharacterController_Update_NoFireWhenUnnoticedPrefix
    {
        [HarmonyPrefix]
        public static void Prefix(global::AICharacterController __instance)
        {
            if (__instance == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            var main = CharacterMainControl.Main;
            var playerReceiver = AICharacterControllerPatches.GetPlayerDamageReceiver(main);
            if (main == null || playerReceiver == null) return;

            // 시야 상실 → 마지막 목격 지점 수색 전환(④). 아래 조기 return들보다 먼저 돌아야 한다.
            AICharacterControllerPatches.TickSightMemory(__instance, main);

            var c = __instance.CharacterMainControl;
            if (c != null && c != main && Team.IsEnemy(c.Team, Teams.player)
                && !CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c))
            {
                // 첫 인지: 게임이 noticed/NoticeFromCharacter만 설정하고 aimTarget은 다음 프레임에 넣는 경우 → 즉시 타겟/인지 동기화해 첫 프레임부터 공격 가능.
                bool noticeSourceIsPlayer = __instance.noticed && __instance.NoticeFromCharacter == main;
                bool targetNotPlayer = __instance.searchedEnemy != playerReceiver
                    || __instance.aimTarget == null
                    || __instance.aimTarget.gameObject != playerReceiver.gameObject;
                if (noticeSourceIsPlayer && targetNotPlayer)
                {
                    if (!AICharacterControllerPatches.IsAggroOnlyWhenHurtPreset(__instance)
                        || AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
                    {
                        // 시야가 없으면 강제 인지가 거부된다 → 아래로 내려가 플레이어 조준을 해제한다(벽 너머 사격 방지).
                        if (AICharacterControllerPatches.ForceNoticedState(__instance, main))
                            return;
                    }
                }
            }

            if (__instance.aimTarget == null) return;
            if (__instance.aimTarget.gameObject != playerReceiver.gameObject) return;

            if (c == null || c == main) return;
            if (!Team.IsEnemy(c.Team, Teams.player)) return;

            // 피격 시에만 어그로 프리셋: 최근 피격이 아니면 플레이어 조준 해제(소리/인지로는 발사 불가).
            if (AICharacterControllerPatches.IsAggroOnlyWhenHurtPreset(__instance) && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
            {
                __instance.aimTarget = null;
                return;
            }

            // 시야 게이트: 소리로 noticed가 되었거나 거리(forceTracePlayerDistance)로 타겟만 잡힌 경우,
            // 플레이어가 실제로 보이지 않으면 조준을 해제한다. 원작처럼 소리 방향 수색만 하고 벽 너머로는 쏘지 않게 함.
            if (!AICharacterControllerPatches.CanSensePlayer(__instance, main))
            {
                __instance.aimTarget = null;
                return;
            }

            if (!__instance.noticed && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
            {
                // 비선공몹: 소리/투사체 등으로 게임이 플레이어를 타겟으로 둔 경우 강제 인지하지 않고 타겟만 해제. 어그로 발생 방지.
                if (AICharacterControllerPatches.IsNonAggro(__instance))
                {
                    __instance.aimTarget = null;
                    return;
                }
                // 게임이 SetTarget 없이 aimTarget=플레이어로 둔 경우(재인지): 강제 인지해 추적·경로·접근이 가능하게 한 뒤 aimTarget 유지.
                if (!AICharacterControllerPatches.ForceNoticedState(__instance, main))
                    __instance.aimTarget = null;
            }
        }
    }

    /// <summary>
    /// 사격 게이트: 적 AI가 플레이어를 타겟으로 두고 방아쇠를 당길 때, 아직 교전 허가(확정 시야)가 나지 않았으면 트리거를 눌러도 무시한다.
    /// v1.3.9 이전에는 이 지연을 aimTarget 해제로 구현했는데, 그러면 지연 동안 BT의 타겟이 매 프레임 지워지고
    /// 지연이 끝나도 BT가 SearchEnemyAround(비동기 탐색)부터 다시 돌아 첫 사격이 크게 늦었다.
    /// 이제 타겟·조준은 보이는 즉시 붙고(적이 플레이어 쪽으로 몸을 돌림), 방아쇠만 여기서 잡는다.
    /// 근접은 Attack() 쪽에서 같은 조건으로 막는다.
    /// </summary>
    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.Trigger))]
    public static class CharacterMainControl_Trigger_HoldFireUntilEngagedPrefix
    {
        [HarmonyPrefix]
        public static void Prefix(CharacterMainControl __instance, ref bool trigger, ref bool triggerThisFrame)
        {
            if (!trigger && !triggerThisFrame) return;
            if (!AdaptiveAISettings.RequireLineOfSightForAggro) return;
            if (__instance == null) return;
            var main = CharacterMainControl.Main;
            if (main == null || __instance == main) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (!Team.IsEnemy(__instance.Team, Teams.player)) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance)) return;

            var ai = AICharacterControllerPatches.GetAIForCharacter(__instance);
            if (ai == null) return;
            if (PerAIPatchControl.IsExcludedFromAdaptivePatches(ai)) return;
            // 타겟이 플레이어가 아니면(적 vs 적) 관여하지 않는다.
            if (!AICharacterControllerPatches.IsTargetingPlayer(ai, main)) return;
            if (AICharacterControllerPatches.CanEngagePlayer(ai, main)) return;

            trigger = false;
            triggerThisFrame = false;
        }
    }

    /// <summary>
    /// 비선공몹이 총소리/전투 소리만으로 인지(noticed)되지 않도록 OnSound 결과를 되돌림. 플레이어에게 피격된 경우만 인지 유지.
    /// AllowSoundNoticeForNonAggro가 true면 원작처럼 소리만으로 인지(기본값, 총소리에 적이 반응함). false면 비선공몹만 소리 인지 되돌림(forceTracePlayerDistance≤0.5).
    /// 참고: 프리셋에서 forceTracePlayerDistance를 지정하지 않으면 0이라 많은 적이 비선공으로 분류되므로, 기본 true 권장.
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), "OnSound")]
    public static class AICharacterControllerOnSoundPatch
    {
        [HarmonyPostfix]
        public static void Postfix(global::AICharacterController __instance, AISound sound)
        {
            if (__instance == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;

            // (A) 소리 추적: 플레이어가 낸 소리를 들을 수 있는 거리면 "그 지점"을 단서로 기록한다.
            // 원작 OnSound와 같은 조건(반경 × hearingAbility)을 그대로 쓴다. 타겟·조준은 걸지 않는다.
            if (AdaptiveAISettings.SoundTrackingEnabled && sound.fromCharacter != null
                && sound.fromCharacter == CharacterMainControl.Main
                && __instance.CharacterMainControl != null
                && Team.IsEnemy(__instance.CharacterMainControl.Team, Teams.player))
            {
                Vector3 sp = sound.pos; sp.y = 0f;
                Vector3 ap = __instance.transform.position; ap.y = 0f;
                if (Vector3.Distance(ap, sp) < sound.radius * __instance.hearingAbility)
                    AICharacterControllerPatches.RecordHeardPlayerSound(__instance, sound.pos);
            }

            // 피격 시에만 어그로 프리셋(라이트맨 등): 소리로는 절대 인지 안 함. 피격된 경우만 유지.
            if (AICharacterControllerPatches.IsAggroOnlyWhenHurtPreset(__instance))
            {
                if (!AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
                {
                    __instance.noticed = false;
                    __instance.alert = false;
                }
                return;
            }
            if (AdaptiveAISettings.AllowSoundNoticeForNonAggro) return;
            if (!AICharacterControllerPatches.IsNonAggro(__instance)) return;
            if (AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance)) return;
            __instance.noticed = false;
            __instance.alert = false;
        }
    }

    /// <summary>
    /// 다른 AI(플레이어가 아닌 캐릭터)에게 피격당했을 때만 어그로 순서 변경. 해당 공격을 한 적이 어그로 1순위로 올라가고 타겟이 됨.
    /// 기지에서는 원본 OnHurt가 세운 인지(noticed)·최근 피격(lastDamageInfo/hurtTimeMarker)을 되돌려, 피해로 인한 어그로가 발생하지 않게 함.
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), "OnHurt")]
    public static class AICharacterControllerOnHurtPatch
    {
        [HarmonyPostfix]
        public static void Postfix(global::AICharacterController __instance, DamageInfo dmgInfo)
        {
            if (__instance == null || dmgInfo.fromCharacter == null) return;

            // 기지: 피해로 인한 어그로 처리 전부 비동작. 원본 OnHurt가 이미 noticed=true 등으로 설정했으므로 되돌림.
            if (PlayerBehaviorCollector.IsInBase())
            {
                __instance.noticed = false;
                __instance.lastDamageInfo = default;
                __instance.hurtTimeMarker = 0f;
                return;
            }

            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance.CharacterMainControl)) return;
            if (dmgInfo.fromCharacter == CharacterMainControl.Main) return;
            var attacker = dmgInfo.fromCharacter;
            // 폭발·부가 피해 등으로 fromCharacter가 AI/플레이어가 아닌 경우 어그로 미적용(허공 공격 방지).
            if (!AICharacterControllerPatches.IsValidAggroDamageSource(attacker)) return;
            if (attacker.mainDamageReceiver == null) return;
            if (!Team.IsEnemy(__instance.CharacterMainControl?.Team ?? 0, attacker.Team)) return;

            // 피격 시에만: 해당 공격을 한 적을 어그로 1순위로 올리고 즉시 타겟으로 설정(어그로 순서 변경은 이 경로에서만 발생).
            var receiver = attacker.mainDamageReceiver;
            __instance.searchedEnemy = receiver;
            try { __instance.SetTarget(receiver.transform); } catch { }
            AICharacterControllerPatches.AddToAggroList(__instance, receiver);
        }
    }

    /// <summary>
    /// Update Postfix: 어그로 순서는 피격 시에만 바뀌므로, 목록에 유효한 적이 있으면 그 1순위를 타겟으로 유지(플레이어 발견 등으로 덮어쓰지 않음).
    /// 현재 타겟 처치 시 목록에서 다음 유효한 적으로 자동 전환. 목록이 비었을 때만 플레이어 등 다른 타겟 허용.
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), "Update")]
    public static class AICharacterController_Update_KeepAggroOnAttackerPostfix
    {
        [HarmonyPostfix]
        public static void Postfix(global::AICharacterController __instance)
        {
            if (__instance == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            // 펫·동료 등 제외 프리셋: 어그로/타겟 덮어쓰지 않음 → 전투 시에도 플레이어 추종 유지(제자리 고정 방지).
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance.CharacterMainControl)) return;

            // 어그로 목록에 살아 있는 적이 있으면 항상 그 순서 우선(플레이어 발견으로 순식간에 바뀌지 않도록).
            DamageReceiver nextTarget = AICharacterControllerPatches.GetNextAggroTarget(__instance);

            if (nextTarget != null)
            {
                if (__instance.searchedEnemy != nextTarget)
                    __instance.searchedEnemy = nextTarget;
                if (__instance.aimTarget == null || __instance.aimTarget.gameObject != nextTarget.gameObject)
                {
                    try { __instance.SetTarget(nextTarget.transform); } catch { }
                }
                return;
            }

            // 목록 비었을 때만: 최근에 다른 AI에게 피격된 경우 lastDamageInfo로 타겟 유지, 무효면 해제. 폭발 등 비캐릭터 원인은 제외.
            if (AICharacterControllerPatches.IsRecentlyHurtByNonPlayer(__instance))
            {
                var dmgInfo = __instance.lastDamageInfo;
                if (dmgInfo.fromCharacter != null && AICharacterControllerPatches.IsValidAggroDamageSource(dmgInfo.fromCharacter) && AICharacterControllerPatches.IsDamageReceiverAlive(dmgInfo.fromCharacter.mainDamageReceiver))
                {
                    nextTarget = dmgInfo.fromCharacter.mainDamageReceiver;
                    if (__instance.searchedEnemy != nextTarget)
                        __instance.searchedEnemy = nextTarget;
                    if (__instance.aimTarget == null || __instance.aimTarget.gameObject != nextTarget.gameObject)
                    {
                        try { __instance.SetTarget(nextTarget.transform); } catch { }
                    }
                    return;
                }
                __instance.searchedEnemy = null;
                __instance.aimTarget = null;
            }

            // 목록 비었을 때: 게임이 거리 등으로 플레이어를 타겟으로 둔 경우, "인지하지 않았는데 어그로 끌리는" 방지는 비선공몹만 적용.
            // 비선공몹: 소리/피격 없이 플레이어 타겟만 있으면 해제. 단, 플레이어가 해당 AI의 소리 감지 범위(기준반경×hearingAbility) 안이면 인지(noticed=true)로 처리해, 소리로 인지할 수 있는 조건일 때만 슬쩍 다가가도 적이 플레이어를 보도록 함.
            var main = CharacterMainControl.Main;
            var playerReceiver = AICharacterControllerPatches.GetPlayerDamageReceiver(main);
            if (main != null && playerReceiver != null)
            {
                bool targetIsPlayer = (__instance.searchedEnemy != null && __instance.searchedEnemy.gameObject == playerReceiver.gameObject)
                    || (__instance.aimTarget != null && __instance.aimTarget.gameObject == playerReceiver.gameObject);
                if (targetIsPlayer && !__instance.noticed && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
                {
                    float soundRadiusRef = AdaptiveAISettings.VisionNoticeSoundRadiusReference;
                    if (soundRadiusRef > 0f)
                    {
                        var ctrl = __instance.CharacterMainControl;
                        if (ctrl != null)
                        {
                            float dist = Vector3.Distance(ctrl.transform.position, main.transform.position);
                            float effectiveSoundRange = soundRadiusRef * __instance.hearingAbility;
                            // 소리 반경 안이어도 시야가 없으면 강제 인지는 거부된다(ForceNoticedState) → 원작처럼 소리 방향 수색만.
                            if (dist <= effectiveSoundRange && AICharacterControllerPatches.ForceNoticedState(__instance, main))
                                return;
                        }
                    }
                    if (AICharacterControllerPatches.IsNonAggro(__instance))
                    {
                        __instance.searchedEnemy = null;
                        __instance.aimTarget = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 소리만 들었을 때 발사 금지(원작: 으르렁+정찰만). 플레이어가 타겟인데 라인 오브 시이트가 없으면 searchedEnemy/aimTarget 해제 → 발사하지 않고 정찰만 함.
    /// 단, 인지(noticed) 또는 최근 플레이어 피격(IsRecentlyHurtByPlayer)이면 타겟 해제하지 않음. ADS 시 레이가 플레이어 무기 등에 막혀 LOS 오탐되는 현상 방지(v1.3.1 도입 후 이슈).
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), "Update")]
    public static class AICharacterController_Update_NoFireWhenNoLOSPostfix
    {
        [HarmonyPostfix]
        public static void Postfix(global::AICharacterController __instance)
        {
            if (!AdaptiveAISettings.NoFireWhenNoLineOfSight || __instance == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            var main = CharacterMainControl.Main;
            var playerReceiver = AICharacterControllerPatches.GetPlayerDamageReceiver(main);
            if (main == null || playerReceiver == null) return;
            if (__instance.searchedEnemy != playerReceiver) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance.CharacterMainControl)) return;
            var c = __instance.CharacterMainControl;
            if (c == null || c == main || !Team.IsEnemy(c.Team, Teams.player)) return;
            // v1.3.7: 판정을 AICharacterControllerPatches.CanSensePlayer로 일원화.
            //  - 이전에는 noticed(소리로도 true)면 무조건 통과시켜, 총성 한 발이면 LOS 검사가 사실상 꺼졌다.
            //  - 또 sightDistance 안이면 벽이 있어도 타겟을 유지해, 검사가 시야 밖에서만 동작했다.
            //  - 레이도 DefaultRaycastLayers 대신 벽 레이어만 봐서 ADS 시 무기에 막히는 오탐이 없다(그 예외가 필요 없어짐).
            if (AICharacterControllerPatches.CanSensePlayer(__instance, main)) return;
            __instance.searchedEnemy = null;
            __instance.aimTarget = null;
        }

    }

    /// <summary>
    /// 근접 공격 거리/목표 헬퍼. SetMoveInput·ApplyMeleeAttackTreeByRange 등에서 사용. (Attack/StartAction 패치 제거 후에도 이동·사거리 판정에 필요.)
    /// </summary>
    public static class CharacterMainControl_Attack_BlockMeleePrefix
    {
        /// <summary>Attack/StartAction/IsReady와 동일한 공격 허용 거리(미터). 멈춤(MeleeStopBeforeAttack) 판정에 사용.</summary>
        internal static float GetMeleeAttackMaxDistance(ItemAgent_MeleeWeapon? meleeWeapon)
        {
            if (meleeWeapon == null) return 0f;
            float baseRange = meleeWeapon.AttackRange + 0.05f;
            float maxDist = baseRange * Mathf.Clamp01(AdaptiveAISettings.MeleeAttackStartRangeRatio) * Mathf.Clamp01(AdaptiveAISettings.MeleeTriggerDistanceRatio);
            if (AdaptiveAISettings.MeleeAttackMaxDistanceCap > 0f)
                maxDist = Mathf.Min(maxDist, AdaptiveAISettings.MeleeAttackMaxDistanceCap);
            return maxDist;
        }

        /// <summary>근접 공격 시 거리/판정에 쓸 목표 위치. AI면 aimTarget → searchedEnemy → 피격 시 공격자 → 플레이어 순.</summary>
        internal static Vector3? GetMeleeAttackTargetPosition(CharacterMainControl attacker, global::AICharacterController? ai, CharacterMainControl? main)
        {
            if (ai != null)
            {
                if (ai.aimTarget != null) return ai.aimTarget.position;
                if (ai.searchedEnemy != null) return ai.searchedEnemy.transform.position;
                var noticeFrom = ai.NoticeFromCharacter;
                if (noticeFrom != null && main != null && noticeFrom != main)
                {
                    var dr = noticeFrom.mainDamageReceiver;
                    if (dr != null) return dr.transform.position;
                }
            }
            var pr = main != null ? AICharacterControllerPatches.GetPlayerDamageReceiver(main) : null;
            if (pr != null) return pr.transform.position;
            if (main != null) return main.transform.position;
            return null;
        }
    }

    /// <summary>
    /// 게임이 플레이어를 조준 대상(SetTarget(플레이어))으로 설정할 때: 미인지 상태면 강제로 인지(ForceNoticedState) 후 원본 허용해 추적 가능하게 함.
    /// BlockAimAtPlayerWhenUnnoticed가 true여도 차단하지 않고, 인지 상태로 만든 뒤 타겟 설정을 허용.
    /// </summary>
    [HarmonyPatch(typeof(global::AICharacterController), nameof(global::AICharacterController.SetTarget))]
    public static class AICharacterControllerSetTargetPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(global::AICharacterController __instance, Transform _aimTarget)
        {
            if (__instance == null) return true;
            if (PlayerBehaviorCollector.IsInBase()) return true;
            AICharacterControllerPatches.UpdateLastNoticedTime(__instance);
            if (_aimTarget == null) return true;
            var main = CharacterMainControl.Main;
            var playerReceiver = AICharacterControllerPatches.GetPlayerDamageReceiver(main);
            if (main == null || playerReceiver == null) return true;

            // 어그로 목록에 유효한 적이 있으면: 플레이어로 타겟 바꾸는 SetTarget 차단하고 목록 1순위 유지(인지 순서 존중).
            if (_aimTarget.gameObject == playerReceiver.gameObject)
            {
                var aggroTarget = AICharacterControllerPatches.GetNextAggroTarget(__instance);
                if (aggroTarget != null)
                {
                    __instance.aimTarget = aggroTarget.transform;
                    __instance.searchedEnemy = aggroTarget;
                    return false;
                }
                // 목록 비었을 때만: 최근 다른 AI에게 피격된 경우 공격자 유지.
                if (AICharacterControllerPatches.IsRecentlyHurtByNonPlayer(__instance))
                {
                    var dmgInfo = __instance.lastDamageInfo;
                    if (dmgInfo.fromCharacter != null && dmgInfo.fromCharacter.mainDamageReceiver != null)
                    {
                        __instance.aimTarget = dmgInfo.fromCharacter.mainDamageReceiver.transform;
                        return false;
                    }
                }
            }

            // 어그로가 플레이어일 때만(플레이어에게 피격 또는 실제로 플레이어를 노리는 경우) 다른 적으로 타겟이 바뀌는 것을 막고 플레이어 유지.
            if (AICharacterControllerPatches.IsAggroOnPlayer(__instance) && _aimTarget.gameObject != playerReceiver.gameObject)
            {
                __instance.aimTarget = playerReceiver.transform;
                return false;
            }

            if (_aimTarget.gameObject != playerReceiver.gameObject) return true;

            // 피격 시에만 어그로 프리셋: 플레이어 타겟은 최근 피격 시에만 허용(소리/시야/거리로는 타겟 불가).
            if (AICharacterControllerPatches.IsAggroOnlyWhenHurtPreset(__instance) && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
            {
                __instance.aimTarget = null;
                return false;
            }

            // 비선공몹: 피해를 입었을 때만 어그로. 미인지(플레이어에게 맞기 전) 상태에서 플레이어 타겟 설정은 무조건 거부.
            if (AICharacterControllerPatches.IsNonAggro(__instance) && !__instance.noticed && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
            {
                __instance.aimTarget = null;
                return false;
            }

            // 선공몹이 플레이어를 타겟으로 둘 때: 시야/거리로 타겟만 잡히고 noticed는 게임이 설정하지 않으므로, 미인지 상태면 항상 강제 인지해 추적·발사·접근이 정상 동작하도록 함.
            if (!__instance.noticed && !AICharacterControllerPatches.IsWithinRecentCombatGrace(__instance) && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(__instance))
                AICharacterControllerPatches.ForceNoticedState(__instance, main);

            return true;
        }
    }
}
