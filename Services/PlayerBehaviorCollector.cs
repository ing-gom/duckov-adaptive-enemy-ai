using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using AdaptiveEnemyAI.Data;
using AdaptiveEnemyAI.Systems;
using AdaptiveEnemyAI.Settings;
using AdaptiveEnemyAI.Patches;
using Ducky.Sdk.GameApis;

namespace AdaptiveEnemyAI.Services
{
    /// <summary>
    /// Samples player move, aim, dash, fire, and damage (PvE/EvP) and keeps summary stats.
    /// No measurement in base; only records during combat.
    /// </summary>
    public sealed class PlayerBehaviorCollector : MonoBehaviour
    {
        /// <summary>Whether in base. If true, no behavior/damage measurement or adaptive AI. Uses LevelManager.Instance.IsBaseLevel.</summary>
        internal static bool IsInBase()
        {
            return LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel;
        }

        /// <summary>Called when invalidating combat cache (e.g. entering/leaving base).</summary>
        internal static void InvalidateCombatContextCache()
        {
            _cachedCombatContextTime = -999f;
        }

        /// <summary>Only spawned enemy AI registered for combat check and patch targets (no FindObjectsOfType).</summary>
        private static readonly List<global::AICharacterController> _spawnedEnemyAis = new List<global::AICharacterController>();
        /// <summary>For O(1) lookup. Used in patches when "process only spawned enemies".</summary>
        private static readonly HashSet<global::AICharacterController> _spawnedEnemyAiSet = new HashSet<global::AICharacterController>();
        /// <summary>Spawn frame per spawned AI (to ease load on spawn frame).</summary>
        private static readonly Dictionary<global::AICharacterController, int> _spawnFrameByAi = new Dictionary<global::AICharacterController, int>();

        /// <summary>Called on spawn: register enemy AI in combat context target list.</summary>
        internal static void RegisterSpawnedEnemyAI(global::AICharacterController ai)
        {
            if (ai == null) return;
            if (_spawnedEnemyAiSet.Contains(ai)) return;
            _spawnedEnemyAiSet.Add(ai);
            _spawnedEnemyAis.Add(ai);
            _spawnFrameByAi[ai] = Time.frameCount;
        }

        /// <summary>Called when AI destroyed: remove from list.</summary>
        internal static void UnregisterSpawnedEnemyAI(global::AICharacterController ai)
        {
            if (ai == null) return;
            _spawnedEnemyAiSet.Remove(ai);
            _spawnedEnemyAis.Remove(ai);
            _spawnFrameByAi.Remove(ai);
        }

        /// <summary>Called when mod disabled: clear list.</summary>
        internal static void ClearSpawnedEnemyAIRegistry()
        {
            _spawnedEnemyAiSet.Clear();
            _spawnedEnemyAis.Clear();
            _spawnFrameByAi.Clear();
        }

        /// <summary>Whether this AI was spawned on the current frame. Skip heavy init on spawn frame to reduce frame drop.</summary>
        internal static bool IsSpawnFrameFor(global::AICharacterController ai)
        {
            return ai != null && _spawnFrameByAi.TryGetValue(ai, out int spawnFrame) && spawnFrame == Time.frameCount;
        }

        /// <summary>Whether this AI is a spawned enemy (subject to patch logic).</summary>
        internal static bool IsSpawnedEnemyAI(global::AICharacterController ai)
        {
            return ai != null && _spawnedEnemyAiSet.Contains(ai);
        }

        /// <summary>Count of registered spawned enemy AI. If 0 right after scene change, for early return in periodic tick.</summary>
        internal static int GetSpawnedEnemyCount() => _spawnedEnemyAis.Count;

        /// <summary>Run action for each registered spawned enemy AI. (Instead of AICharacterController.Update patch, for value/logic apply.)</summary>
        internal static void ForEachSpawnedEnemyAI(System.Action<global::AICharacterController> action)
        {
            if (action == null) return;
            for (int i = 0; i < _spawnedEnemyAis.Count; i++)
            {
                var ai = _spawnedEnemyAis[i];
                if (ai != null)
                    action(ai);
            }
        }

        /// <summary>Direction and distance to nearest enemy (spawned AI) from player. Horizontal only. False if none.</summary>
        internal static bool GetNearestEnemyToPlayer(out Vector3 toEnemyFlat, out float distSq)
        {
            toEnemyFlat = Vector3.zero;
            distSq = float.MaxValue;
            if (CharacterMainControl.Main == null) return false;
            Vector3 playerPos = CharacterMainControl.Main.transform.position;
            const float maxDistSq = 25f * 25f;
            global::AICharacterController? nearest = null;
            for (int i = 0; i < _spawnedEnemyAis.Count; i++)
            {
                var ai = _spawnedEnemyAis[i];
                if (ai == null) continue;
                var cc = ai.CharacterMainControl;
                if (cc == null) continue;
                if (!Team.IsEnemy(cc.Team, Teams.player)) continue;
                float sq = (cc.transform.position - playerPos).sqrMagnitude;
                if (sq < distSq && sq <= maxDistSq)
                {
                    distSq = sq;
                    nearest = ai;
                }
            }
            if (nearest == null || nearest.CharacterMainControl == null) return false;
            Vector3 toEnemy = nearest.CharacterMainControl.transform.position - playerPos;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude < 0.0001f) return false;
            toEnemyFlat = toEnemy.normalized;
            return true;
        }

        /// <summary>Combat state cache (avoids sustained frame drop: no full list scan every frame).</summary>
        private static bool _cachedCombatContext;
        private static float _cachedCombatContextTime = -999f;
        private const float CombatContextCacheInterval = 0.15f;

        /// <summary>Combat by enemy awareness/distance. (1) Any enemy has noticed player, or (2) enemy within 25m of player. Spawned enemies only. Result cached 0.15s. Recording (combat time, dash, fire, move, aim) uses damage-based IsInDamageBasedCombatWindow().</summary>
        internal static bool IsPlayerInCombatContext()
        {
            if (CharacterMainControl.Main == null) return false;
            if (IsInBase()) return false;

            float now = Time.time;
            if (now - _cachedCombatContextTime <= CombatContextCacheInterval)
                return _cachedCombatContext;

            Vector3 playerPos = CharacterMainControl.Main.transform.position;
            const float combatRadiusSq = 25f * 25f;
            bool inCombat = false;

            for (int i = 0; i < _spawnedEnemyAis.Count; i++)
            {
                var ai = _spawnedEnemyAis[i];
                if (ai == null) continue;
                var cc = ai.CharacterMainControl;
                if (cc == null) continue;
                if (!Team.IsEnemy(cc.Team, Teams.player)) continue;
                if (ai.noticed) { inCombat = true; break; }
                if ((cc.transform.position - playerPos).sqrMagnitude <= combatRadiusSq) { inCombat = true; break; }
            }

            _cachedCombatContextTime = now;
            _cachedCombatContext = inCombat;
            return inCombat;
        }

