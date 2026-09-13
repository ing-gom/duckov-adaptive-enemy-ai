using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// AI_PathControl(경로 추종 이동) 개선: 경로 실패 알림, 빈 경로 예외 방지, 이동 속도 배율 지원.
    /// </summary>
    public static class AI_PathControlPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.AI_PathControl";

        private static Harmony? _harmony;
        private static bool _applied;

        private static Type? _pathControlType;
        private static Type? _pathType;
        private static FieldInfo? _pathField;
        private static PropertyInfo? _pathErrorProp;
        private static PropertyInfo? _pathVectorPathProp;
        private static MethodInfo? _moveToPosMethod;
        /// <summary>vectorPath 타입별 Count 프로퍼티 캐시. 매 프레임 GetType().GetProperty 호출 방지로 프레임 끊김 완화.</summary>
        private static PropertyInfo? _vectorPathCountProp;
        private static Type? _cachedVectorPathType;

        /// <summary>경로 계산 실패 시 발생. 인자는 해당 AI_PathControl 인스턴스.</summary>
        public static event Action<object>? PathFailed;

        /// <summary>인스턴스별 상태(마지막 경로 실패 여부, 속도 배율).</summary>
        private static readonly Dictionary<object, PathControlState> _stateByInstance = new Dictionary<object, PathControlState>();
        private static readonly object _stateLock = new object();

        private sealed class PathControlState
        {
            public bool LastPathFailed;
            public float SpeedMultiplier = 1f;
        }

        /// <summary>해당 PathControl에 대한 속도 배율 반환 (1f = 기본).</summary>
        public static float GetSpeedMultiplier(object pathControl)
        {
            if (pathControl == null) return 1f;
            lock (_stateLock)
            {
                if (_stateByInstance.TryGetValue(pathControl, out var state))
                    return state.SpeedMultiplier;
            }
            return 1f;
        }

        /// <summary>해당 PathControl의 속도 배율 설정 (0.1f~2f 권장).</summary>
        public static void SetSpeedMultiplier(object pathControl, float multiplier)
        {
            if (pathControl == null) return;
            multiplier = Mathf.Clamp(multiplier, 0.01f, 5f);
            lock (_stateLock)
            {
                if (!_stateByInstance.TryGetValue(pathControl, out var state))
                {
                    state = new PathControlState();
                    _stateByInstance[pathControl] = state;
                }
                state.SpeedMultiplier = multiplier;
            }
        }

        /// <summary>마지막 경로 요청이 실패했는지 여부. 확인 후 false로 리셋하는 쪽에서 사용.</summary>
        public static bool GetAndClearLastPathFailed(object pathControl)
        {
            if (pathControl == null) return false;
            lock (_stateLock)
            {
                if (!_stateByInstance.TryGetValue(pathControl, out var state))
                    return false;
                bool v = state.LastPathFailed;
                state.LastPathFailed = false;
                return v;
            }
        }

        /// <summary>캐릭터 또는 그 자식에서 PathControl 컴포넌트 반환. AI_PathControl이 CharacterMainControl 자식 오브젝트에 있는 경우 대비.</summary>
        private static object GetPathControlForCharacter(CharacterMainControl character)
        {
            if (character == null || _pathControlType == null) return null;
            try
            {
                var comp = character.GetComponent(_pathControlType);
                if (comp != null) return comp;
                return character.GetComponentInChildren(_pathControlType, true);
            }
            catch (Exception) { return null; }
        }

        /// <summary>해당 캐릭터가 현재 경로를 따라 이동 중인지. 무빙 패턴(스트라핑) 완화 등에 사용.</summary>
        public static bool IsCharacterPathFollowing(CharacterMainControl character)
        {
            if (character == null || _pathControlType == null) return false;
            try
            {
                var comp = GetPathControlForCharacter(character);
                if (comp == null) return false;
                var movingProp = AccessTools.Property(_pathControlType, "Moving");
                return movingProp != null && (bool)movingProp.GetValue(comp);
            }
            catch (Exception) { return false; }
        }

        /// <summary>해당 캐릭터가 경로 끝 구간(도착 직전)인지. 이 구간에서 스트라핑을 줄이면 목표점 도달이 자연스러움.</summary>
        public static bool IsCharacterReachedEndOfPath(CharacterMainControl character)
        {
            if (character == null || _pathControlType == null) return false;
            try
            {
                var comp = GetPathControlForCharacter(character);
                if (comp == null) return false;
                var reachedProp = AccessTools.Property(_pathControlType, "ReachedEndOfPath");
                return reachedProp != null && (bool)reachedProp.GetValue(comp);
            }
            catch (Exception) { return false; }
        }

        /// <summary>해당 캐릭터의 PathControl이 현재 경로 계산 결과를 기다리는 중인지.</summary>
        public static bool IsCharacterWaitingForPathResult(CharacterMainControl character)
        {
            if (character == null || _pathControlType == null) return false;
            try
            {
                var comp = GetPathControlForCharacter(character);
                if (comp == null) return false;
                var waitingProp = AccessTools.Property(_pathControlType, "WaitingForPathResult");
                return waitingProp != null && (bool)waitingProp.GetValue(comp);
            }
            catch (Exception) { return false; }
        }

        /// <summary>해당 캐릭터의 PathControl에서 마지막 경로 실패 여부를 확인하고 플래그를 리셋. 장애물 우회 실패 시 폴백 이동/재요청 쿨다운에 사용.</summary>
        public static bool GetAndClearLastPathFailedForCharacter(CharacterMainControl character)
        {
            if (character == null || _pathControlType == null) return false;
            try
            {
                var comp = GetPathControlForCharacter(character);
                return comp != null && GetAndClearLastPathFailed(comp);
            }
            catch (Exception) { return false; }
        }

        /// <summary>캐릭터의 PathControl에 목표 위치로 경로 요청. (AICharacterController.MoveToPos 패치를 거치지 않으므로 인지 후 플레이어 쪽 우회 이동에 사용)</summary>
        public static bool RequestPathToPosition(CharacterMainControl character, Vector3 targetPosition)
        {
            if (character == null || _pathControlType == null || _moveToPosMethod == null) return false;
            try
            {
                var comp = GetPathControlForCharacter(character);
                if (comp == null) return false;
                _moveToPosMethod.Invoke(comp, new object[] { targetPosition });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>경로 추종 중일 때만 moveInput에 속도 배율 적용. CharacterMainControlSetMoveInputPatches에서 호출.</summary>
        public static void ApplyPathSpeedMultiplier(CharacterMainControl character, ref Vector3 moveInput)
        {
            if (character == null) return;
            if (_pathControlType == null) return;
            try
            {
                var comp = GetPathControlForCharacter(character);
                if (comp == null) return;
                var movingProp = AccessTools.Property(_pathControlType, "Moving");
                if (movingProp == null) return;
                if (!(bool)movingProp.GetValue(comp)) return;

                float mult = GetSpeedMultiplier(comp);
                if (Mathf.Approximately(mult, 1f)) return;
                moveInput *= mult;
            }
            catch (Exception) { }
        }

        /// <summary>스폰 시 등에서 전역 PathSpeedMultiplier 설정을 해당 캐릭터의 PathControl에 적용할 때 호출.</summary>
        public static void ApplyDefaultSpeedMultiplierFromSettings(CharacterMainControl character)
        {
            if (character == null || _pathControlType == null) return;
            float mult = AdaptiveAISettings.PathSpeedMultiplier;
            if (mult < 0.01f || mult > 5f) mult = Mathf.Clamp(mult, 0.01f, 5f);
            if (Mathf.Approximately(mult, 1f)) return;
            try
            {
                var comp = GetPathControlForCharacter(character);
                if (comp != null)
                    SetSpeedMultiplier(comp, mult);
            }
            catch (Exception) { }
        }

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _pathControlType = AccessTools.TypeByName("AI_PathControl");
                _pathType = AccessTools.TypeByName("Pathfinding.Path") ?? AccessTools.TypeByName("Path");
                if (_pathControlType == null || _pathType == null)
                    return;

                _pathField = AccessTools.Field(_pathControlType, "path");
                _pathErrorProp = AccessTools.Property(_pathType, "error");
                if (_pathErrorProp == null)
                    _pathErrorProp = AccessTools.Property(_pathType, "Error");
                _pathVectorPathProp = AccessTools.Property(_pathType, "vectorPath");
                if (_pathVectorPathProp == null)
                    _pathVectorPathProp = AccessTools.Property(_pathType, "VectorPath");

                _moveToPosMethod = AccessTools.Method(_pathControlType, "MoveToPos", new[] { typeof(Vector3) });

                _harmony = new Harmony(HarmonyId);

                var onPathComplete = AccessTools.Method(_pathControlType, "OnPathComplete");
                if (onPathComplete != null)
                    _harmony.Patch(onPathComplete, postfix: new HarmonyMethod(AccessTools.Method(typeof(AI_PathControlPatches), nameof(OnPathComplete_Postfix))));

                var update = AccessTools.Method(_pathControlType, "Update");
                if (update != null)
                    _harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(AI_PathControlPatches), nameof(Update_Prefix))));

                _applied = true;
            }
            catch (Exception)
            {
                // 모드 로드 실패 시 무시
            }
        }

        public static void RemovePatches()
        {
            if (!_applied || _harmony == null) return;
            try
            {
                _harmony.UnpatchAll(HarmonyId);
                _applied = false;
                lock (_stateLock)
                    _stateByInstance.Clear();
                PathFailed = null;
            }
            catch (Exception) { }
        }

        /// <summary>vectorPath 인스턴스의 Count 반환. 타입별 PropertyInfo를 한 번만 조회해 캐시하여 매 프레임 리플렉션 비용 제거.</summary>
        private static int GetVectorPathCount(object vectorPath)
        {
            if (vectorPath == null) return 0;
            var type = vectorPath.GetType();
            if (type != _cachedVectorPathType)
            {
                _cachedVectorPathType = type;
                _vectorPathCountProp = type.GetProperty("Count");
            }
            if (_vectorPathCountProp == null) return 0;
            try
            {
                var v = _vectorPathCountProp.GetValue(vectorPath);
                return v is int i ? i : 0;
            }
            catch { return 0; }
        }

        private static void EnsureState(object instance)
        {
            lock (_stateLock)
            {
                if (!_stateByInstance.ContainsKey(instance))
                    _stateByInstance[instance] = new PathControlState();
            }
        }

        private static void OnPathComplete_Postfix(object __instance, object p)
        {
            if (__instance == null || p == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (_pathErrorProp == null || _pathVectorPathProp == null) return;

            try
            {
                bool error = (bool)_pathErrorProp.GetValue(p);
                var vectorPath = _pathVectorPathProp.GetValue(p);
                int count = 0;
                if (vectorPath != null)
                {
                    count = GetVectorPathCount(vectorPath);
                }

                bool failed = error || vectorPath == null || count == 0;
                EnsureState(__instance);
                lock (_stateLock)
                {
                    if (_stateByInstance.TryGetValue(__instance, out var state))
                        state.LastPathFailed = failed;
                }

                if (failed)
                    PathFailed?.Invoke(__instance);
            }
            catch (Exception) { }
        }

        private static bool Update_Prefix(object __instance)
        {
            if (__instance == null || _pathField == null || _pathVectorPathProp == null) return true;
            if (PlayerBehaviorCollector.IsInBase()) return true;

            try
            {
                object path = _pathField.GetValue(__instance);
                if (path == null) return true;

                var vectorPath = _pathVectorPathProp.GetValue(path);
                if (vectorPath == null)
                {
                    _pathField.SetValue(__instance, null);
                    return true;
                }
                if (GetVectorPathCount(vectorPath) == 0)
                {
                    _pathField.SetValue(__instance, null);
                    return true;
                }
            }
            catch (Exception) { }

            return true;
        }
    }
}
