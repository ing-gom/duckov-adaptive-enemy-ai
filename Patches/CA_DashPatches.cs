using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Duckov;
using HarmonyLib;
using UnityEngine;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// 대시 종료 시 AI가 곧바로 멈추는 현상을 완화하기 위한 패치(OnStop).
    /// 적 AI가 스태미나 부족으로 대시가 아예 시작되지 않는 문제를 보정하기 위한 패치(OnStart).
    /// 회피 대시 시 방향이 MoveInput에서 읽히기 전에 BT가 덮어써 제자리 구르기가 나오므로, 캐시된 회피 방향을 OnStart 직후 적용.
    /// 비선공몹(forceTracePlayerDistance &lt;= 0)이 대시 시 플레이어 인지·선공 설정은 NonAggroDodgeSetsNoticed로 제어. 기본 비활성 시 회피만 하고 인지하지 않아 "공격 안 하고 주변만 도는" 버그 방지.
    /// CA_Dash는 CharacterActionBase 상속; actionTimer/OnUpdateAction 등은 dockov/files/CharacterActionBase.cs 참조.
    /// </summary>
    public static class CA_DashPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.CA_Dash";

        private static Harmony? _harmony;
        private static bool _applied;

        /// <summary>대시 종료 후 추가 이동 목표 거리 (미터). 0이면 게임 원작처럼 대시만으로 끝. 과도한 거리 감소를 위해 0 사용.</summary>
        private const float PostDashMoveDistance = 0f;

        /// <summary>비선공 판정: forceTracePlayerDistance가 이 값 이하면 선공하지 않는 몹으로 간주.</summary>
        private const float NonAggroForceTraceThreshold = 0.5f;

        private static FieldInfo _currentStaminaField;
        private static FieldInfo _dashDirectionField;
        private static FieldInfo _noticeFromCharacterField;
        private static FieldInfo _noticeTimeMarkerField;
        private static FieldInfo _noticeFromPosField;
        private static FieldInfo _noticeFromDirectionField;

        /// <summary>다음 단계 추측용 디버그: 대시 구간별 로그 출력 여부 (0=없음, 1=시작, 2=중간, 3=끝).</summary>
        private static readonly Dictionary<CA_Dash, int> _dashDebugLoggedPhase = new Dictionary<CA_Dash, int>();
        /// <summary>회피 대시당 OnUpdateAction 호출 횟수. 시작 로그를 업데이트 횟수 기준으로 찍기 위함.</summary>
        private static readonly Dictionary<CA_Dash, int> _dashUpdateCount = new Dictionary<CA_Dash, int>();
        /// <summary>OnStart "회피 대시 방향 강제 적용" 로그 중복 방지: 프레임당 이미 로그한 AI ID.</summary>
        private static readonly HashSet<int> _onStartLoggedAiThisFrame = new HashSet<int>();
        private static int _onStartLogFrame = -1;
        /// <summary>OnUpdateAction Postfix 진입 여부를 대시당 1회만 로그하기 위함.</summary>
        private static readonly HashSet<CA_Dash> _onUpdateActionEnteredLogged = new HashSet<CA_Dash>();
        /// <summary>대시당 회피 방향 저장. IncomingDodgeDirection은 Dash() 반환 시 Postfix에서 초기화되므로 OnUpdateAction에서는 0이라, 인스턴스별로 보관.</summary>
        private static readonly Dictionary<CA_Dash, Vector3> _dashDirectionByInstance = new Dictionary<CA_Dash, Vector3>();

        /// <summary>비선공몹이 대시로 어그로 걸었을 때, BT가 매 프레임 타겟을 덮어쓰지 않도록 유지할 시간(초).</summary>
        private const float DodgeAggroPersistDuration = 15f;

        /// <summary>대시로 플레이어 선공을 건 비선공몹 AI → 유지 종료 시각(Time.time). Update Postfix에서 이 시각까지 매 프레임 타겟 재적용.</summary>
        private static readonly ConditionalWeakTable<global::AICharacterController, DodgeAggroUntil> _dodgeAggroUntil = new ConditionalWeakTable<global::AICharacterController, DodgeAggroUntil>();

        private sealed class DodgeAggroUntil
        {
            internal float UntilTime;
        }

        /// <summary>제자리 구르기 보정 시 사용할 "실제로 움직이는" 트랜스폼. Movement가 다른 오브젝트에 붙어 있으면 그쪽이 움직이므로 해당 transform을 반환.</summary>
        private static Transform GetEffectiveMoveTransform(CharacterMainControl cc)
        {
            if (cc == null) return null;
            if (cc.movementControl != null)
                return cc.movementControl.transform;
            return cc.transform;
        }

        /// <summary>해당 씬에서 CharacterMainControl이 붙은 오브젝트와 실제로 움직이는 트랜스폼이 같은지 확인. 다르면 제자리 구르기 보정 시 위치를 잘못 읽을 수 있음.</summary>
        private static bool IsCharacterTransformSameAsMovementTransform(CharacterMainControl cc)
        {
            if (cc?.movementControl == null) return true;
            return cc.transform == cc.movementControl.transform;
        }

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(CA_DashPatches).Assembly);
                _applied = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AdaptiveEnemyAI] CA_DashPatches.ApplyPatches: {ex.Message}");
            }
        }

        public static void RemovePatches()
        {
            if (!_applied || _harmony == null) return;
            try
            {
                _harmony.UnpatchAll(HarmonyId);
                _applied = false;
                _dashDirectionByInstance.Clear();
                _dashDebugLoggedPhase.Clear();
                _dashUpdateCount.Clear();
                _onUpdateActionEnteredLogged.Clear();
            }
            catch (Exception) { }
        }

        /// <summary>비선공몹이 대시 액션을 발생시켰을 때, 해당 액션을 유발한 플레이어를 선공(타겟)하도록 설정. NonAggroDodgeSetsNoticed가 false면 미적용(회피만 하고 인지하지 않아 주변만 도는 버그 방지).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetNonAggroMobToTargetPlayer(CharacterMainControl cc)
        {
            if (cc == null) return;
            if (!AdaptiveAISettings.NonAggroDodgeSetsNoticed) return;
            var main = CharacterMainControl.Main;
            if (main == null || main.mainDamageReceiver == null) return;
            if (!Team.IsEnemy(cc.Team, Teams.player)) return;

            var ai = cc.GetComponent<global::AICharacterController>();
            if (ai == null) return;
            if (ai.forceTracePlayerDistance > NonAggroForceTraceThreshold) return;

            ai.searchedEnemy = main.mainDamageReceiver;
            ai.noticed = true;
            ai.alert = true;

            var until = _dodgeAggroUntil.GetOrCreateValue(ai);
            until.UntilTime = Time.time + DodgeAggroPersistDuration;

            if (_noticeTimeMarkerField == null)
                _noticeTimeMarkerField = AccessTools.Field(typeof(global::AICharacterController), "noticeTimeMarker");
            if (_noticeFromCharacterField == null)
                _noticeFromCharacterField = AccessTools.Field(typeof(global::AICharacterController), "noticeFromCharacter");
            if (_noticeFromPosField == null)
                _noticeFromPosField = AccessTools.Field(typeof(global::AICharacterController), "noticeFromPos");
            if (_noticeFromDirectionField == null)
                _noticeFromDirectionField = AccessTools.Field(typeof(global::AICharacterController), "noticeFromDirection");

            try
            {
                _noticeTimeMarkerField?.SetValue(ai, Time.time);
                _noticeFromCharacterField?.SetValue(ai, main);
                _noticeFromPosField?.SetValue(ai, main.transform.position);
                Vector3 dir = (main.transform.position - cc.transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f) dir.Normalize();
                else dir = Vector3.forward;
                _noticeFromDirectionField?.SetValue(ai, dir);
            }
            catch { }

            if (AdaptiveAISettings.DebugLogDash)
                Debug.Log($"[AdaptiveEnemyAI] 비선공몹 대시 → 플레이어 선공: AI={cc.GetInstanceID()} forceTrace={ai.forceTracePlayerDistance}");
        }

        /// <summary>적 AI 대시 시 스태미나가 부족하면 최소 필요량으로 보정. 게임 원본은 플레이어만 스태미나를 채우므로 적은 0인 경우가 많음.</summary>
        [HarmonyPatch(typeof(CA_Dash), "OnStart")]
        public static class CA_Dash_OnStart_Prefix
        {
            [HarmonyPrefix]
            public static void Prefix(CA_Dash __instance)
            {
                if (__instance?.characterController == null) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                var cc = __instance.characterController;
                if (cc == CharacterMainControl.Main) return;

                if (cc.CurrentStamina >= 10f) return;

                float cost = 10f;
                try
                {
                    var staminaCostField = AccessTools.Field(typeof(CA_Dash), "staminaCost");
                    if (staminaCostField != null)
                        cost = (float)(staminaCostField.GetValue(__instance) ?? 10f);
                }
                catch { }

                if (cc.CurrentStamina >= cost) return;

                if (_currentStaminaField == null)
                    _currentStaminaField = AccessTools.Field(typeof(CharacterMainControl), "currentStamina");
                if (_currentStaminaField == null) return;

                try
                {
                    _currentStaminaField.SetValue(cc, cost);
                    if (AdaptiveAISettings.DebugLogDash)
                        Debug.Log($"[AdaptiveEnemyAI] 적 대시 스태미나 보정: AI={cc.GetInstanceID()} stamina={cost}");
                }
                catch { }
            }
        }

        /// <summary>회피 대시 시 모드가 캐시한 방향을 강제 적용. CA_Dash는 OnStart에서 MoveInput을 읽는데, AI는 BT가 이미 MoveInput을 덮어써 제자리 구르기가 나올 수 있음.</summary>
        [HarmonyPatch(typeof(CA_Dash), "OnStart")]
        public static class CA_Dash_OnStart_Postfix
        {
            [HarmonyPostfix]
            public static void Postfix(CA_Dash __instance, ref bool __result)
            {
                if (!__result || __instance?.characterController == null) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                var cc = __instance.characterController;
                if (cc == CharacterMainControl.Main) return;

                // 비선공몹이 대시 액션을 발생시켰으면(회피/접근 등), 해당 액션을 유발한 플레이어를 선공하도록 설정
                SetNonAggroMobToTargetPlayer(cc);

                Vector3 cached = CharacterMainControlDashPatches.IncomingDodgeDirection;
                if (cached.sqrMagnitude < 0.01f) return;

                Vector3 dir = cached;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) return;
                dir.Normalize();

                if (_dashDirectionField == null)
                    _dashDirectionField = AccessTools.Field(typeof(CA_Dash), "dashDirection");
                if (_dashDirectionField != null)
                {
                    try { _dashDirectionField.SetValue(__instance, dir); } catch { }
                }

                _dashDirectionByInstance[__instance] = dir;

                float dashSpeed = cc.DashSpeed;
                if (dashSpeed < 0.1f)
                    dashSpeed = AdaptiveAISettings.DodgeDashSpeedFallback;
                var speedCurveProp = AccessTools.Property(typeof(CA_Dash), "speedCurve");
                var speedCurve = speedCurveProp?.GetValue(__instance) as AnimationCurve;
                float speedMult = (speedCurve != null && speedCurve.keys.Length > 0) ? speedCurve.Evaluate(0f) : 1f;
                cc.SetForceMoveVelocity(dashSpeed * speedMult * dir);
                if (!cc.DashCanControl)
                    cc.movementControl.ForceTurnTo(dir);

                // 제자리 구르기 보정: CMC 붙은 오브젝트와 실제 이동 트랜스폼이 같은지 한 번 더 확인. 다르면 OnUpdateAction에서 effective transform 기준으로 위치 보정함.
                if (!IsCharacterTransformSameAsMovementTransform(cc) && AdaptiveAISettings.DebugLogDash)
                    Debug.Log($"[AdaptiveEnemyAI] 회피 대시(제자리 구르기 보정): CMC transform과 Movement transform이 다름. AI={cc.GetInstanceID()} CMC={cc.transform.GetInstanceID()} Movement={cc.movementControl?.transform?.GetInstanceID() ?? -1}");

                // 캐시는 OnStop에서만 해제. 대시 중 매 프레임 OnUpdateAction Postfix에서 velocity 재적용용.
                if (AdaptiveAISettings.DebugLogDash)
                {
                    int frame = Time.frameCount;
                    if (frame != _onStartLogFrame) { _onStartLogFrame = frame; _onStartLoggedAiThisFrame.Clear(); }
                    int aiId = cc.GetInstanceID();
                    if (_onStartLoggedAiThisFrame.Add(aiId))
                    {
                        Transform moveT = GetEffectiveMoveTransform(cc);
                        Vector3 pos0 = moveT != null ? moveT.position : cc.transform.position;
                        Debug.Log($"[AdaptiveEnemyAI] 회피 대시 방향 강제 적용: AI={aiId} dir=({dir.x:F2},{dir.z:F2}) 초기위치=({pos0.x:F2},{pos0.z:F2})");
                    }
                }
            }
        }

        /// <summary>회피 대시 중 매 프레임 velocity만 재적용. 이동은 게임 내장 Movement(SetForceMoveVelocity → UpdateMovement)에 맡겨 벽 충돌·거리를 원작과 동일하게 유지.</summary>
        [HarmonyPatch(typeof(CA_Dash), "OnUpdateAction")]
        public static class CA_Dash_OnUpdateAction_Postfix
        {
            [HarmonyPostfix]
            public static void Postfix(CA_Dash __instance, float deltaTime)
            {
                if (__instance?.characterController == null) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                var cc = __instance.characterController;
                if (cc == CharacterMainControl.Main) return;

                if (!_dashDirectionByInstance.TryGetValue(__instance, out Vector3 cached))
                    cached = CharacterMainControlDashPatches.IncomingDodgeDirection;
                if (AdaptiveAISettings.DebugLogDash && _onUpdateActionEnteredLogged.Add(__instance))
                    Debug.Log($"[AdaptiveEnemyAI] CA_Dash OnUpdateAction 호출됨 AI={cc.GetInstanceID()} fromInstanceCache={_dashDirectionByInstance.ContainsKey(__instance)} cachedSqrMag={cached.sqrMagnitude:F3}");
                if (cached.sqrMagnitude < 0.01f) return;

                Vector3 dir = cached;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) return;
                dir.Normalize();

                float dashSpeed = cc.DashSpeed;
                if (dashSpeed < 0.1f)
                    dashSpeed = AdaptiveAISettings.DodgeDashSpeedFallback;
                var speedCurveProp = AccessTools.Property(typeof(CA_Dash), "speedCurve");
                var speedCurve = speedCurveProp?.GetValue(__instance) as AnimationCurve;
                float dashTime = 0.3f;
                try
                {
                    var dashTimeField = AccessTools.Field(typeof(CA_Dash), "dashTime");
                    if (dashTimeField != null) dashTime = (float)(dashTimeField.GetValue(__instance) ?? 0.3f);
                }
                catch { }
                float actionTimer = (__instance as CharacterActionBase)?.ActionTimer ?? 0f;
                float t = dashTime > 0.01f ? Mathf.Clamp01(actionTimer / dashTime) : 0f;
                float speedMult = (speedCurve != null && speedCurve.keys.Length > 0) ? speedCurve.Evaluate(t) : 1f;
                float speed = dashSpeed * speedMult;
                cc.SetForceMoveVelocity(speed * dir);

                if (AdaptiveAISettings.DebugLogDash)
                {
                    Transform logT = GetEffectiveMoveTransform(cc);
                    if (logT == null) logT = cc.transform;
                    if (!_dashUpdateCount.TryGetValue(__instance, out int updateCount))
                        _dashUpdateCount[__instance] = 0;
                    _dashUpdateCount[__instance] = updateCount + 1;
                    int count = _dashUpdateCount[__instance];
                    if (count == 1)
                        Debug.Log($"[AdaptiveEnemyAI] 회피 대시 OnUpdateAction 진입 AI={cc.GetInstanceID()} t={actionTimer:F2} dashTime={dashTime:F2}");
                    int phase = 0;
                    if (count <= 2) phase = 1;
                    else if (actionTimer >= dashTime * 0.3f && actionTimer <= dashTime * 0.7f) phase = 2;
                    else if (actionTimer >= dashTime - 0.15f || count >= 8) phase = 3;
                    if (phase != 0 && (!_dashDebugLoggedPhase.TryGetValue(__instance, out int last) || last < phase))
                    {
                        _dashDebugLoggedPhase[__instance] = phase;
                        Vector3 pos = logT.position;
                        Vector3 vel = cc.Velocity;
                        string phaseLabel = phase == 1 ? "시작" : (phase == 2 ? "중간" : "끝");
                        Debug.Log($"[AdaptiveEnemyAI] 회피 대시 [게임 이동] [{phaseLabel}] AI={cc.GetInstanceID()} count={count} t={actionTimer:F2} pos=({pos.x:F2},{pos.z:F2}) velocity=({vel.x:F2},{vel.z:F2}) speed={speed:F1}");
                    }
                }
            }
        }

        [HarmonyPatch(typeof(CA_Dash), "OnStop")]
        public static class CA_Dash_OnStop_Postfix
        {
            [HarmonyPostfix]
            public static void Postfix(CA_Dash __instance)
            {
                if (__instance == null || __instance.characterController == null)
                    return;
                if (PlayerBehaviorCollector.IsInBase()) return;

                _dashDebugLoggedPhase.Remove(__instance);
                _dashUpdateCount.Remove(__instance);
                _onUpdateActionEnteredLogged.Remove(__instance);
                _dashDirectionByInstance.Remove(__instance);

                var cc = __instance.characterController;
                var ai = cc.GetComponent<global::AICharacterController>();
                if (ai != null)
                    CharacterMainControlDashPatches.IncomingDodgeDirection = default;

                if (ai == null)
                    return;

                var dashDirField = AccessTools.Field(typeof(CA_Dash), "dashDirection");
                if (dashDirField == null) return;
                var dashDir = (Vector3)dashDirField.GetValue(__instance);
                if (dashDir.sqrMagnitude < 0.01f)
                    return;
                if (PostDashMoveDistance <= 0f)
                    return;

                dashDir.y = 0f;
                dashDir.Normalize();
                Transform moveT = GetEffectiveMoveTransform(cc);
                Vector3 fromPos = moveT != null ? moveT.position : cc.transform.position;
                Vector3 target = fromPos + dashDir * PostDashMoveDistance;
                ai.MoveToPos(target);
            }
        }

        /// <summary>비선공몹이 대시로 어그로 건 경우, BT가 searchedEnemy를 덮어써도 Update 직후 매 프레임 타겟을 다시 적용. NonAggroDodgeSetsNoticed가 false면 이 테이블에 진입하지 않으므로 스킵.</summary>
        [HarmonyPatch(typeof(global::AICharacterController), "Update")]
        public static class AICharacterController_Update_Postfix
        {
            [HarmonyPostfix]
            public static void Postfix(global::AICharacterController __instance)
            {
                if (__instance == null) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                if (!AdaptiveAISettings.NonAggroDodgeSetsNoticed) return;
                if (!_dodgeAggroUntil.TryGetValue(__instance, out var until) || Time.time >= until.UntilTime)
                    return;

                var cc = __instance.CharacterMainControl;
                if (cc == null || cc == CharacterMainControl.Main) return;
                var main = CharacterMainControl.Main;
                if (main == null || main.mainDamageReceiver == null) return;
                if (!Team.IsEnemy(cc.Team, Teams.player)) return;
                if (__instance.forceTracePlayerDistance > NonAggroForceTraceThreshold) return;

                __instance.searchedEnemy = main.mainDamageReceiver;
                __instance.noticed = true;
                __instance.alert = true;

                if (_noticeTimeMarkerField == null)
                    _noticeTimeMarkerField = AccessTools.Field(typeof(global::AICharacterController), "noticeTimeMarker");
                if (_noticeFromCharacterField == null)
                    _noticeFromCharacterField = AccessTools.Field(typeof(global::AICharacterController), "noticeFromCharacter");
                if (_noticeFromPosField == null)
                    _noticeFromPosField = AccessTools.Field(typeof(global::AICharacterController), "noticeFromPos");
                if (_noticeFromDirectionField == null)
                    _noticeFromDirectionField = AccessTools.Field(typeof(global::AICharacterController), "noticeFromDirection");

                try
                {
                    _noticeTimeMarkerField?.SetValue(__instance, Time.time);
                    _noticeFromCharacterField?.SetValue(__instance, main);
                    _noticeFromPosField?.SetValue(__instance, main.transform.position);
                    Vector3 dir = (main.transform.position - cc.transform.position);
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.01f) dir.Normalize();
                    else dir = Vector3.forward;
                    _noticeFromDirectionField?.SetValue(__instance, dir);
                }
                catch { }
            }
        }
    }
}