        private const float SampleInterval = 0.1f;
        private const float EmaAlpha = 0.15f;
        private const float TimestampWindow = 60f;
        private const int MaxTimestamps = 128;

        private float _sampleTimer;
        private float _nullCleanupTimer = 1f;
        private float _emaMove = 0.5f;
        private float _emaRun = 0.5f;
        private int _welfordN;
        private float _welfordMean;
        private float _welfordM2;
        private readonly List<float> _dashTimestamps = new List<float>();
        private readonly List<float> _shootTimestamps = new List<float>();

        /// <summary>Total time (seconds) accumulated only in combat. Per-minute = accumulated / (this/60).</summary>
        private float _totalCombatTimeSeconds;
        /// <summary>PvE: combat accumulated hit count.</summary>
        private int _totalPlayerToEnemyHits;
        /// <summary>PvE: combat accumulated crit count.</summary>
        private int _totalPlayerToEnemyCrits;
        /// <summary>PvE: distance sum (average = sum / hits).</summary>
        private float _sumPlayerToEnemyDistance;
        /// <summary>EvP: combat accumulated hit count.</summary>
        private int _totalEnemyToPlayerHits;
        /// <summary>EvP: distance sum.</summary>
        private float _sumEnemyToPlayerDistance;
        /// <summary>Accumulated dash count during combat. Per minute = this / (combatTime/60).</summary>
        private int _totalDashesInCombat;
        /// <summary>Accumulated shoot count during combat.</summary>
        private int _totalShootsInCombat;

        /// <summary>Loaded combat pattern values (used in GetSummary when no session accumulation).</summary>
        private float _loadedPlayerToEnemyAvgDistanceEma;
        private float _loadedPlayerToEnemyHitsPerMin;
        private float _loadedPlayerToEnemyCritsPerMin;
        private float _loadedEnemyToPlayerHitsPerMin;
        private float _loadedEnemyToPlayerAvgDistanceEma;
        private float _loadedDashCountPerMin;
        private float _loadedShootCountPerMin;

        /// <summary>Movement pattern: move direction · enemy direction dot EMA.</summary>
        private float _emaMoveDirDot;
        /// <summary>Movement pattern: lateral component ratio EMA.</summary>
        private float _emaLateralRatio;
        private int _moveDotWelfordN;
        private float _moveDotWelfordMean;
        private float _moveDotWelfordM2;
        private float _loadedMoveDirDotToEnemyEma;
        private float _loadedLateralMoveRatioEma;
        private float _loadedMoveDirDotVariance;

        /// <summary>Player disadvantage: combat player HP ratio EMA (0~1).</summary>
        private float _emaPlayerHealthRatio = 1f;
        /// <summary>Player disadvantage: enemy count within range of player EMA (2.4 many-vs-one).</summary>
        private float _emaEnemyCountNearPlayer;
        private const float EnemyCountNearPlayerRadiusSq = 25f * 25f;

        // v1.1.8: Reload timing — last EvP hit time (no assign)
        /// <summary>Time when enemy last damaged player (Time.time). Used when pet updates path on player hit.</summary>
        internal float _lastEnemyToPlayerHitTime = -999f;

        /// <summary>Last EvP hit time. -999f if none. Used to prevent pet freeze (update path toward leader on player hit).</summary>
        internal static float GetLastEnemyToPlayerHitTime()
        {
            return Instance?._lastEnemyToPlayerHitTime ?? -999f;
        }
        /// <summary>Risky reload ratio EMA (0~1).</summary>
        private float _emaReloadRiskRatio;
        // v1.1.8: Threat response — fixed ring buffer (2s before/after)
        private const int ThreatRingSize = 50;
        private readonly float[] _ringTime = new float[ThreatRingSize];
        private readonly float[] _ringDot = new float[ThreatRingSize];
        private readonly int[] _ringDashes = new int[ThreatRingSize];
        private int _ringWrite;
        private int _ringCount;
        private float _threatResponsePendingHitTime = -1f;
        private float _threatResponseBeforeAvgDot;
        private int _threatResponseDashesAtHit;
        private float _emaThreatResponseRetreat;
        // v1.1.8: Exchange ratio
        private float _emaExchangeRatio = 1f;

        // Short-term (last N seconds) layer for "this engagement" response
        private const float ShortTermEmaAlphaReload = 0.2f;
        private const float ShortTermEmaAlphaMoveDot = 0.15f;
        /// <summary>Short-term reload risk EMA (0~1). Updated only in combat window.</summary>
        private float _shortTermReloadRiskEma = 0.5f;
        /// <summary>Edge detection for reload start events (habit tracker).</summary>
        private bool _wasReloadingLastSample;
        /// <summary>Short-term move·enemy dot EMA (-1~1). Straight approach in recent window → defensive boost.</summary>
        private float _shortTermMoveDirDotEma;

        // ---- Enhanced adaptation systems (Features 3, 4, 5) ----
        private readonly ShortTermPatternDetector _shortTermDetector = new ShortTermPatternDetector();
        private readonly PlayerHabitTracker _habitTracker = new PlayerHabitTracker();
        private readonly MultiAxisProfile _multiAxisProfile = new MultiAxisProfile();

        /// <summary>Short-term pattern detector instance. For immediate tactical modifiers.</summary>
        /// <summary>Reset ephemeral feature state on scene change. ShortTerm/Habit use absolute time/position, so stale data from previous map must be cleared.</summary>
        internal static void ResetEphemeralFeatureState()
        {
            var inst = Instance;
            if (inst == null) return;
            inst._shortTermDetector.Reset();
            inst._habitTracker.Reset();
            inst._multiAxisProfile.Reset();
        }

        internal static ShortTermPatternDetector? GetShortTermDetector() => Instance?._shortTermDetector;
        /// <summary>Player habit tracker instance. For exploitable pattern predictions.</summary>
        internal static PlayerHabitTracker? GetHabitTracker() => Instance?._habitTracker;
        /// <summary>Multi-axis profile instance. For per-axis parameter computation.</summary>
        internal static MultiAxisProfile? GetMultiAxisProfile() => Instance?._multiAxisProfile;

        private static float s_lastMoveMagnitude;
        private static Vector3 s_lastMoveInput;
        private static Vector3 s_lastAimPoint;
        private static bool s_lastAimValid;

        internal static void SetLastMoveMagnitude(float magnitude) => s_lastMoveMagnitude = magnitude;

        /// <summary>Player move input (direction + magnitude). For movement pattern collection. Also updates magnitude.</summary>
        internal static void SetLastMoveInput(Vector3 moveInput)
        {
            s_lastMoveMagnitude = moveInput.magnitude;
            s_lastMoveInput = moveInput;
        }
        internal static void SetAimPointAndDelta(Vector3 aim, out float deltaLength)
        {
            deltaLength = 0f;
            if (s_lastAimValid) deltaLength = (aim - s_lastAimPoint).magnitude;
            s_lastAimPoint = aim;
            s_lastAimValid = true;
        }

        internal static void RecordDash() => Instance?._RecordDash();
        internal static void RecordShoot() => Instance?._RecordShoot();
        internal static void RecordAimDelta(float delta) => Instance?.AddAimDelta(delta);

        private static PlayerBehaviorCollector? s_instance;
        private static PlayerBehaviorCollector? Instance => s_instance;

        /// <summary>For same instance reference on save/load (DontDestroyOnLoad singleton).</summary>
        internal static PlayerBehaviorCollector? GetInstanceForSave() => s_instance;

        /// <summary>Debug: log first OnHurt / PvE / EvP once only.</summary>
        private static bool _loggedFirstOnHurt;
        private static bool _loggedFirstP2E;
        private static bool _loggedFirstE2P;

        /// <summary>Whether we saved once at combat start in current combat window.</summary>
        private bool _savedAtCombatStart;
        /// <summary>Time of last PvE or EvP damage (either). For combat end (save) and combat window detection.</summary>
        private float _lastDamageTime = -999f;
        private const float CombatEndNoDamageSeconds = 5f;

        /// <summary>Combat count per map (for map familiarity). Restored on load, +1 on combat start, written on save.</summary>
        private readonly Dictionary<string, int> _mapCombatCounts = new Dictionary<string, int>();

        /// <summary>Whether in combat window: within 5s of last damage (PvE/EvP). Recording (combat time, dash, fire, move, aim) only in this window.</summary>
        private bool IsInDamageBasedCombatWindow() => _lastDamageTime >= 0f && (Time.time - _lastDamageTime) < CombatEndNoDamageSeconds;

        private void Awake()
        {
            s_instance = this;
            ItemAgent_Gun.OnMainCharacterShootEvent += OnMainCharacterShoot;
            Health.OnHurt += OnHurt;
#if DEBUG
            Debug.Log("[AdaptiveEnemyAI] PlayerBehaviorCollector Awake: Health.OnHurt subscribed");
#endif
        }

        private void OnDestroy()
        {
            Health.OnHurt -= OnHurt;
            ItemAgent_Gun.OnMainCharacterShootEvent -= OnMainCharacterShoot;
            s_instance = null;
        }

        private void OnHurt(Health receiverHealth, DamageInfo damageInfo)
        {
            if (receiverHealth == null) return;
            if (IsInBase()) return;
            if (CharacterMainControl.Main == null) return;

            bool wasInCombat = IsInDamageBasedCombatWindow();

            if (!_loggedFirstOnHurt)
            {
                _loggedFirstOnHurt = true;
#if DEBUG
                bool receiverIsMain = receiverHealth.IsMainCharacterHealth;
                bool fromMain = damageInfo.fromCharacter != null && damageInfo.fromCharacter.IsMainCharacter;
                Debug.Log($"[AdaptiveEnemyAI] Combat: first OnHurt (receiver=main:{receiverIsMain}, attacker=main:{fromMain}, fromCharacter null:{damageInfo.fromCharacter == null})");
#endif
            }

            var judgement = damageInfo.To(receiverHealth);
            bool playerToEnemy = judgement.IsFromMainToEnemy();
            bool enemyToPlayer = judgement.IsFromEnemyToMain();

            if (playerToEnemy)
            {
                if (!wasInCombat)
                    IncrementMapCombatCount();
                if (!_loggedFirstP2E)
                {
                    _loggedFirstP2E = true;
#if DEBUG
                    Debug.Log("[AdaptiveEnemyAI] Combat: first PvE hit recorded");
#endif
                    TrySaveAtCombatStart();
                }
                Vector3 playerPos = CharacterMainControl.Main.transform.position;
                Vector3 hitPoint = damageInfo.damagePoint;
                float dist = Vector3.Distance(new Vector3(playerPos.x, 0f, playerPos.z), new Vector3(hitPoint.x, 0f, hitPoint.z));
                bool isCrit = damageInfo.crit > 0;
                _totalPlayerToEnemyHits++;
                if (isCrit) _totalPlayerToEnemyCrits++;
                _sumPlayerToEnemyDistance += dist;
                _lastDamageTime = Time.time;
                EnsureMinCombatTime();
            }
            else if (enemyToPlayer && damageInfo.fromCharacter != null)
            {
                float hitTime = Time.time;
                _lastEnemyToPlayerHitTime = hitTime;
                if (!wasInCombat)
                    IncrementMapCombatCount();
                if (!_loggedFirstE2P)
                {
                    _loggedFirstE2P = true;
#if DEBUG
                    Debug.Log("[AdaptiveEnemyAI] Combat: first EvP hit recorded");
#endif
                    TrySaveAtCombatStart();
                }
                Vector3 enemyPos = damageInfo.fromCharacter.transform.position;
                Vector3 hitPoint = damageInfo.damagePoint;
                float dist = Vector3.Distance(new Vector3(hitPoint.x, 0f, hitPoint.z), new Vector3(enemyPos.x, 0f, enemyPos.z));
                _totalEnemyToPlayerHits++;
                _sumEnemyToPlayerDistance += dist;
                _lastDamageTime = hitTime;
                EnsureMinCombatTime();
                // v1.1.8 Threat response: save 2s-before avg dot/dashes, then compare with 2s-after to update retreat-type EMA
                float beforeSum = 0f;
                int beforeN = 0;
                for (int i = 0; i < _ringCount; i++)
                {
                    int idx = (_ringWrite - 1 - i + ThreatRingSize * 2) % ThreatRingSize;
                    float t = _ringTime[idx];
                    if (t >= hitTime - 2f && t <= hitTime)
                    {
                        beforeSum += _ringDot[idx];
                        beforeN++;
                    }
                }
                _threatResponsePendingHitTime = hitTime;
                _threatResponseBeforeAvgDot = beforeN > 0 ? beforeSum / beforeN : _emaMoveDirDot;
                _threatResponseDashesAtHit = _totalDashesInCombat;
            }
        }

        /// <summary>Current map (combat map) combat count +1. Only when not base and scene valid.</summary>
        private void IncrementMapCombatCount()
        {
            if (IsInBase()) return;
            try
            {
                Scene s = SceneManager.GetActiveScene();
                if (!s.IsValid() || string.IsNullOrEmpty(s.name)) return;
                string mapId = s.name;
                _mapCombatCounts[mapId] = _mapCombatCounts.TryGetValue(mapId, out int cur) ? cur + 1 : 1;
            }
            catch { /* ignore scene lookup failure */ }
        }

        /// <summary>Map familiarity 0~1. This map combat count / MapFamiliarityCombatCap. For aggression offset in patches.</summary>
        internal static float GetMapFamiliarity(string mapId)
        {
            if (string.IsNullOrEmpty(mapId) || !AdaptiveAISettings.MapFamiliarityEnabled) return 0f;
            var inst = Instance;
            if (inst == null) return 0f;
            float cap = Mathf.Max(1f, AdaptiveAISettings.MapFamiliarityCombatCap);
            int count = inst._mapCombatCounts.TryGetValue(mapId, out int c) ? c : 0;
            return Mathf.Clamp01((float)count / cap);
        }

        /// <summary>Current map familiarity 0~1 (active scene). Used by dodge/aim familiarity scaling.</summary>
        internal static float GetCurrentMapFamiliarity()
        {
            try
            {
                string mapId = SceneManager.GetActiveScene().name;
                return GetMapFamiliarity(mapId);
            }
            catch { return 0f; }
        }

        /// <summary>Save once at combat start (first hit). Movement-related fields keep existing file values (avoid overwriting with less-reflected EMA).</summary>
        private void TrySaveAtCombatStart()
        {
            if (_savedAtCombatStart) return;
            _savedAtCombatStart = true;
            BehaviorSaveLoadSystem.Save(preserveMovementFromExisting: true);
        }

        /// <summary>When there are hits, clamp combat time to at least 1s for per-minute calculation.</summary>
        private void EnsureMinCombatTime()
        {
            const float minCombatTimeSeconds = 1f;
            if (_totalCombatTimeSeconds < minCombatTimeSeconds)
                _totalCombatTimeSeconds = minCombatTimeSeconds;
        }

        private void OnMainCharacterShoot(ItemAgent_Gun _) => _RecordShoot();

        private void _RecordDash()
        {
            if (IsInBase()) return;
            if (!IsInDamageBasedCombatWindow()) return;
            _totalDashesInCombat++;
            float t = Time.time;
            _dashTimestamps.Add(t);
            TrimTimestamps(_dashTimestamps, t);

            // Feed short-term detector and habit tracker
            Vector3 dashDir = s_lastMoveInput.sqrMagnitude > 0.001f ? s_lastMoveInput.normalized : Vector3.zero;
            if (AdaptiveAISettings.ShortTermPatternEnabled)
                _shortTermDetector.RecordDash(dashDir);
            if (AdaptiveAISettings.HabitExploitationEnabled)
                _habitTracker.RecordDash(dashDir);
        }

        private void _RecordShoot()
        {
            if (IsInBase()) return;
            if (!IsInDamageBasedCombatWindow()) return;
            _totalShootsInCombat++;
            float t = Time.time;
            _shootTimestamps.Add(t);
            TrimTimestamps(_shootTimestamps, t);

            // Feed short-term detector and habit tracker
            if (AdaptiveAISettings.ShortTermPatternEnabled)
                _shortTermDetector.RecordShot();
            if (AdaptiveAISettings.HabitExploitationEnabled)
            {
                Vector3 playerPos = CharacterMainControl.Main != null ? CharacterMainControl.Main.transform.position : Vector3.zero;
                _habitTracker.RecordShot(playerPos);
            }
        }

        private static void TrimTimestamps(List<float> list, float now)
        {
            while (list.Count > 0 && now - list[0] > TimestampWindow) list.RemoveAt(0);
            while (list.Count > MaxTimestamps) list.RemoveAt(0);
        }

        private void Update()
        {
            if (CharacterMainControl.Main == null) return;
            // Scene change/loading: clear registry and skip (avoid lag before map load)
            if (!LevelManager.LevelInited || LevelManager.Instance == null)
            {
                if (GetSpawnedEnemyCount() > 0)
                    ClearSpawnedEnemyAIRegistry();
                return;
            }
            if (IsInBase()) return;

            // 씬 전환/탈출 중에는 Save() 스킵 (동기 세이브가 멈춤 유발 가능. 실제 저장은 OnCollectSaveData에서 처리)
            if (_savedAtCombatStart && _lastDamageTime >= 0f && (Time.time - _lastDamageTime) >= CombatEndNoDamageSeconds)
            {
                if (SceneManager.GetActiveScene().isLoaded)
                {
                    BehaviorSaveLoadSystem.Save();
                }
                _savedAtCombatStart = false;
                _lastDamageTime = -999f;
            }

            if (IsInDamageBasedCombatWindow())
                _totalCombatTimeSeconds += Time.deltaTime;

            _sampleTimer -= Time.deltaTime;
            if (_sampleTimer > 0f)
            {
                _nullCleanupTimer -= Time.deltaTime;
                if (_nullCleanupTimer <= 0f)
                {
                    _nullCleanupTimer = 1f;
                    RemoveNullsFromSpawnedEnemyList();
                }
                return;
            }
            _sampleTimer = SampleInterval;
            _nullCleanupTimer -= Time.deltaTime;
            if (_nullCleanupTimer <= 0f)
            {
                _nullCleanupTimer = 1f;
                RemoveNullsFromSpawnedEnemyList();
            }

            if (!IsInDamageBasedCombatWindow()) return;

            // 2.1 Player HP ratio sample (combat only)
            var main = CharacterMainControl.Main;
            if (main?.Health != null)
            {
                float maxHp = main.Health.MaxHealth;
                float curHp = main.Health.CurrentHealth;
                float ratio = maxHp > 0f ? Mathf.Clamp01(curHp / maxHp) : 1f;
                _emaPlayerHealthRatio = EmaAlpha * _emaPlayerHealthRatio + (1f - EmaAlpha) * ratio;
            }

            // 2.4 Concurrent enemy count: spawned enemies within 25m of player → EMA
            int countNear = CountEnemiesNearPlayer();
            _emaEnemyCountNearPlayer = EmaAlpha * _emaEnemyCountNearPlayer + (1f - EmaAlpha) * countNear;

            float now = Time.time;
            // v1.1.8 Reload timing: risky reload (reloading && (enemy>=1 or hit in last 3s)) ratio EMA
            bool isReloading = main != null && CharacterMainControlDashPatches.IsCharacterReloading(main);
            bool recentHit = (now - _lastEnemyToPlayerHitTime) <= 3f;
            bool dangerousReload = isReloading && (countNear >= 1 || recentHit);
            float reloadRiskSample = dangerousReload ? 1f : 0f;
            _emaReloadRiskRatio = EmaAlpha * _emaReloadRiskRatio + (1f - EmaAlpha) * reloadRiskSample;
            _shortTermReloadRiskEma = ShortTermEmaAlphaReload * _shortTermReloadRiskEma + (1f - ShortTermEmaAlphaReload) * reloadRiskSample;

            // Feed reload event to habit tracker (detect reload start edge)
            if (AdaptiveAISettings.HabitExploitationEnabled && isReloading && !_wasReloadingLastSample)
                _habitTracker.RecordReload();
            _wasReloadingLastSample = isReloading;

            // v1.1.8 Threat response: push (time, dot, dashes) to ring buffer
            _ringTime[_ringWrite] = now;
            _ringDot[_ringWrite] = _emaMoveDirDot;
            _ringDashes[_ringWrite] = _totalDashesInCombat;
            _ringWrite = (_ringWrite + 1) % ThreatRingSize;
            if (_ringCount < ThreatRingSize) _ringCount++;

            // After 2s: retreat-type verdict → update EMA
            if (_threatResponsePendingHitTime >= 0f && now >= _threatResponsePendingHitTime + 2f)
            {
                float afterSum = 0f;
                int afterN = 0;
                for (int i = 0; i < _ringCount; i++)
                {
                    int idx = (_ringWrite - 1 - i + ThreatRingSize * 2) % ThreatRingSize;
                    float t = _ringTime[idx];
                    if (t >= _threatResponsePendingHitTime && t <= _threatResponsePendingHitTime + 2f)
                    {
                        afterSum += _ringDot[idx];
                        afterN++;
                    }
                }
                float afterAvgDot = afterN > 0 ? afterSum / afterN : _emaMoveDirDot;
                int dashesAfter = Mathf.Max(0, _totalDashesInCombat - _threatResponseDashesAtHit);
                bool retreatType = (_threatResponseBeforeAvgDot - afterAvgDot > 0.15f) && (dashesAfter > 0);
                float retreatSample = retreatType ? 1f : 0f;
                _emaThreatResponseRetreat = EmaAlpha * _emaThreatResponseRetreat + (1f - EmaAlpha) * retreatSample;
                _threatResponsePendingHitTime = -1f;
            }

            float move = Mathf.Clamp01(s_lastMoveMagnitude);
            _emaMove = EmaAlpha * _emaMove + (1f - EmaAlpha) * move;
            float run = CharacterMainControl.Main.Running ? 1f : 0f;
            _emaRun = EmaAlpha * _emaRun + (1f - EmaAlpha) * run;

            // Movement pattern: in combat + has move input + nearest enemy → dot, lateral ratio, variance sample
            const float moveMinSq = 0.01f;
            if (s_lastMoveInput.sqrMagnitude >= moveMinSq && GetNearestEnemyToPlayer(out Vector3 toEnemy, out _))
            {
                Vector3 moveFlat = s_lastMoveInput;
                moveFlat.y = 0f;
                if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = Vector3.forward;
                moveFlat.Normalize();
                float dot = Vector3.Dot(moveFlat, toEnemy);
                _emaMoveDirDot = EmaAlpha * _emaMoveDirDot + (1f - EmaAlpha) * Mathf.Clamp(dot, -1f, 1f);
                float along = dot;
                Vector3 lateral = moveFlat - toEnemy * along;
                float lateralMag = lateral.magnitude;
                float moveMag = moveFlat.magnitude;
                float lateralRatio = (moveMag > 0.0001f) ? Mathf.Clamp01(lateralMag / moveMag) : 0f;
                _emaLateralRatio = EmaAlpha * _emaLateralRatio + (1f - EmaAlpha) * lateralRatio;
                _moveDotWelfordN++;
                float delta = dot - _moveDotWelfordMean;
                _moveDotWelfordMean += delta / _moveDotWelfordN;
                _moveDotWelfordM2 += delta * (dot - _moveDotWelfordMean);

                // Feed short-term detector and habit tracker with movement data
                if (AdaptiveAISettings.ShortTermPatternEnabled)
                    _shortTermDetector.RecordMovement(moveFlat, move, dot);
                if (AdaptiveAISettings.HabitExploitationEnabled)
                {
                    Vector3 playerPos = main != null ? main.transform.position : Vector3.zero;
                    float hpRatio = (main?.Health != null && main.Health.MaxHealth > 0f)
                        ? Mathf.Clamp01(main.Health.CurrentHealth / main.Health.MaxHealth) : 1f;
                    _habitTracker.SampleMovement(playerPos, moveFlat, move, dot, hpRatio);
                }
            }
            _shortTermMoveDirDotEma = ShortTermEmaAlphaMoveDot * _shortTermMoveDirDotEma + (1f - ShortTermEmaAlphaMoveDot) * Mathf.Clamp(_emaMoveDirDot, -1f, 1f);

            // Tick enhanced adaptation systems
            if (AdaptiveAISettings.ShortTermPatternEnabled)
                _shortTermDetector.Tick();
            if (AdaptiveAISettings.MultiAxisEnabled)
                _multiAxisProfile.ComputeFrom(GetSummary());

            TrimTimestamps(_dashTimestamps, now);
            TrimTimestamps(_shootTimestamps, now);
        }

        /// <summary>Remove destroyed (null) AI from spawned list then rebuild Set from list. (null cannot be removed from Set, so clean list then rebuild Set.)</summary>
        private static void RemoveNullsFromSpawnedEnemyList()
        {
            for (int i = _spawnedEnemyAis.Count - 1; i >= 0; i--)
            {
                if (_spawnedEnemyAis[i] == null)
                    _spawnedEnemyAis.RemoveAt(i);
            }
            _spawnedEnemyAiSet.Clear();
            for (int i = 0; i < _spawnedEnemyAis.Count; i++)
            {
                var ai = _spawnedEnemyAis[i];
                if (ai != null)
                    _spawnedEnemyAiSet.Add(ai);
            }
        }

        internal void AddAimDelta(float deltaLength)
        {
            if (IsInBase()) return;
            if (!IsInDamageBasedCombatWindow()) return;
            _welfordN++;
            float delta = deltaLength - _welfordMean;
            _welfordMean += delta / _welfordN;
            _welfordM2 += delta * (deltaLength - _welfordMean);
        }

        /// <summary>Summary cache (computed once per frame, no alloc).</summary>
        private readonly BehaviorProfileSummary _cachedSummary = new BehaviorProfileSummary();
        private int _cachedSummaryFrame = -1;

        /// <summary>Current summary. Returns cache on same-frame re-call (skip recompute).</summary>
        public BehaviorProfileSummary GetSummary()
        {
            int frame = Time.frameCount;
            if (frame == _cachedSummaryFrame)
                return _cachedSummary;

            float now = Time.time;
            TrimTimestamps(_dashTimestamps, now);
            TrimTimestamps(_shootTimestamps, now);
            _cachedSummaryFrame = frame;
            _FillSummaryInto(_cachedSummary);
            return _cachedSummary;
        }

        private void _FillSummaryInto(BehaviorProfileSummary s)
        {
            float totalCombatMinutes = _totalCombatTimeSeconds / 60f;
            float dashPerMin = totalCombatMinutes > 0f && _totalDashesInCombat > 0
                ? _totalDashesInCombat / totalCombatMinutes
                : _loadedDashCountPerMin;
            float shootPerMin = totalCombatMinutes > 0f && _totalShootsInCombat > 0
                ? _totalShootsInCombat / totalCombatMinutes
                : _loadedShootCountPerMin;
            float variance = _welfordN > 1 ? _welfordM2 / (_welfordN - 1) : 0f;
            float avgDistP2E = _loadedPlayerToEnemyAvgDistanceEma;
            float hitsPerMinP2E = _loadedPlayerToEnemyHitsPerMin;
            float critsPerMinP2E = _loadedPlayerToEnemyCritsPerMin;
            bool hasP2E = totalCombatMinutes > 0f && _totalPlayerToEnemyHits > 0;
            if (hasP2E)
            {
                avgDistP2E = _sumPlayerToEnemyDistance / _totalPlayerToEnemyHits;
                hitsPerMinP2E = _totalPlayerToEnemyHits / totalCombatMinutes;
                critsPerMinP2E = _totalPlayerToEnemyCrits / totalCombatMinutes;
            }

            float avgDistE2P = _loadedEnemyToPlayerAvgDistanceEma;
            float hitsPerMinE2P = _loadedEnemyToPlayerHitsPerMin;
            bool hasE2P = totalCombatMinutes > 0f && _totalEnemyToPlayerHits > 0;
            if (hasE2P)
            {
                avgDistE2P = _sumEnemyToPlayerDistance / _totalEnemyToPlayerHits;
                hitsPerMinE2P = _totalEnemyToPlayerHits / totalCombatMinutes;
            }

            s.MoveStrengthEma = _emaMove;
            s.RunRatioEma = _emaRun;
            s.AimChangeVariance = variance;
            s.AimSampleCount = _welfordN;
            s.DashCountPerMin = dashPerMin;
            s.ShootCountPerMin = shootPerMin;
            s.PlayerToEnemyAvgDistanceEma = avgDistP2E;
            s.PlayerToEnemyHitsPerMin = hitsPerMinP2E;
            s.PlayerToEnemyCritsPerMin = critsPerMinP2E;
            s.EnemyToPlayerHitsPerMin = hitsPerMinE2P;
            s.EnemyToPlayerAvgDistanceEma = avgDistE2P;
            s.HasCombatSamplesP2E = hasP2E || (_loadedPlayerToEnemyHitsPerMin > 0f);
            s.HasCombatSamplesE2P = hasE2P || (_loadedEnemyToPlayerHitsPerMin > 0f);

            float moveDotVar = _moveDotWelfordN > 1 ? _moveDotWelfordM2 / (_moveDotWelfordN - 1) : _loadedMoveDirDotVariance;
            s.MoveDirDotToEnemyEma = _moveDotWelfordN >= 2 ? _emaMoveDirDot : _loadedMoveDirDotToEnemyEma;
            s.LateralMoveRatioEma = _moveDotWelfordN >= 2 ? _emaLateralRatio : _loadedLateralMoveRatioEma;
            s.MoveDirDotVariance = moveDotVar;

            s.PlayerHealthRatioEma = _emaPlayerHealthRatio;
            s.EnemyCountNearPlayerEma = _emaEnemyCountNearPlayer;

            // v1.1.8
            s.ReloadRiskRatioEma = _emaReloadRiskRatio;
            s.ThreatResponseRetreatTendencyEma = _emaThreatResponseRetreat;
            s.LastEnemyToPlayerHitTime = _lastEnemyToPlayerHitTime;
            const float exchangeEpsilon = 1e-5f;
            float exchangeRatio = (hitsPerMinE2P + exchangeEpsilon) > 0f
                ? Mathf.Clamp(hitsPerMinP2E / (hitsPerMinE2P + exchangeEpsilon), 0f, 10f)
                : 1f;
            if (hasP2E || hasE2P)
                _emaExchangeRatio = EmaAlpha * _emaExchangeRatio + (1f - EmaAlpha) * exchangeRatio;
            s.ExchangeRatioEma = _emaExchangeRatio;

            // Short-term (last N seconds) for "this engagement" correction
            float shortTermWindow = Mathf.Max(1f, AdaptiveAISettings.ShortTermWindowSeconds);
            float now = Time.time;
            bool shortTermValid = _lastDamageTime >= 0f && (now - _lastDamageTime) <= shortTermWindow + 3f;
            if (shortTermValid)
            {
                int dashCountInWindow = 0;
                for (int i = 0; i < _dashTimestamps.Count; i++)
                {
                    if (_dashTimestamps[i] >= now - shortTermWindow)
                        dashCountInWindow++;
                }
                s.ShortTermDashesPerMin = (dashCountInWindow / (shortTermWindow / 60f));
                s.ShortTermReloadRiskEma = _shortTermReloadRiskEma;
                s.ShortTermMoveDirDotEma = _shortTermMoveDirDotEma;
            }
            else
            {
                s.ShortTermDashesPerMin = 0f;
                s.ShortTermReloadRiskEma = 0.5f;
                s.ShortTermMoveDirDotEma = 0f;
            }
        }

        /// <summary>Count of spawned enemies (enemy team) within EnemyCountNearPlayerRadiusSq (25m) of player.</summary>
        private int CountEnemiesNearPlayer()
        {
            if (CharacterMainControl.Main == null) return 0;
            Vector3 playerPos = CharacterMainControl.Main.transform.position;
            int count = 0;
            for (int i = 0; i < _spawnedEnemyAis.Count; i++)
            {
                var ai = _spawnedEnemyAis[i];
                if (ai == null) continue;
                var cc = ai.CharacterMainControl;
                if (cc == null) continue;
                if (!Team.IsEnemy(cc.Team, Teams.player)) continue;
                if ((cc.transform.position - playerPos).sqrMagnitude <= EnemyCountNearPlayerRadiusSq)
                    count++;
            }
            return count;
        }

        /// <summary>Build DTO for save. Also saves combat accumulations so load can continue accumulating.</summary>
        public BehaviorProfileSaveData ToSaveData()
        {
            var s = GetSummary();
            var mapCountsList = new List<MapCombatEntry>();
            foreach (var kv in _mapCombatCounts)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                mapCountsList.Add(new MapCombatEntry { MapId = kv.Key, CombatCount = kv.Value });
            }
            return new BehaviorProfileSaveData
            {
                Version = 9,
                MoveStrengthEma = s.MoveStrengthEma,
                RunRatioEma = s.RunRatioEma,
                AimChangeVariance = s.AimChangeVariance,
                DashCountPerMin = s.DashCountPerMin,
                ShootCountPerMin = s.ShootCountPerMin,
                PlayerToEnemyAvgDistanceEma = s.PlayerToEnemyAvgDistanceEma,
                PlayerToEnemyHitsPerMin = s.PlayerToEnemyHitsPerMin,
                PlayerToEnemyCritsPerMin = s.PlayerToEnemyCritsPerMin,
                EnemyToPlayerHitsPerMin = s.EnemyToPlayerHitsPerMin,
                EnemyToPlayerAvgDistanceEma = s.EnemyToPlayerAvgDistanceEma,
                TotalCombatTimeSeconds = _totalCombatTimeSeconds,
                TotalPlayerToEnemyHits = _totalPlayerToEnemyHits,
                TotalPlayerToEnemyCrits = _totalPlayerToEnemyCrits,
                SumPlayerToEnemyDistance = _sumPlayerToEnemyDistance,
                TotalEnemyToPlayerHits = _totalEnemyToPlayerHits,
                SumEnemyToPlayerDistance = _sumEnemyToPlayerDistance,
                TotalDashesInCombat = _totalDashesInCombat,
                TotalShootsInCombat = _totalShootsInCombat,
                MoveDirDotToEnemyEma = s.MoveDirDotToEnemyEma,
                LateralMoveRatioEma = s.LateralMoveRatioEma,
                MoveDirDotVariance = s.MoveDirDotVariance,
                SavedAtUtcTicks = DateTime.UtcNow.Ticks,
                MapCombatCounts = mapCountsList,
                ReloadRiskRatioEma = s.ReloadRiskRatioEma,
                ThreatResponseRetreatTendencyEma = s.ThreatResponseRetreatTendencyEma,
                // V9: Player habit data
                HabitDashHistogram = _habitTracker.GetDashHistogramForSave(),
                HabitTotalDashSamples = _habitTracker.TotalDashSamples,
                HabitPostShootBehavior = _habitTracker.GetPostShootBehaviorForSave(),
                HabitPostShootSamples = _habitTracker.PostShootSamples,
                HabitFleeHpSum = _habitTracker.FleeHpSum,
                HabitFleeObservations = _habitTracker.FleeObservations
            };
        }

        /// <summary>Returns current summary (null if no collector).</summary>
        public static BehaviorProfileSummary? GetCurrentSummary() => Instance?.GetSummary();

        /// <summary>Recent player fire timing pattern. False if not in combat or fewer than 2 timestamps. Returns avgInterval(s), inBurst, nextShotExpected.</summary>
        internal static bool GetRecentShootPattern(out float avgInterval, out bool inBurst, out float nextShotExpected)
        {
            avgInterval = 0.2f;
            inBurst = false;
            nextShotExpected = 0f;
            var inst = Instance;
            if (inst == null) return false;
            return inst._GetRecentShootPattern(out avgInterval, out inBurst, out nextShotExpected);
        }

        private bool _GetRecentShootPattern(out float avgInterval, out bool inBurst, out float nextShotExpected)
        {
            avgInterval = 0.2f;
            inBurst = false;
            nextShotExpected = 0f;
            if (!IsInDamageBasedCombatWindow()) return false;
            float now = Time.time;
            const float window = 2f;
            const float burstWindow = 0.5f;
            const float burstIntervalMax = 0.4f;
            const int maxCount = 10;

            int n = _shootTimestamps.Count;
            if (n < 2) return false;

            // Copy only timestamps within last window seconds (newest first)
            int collected = 0;
            float first = 0f, last = 0f;
            float sumInterval = 0f;
            int intervalCount = 0;
            int inBurstWindowCount = 0;

            for (int i = n - 1; i >= 0 && collected < maxCount; i--)
            {
                float t = _shootTimestamps[i];
                if (now - t > window) break;
                if (collected == 0) last = t;
                first = t;
                collected++;
                if (now - t <= burstWindow) inBurstWindowCount++;
                if (i > 0 && now - _shootTimestamps[i - 1] <= window)
                {
                    float interval = t - _shootTimestamps[i - 1];
                    if (interval > 0f && interval < 5f)
                    {
                        sumInterval += interval;
                        intervalCount++;
                    }
                }
            }
            if (collected < 2) return false;

            avgInterval = intervalCount > 0 ? sumInterval / intervalCount : (last - first) / (collected - 1);
            if (avgInterval < 0.05f) avgInterval = 0.2f;
            inBurst = inBurstWindowCount >= 2 || (intervalCount > 0 && avgInterval < burstIntervalMax);
            nextShotExpected = last + (inBurst ? 0.15f : Mathf.Min(avgInterval, 1.5f));
            return true;
        }

        /// <summary>Initialize summary (EMA etc.) from loaded data. V3+: restore combat accumulations then keep accumulating. V2 and below: reset accumulations and use loaded per-minute only. HP ratio and enemy count are runtime-only, so init to 1f/0 on load.</summary>
        public void LoadFromSaveData(BehaviorProfileSaveData? data)
        {
            if (data == null) return;
            _emaPlayerHealthRatio = 1f;
            _emaEnemyCountNearPlayer = 0f;
            _emaMove = data.MoveStrengthEma;
            _emaRun = data.RunRatioEma;
            _welfordMean = 0f;
            _welfordM2 = data.AimChangeVariance;
            _welfordN = 2;
            _dashTimestamps.Clear();
            _shootTimestamps.Clear();

            if (data.Version >= 3)
            {
                _totalCombatTimeSeconds = data.TotalCombatTimeSeconds;
                _totalPlayerToEnemyHits = data.TotalPlayerToEnemyHits;
                _totalPlayerToEnemyCrits = data.TotalPlayerToEnemyCrits;
                _sumPlayerToEnemyDistance = data.SumPlayerToEnemyDistance;
                _totalEnemyToPlayerHits = data.TotalEnemyToPlayerHits;
                _sumEnemyToPlayerDistance = data.SumEnemyToPlayerDistance;
                _totalDashesInCombat = data.TotalDashesInCombat;
                _totalShootsInCombat = data.TotalShootsInCombat;
            }
            else
            {
                _totalCombatTimeSeconds = 0f;
                _totalPlayerToEnemyHits = 0;
                _totalPlayerToEnemyCrits = 0;
                _sumPlayerToEnemyDistance = 0f;
                _totalEnemyToPlayerHits = 0;
                _sumEnemyToPlayerDistance = 0f;
                _totalDashesInCombat = 0;
                _totalShootsInCombat = 0;
            }

            if (data.Version >= 2)
            {
                _loadedPlayerToEnemyAvgDistanceEma = data.PlayerToEnemyAvgDistanceEma;
                _loadedPlayerToEnemyHitsPerMin = data.PlayerToEnemyHitsPerMin;
                _loadedPlayerToEnemyCritsPerMin = data.PlayerToEnemyCritsPerMin;
                _loadedEnemyToPlayerHitsPerMin = data.EnemyToPlayerHitsPerMin;
                _loadedEnemyToPlayerAvgDistanceEma = data.EnemyToPlayerAvgDistanceEma;
                _loadedDashCountPerMin = data.DashCountPerMin;
                _loadedShootCountPerMin = data.ShootCountPerMin;
            }
            else
            {
                _loadedPlayerToEnemyAvgDistanceEma = 0f;
                _loadedPlayerToEnemyHitsPerMin = 0f;
                _loadedPlayerToEnemyCritsPerMin = 0f;
                _loadedEnemyToPlayerHitsPerMin = 0f;
                _loadedEnemyToPlayerAvgDistanceEma = 0f;
                _loadedDashCountPerMin = 0f;
                _loadedShootCountPerMin = 0f;
            }

            if (data.Version >= 4)
            {
                _loadedMoveDirDotToEnemyEma = data.MoveDirDotToEnemyEma;
                _loadedLateralMoveRatioEma = data.LateralMoveRatioEma;
                _loadedMoveDirDotVariance = data.MoveDirDotVariance;
                _emaMoveDirDot = data.MoveDirDotToEnemyEma;
                _emaLateralRatio = data.LateralMoveRatioEma;
                _moveDotWelfordM2 = data.MoveDirDotVariance;
                _moveDotWelfordN = 2;
                _moveDotWelfordMean = 0f;
            }
            else
            {
                _loadedMoveDirDotToEnemyEma = 0f;
                _loadedLateralMoveRatioEma = 0f;
                _loadedMoveDirDotVariance = 0f;
            }

            // V6: player disadvantage (HP, enemy count) not saved → above already set _emaPlayerHealthRatio=1f, _emaEnemyCountNearPlayer=0f

            if (data.Version >= 7 && data.MapCombatCounts != null)
            {
                _mapCombatCounts.Clear();
                foreach (var e in data.MapCombatCounts)
                {
                    if (string.IsNullOrEmpty(e.MapId)) continue;
                    _mapCombatCounts[e.MapId] = e.CombatCount;
                }
            }
            else if (data.Version < 7)
                _mapCombatCounts.Clear();

            if (data.Version >= 8)
            {
                _emaReloadRiskRatio = data.ReloadRiskRatioEma;
                _emaThreatResponseRetreat = data.ThreatResponseRetreatTendencyEma;
            }
            else
            {
                _emaReloadRiskRatio = 0f;
                _emaThreatResponseRetreat = 0f;
            }
            _threatResponsePendingHitTime = -1f;

            // V9: Player habit data
            if (data.Version >= 9)
            {
                _habitTracker.LoadFromSave(
                    data.HabitDashHistogram, data.HabitTotalDashSamples,
                    data.HabitPostShootBehavior, data.HabitPostShootSamples,
                    data.HabitFleeHpSum, data.HabitFleeObservations);
            }

            // V5: time decay by save time — older patterns blend toward neutral
            float decay = GetProfileDecayFactor(data);
            if (decay < 1f)
            {
                const float defMove = 0.5f, defRun = 0.5f;
                const float defDashPerMin = 3f, defShootPerMin = 30f;
                const float defP2EDist = 10f, defP2EHits = 30f, defP2ECrits = 5f;
                const float defE2PHits = 15f, defE2PDist = 10f;
                const float defMoveDot = 0f, defLateral = 0.3f, defMoveVar = 0.2f;

                _emaMove = decay * _emaMove + (1f - decay) * defMove;
                _emaRun = decay * _emaRun + (1f - decay) * defRun;
                _loadedPlayerToEnemyAvgDistanceEma = decay * _loadedPlayerToEnemyAvgDistanceEma + (1f - decay) * defP2EDist;
                _loadedPlayerToEnemyHitsPerMin = decay * _loadedPlayerToEnemyHitsPerMin + (1f - decay) * defP2EHits;
                _loadedPlayerToEnemyCritsPerMin = decay * _loadedPlayerToEnemyCritsPerMin + (1f - decay) * defP2ECrits;
                _loadedEnemyToPlayerHitsPerMin = decay * _loadedEnemyToPlayerHitsPerMin + (1f - decay) * defE2PHits;
                _loadedEnemyToPlayerAvgDistanceEma = decay * _loadedEnemyToPlayerAvgDistanceEma + (1f - decay) * defE2PDist;
                _loadedDashCountPerMin = decay * _loadedDashCountPerMin + (1f - decay) * defDashPerMin;
                _loadedShootCountPerMin = decay * _loadedShootCountPerMin + (1f - decay) * defShootPerMin;
                _loadedMoveDirDotToEnemyEma = decay * _loadedMoveDirDotToEnemyEma + (1f - decay) * defMoveDot;
                _loadedLateralMoveRatioEma = decay * _loadedLateralMoveRatioEma + (1f - decay) * defLateral;
                _loadedMoveDirDotVariance = decay * _loadedMoveDirDotVariance + (1f - decay) * defMoveVar;
                _emaMoveDirDot = _loadedMoveDirDotToEnemyEma;
                _emaLateralRatio = _loadedLateralMoveRatioEma;
                _moveDotWelfordM2 = _loadedMoveDirDotVariance;
                if (data.Version >= 8)
                {
                    _emaReloadRiskRatio = decay * _emaReloadRiskRatio;
                    _emaThreatResponseRetreat = decay * _emaThreatResponseRetreat;
                }

                _totalCombatTimeSeconds *= decay;
                _totalPlayerToEnemyHits = (int)(_totalPlayerToEnemyHits * decay + 0.5f);
                _totalPlayerToEnemyCrits = (int)(_totalPlayerToEnemyCrits * decay + 0.5f);
                _sumPlayerToEnemyDistance *= decay;
                _totalEnemyToPlayerHits = (int)(_totalEnemyToPlayerHits * decay + 0.5f);
                _sumEnemyToPlayerDistance *= decay;
                _totalDashesInCombat = (int)(_totalDashesInCombat * decay + 0.5f);
                _totalShootsInCombat = (int)(_totalShootsInCombat * decay + 0.5f);
            }
        }

        /// <summary>Decay factor by days since save time (1=latest, 0=very old). Uses half-life (days). Legacy saves (pre-V5 or no save time) use 1 (no decay); from next save onward record time and apply decay.</summary>
        private static float GetProfileDecayFactor(BehaviorProfileSaveData data)
        {
            if (data.Version < 5 || data.SavedAtUtcTicks <= 0)
                return 1f;

            float halfLife = AdaptiveAISettings.PatternHalfLifeDays;
            if (halfLife <= 0f) return 1f;

            long nowTicks = DateTime.UtcNow.Ticks;
            double ageDays = (nowTicks - data.SavedAtUtcTicks) / (double)TimeSpan.TicksPerDay;
            if (ageDays <= 0d) return 1f;
            return (float)Math.Exp(-ageDays * Math.Log(2) / halfLife);
        }
    }
}
