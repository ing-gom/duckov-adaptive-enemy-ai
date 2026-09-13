using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Duckov;
using Duckov.Utilities;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Data;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// 플레이어 vs AI 사거리 비교에 따라 적 이동 입력을 보정합니다. 모든 AI는 기본적으로 거리를 줄이는 쪽으로 동작합니다.
    /// - AI 사거리 &gt; 플레이어 사거리: 플레이어 장거리면 접근 유도(도망 X)·이동 시 엄폐물 징검다리, 플레이어 단거리+가까우면 거리 벌리기, 저체력 시 후퇴.
    /// - AI 사거리 &lt; 플레이어 사거리: 플레이어 단거리+가까우면 거리 벌리기, 아니면 근접 유도·엄폐 끼며 접근.
    /// - 비슷한 사거리: 플레이어 장거리면 접근(엄폐 징검다리), 플레이어 단거리+가까우면 후퇴.
    /// - 판단력이 높은 AI: 플레이어 인지 후 "플레이어가 올 것 같다"고 판단하면 엄폐물 뒤에서 대기, 가까이 오면 교전(엄폐 복병 대기).
    /// </summary>
    public static class CharacterMainControlSetMoveInputPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.CharacterMainControl.SetMoveInput";
        private static Harmony? _harmony;
        private static bool _applied;

        /// <summary>AI 사거리 우위 시 "플레이어 사거리 + 이 값" 안이면 후퇴 유도.</summary>
        private const float StandOffMargin = 2f;
        /// <summary>AI 사거리 열위 시 접근 방향 블렌드 강도 (사거리 밖). Higher = more visible "rush in" when aggressive.</summary>
        private const float ApproachBlendStrengthOutOfRange = 0.72f;
        /// <summary>AI 사거리 열위 시 접근 방향 블렌드 강도 (사거리 안이지만 가까워지기 유도).</summary>
        private const float ApproachBlendStrengthInRange = 0.42f;
        /// <summary>사거리 "비슷하다"고 볼 차이(m). 이하면 이동 보정 안 함.</summary>
        private const float RangeSimilarTolerance = 2f;
        /// <summary>플레이어 단거리/근접일 때 이 거리(m) 안이면 거리 벌리기(후퇴) 유도. 14→10 완화로 과도한 뒤로 걷기 감소.</summary>
        private const float PlayerShortRangeRetreatDistMax = 10f;
        /// <summary>이 거리(m) 미만이면 전술 후퇴 블렌드 미적용. 궤도/등뒤 무빙만 적용해 근접 시 뒤로 가기와 궤도가 겹쳐 흔들리는 현상 방지.</summary>
        private const float RetreatSuppressWhenCloserThan = 4f;
        /// <summary>총기 적 전용: 이 거리(m) 미만이면 "너무 가까움"으로 보고 후퇴(밖으로) 블렌드 적용. 접근 블렌드는 이 구간에서 스킵해 왓다갓다 방지. 기본 3 (2.5→3).</summary>
        private const float RangedTooCloseRetreatDist = 3f;
        /// <summary>이 거리(m) 미만에서 이동 방향을 직전 프레임과 블렌드해 근접 시 무빙 흔들림 완화.</summary>
        private const float CloseRangeMoveSmoothDist = 5f;
        /// <summary>근접 구간 이동 방향 스무딩: 새 방향 반영 비율(0~1). 낮을수록 부드러움. 기본 0.35.</summary>
        private const float CloseRangeMoveSmoothFactor = 0.35f;
        /// <summary>엄폐물 끼며 접근 시 블렌드 비율은 AdaptiveAISettings.CoverApproachBlendStrength 사용.</summary>
        /// <summary>엄폐 탐지 레이캐스트 최대 거리.</summary>
        private const float CoverRaycastMaxDist = 50f;
        /// <summary>엄폐/장애물 레이 발사 높이(m). 이 높이를 낮추면 무릎~하체 높이 방해물(이동 방해)도 엄폐·우회 판정에 포함. 기본 0.35.</summary>
        private const float CoverRaycastOriginHeight = 0.35f;
        /// <summary>낮은 높이 보조 레이(m). 이 높이에서 한 번 더 쏴 낮은 장애물(턱, 낮은 벽) 감지. 기본 0.18.</summary>
        private const float CoverRaycastOriginHeightLow = 0.18f;
        /// <summary>레이에 막히지 않지만 캐릭터 몸이 막히는 엄폐물(얇은 벽 등) 감지용 SphereCast 반경(m). 이 값 이내에 콜라이더가 있으면 장애물로 간주.</summary>
        private const float CoverSphereCastRadius = 0.28f;
        /// <summary>장애물 감지 일관화: AI 위치/각도 차이로 한 줄 레이만 쓰면 놓치는 경우 방지. 좌우 이 거리(m) 오프셋 레이를 추가로 쏨.</summary>
        private const float CoverLateralRayOffset = 0.24f;
        /// <summary>엄폐 목표: 장애물 표면(AI 쪽)에서 AI 방향으로 밀어낸 거리(m). 이만큼 떨어진 점을 향해 플레이어–AI 사이에 엄폐가 오게 함.</summary>
        private const float CoverTargetOffsetBehindObstacle = 0.8f;
        /// <summary>엄폐 목표 오프셋 하한(m). 두꺼운 장애물이어도 목표가 항상 장애물–AI 사이 공간에 오도록, 너무 좁게 잡히지 않게 함.</summary>
        private const float CoverTargetOffsetMin = 0.35f;
        /// <summary>OverlapSphere 보조 엄폐 탐지 시 샘플당 수집할 콜라이더 버퍼 크기.</summary>
        private const int CoverOverlapSphereBufferSize = 32;
        private static Collider[]? _coverOverlapSphereBuffer;

        /// <summary>true면 선호 거리 후퇴 적용 시 콘솔에 로그 (쓰로틀 1.5초). F9 오버레이에서 토글 가능.</summary>
        internal static bool DebugLogPreferredRangeRetreat;
        private static float _lastPreferredRetreatLogTime = -999f;
        private const float PreferredRetreatLogInterval = 1.5f;

        /// <summary>true면 플레이어 에임 추적(SetAimPoint/ApplyPlayerAimSync) 추적용 디버그 로그 (쓰로틀 0.6초·AI별). F9 오버레이에서 토글 가능.</summary>
        internal static bool DebugLogAimTracking;
        private static readonly Dictionary<CharacterMainControl, float> _lastAimTrackingLogByCharacter = new Dictionary<CharacterMainControl, float>();
        private const float AimTrackingLogInterval = 0.6f;

        /// <summary>shooting=True 전용 로그 쓰로틀 (에임추적 발사 중 로그용).</summary>
        private static readonly Dictionary<CharacterMainControl, float> _lastShootingTrueLogByCharacter = new Dictionary<CharacterMainControl, float>();
        private const float ShootingTrueLogInterval = 0.5f;

        /// <summary>이번 프레임에 무빙 패턴(스트라핑·소프트 회피·카운터 무빙)이 적용된 캐릭터. 가속도 배율 적용용.</summary>
        private static readonly HashSet<CharacterMainControl> _charactersWithPatternActiveThisFrame = new HashSet<CharacterMainControl>();
        /// <summary>이번 프레임에 무빙 방향이 이전 프레임과 크게 바뀐 캐릭터. 방향 전환 시 일시 가속 적용용.</summary>
        private static readonly HashSet<CharacterMainControl> _directionChangeThisFrame = new HashSet<CharacterMainControl>();
        private static int _lastPatternAccFrame = -1;
        /// <summary>회피 대시 중 SetMoveInput 호출 로그 쓰로틀 (다음 단계 추측용).</summary>
        private static readonly Dictionary<CharacterMainControl, float> _lastSetMoveInputDodgeLogTime = new Dictionary<CharacterMainControl, float>();
        private const float SetMoveInputDodgeLogInterval = 0.5f;

        /// <summary>발사 중 이동 유지: 캐릭터별 직전 프레임 이동 입력. 공격 중 moveInput이 0일 때 복원용.</summary>
        private static readonly Dictionary<CharacterMainControl, Vector3> _lastMoveInputByCharacter = new Dictionary<CharacterMainControl, Vector3>();
        /// <summary>경계선 구간 이동 방향 스무딩: 직전 프레임 방향과 블렌드해 작은 움직임·진동 완화, 웨이포인트처럼 구간을 길게 유지.</summary>
        private static readonly Dictionary<CharacterMainControl, Vector3> _lastBoundaryDirByCharacter = new Dictionary<CharacterMainControl, Vector3>();
        /// <summary>경계선 방향 스무딩 시 새 방향 반영 비율(0~1). 낮을수록 방향 유지 시간 길어짐. 기본 0.26.</summary>
        private const float BoundaryDirSmoothFactor = 0.26f;
        /// <summary>ApplyMovementAimSyncToAllStored에서 키 복사용 재사용 리스트. 매 프레임 할당 방지.</summary>
        private static readonly List<CharacterMainControl> _cachedKeysForAimSync = new List<CharacterMainControl>();

        /// <summary>접근/엄폐 이탈 대시 조건용: AI별로 접근 중·엄폐 이탈 적용 시각 기록.</summary>
        private static readonly ConditionalWeakTable<global::AICharacterController, ApproachStateHolder> _approachStateByAi = new ConditionalWeakTable<global::AICharacterController, ApproachStateHolder>();
        private sealed class ApproachStateHolder
        {
            public float LastApproachTime = -999f;
            public float LastLeaveCoverEngageTime = -999f;
        }

        /// <summary>제안3: 이동 방향을 가리키는 프록시 Transform. aimTarget으로 두면 게임이 자연스럽게 해당 방향으로 조준.</summary>
        private static readonly Dictionary<CharacterMainControl, Transform> _aimProxyByCharacter = new Dictionary<CharacterMainControl, Transform>();

        /// <summary>에임 스무딩: AI별로 보간된 에임 목표 위치. AimTrackingSmoothEnabled일 때 사용.</summary>
        private static readonly ConditionalWeakTable<global::AICharacterController, SmoothedAimHolder> _smoothedAimByAi = new ConditionalWeakTable<global::AICharacterController, SmoothedAimHolder>();
        private sealed class SmoothedAimHolder { internal Vector3 Position; internal bool Valid; }
        private static FieldInfo? _noticeTimeMarkerFieldForAim;

        /// <summary>플레이어 대시 시 에임 "놓침" 연출: 대시 시작 시점의 플레이어 위치. 대시 중에는 이 위치를 목표로 유지.</summary>
        private static bool _playerWasDashingLastFrame;
        private static Vector3 _playerDashStartPosition;
        private static bool _playerDashStartValid;

        /// <summary>이동용: AI별로 보간된 플레이어 위치(에임과 동일한 반응속도/숙련도 패턴). 실시간이 아닌 자연스러운 추적용.</summary>
        private static readonly ConditionalWeakTable<global::AICharacterController, SmoothedMovementHolder> _smoothedMovementTargetByAi = new ConditionalWeakTable<global::AICharacterController, SmoothedMovementHolder>();
        private sealed class SmoothedMovementHolder { internal Vector3 Position; internal bool Valid; }

        /// <summary>벽 너머 에임 완화: AI별 LOS 전환·획득 시각. AimSyncRequireLineOfSight / AimAcquisitionDelayAfterLOS 사용.</summary>
        private static readonly ConditionalWeakTable<global::AICharacterController, LosStateHolder> _losStateByAi = new ConditionalWeakTable<global::AICharacterController, LosStateHolder>();
        private sealed class LosStateHolder { internal bool HadObstacleLastFrame; internal float TimeWhenGainedLOS; }

        /// <summary>후퇴 방향 캡: 이 dot(toPlayer) 이하면 블렌드해 완전 뒤로 걷기 완화.</summary>
        private const float RetreatCapDotThreshold = -0.4f;
        /// <summary>후퇴 캡 적용 시 목표 dot. 0.15 = 소폭 접근+옆(순수 옆걸음·평행 접근 방지).</summary>
        private const float RetreatCapTargetDot = 0.15f;
        /// <summary>후퇴 시 직선 후퇴 대신 궤도(외각) 방향으로 블렌드. 0.7 = 70% 궤도로 플레이어 관통 방지.</summary>
        private const float RetreatArcBlend = 0.7f;
        /// <summary>플레이어 전방 기준 dot(toPlayer). 이 값 미만이면 AI가 플레이어 "뒤에" 있음. 뒤에선 궤도/등뒤 블렌드 약화.</summary>
        private const float BehindPlayerDotThreshold = -0.25f;
        /// <summary>이 거리(m) 이내이고 플레이어 뒤에 있으면 '뒤 도달'로 보고 이동 감소. 경계선(3m) 포함해 뒤쪽에서 머무르도록 3.5로 확대.</summary>
        private const float StopWhenReachedBackDistMax = 3.5f;
        /// <summary>이 거리(m) 미만이면 겹침으로 보고 플레이어 반대 방향으로 이동 강제해 겹침 해소.</summary>
        private const float OverlapEscapeDist = 1.2f;
        /// <summary>겹침 구간에서 플레이어 반대 방향으로 밀어낼 때 이동 크기(0~1).</summary>
        private const float OverlapEscapeMagnitude = 0.55f;
        /// <summary>이 거리(m) 이내에서 이동 방향이 플레이어 쪽이면 플레이어를 장애물로 간주(경로 끊기·이동 보정).</summary>
        private const float PlayerAsObstacleDist = 2.2f;

        /// <summary>장애물 우회용으로 플레이어 쪽 경로를 요청한 캐릭터·시각. 이 시간(초) 이내면 경로 추종을 끊지 않음.</summary>
        private static readonly Dictionary<CharacterMainControl, float> _lastPathRequestToPlayerTime = new Dictionary<CharacterMainControl, float>();
        private const float PathRequestToPlayerGraceSeconds = 4f;
        /// <summary>경계선 도달 시 "플레이어 뒤 웨이포인트"로 경로 요청한 캐릭터의 플레이어 전방 방향. 플레이어 회전 시 웨이포인트 재요청 판단용.</summary>
        private static readonly Dictionary<CharacterMainControl, Vector3> _boundaryWaypointPlayerForwardByCharacter = new Dictionary<CharacterMainControl, Vector3>();
        /// <summary>경계선에서 웨이포인트 재요청 쓰로틀(초). 이 간격 이내에는 같은 이유로 재요청하지 않음.</summary>
        private const float BoundaryWaypointRequestInterval = 0.8f;
        /// <summary>경계선 진입 후 이 시간(초) 동안은 거리 미세 변동으로 경계 이탈로 보지 않음. 진동 방지.</summary>
        private const float BoundaryLockDuration = 0.35f;
        /// <summary>경계선 상태 유지 만료 시각(캐릭터별). Time.time이 이 값 미만이면 여전히 경계로 간주.</summary>
        private static readonly Dictionary<CharacterMainControl, float> _boundaryLockUntilByCharacter = new Dictionary<CharacterMainControl, float>();
        /// <summary>근접몹이 경로 추종 중일 때 목표(플레이어 위치)를 이 주기(초)마다 갱신. 과거 위치 도달 시 휘두르는 현상 방지.</summary>
        private const float MeleePathRefreshIntervalSeconds = 0.45f;
        private static readonly Dictionary<CharacterMainControl, float> _lastMeleePathRefreshTime = new Dictionary<CharacterMainControl, float>();
        /// <summary>경로 계산 실패 후 재요청 전 대기 시간(초). 실패 시 매 프레임 요청을 막고 폴백 이동으로 끼임 완화.</summary>
        private const float PathFailCooldownSeconds = 1.8f;
        /// <summary>경로 실패 시각(캐릭터별). 이 시간 이후에만 플레이어 쪽 경로 재요청.</summary>
        private static readonly Dictionary<CharacterMainControl, float> _lastPathFailTime = new Dictionary<CharacterMainControl, float>();
        /// <summary>장애물 우회 경로 추종 중 이 시간(초) 지나면 경로를 끊고 재요청해 끼임/구식 경로 완화.</summary>
        private const float PathRefreshAfterFollowingSeconds = 5f;
        /// <summary>경로 추종 중인 AI에 대해 장애물 판정(IsObstacleBetween) 및 경로 갱신 판단을 수행하는 주기(초). 매 프레임 호출 시 프레임 드랍 유발을 줄이기 위함. 기본 0.18초.</summary>
        private const float PathFollowCheckInterval = 0.18f;
        private static readonly Dictionary<CharacterMainControl, float> _lastPathFollowCheckTime = new Dictionary<CharacterMainControl, float>();
        /// <summary>제자리 걸음 판정: 수평 이동이 이 거리(m) 이상이면 “이동함”으로 보고 기준 위치·시간 갱신.</summary>
        private const float StuckMoveThreshold = 0.28f;
        private const float StuckMoveThresholdSq = 0.078f; // StuckMoveThreshold^2
        /// <summary>이 시간(초) 동안 거의 안 움직이면 제자리 걸음으로 보고 경로 끊기.</summary>
        private const float StuckDurationSeconds = 1.0f;
        /// <summary>경로 추종 중 이동 방향으로 이 거리(m) 안에 장애물이 있으면 즉시 경로 끊기(벽에 낑기기 전 선제 대응).</summary>
        private const float PathBlockedAheadRayDist = 0.6f;
        /// <summary>제자리 걸음으로 경로 끊은 뒤 이 시간(초) 동안은 경로 재요청 대신 폴백 이동만 적용.</summary>
        private const float StuckBreakFallbackSeconds = 1.2f;
        private static readonly Dictionary<CharacterMainControl, Vector3> _positionWhenStuckCheck = new Dictionary<CharacterMainControl, Vector3>();
        private static readonly Dictionary<CharacterMainControl, float> _timeWhenStuckCheck = new Dictionary<CharacterMainControl, float>();
        private static readonly Dictionary<CharacterMainControl, float> _lastStuckBreakTime = new Dictionary<CharacterMainControl, float>();

        /// <summary>자연스러운 멈춤 패턴: 캐릭터별로 멈춤이 끝나는 시각(Time.time).</summary>
        private static readonly Dictionary<CharacterMainControl, float> _movePauseUntilTime = new Dictionary<CharacterMainControl, float>();
        /// <summary>자연스러운 멈춤 패턴: 다음 멈춤 시도 판정 시각(Time.time).</summary>
        private static readonly Dictionary<CharacterMainControl, float> _movePauseNextCheckTime = new Dictionary<CharacterMainControl, float>();

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                var setMoveInput = AccessTools.Method(typeof(CharacterMainControl), "SetMoveInput");
                if (setMoveInput == null) return;
                _harmony.Patch(
                    setMoveInput,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(SetMoveInput_Prefix))));
                var getterRunAcc = AccessTools.PropertyGetter(typeof(CharacterMainControl), "CharacterRunAcc");
                if (getterRunAcc != null)
                    _harmony.Patch(getterRunAcc, postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(CharacterRunAcc_Postfix))));
                var getterWalkAcc = AccessTools.PropertyGetter(typeof(CharacterMainControl), "CharacterWalkAcc");
                if (getterWalkAcc != null)
                    _harmony.Patch(getterWalkAcc, postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(CharacterWalkAcc_Postfix))));
                var setAimPoint = AccessTools.Method(typeof(CharacterMainControl), "SetAimPoint");
                if (setAimPoint != null)
                    _harmony.Patch(setAimPoint, prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(SetAimPoint_Prefix))));
                var setRunInput = AccessTools.Method(typeof(CharacterMainControl), "SetRunInput");
                if (setRunInput != null)
                    _harmony.Patch(setRunInput, prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(SetRunInput_Prefix))));
                var canRun = AccessTools.Method(typeof(CharacterMainControl), "CanRun");
                if (canRun != null)
                    _harmony.Patch(canRun, postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(CanRun_MeleeOutOfRangePostfix))));
                var getCurrentAimPoint = AccessTools.Method(typeof(CharacterMainControl), "GetCurrentAimPoint");
                if (getCurrentAimPoint != null)
                    _harmony.Patch(getCurrentAimPoint, postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(GetCurrentAimPoint_Postfix))));
                var gunType = AccessTools.TypeByName("ItemAgent_Gun");
                if (gunType != null)
                {
                    var transToFire = AccessTools.Method(gunType, "TransToFire");
                    if (transToFire != null)
                    {
                        _harmony.Patch(transToFire,
                            prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(TransToFire_PrefixForBulletAim))),
                            postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlSetMoveInputPatches), nameof(TransToFire_PostfixForBulletAim))));
                    }
                }
                _applied = true;
            }
            catch (System.Exception)
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
                _lastMoveInputByCharacter.Clear();
                _lastBoundaryDirByCharacter.Clear();
                _lastPathRequestToPlayerTime.Clear();
                _boundaryWaypointPlayerForwardByCharacter.Clear();
                _boundaryLockUntilByCharacter.Clear();
                _lastPathFailTime.Clear();
                _positionWhenStuckCheck.Clear();
                _timeWhenStuckCheck.Clear();
                _lastStuckBreakTime.Clear();
                _movePauseUntilTime.Clear();
                _movePauseNextCheckTime.Clear();
                foreach (var t in _aimProxyByCharacter.Values)
                    if (t != null && t.gameObject != null) UnityEngine.Object.Destroy(t.gameObject);
                _aimProxyByCharacter.Clear();
            }
            catch (System.Exception) { }
        }

        /// <summary>무빙 패턴이 이 캐릭터에 적용된 것으로 표시. 같은 프레임 내 CharacterRunAcc/CharacterWalkAcc 조회 시 배율 적용.</summary>
        private static void MarkMovementPatternActive(CharacterMainControl c)
        {
            if (c == null) return;
            _charactersWithPatternActiveThisFrame.Add(c);
        }

        /// <summary>저장된 이동 입력이 플레이어 기준 옆방향(측면) 성분이 충분히 있으면 true. 옆으로 움직일 때도 가속도 배율 적용용.</summary>
        private static bool HasSignificantLateralMovement(CharacterMainControl c)
        {
            if (c == null || c == CharacterMainControl.Main) return false;
            if (!_lastMoveInputByCharacter.TryGetValue(c, out Vector3 move)) return false;
            Vector3 moveFlat = move;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) return false;
            moveFlat.Normalize();
            var main = CharacterMainControl.Main;
            if (main == null) return false;
            Vector3 toPlayer = GetSmoothedPlayerPositionForMovement(c) - c.transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f) return false;
            toPlayer.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, toPlayer);
            if (right.sqrMagnitude < 0.01f) return false;
            right.Normalize();
            float lateral = Mathf.Abs(Vector3.Dot(moveFlat, right));
            return lateral >= 0.35f;
        }

        /// <summary>CanRun()이 false일 때 근접·사거리 밖이면 true로 덮어써 달리기 허용(트리거 구간 속도 저하 방지).</summary>
        private static void CanRun_MeleeOutOfRangePostfix(CharacterMainControl __instance, ref bool __result)
        {
            if (__result) return;
            if (__instance == null || __instance == CharacterMainControl.Main) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (!Team.IsEnemy(__instance.Team, Teams.player)) return;
            if (PerAIPatchControl.IsExcludedFromAdaptivePatches(__instance)) return;
            var melee = __instance.GetMeleeWeapon();
            if (melee == null) return;
            if (__instance.attackAction != null && __instance.attackAction.Running) return;
            var ai = __instance.GetComponent<global::AICharacterController>() ?? __instance.GetComponentInParent<global::AICharacterController>();
            if (ai == null || !AICharacterControllerPatches.IsAggroOnPlayer(ai)) return;
            var main = CharacterMainControl.Main;
            Vector3? tpos = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackTargetPosition(__instance, ai, main);
            if (!tpos.HasValue) return;
            Vector3 toT = tpos.Value - __instance.transform.position;
            toT.y = 0f;
            float dist = toT.magnitude;
            float maxDist = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackMaxDistance(melee);
            if (maxDist > 0f && dist > maxDist)
                __result = true;
        }

        /// <summary>멀리서 접근 중이면 달리기 보정. 근접 트리거 보정: BT가 공격 노드에서 SetRunInput(false) 호출해도 사거리 밖이면 달리기 유지(차단 구간 속도 저하 방지).</summary>
        private static void SetRunInput_Prefix(CharacterMainControl __instance, ref bool _runInput)
        {
            if (__instance == null || __instance == CharacterMainControl.Main) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (!Team.IsEnemy(__instance.Team, Teams.player)) return;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance)) return;
            if (_runInput) return;

            var main = CharacterMainControl.Main;
            if (main == null) return;

            // 근접: 휘두르는 중이 아닌데 걸음으로 들어오면 → 사거리 밖이면 달리기 유지(Attack() 차단 구간에서 트리거만 반복·속도 저하 방지).
            var melee = __instance.GetMeleeWeapon();
            if (melee != null && !PerAIPatchControl.IsExcludedFromAdaptivePatches(__instance) && (__instance.attackAction == null || !__instance.attackAction.Running))
            {
                var ai = __instance.GetComponent<global::AICharacterController>() ?? __instance.GetComponentInParent<global::AICharacterController>();
                if (ai != null && AICharacterControllerPatches.IsAggroOnPlayer(ai))
                {
                    Vector3? tpos = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackTargetPosition(__instance, ai, main);
                    if (tpos.HasValue)
                    {
                        Vector3 toT = tpos.Value - __instance.transform.position;
                        toT.y = 0f;
                        float dist = toT.magnitude;
                        float maxDist = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackMaxDistance(melee);
                        if (maxDist > 0f && dist > maxDist)
                        {
                            _runInput = true;
                            return;
                        }
                    }
                }
            }

            Vector3 toPlayer = GetSmoothedPlayerPositionForMovement(__instance) - __instance.transform.position;
            toPlayer.y = 0f;
            float distSq = toPlayer.sqrMagnitude;
            if (toPlayer.sqrMagnitude < 0.01f) return;
            toPlayer.Normalize();

            if (!_lastMoveInputByCharacter.TryGetValue(__instance, out Vector3 move)) return;
            Vector3 moveFlat = move;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) return;
            moveFlat.Normalize();
            float dot = Vector3.Dot(moveFlat, toPlayer);
            if (dot < AdaptiveAISettings.RunWhenApproachingFromDistanceDot) return;

            // 근접몹: 접근 중이면 거리 무관하게 달리기(기본적으로 뛰어서 다가옴). 원작 동작 시에는 원거리와 동일 기준 사용.
            float distThreshold = (AdaptiveAISettings.MeleeUseOriginalBehavior || __instance.GetMeleeWeapon() == null)
                ? AdaptiveAISettings.RunWhenApproachingFromDistance
                : 1.5f;
            if (distThreshold <= 0f) return;
            if (distSq < distThreshold * distThreshold) return;
            _runInput = true;
        }

        /// <summary>ItemAgent_Gun.TransToFire에서 총알 방향 계산할 때만 true. GetCurrentAimPoint_Postfix는 이때만 시각 방향으로 덮어써서 에임 추적은 유지하고 총알만 시각과 맞춤.</summary>
        internal static bool GetCurrentAimPointForBullet;

        /// <summary>TransToFire 진입 시 Holder가 적 AI이면 플래그 설정. 일반 AI는 AlignBulletToAimVisual일 때 시각 방향, 터렛은 항상 플레이어 방향(GetCurrentAimPoint_Postfix에서 분기).</summary>
        public static void TransToFire_PrefixForBulletAim(object __instance)
        {
            GetCurrentAimPointForBullet = false;
            if (__instance == null) return;
            bool alignVisual = AdaptiveAISettings.AlignBulletToAimVisual;
            try
            {
                var holderProp = __instance.GetType().GetProperty("Holder", BindingFlags.Public | BindingFlags.Instance);
                var holder = holderProp?.GetValue(__instance) as CharacterMainControl;
                if (holder == null || holder == CharacterMainControl.Main || !Team.IsEnemy(holder.Team, Teams.player)) return;
                bool isTurret = CharacterMainControlDashPatches.IsDashBlockedForPreset(holder);
                if (isTurret || alignVisual)
                    GetCurrentAimPointForBullet = true;
            }
            catch { }
        }

        /// <summary>TransToFire 종료 후 플래그 해제.</summary>
        public static void TransToFire_PostfixForBulletAim()
        {
            GetCurrentAimPointForBullet = false;
        }

        /// <summary>AI 사격 시 GetCurrentAimPoint 덮어씀. 터렛: 항상 플레이어 방향(말 탑승 시 높이 1m 보정). 일반 AI: AlignBulletToAimVisual일 때 플레이어 어그로인 경우에만 플레이어 방향(그 외에는 원래 타겟 방향 유지, 적 vs 적 교전 시 플레이어에게 오발 방지).</summary>
        public static void GetCurrentAimPoint_Postfix(CharacterMainControl __instance, ref Vector3 __result)
        {
            if (!GetCurrentAimPointForBullet) return;
            if (__instance == null || __instance == CharacterMainControl.Main) return;
            if (!Team.IsEnemy(__instance.Team, Teams.player)) return;
            var main = CharacterMainControl.Main;
            Vector3 aiPos = __instance.transform.position;
            bool isTurret = CharacterMainControlDashPatches.IsDashBlockedForPreset(__instance);
            if (isTurret)
            {
                if (main == null) return;
                Vector3 targetPos = main.transform.position + Vector3.up * 1f;
                Vector3 dir = targetPos - aiPos;
                if (dir.sqrMagnitude < 0.01f) return;
                dir.Normalize();
                __result = aiPos + dir * 100f;
                return;
            }
            if (!AdaptiveAISettings.AlignBulletToAimVisual) return;
            var ai = __instance.GetComponent<global::AICharacterController>() ?? __instance.GetComponentInParent<global::AICharacterController>();
            if (ai == null) return;
            // 적 vs 적(스캐브·PMC·스파이더봇 등) 교전 중에는 어그로가 플레이어가 아니므로 에임 덮어쓰지 않음 → 원래 타겟(다른 AI) 방향으로 발사.
            if (!AICharacterControllerPatches.IsAggroOnPlayer(ai)) return;
            Vector3 dir2;
            if (main != null)
            {
                Vector3 targetPos = main.transform.position + Vector3.up * 0.5f;
                dir2 = targetPos - aiPos;
                if (dir2.sqrMagnitude < 0.01f) dir2 = __instance.CurrentAimDirection;
            }
            else
                dir2 = __instance.CurrentAimDirection;
            if (dir2.sqrMagnitude < 0.01f) return;
            dir2.Normalize();
            __result = aiPos + dir2 * 100f;
        }

        private static void SetMoveInput_Prefix(CharacterMainControl __instance, ref Vector3 moveInput)
        {
            if (__instance == null) return;
            if (__instance == CharacterMainControl.Main) return;
            // 기지에서는 적응형 이동·지그재그 미적용 — 플레이어에게 지그재그로 다가오는 현상 방지
            if (PlayerBehaviorCollector.IsInBase()) return;
            // 회피 대시 시 모드가 설정한 방향을 유지 (접근/후퇴 블렌드로 덮어쓰지 않음)
            if (CharacterMainControlDashPatches.DashIsIncomingDodge)
            {
                if (AdaptiveAISettings.DebugLogDash)
                {
                    float now = Time.time;
                    if (!_lastSetMoveInputDodgeLogTime.TryGetValue(__instance, out float last) || now - last >= SetMoveInputDodgeLogInterval)
                    {
                        _lastSetMoveInputDodgeLogTime[__instance] = now;
                        float mag = moveInput.magnitude;
                        Debug.Log($"[AdaptiveEnemyAI] SetMoveInput(회피 대시 중) 호출됨: AI={__instance.GetInstanceID()} dir=({moveInput.x:F2},{moveInput.z:F2}) mag={mag:F2} → BT/이동이 이 값으로 덮어쓰면 제자리 구르기 원인 가능");
                    }
                }
                return;
            }
            if (!Team.IsEnemy(__instance.Team, Teams.player)) return;
            // 플레이어와 같은 팀(펫·동료): 이동 수정 미적용 — 플레이어가 적에게 피해를 줄 때 펫이 제자리 고정되는 현상 방지
            if (CharacterMainControl.Main != null && __instance.Team == CharacterMainControl.Main.Team) return;
            // 팻 등 제외 프리셋: 이동 입력 전혀 수정 안 함 → 게임이 넣은 웨이포인트 방향 그대로 통과, 플레이어 추종 유지
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance))
                return;

            if (Time.frameCount != _lastPatternAccFrame)
            {
                _charactersWithPatternActiveThisFrame.Clear();
                _directionChangeThisFrame.Clear();
                _lastPatternAccFrame = Time.frameCount;
            }

            var ai = __instance.GetComponent<global::AICharacterController>()
                ?? __instance.GetComponentInParent<global::AICharacterController>();
            if (ai == null) return;
            AICharacterControllerPatches.UpdateLastNoticedTime(ai);

            // 터렛 프리셋: 이동만 차단. 게임이 인지 안 할 때는 정면+터렛 무기 사거리 안이면 보조 인지(앞에서만 보임). 1.3.5 대응: 플레이어 DamageReceiver는 폴백(GetPlayerDamageReceiver) 사용.
            if (CharacterMainControlDashPatches.IsDashBlockedForPreset(__instance))
            {
                var mainTurret = CharacterMainControl.Main;
                if (!ai.noticed && mainTurret != null && AICharacterControllerPatches.GetPlayerDamageReceiver(mainTurret) != null
                    && !AICharacterControllerPatches.IsAggroOnlyWhenHurtPreset(ai))
                {
                    var loadoutTurret = AICharacterControllerPatches.GetEnemyLoadout(ai);
                    float minRange = AdaptiveAISettings.TurretNoticeMinRangeFallback > 0f ? AdaptiveAISettings.TurretNoticeMinRangeFallback : 1f;
                    float turretRange = Mathf.Max(loadoutTurret.WeaponRange, minRange);
                    Vector3 toPlayerTurret = mainTurret.transform.position - __instance.transform.position;
                    toPlayerTurret.y = 0f;
                    float distSq = toPlayerTurret.sqrMagnitude;
                    float maxRangeSq = turretRange * turretRange;
                    if (distSq <= maxRangeSq && AICharacterControllerPatches.IsPlayerInTurretFrontCone(ai, mainTurret))
                        AICharacterControllerPatches.ForceNoticedState(ai, mainTurret);
                }
                moveInput = Vector3.zero;
                return;
            }

            // 근접 트리거 보정: BT가 공격 노드에서 moveInput=0으로 호출해도, 사거리 밖이면 타겟 방향 이동으로 복원(차단 구간에서 제자리 멈춤 방지).
            var meleeWeapon = __instance.GetMeleeWeapon();
            if (meleeWeapon != null && !PerAIPatchControl.IsExcludedFromAdaptivePatches(__instance)
                && (__instance.attackAction == null || !__instance.attackAction.Running)
                && AICharacterControllerPatches.IsAggroOnPlayer(ai) && moveInput.sqrMagnitude < 0.01f)
            {
                var player = CharacterMainControl.Main;
                Vector3? tpos = player != null ? CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackTargetPosition(__instance, ai, player) : null;
                if (tpos.HasValue)
                {
                    Vector3 toT = tpos.Value - __instance.transform.position;
                    toT.y = 0f;
                    float toTargetDist = toT.magnitude;
                    float maxDist = CharacterMainControl_Attack_BlockMeleePrefix.GetMeleeAttackMaxDistance(meleeWeapon);
                    if (maxDist > 0f && toTargetDist > maxDist && toTargetDist > 0.01f)
                    {
                        toT.Normalize();
                        moveInput = toT;
                    }
                }
            }

            // 근접 공격(휘두르기) 중 이동 입력 제거: MeleeUseOriginalBehavior=false일 때만 적용(멈춰서 휘두르기). true(원작)면 공격 중에도 이동 유지.
            // 적 vs 적 근접 전투 시에는 적용하지 않음(플레이어 타겟일 때만). 팻 등 제외 프리셋에는 미적용(펫 제자리 고정 방지).
            if (!AdaptiveAISettings.MeleeUseOriginalBehavior
                && !CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance)
                && !PerAIPatchControl.IsExcludedFromAdaptivePatches(__instance)
                && __instance.GetMeleeWeapon() != null && __instance.attackAction != null && __instance.attackAction.Running
                && AICharacterControllerPatches.IsAggroOnPlayer(ai))
            {
                moveInput = Vector3.zero;
                return;
            }

            // 근접: 공격 허용 거리(maxDist) 안에 들어왔을 때 정면 접근만 제거. 대신 궤도/시야밖 방향으로 계속 이동해 플레이어 앞에서 제자리 정지 방지.
            if (!AdaptiveAISettings.MeleeUseOriginalBehavior
                && AdaptiveAISettings.MeleeStopBeforeAttackEnabled && __instance.GetMeleeWeapon() != null && !PerAIPatchControl.IsExcludedFromAdaptivePatches(__instance)
                && AICharacterControllerPatches.IsAggroOnPlayer(ai)
                && (__instance.attackAction == null || !__instance.attackAction.Running))
            {
                Vector3 targetPos = GetMeleeTargetPositionForStop(ai);
                if (targetPos != Vector3.zero)
                {
                    Vector3 a = __instance.transform.position;
                    a.y = 0f;
                    Vector3 p = targetPos;
                    p.y = 0f;
                    float distFlat = Vector3.Distance(a, p);
                    var meleeForStop = __instance.GetMeleeWeapon();
                    float baseRangeStop = meleeForStop != null ? (meleeForStop.AttackRange + 0.05f) : 0f;
                    float stopThreshold = baseRangeStop * Mathf.Clamp(AdaptiveAISettings.MeleeStopRangeMultiplier, 0.1f, 5f);
                    if (stopThreshold > 0f && distFlat <= stopThreshold)
                    {
                        var mainForOrbit = CharacterMainControl.Main;
                        if (mainForOrbit != null && AdaptiveAISettings.OrbitAroundPlayerEnabled && GetOrbitDirection(p, a, mainForOrbit, out Vector3 orbitDir))
                        {
                            float minMag = Mathf.Max(0.4f, Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveMagnitude, 0.2f, 1f));
                            moveInput.x = orbitDir.x * minMag;
                            moveInput.z = orbitDir.z * minMag;
                            moveInput.y = 0f;
                            // return 하지 않음 → 아래 궤도/Cap/EnsureMinMove까지 실행되어 일관되게 동작
                        }
                        else
                        {
                            moveInput = Vector3.zero;
                            return;
                        }
                    }
                }
            }

            // 비선공몹·피격 시에만 어그로 프리셋: 플레이어 인지(또는 최근 피격) 전까지 모드 로직 미적용 + 플레이어 방향 이동 성분 제거.
            if ((AICharacterControllerPatches.IsNonAggro(ai) || AICharacterControllerPatches.IsAggroOnlyWhenHurtPreset(ai)) && !ai.noticed && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(ai))
            {
                BlockMoveTowardPlayerForNonAggroUnnoticed(__instance, ref moveInput);
                return;
            }

            // 경로 추종 중일 때 AI_PathControlPatches에서 설정한 속도 배율 적용
            AI_PathControlPatches.ApplyPathSpeedMultiplier(__instance, ref moveInput);

            // 미인지(패트롤·순찰)일 때는 모드 무빙 코드 전부 미적용. BT/원작 이동만 사용.
            // 이슈3: 최근 교전 유예(RecentCombatGraceSeconds) 안이면 수색 허용. 플레이어 공격 인지(최근 피격) 시에도 접근 허용.
            // 원작은 거리(forceTracePlayerDistance)로 searchedEnemy만 설정하고 noticed는 OnSound/OnHurt에서만 true로 함. 어그로가 이미 플레이어(searchedEnemy/aimTarget==플레이어)면 인지한 것으로 보고 접근 보정 적용.
            bool unnoticedAndNoGrace = AdaptiveAISettings.MoveInputBlendOnlyWhenNoticed && !ai.noticed && !AICharacterControllerPatches.IsWithinRecentCombatGrace(ai) && !AICharacterControllerPatches.IsRecentlyHurtByPlayer(ai) && !AICharacterControllerPatches.IsAggroOnPlayer(ai);
            if (unnoticedAndNoGrace)
            {
                var playerUnnoticed = CharacterMainControl.Main;
                Vector3 toPlayerFlatUnnoticed = Vector3.forward;
                if (playerUnnoticed != null)
                {
                    Vector3 vecToPlayer = playerUnnoticed.transform.position - __instance.transform.position;
                    vecToPlayer.y = 0f;
                    if (vecToPlayer.sqrMagnitude >= 0.0001f)
                        toPlayerFlatUnnoticed = vecToPlayer.normalized;
                }
                // 패트롤/미인지 시 플레이어 쪽 이동 성분 제거(forceTracePlayerDistance·경로 방향 포함). dot > 0.1 이면 접근으로 보고 제거.
                if (AdaptiveAISettings.BlockApproachWhenUnnoticed && playerUnnoticed != null)
                {
                    Vector3 vecToPlayer = playerUnnoticed.transform.position - __instance.transform.position;
                    vecToPlayer.y = 0f;
                    float distSq = vecToPlayer.sqrMagnitude;
                    if (distSq >= 0.0001f)
                    {
                        Vector3 moveFlatIn = moveInput;
                        moveFlatIn.y = 0f;
                        if (moveFlatIn.sqrMagnitude >= 0.0001f)
                        {
                            float dot = Vector3.Dot(moveFlatIn.normalized, toPlayerFlatUnnoticed);
                            if (dot > 0.1f)
                            {
                                Vector3 towardComponent = toPlayerFlatUnnoticed * Vector3.Dot(moveFlatIn, toPlayerFlatUnnoticed);
                                moveFlatIn -= towardComponent;
                                if (moveFlatIn.sqrMagnitude < 0.01f)
                                {
                                    float minMag = Mathf.Clamp01(AdaptiveAISettings.UnnoticedMinMoveMagnitude);
                                    if (minMag > 0.01f)
                                    {
                                        Vector3 right = Vector3.Cross(Vector3.up, toPlayerFlatUnnoticed);
                                        if (right.sqrMagnitude > 0.01f) right.Normalize();
                                        else right = Quaternion.AngleAxis(90f, Vector3.up) * toPlayerFlatUnnoticed;
                                        moveInput.x = right.x * minMag;
                                        moveInput.z = right.z * minMag;
                                        moveInput.y = 0f;
                                    }
                                    else
                                        moveInput = Vector3.zero;
                                }
                                else
                                {
                                    float origMag = new Vector3(moveInput.x, 0f, moveInput.z).magnitude;
                                    moveFlatIn.Normalize();
                                    moveInput.x = moveFlatIn.x * origMag;
                                    moveInput.z = moveFlatIn.z * origMag;
                                }
                            }
                        }
                    }
                }
                // 감지 거리 진입 등으로 게임이 접근 이동을 넣었을 때, 다른 곳을 보며 다가오지 않도록 이동 방향으로 바라보기만 적용.
                ApplyFaceMovementDirection(__instance, moveInput, toPlayerFlatUnnoticed);
                ApplyNoBackpedalCap(__instance, ref moveInput);
                return;
            }

            // 어그로가 플레이어가 아닐 때(다른 AI에게 피격된 경우 등) 플레이어 중심 이동·접근·후퇴 보정 적용하지 않음. BT/원작만 사용.
            if (!AICharacterControllerPatches.IsAggroOnPlayer(ai))
                return;

            var main = CharacterMainControl.Main;
            if (main == null) return;

            // 패트롤 중 인지 시: 기존 경로를 한 번 끊어 전투/접근으로 전환. 단, 장애물 우회용으로 우리가 요청한 플레이어 경로는 끊지 않음.
            if (AI_PathControlPatches.IsCharacterPathFollowing(__instance))
            {
                bool weRequestedPathRecently = _lastPathRequestToPlayerTime.TryGetValue(__instance, out float reqTime) && (Time.time - reqTime) < PathRequestToPlayerGraceSeconds;
                if (!weRequestedPathRecently)
                {
                    try { ai.StopMove(); } catch { }
                }
                else
                {
                    if (_lastPathRequestToPlayerTime.TryGetValue(__instance, out float reqT) && (Time.time - reqT) >= PathRefreshAfterFollowingSeconds)
                    {
                        // 장애물 우회 경로를 오래 따라온 경우 끊고 재요청해 끼임/구식 경로 완화
                        try { ai.StopMove(); } catch { }
                        _lastPathRequestToPlayerTime.Remove(__instance);
                        _positionWhenStuckCheck.Remove(__instance);
                        _timeWhenStuckCheck.Remove(__instance);
                    }
                    else
                    {
                        // 이동 방향 바로 앞에 벽이 있으면 경로 끊고 새 경로 요청(waypoint 갱신). 다음 프레임 ApplyApproachWhenNoticedAndNoPath에서 플레이어 쪽 경로 재요청.
                        Vector3 moveFlatForRay = moveInput;
                        moveFlatForRay.y = 0f;
                        if (moveFlatForRay.sqrMagnitude > 0.01f && IsObstacleInMoveDirection(__instance, moveFlatForRay, PathBlockedAheadRayDist))
                        {
                            try { ai.StopMove(); } catch { }
                            _lastPathRequestToPlayerTime.Remove(__instance);
                            _positionWhenStuckCheck.Remove(__instance);
                            _timeWhenStuckCheck.Remove(__instance);
                            // _lastStuckBreakTime 설정 안 함 → 폴백 대신 다음 프레임에 새 경로 요청으로 waypoint 갱신
                        }
                        else
                        {
                        // 제자리 걸음 감지: 이동 입력은 있는데 위치가 거의 안 바뀌면 경로 끊고 잠시 폴백만
                        Vector3 posFlat = __instance.transform.position;
                        posFlat.y = 0f;
                        float now = Time.time;
                        if (!_positionWhenStuckCheck.TryGetValue(__instance, out Vector3 refPos) || !_timeWhenStuckCheck.TryGetValue(__instance, out float refTime))
                        {
                            _positionWhenStuckCheck[__instance] = posFlat;
                            _timeWhenStuckCheck[__instance] = now;
                        }
                        else
                        {
                            float movedSq = (posFlat - refPos).sqrMagnitude;
                            if (movedSq > StuckMoveThresholdSq)
                            {
                                _positionWhenStuckCheck[__instance] = posFlat;
                                _timeWhenStuckCheck[__instance] = now;
                            }
                            else if ((now - refTime) >= StuckDurationSeconds)
                            {
                                try { ai.StopMove(); } catch { }
                                _lastPathRequestToPlayerTime.Remove(__instance);
                                _lastStuckBreakTime[__instance] = now;
                                _positionWhenStuckCheck.Remove(__instance);
                                _timeWhenStuckCheck.Remove(__instance);
                            }
                        }
                        }
                    }
                }
            }

            if (!PlayerLoadoutService.IsValid) return;

            var profile = PlayerLoadoutService.Current;
            var enemy = AICharacterControllerPatches.GetEnemyLoadout(ai);

            float playerRange = profile.IsMelee ? 0f : profile.WeaponRange;
            float aiRange = enemy.IsMelee ? 0f : enemy.WeaponRange;

            Vector3 aiPos = __instance.transform.position;
            Vector3 playerPos = GetSmoothedPlayerPositionForMovement(__instance);
            Vector3 toPlayer = playerPos - aiPos;
            toPlayer.y = 0f;
            float dist = toPlayer.magnitude;
            if (dist < 0.01f) return;
            Vector3 toPlayerFlat = toPlayer / dist;
            Vector3 retreatDir = -toPlayerFlat;
            // 궤도/등뒤/후퇴는 플레이어 현재 방향 기준으로 갱신(스무딩 위치 사용 시 처음 정해진 뒤쪽으로만 가는 현상 방지)
            var mainForRetreat = CharacterMainControl.Main;
            Vector3 livePlayerPos = (mainForRetreat != null) ? mainForRetreat.transform.position : playerPos;
            Vector3 toLive = livePlayerPos - aiPos;
            toLive.y = 0f;
            float liveDist = toLive.magnitude;
            if (liveDist < 0.01f) liveDist = dist;
            if (mainForRetreat != null && GetOrbitDirection(livePlayerPos, aiPos, mainForRetreat, out Vector3 orbitDirForRetreat, __instance))
                retreatDir = Vector3.Lerp(-toPlayerFlat, orbitDirForRetreat, RetreatArcBlend).normalized;
            // 겹침 근본 방지: 접근 방향을 "플레이어 위치"가 아닌 "최소 유지 거리 위의 목표점"으로 통일. 옵션으로 목표 중심을 플레이어 등 뒤 Nm로 두어 겹침 완화.
            float desiredMinDist = GetDesiredMinDistanceFromPlayer(__instance, enemy.IsMelee);
            Vector3 movementCenter = GetMovementTargetCenterPosition(mainForRetreat, livePlayerPos, AdaptiveAISettings.ApproachTargetOffsetBehindPlayerMeters);
            Vector3 approachTargetPos = GetApproachTargetPosition(aiPos, movementCenter, desiredMinDist);
            Vector3 toApproachTarget = approachTargetPos - aiPos;
            toApproachTarget.y = 0f;
            float toApproachTargetMagSq = toApproachTarget.sqrMagnitude;
            Vector3 toApproachTargetFlat;
            bool inBoundaryBand = liveDist >= desiredMinDist - ApproachBoundaryHysteresisBand && liveDist <= desiredMinDist + ApproachBoundaryHysteresisBand;
            if (toApproachTargetMagSq >= 0.01f && !inBoundaryBand)
                toApproachTargetFlat = toApproachTarget / Mathf.Sqrt(toApproachTargetMagSq);
            else
            {
                // 경계선 근처 또는 목표점 도달: 플레이어 방향(toPlayerFlat) 대신 궤도/등뒤 방향 사용 → 왓다갓다 방지, 주변 돌며 뒤로 가는 무빙 유지
                if (mainForRetreat != null && GetOrbitDirection(livePlayerPos, aiPos, mainForRetreat, out Vector3 orbitForBoundary, __instance))
                    toApproachTargetFlat = orbitForBoundary;
                else if (mainForRetreat != null && GetDirectionTowardPlayerBack(livePlayerPos, aiPos, mainForRetreat, out Vector3 dirToBackBoundary))
                    toApproachTargetFlat = dirToBackBoundary;
                else
                    toApproachTargetFlat = retreatDir;
            }
            // 이동 보정 시 공격적 판단(공격성)과 방어적 판단(방어 성향) 둘 다 적용. 접근/후퇴/궤도 블렌드에 retreatStanceMult·approachStanceMult 사용.
            var summary = PlayerBehaviorCollector.GetCurrentSummary();
            float defensiveStance = 0.5f;
            if (AdaptiveAISettings.TacticalStanceByPlayerPatternEnabled && summary != null)
                defensiveStance = summary.GetDefensiveStanceFactor();
            float retreatStanceMult = 1f + defensiveStance * Mathf.Clamp01(AdaptiveAISettings.DefensiveStanceWeightRetreat);
            float approachStanceMult = Mathf.Max(0.1f, 1f - defensiveStance * Mathf.Clamp01(AdaptiveAISettings.DefensiveStanceWeightApproach));
            if (AdaptiveAISettings.TacticalModeTierEnabled && summary != null && ai != null)
            {
                int mode = Data.BehaviorProfileSummary.GetTacticalModeIndex(Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f), defensiveStance);
                retreatStanceMult *= AdaptiveAISettings.GetTacticalModeRetreatBlendMult(mode);
                approachStanceMult *= AdaptiveAISettings.GetTacticalModeApproachBlendMult(mode);
            }

            // 수평 이동만 보정 (입력도 수평으로 정규화 후 블렌드)
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            // 경계선(접근 목표 원) 도달: 1) 멈춤 2) 플레이어 뒤 웨이포인트로 이동 명령 3) 이동 중 플레이어 방향 바뀌면 웨이포인트 재요청 4) 웨이포인트 이동 중엔 접근거리 무시·엄폐 회피만
            bool atOrNearBoundary = liveDist >= desiredMinDist - 0.5f && liveDist <= desiredMinDist + 2.5f;
            bool inBoundaryNow = inBoundaryBand || atOrNearBoundary;
            if (inBoundaryNow)
                _boundaryLockUntilByCharacter[__instance] = Time.time + BoundaryLockDuration;
            bool boundaryLockActive = _boundaryLockUntilByCharacter.TryGetValue(__instance, out float lockUntil) && Time.time < lockUntil;
            if (inBoundaryNow || boundaryLockActive)
            {
                bool pathFollowing = AI_PathControlPatches.IsCharacterPathFollowing(__instance);
                bool weRequestedPathRecently = _lastPathRequestToPlayerTime.TryGetValue(__instance, out float reqTime) && (Time.time - reqTime) < PathRequestToPlayerGraceSeconds;
                Vector3 storedForward = default;
                bool isBoundaryWaypointMode = weRequestedPathRecently && _boundaryWaypointPlayerForwardByCharacter.TryGetValue(__instance, out storedForward);

                if (pathFollowing && isBoundaryWaypointMode)
                {
                    // 웨이포인트(플레이어 뒤) 이동 중: 플레이어 방향 변경 시 웨이포인트 재요청
                    if (mainForRetreat != null)
                    {
                        Vector3 curForward = mainForRetreat.transform.forward;
                        curForward.y = 0f;
                        storedForward.y = 0f;
                        if (curForward.sqrMagnitude > 0.01f && storedForward.sqrMagnitude > 0.01f)
                        {
                            curForward.Normalize();
                            storedForward.Normalize();
                            if (Vector3.Dot(curForward, storedForward) < 0.95f)
                            {
                                if (!_lastPathRequestToPlayerTime.TryGetValue(__instance, out float lastReq) || (Time.time - lastReq) >= BoundaryWaypointRequestInterval)
                                {
                                    if (!AI_PathControlPatches.IsCharacterWaitingForPathResult(__instance))
                                    {
                                        Vector3 waypoint = GetWaypointPositionBehindPlayer(mainForRetreat, desiredMinDist);
                                        if (AI_PathControlPatches.RequestPathToPosition(__instance, waypoint))
                                        {
                                            _lastPathRequestToPlayerTime[__instance] = Time.time;
                                            Vector3 fwd = mainForRetreat.transform.forward;
                                            fwd.y = 0f;
                                            if (fwd.sqrMagnitude > 0.01f) _boundaryWaypointPlayerForwardByCharacter[__instance] = fwd.normalized;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    // 이동 입력은 경로 추종이 채우므로 건드리지 않음(접근거리·궤도 블렌드 무시)
                }
                else if (!pathFollowing)
                {
                    // 경계선 도달 → 멈춤 후 플레이어 뒤 웨이포인트로 이동 명령
                    moveInput.x = 0f;
                    moveInput.z = 0f;
                    moveInput.y = 0f;
                    if (mainForRetreat != null)
                    {
                        bool alreadyRequestedBoundary = _boundaryWaypointPlayerForwardByCharacter.ContainsKey(__instance);
                        bool throttleOk = !_lastPathRequestToPlayerTime.TryGetValue(__instance, out float lastReq) || (Time.time - lastReq) >= BoundaryWaypointRequestInterval;
                        bool canRequest = !alreadyRequestedBoundary || throttleOk;
                        if (canRequest && !AI_PathControlPatches.IsCharacterWaitingForPathResult(__instance))
                        {
                            Vector3 waypoint = GetWaypointPositionBehindPlayer(mainForRetreat, desiredMinDist);
                            if (AI_PathControlPatches.RequestPathToPosition(__instance, waypoint))
                            {
                                _lastPathRequestToPlayerTime[__instance] = Time.time;
                                Vector3 fwd = mainForRetreat.transform.forward;
                                fwd.y = 0f;
                                if (fwd.sqrMagnitude > 0.01f) _boundaryWaypointPlayerForwardByCharacter[__instance] = fwd.normalized;
                            }
                        }
                    }
                    moveFlat = moveInput;
                    moveFlat.y = 0f;
                }
                else
                {
                    // 경로 추종 중이지만 '경계 웨이포인트'가 아님(접근/엄폐 경로 등) → 기존 궤도/등뒤 블렌드 유지
                    bool alreadyBehind = mainForRetreat != null && IsAIBehindPlayer(aiPos, livePlayerPos, mainForRetreat);
                    Vector3 boundaryOutDir = default;
                    float boundaryOutMag = 0f;
                    if (alreadyBehind && mainForRetreat != null && GetDirectionTowardPlayerBack(livePlayerPos, aiPos, mainForRetreat, out Vector3 dirToBackStay))
                    {
                        dirToBackStay.y = 0f;
                        if (dirToBackStay.sqrMagnitude > 0.01f)
                        {
                            dirToBackStay.Normalize();
                            boundaryOutDir = dirToBackStay;
                            boundaryOutMag = Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveMagnitude * 0.75f, 0.42f, 0.65f);
                        }
                    }
                    else
                    {
                        Vector3 boundaryDir = GetApproachDirectionWithOptionalCover(aiPos, livePlayerPos, toApproachTargetFlat, useCoverAsSteppingStones: true);
                        if (boundaryDir.sqrMagnitude < 0.01f && toApproachTargetFlat.sqrMagnitude > 0.01f)
                        {
                            boundaryDir = toApproachTargetFlat;
                            boundaryDir.y = 0f;
                            boundaryDir.Normalize();
                        }
                        float minMag = Mathf.Max(0.74f, Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveMagnitude, 0.4f, 1f));
                        if (boundaryDir.sqrMagnitude > 0.01f)
                        {
                            boundaryDir.y = 0f;
                            boundaryDir.Normalize();
                            float curMag = moveFlat.magnitude;
                            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toApproachTargetFlat.normalized;
                            else moveFlat.Normalize();
                            // 공격적 판단(공격성) + 방어적 판단(방어 성향) 둘 다 반영: 공격성으로 기본 블렌드, 방어 성향 높을수록 궤도/경계 방향 블렌드 감소
                            float boundaryBlend = (ai != null)
                                ? Mathf.Lerp(0.72f, 0.92f, Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f))
                                : 0.88f;
                            boundaryBlend *= approachStanceMult;
                            Vector3 blended = Vector3.Lerp(moveFlat, boundaryDir, boundaryBlend).normalized;
                            boundaryOutDir = blended;
                            boundaryOutMag = Mathf.Max(curMag, minMag);
                        }
                        else
                        {
                            Vector3 fallbackDir = toApproachTargetFlat;
                            if (fallbackDir.sqrMagnitude < 0.01f && mainForRetreat != null)
                            {
                                if (GetOrbitDirection(livePlayerPos, aiPos, mainForRetreat, out Vector3 orbitFallback, __instance))
                                    fallbackDir = orbitFallback;
                                else if (GetDirectionTowardPlayerBack(livePlayerPos, aiPos, mainForRetreat, out Vector3 backFallback))
                                    fallbackDir = backFallback;
                                else
                                    fallbackDir = retreatDir;
                            }
                            if (fallbackDir.sqrMagnitude < 0.01f) fallbackDir = retreatDir;
                            if (fallbackDir.sqrMagnitude > 0.01f)
                            {
                                fallbackDir.y = 0f;
                                fallbackDir.Normalize();
                                boundaryOutDir = fallbackDir;
                                boundaryOutMag = minMag;
                            }
                        }
                    }
                    if (boundaryOutDir.sqrMagnitude > 0.01f)
                    {
                        boundaryOutDir.Normalize();
                        if (_lastBoundaryDirByCharacter.TryGetValue(__instance, out Vector3 lastBoundary) && lastBoundary.sqrMagnitude > 0.01f)
                        {
                            lastBoundary.Normalize();
                            boundaryOutDir = Vector3.Slerp(lastBoundary, boundaryOutDir, BoundaryDirSmoothFactor).normalized;
                        }
                        _lastBoundaryDirByCharacter[__instance] = boundaryOutDir;
                        moveInput.x = boundaryOutDir.x * boundaryOutMag;
                        moveInput.z = boundaryOutDir.z * boundaryOutMag;
                        moveInput.y = 0f;
                    }
                    moveFlat = moveInput;
                    moveFlat.y = 0f;
                }
            }
            else
            {
                _boundaryWaypointPlayerForwardByCharacter.Remove(__instance);
                _boundaryLockUntilByCharacter.Remove(__instance);
            }
            // 엄폐물 때문에 플레이어가 맞지 않는 경우: 우회(플랭크) 이동 강제 → 가만히 서 있지 않고 옆/뒤로 돌아 공격 시도
            bool coverBetween = ai != null && (ai.hasObsticleToTarget || TryGetCoverDirection(aiPos, livePlayerPos, out _));
            if (coverBetween && liveDist >= 1.5f && liveDist <= 35f && moveFlat.sqrMagnitude < 0.4f)
            {
                Vector3 flankDir = GetApproachDirectionWithOptionalCover(aiPos, livePlayerPos, toApproachTargetFlat, useCoverAsSteppingStones: true);
                if (flankDir.sqrMagnitude > 0.01f)
                {
                    flankDir.y = 0f;
                    flankDir.Normalize();
                    float mag = Mathf.Max(0.65f, Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveMagnitude, 0.4f, 1f));
                    moveInput.x = flankDir.x * mag;
                    moveInput.z = flankDir.z * mag;
                    moveInput.y = 0f;
                    moveFlat = moveInput;
                    moveFlat.y = 0f;
                }
            }
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = Vector3.forward;

            // 총기 적 전용: 너무 가까우면 맨 먼저 후퇴 블렌드 적용(실시간 거리 liveDist 사용해 즉시 반응).
            if (!enemy.IsMelee && liveDist > OverlapEscapeDist && liveDist < RangedTooCloseRetreatDist)
            {
                moveFlat.Normalize();
                float retreatBlend = Mathf.Clamp01(AdaptiveAISettings.RetreatBlendStrength * retreatStanceMult * 1.2f);
                Vector3 blended = Vector3.Lerp(moveFlat, retreatDir, retreatBlend);
                blended.y = moveInput.y;
                moveInput = blended;
                moveFlat = moveInput;
                moveFlat.y = 0f;
                if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = Vector3.forward;
            }

            // 재장전 중: 이동 입력이 거의 0이면 패턴(직전 이동 → 옆걸음)으로 반드시 채워 가만히 있지 않게 함. 가까운 엄폐(플레이어-엄폐물-AI)가 있을 때만 판단능력에 따라 엄폐로 블렌드, 없으면 패턴 그대로 유지.
            if (CharacterMainControlDashPatches.IsCharacterReloading(__instance))
            {
                float reloadMag = Mathf.Clamp01(AdaptiveAISettings.MoveWhileReloadingMagnitude);
                if (moveInput.sqrMagnitude < 0.01f)
                {
                    if (AdaptiveAISettings.MoveWhileReloadingEnabled && _lastMoveInputByCharacter.TryGetValue(__instance, out Vector3 last) && last.sqrMagnitude > 0.01f)
                    {
                        last.y = 0f;
                        last.Normalize();
                        moveInput = last * reloadMag;
                        moveInput.y = 0f;
                    }
                    else
                    {
                        Vector3 right = Vector3.Cross(Vector3.up, toPlayerFlat);
                        if (right.sqrMagnitude > 0.01f)
                            right.Normalize();
                        else
                            right = (Quaternion.AngleAxis(90f, Vector3.up) * toPlayerFlat).normalized;
                        moveInput = right * 0.5f;
                        moveInput.y = 0f;
                    }
                }
                moveFlat = moveInput;
                moveFlat.y = 0f;
                if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = Vector3.forward;
                // 가까운 엄폐(플레이어-엄폐물-AI)가 있을 때만 엄폐 방향으로 블렌드. 없으면 패턴 그대로.
                ApplyCoverToReload(ref moveInput, ref moveFlat, __instance, aiPos, playerPos, ai, toPlayerFlat);
            }

            // 경로 추종 중: waypoint 방향 우선, 전술(접근/후퇴/엄폐) 블렌드는 건너뛰고 무빙 패턴만 섞음.
            // 실시간으로 너무 가까우면 경로 끊어 비비기 방지. 단, 엄폐물 우회용·경계선 플레이어 뒤 웨이포인트는 관통 허용(접근거리 무시).
            if (AI_PathControlPatches.IsCharacterPathFollowing(__instance))
            {
                float minDistToKeepPath = enemy.IsMelee ? AdaptiveAISettings.MinDistanceFromPlayer * 0.5f : RangedTooCloseRetreatDist;
                bool weRequestedFlankingPath = _lastPathRequestToPlayerTime.TryGetValue(__instance, out float reqTime) && (Time.time - reqTime) < PathRequestToPlayerGraceSeconds;
                bool weRequestedBoundaryWaypoint = weRequestedFlankingPath && _boundaryWaypointPlayerForwardByCharacter.ContainsKey(__instance);
                if (AdaptiveAISettings.MinDistanceFromPlayer > 0f && liveDist < minDistToKeepPath && !weRequestedFlankingPath)
                {
                    try { ai.StopMove(); } catch { }
                    _lastPathRequestToPlayerTime.Remove(__instance);
                    _boundaryWaypointPlayerForwardByCharacter.Remove(__instance);
                }
                // 경계선 웨이포인트(플레이어 뒤) 이동 중이면 궤도 블렌드 적용 안 함. 그 외 경로 추종 시에만 경계선 궤도/등뒤 블렌드
                if ((inBoundaryBand || atOrNearBoundary) && !weRequestedBoundaryWaypoint)
                {
                    Vector3 pathBoundaryOutDir = default;
                    float pathBoundaryOutMag = 0f;
                    bool alreadyBehindPath = mainForRetreat != null && IsAIBehindPlayer(aiPos, livePlayerPos, mainForRetreat);
                    if (alreadyBehindPath && mainForRetreat != null && GetDirectionTowardPlayerBack(livePlayerPos, aiPos, mainForRetreat, out Vector3 dirToBackStayPath))
                    {
                        dirToBackStayPath.y = 0f;
                        if (dirToBackStayPath.sqrMagnitude > 0.01f)
                        {
                            dirToBackStayPath.Normalize();
                            pathBoundaryOutDir = dirToBackStayPath;
                            pathBoundaryOutMag = Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveMagnitude * 0.75f, 0.42f, 0.65f);
                        }
                    }
                    else
                    {
                        Vector3 boundaryDir = GetApproachDirectionWithOptionalCover(aiPos, livePlayerPos, toApproachTargetFlat, useCoverAsSteppingStones: true);
                        if (boundaryDir.sqrMagnitude < 0.01f && toApproachTargetFlat.sqrMagnitude > 0.01f)
                        {
                            boundaryDir = toApproachTargetFlat;
                            boundaryDir.y = 0f;
                            boundaryDir.Normalize();
                        }
                        float minMag = Mathf.Max(0.68f, Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveMagnitude, 0.3f, 1f));
                        if (boundaryDir.sqrMagnitude > 0.01f)
                        {
                            boundaryDir.y = 0f;
                            boundaryDir.Normalize();
                            float curMag = moveFlat.magnitude;
                            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toApproachTargetFlat.normalized;
                            else moveFlat.Normalize();
                            // 공격적 판단(공격성) + 방어적 판단(방어 성향) 둘 다 반영
                            float pathBoundaryBlend = (ai != null)
                                ? Mathf.Lerp(0.72f, 0.92f, Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f))
                                : 0.88f;
                            pathBoundaryBlend *= approachStanceMult;
                            Vector3 blended = Vector3.Lerp(moveFlat, boundaryDir, pathBoundaryBlend).normalized;
                            pathBoundaryOutDir = blended;
                            pathBoundaryOutMag = Mathf.Max(curMag, minMag);
                        }
                        else
                        {
                            Vector3 fallbackDir = toApproachTargetFlat;
                            if (fallbackDir.sqrMagnitude < 0.01f && mainForRetreat != null)
                            {
                                if (GetOrbitDirection(livePlayerPos, aiPos, mainForRetreat, out Vector3 orbitFallback, __instance))
                                    fallbackDir = orbitFallback;
                                else if (GetDirectionTowardPlayerBack(livePlayerPos, aiPos, mainForRetreat, out Vector3 backFallback))
                                    fallbackDir = backFallback;
                                else
                                    fallbackDir = retreatDir;
                            }
                            if (fallbackDir.sqrMagnitude < 0.01f) fallbackDir = retreatDir;
                            if (fallbackDir.sqrMagnitude > 0.01f)
                            {
                                fallbackDir.y = 0f;
                                fallbackDir.Normalize();
                                pathBoundaryOutDir = fallbackDir;
                                pathBoundaryOutMag = minMag;
                            }
                        }
                    }
                    if (pathBoundaryOutDir.sqrMagnitude > 0.01f)
                    {
                        pathBoundaryOutDir.Normalize();
                        if (_lastBoundaryDirByCharacter.TryGetValue(__instance, out Vector3 lastPathBoundary) && lastPathBoundary.sqrMagnitude > 0.01f)
                        {
                            lastPathBoundary.Normalize();
                            pathBoundaryOutDir = Vector3.Slerp(lastPathBoundary, pathBoundaryOutDir, BoundaryDirSmoothFactor).normalized;
                        }
                        _lastBoundaryDirByCharacter[__instance] = pathBoundaryOutDir;
                        moveInput.x = pathBoundaryOutDir.x * pathBoundaryOutMag;
                        moveInput.z = pathBoundaryOutDir.z * pathBoundaryOutMag;
                        moveInput.y = 0f;
                        moveFlat = moveInput;
                        moveFlat.y = 0f;
                    }
                }
                // 플레이어 뒤 웨이포인트 이동 중: 궤도/등뒤/정지 등 보정 전부 스킵 → 경로 방향만 유지해 실제로 이동하도록 함
                if (!weRequestedBoundaryWaypoint)
                {
                    ApplySoftEvasionIfThreat(__instance, ref moveInput, toPlayerFlat, ai);
                    ApplyMovementPattern(__instance, ref moveInput, toPlayerFlat);
                    ApplyMoveOutOfPlayerView(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                    ApplyMoveToPlayerBackWhenClose(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                    ApplyOrbitAroundPlayer(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                    ApplyMoveToTargetBackWhenAtApproachDistance(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, desiredMinDist, ai, __instance);
                    ApplyPlayerAsObstacleDeflection(ref moveInput, __instance, toPlayerFlat, liveDist, aiPos, livePlayerPos);
                    ApplyStopWhenReachedPlayerBack(ref moveInput, aiPos, livePlayerPos, liveDist, mainForRetreat, __instance);
                    ApplyPostDashPauseIfActive(__instance, ai, ref moveInput);
                    ApplyMovePauseIfActive(__instance, ref moveInput);
                    ApplyNoBackpedalCap(__instance, ref moveInput);
                }
                ApplyFaceMovementDirection(__instance, moveInput, toPlayerFlat);
                UpdateApproachStateForDash(ai, moveInput, toPlayerFlat);
                CapMoveInputByMinDistanceFromPlayer(ref moveInput, __instance);
                EnsureMinMoveAfterCap(ref moveInput, __instance);
                StoreLastMoveInput(__instance, ref moveInput);
                return;
            }

            // AI 사거리 우위: 플레이어 장거리면 도망 말고 접근 유도. 플레이어 단거리면 가까우면 거리 벌리기. 저체력 시 후퇴, 재장전 시 접근.
            if (aiRange > playerRange + RangeSimilarTolerance)
            {
                bool lowHealth = IsHealthRatioAtOrBelow(__instance, AdaptiveAISettings.StandOffHealthRatioMax);
                bool playerReloading = AdaptiveAISettings.ApproachWhenPlayerReloadingEnabled && IsPlayerReloading();
                // 플레이어 장거리: 근접해야 하므로 접근 유도. 이동 시 엄폐물 징검다리 사용. 이미 근접(4m 미만)이면 접근 블렌드 스킵 → 궤도/등뒤만 적용해 앞뒤 흔들림 방지. 총기 적은 RangedTooCloseRetreatDist 미만에서 접근 스킵(밖으로 나가야 함).
                if (profile.IsLongRange && dist > 0.5f && dist >= RetreatSuppressWhenCloserThan && (enemy.IsMelee || dist >= RangedTooCloseRetreatDist))
                {
                    float blend = ApproachBlendStrengthOutOfRange * approachStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 approachDir = GetApproachDirectionWithOptionalCover(aiPos, playerPos, toApproachTargetFlat, useCoverAsSteppingStones: true);
                    Vector3 blended = Vector3.Lerp(moveFlat.normalized, approachDir, blend);
                    blended.y = moveInput.y;
                    moveInput = blended;
                    ApplyPostHitPushIfRetreatType(ref moveInput, toPlayerFlat, summary);
                    ApplyRetreatCap(ref moveInput, toPlayerFlat);
                    ApplySoftEvasionIfThreat(__instance, ref moveInput, toPlayerFlat, ai);
                    ApplyMovementPattern(__instance, ref moveInput, toPlayerFlat);
                ApplyMoveOutOfPlayerView(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                ApplyMoveToPlayerBackWhenClose(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                ApplyOrbitAroundPlayer(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                ApplyMoveToTargetBackWhenAtApproachDistance(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, desiredMinDist, ai, __instance);
                ApplyPlayerAsObstacleDeflection(ref moveInput, __instance, toPlayerFlat, liveDist, aiPos, livePlayerPos);
                ApplyStopWhenReachedPlayerBack(ref moveInput, aiPos, livePlayerPos, liveDist, mainForRetreat, __instance);
                ApplyPostDashPauseIfActive(__instance, ai, ref moveInput);
                ApplyMovePauseIfActive(__instance, ref moveInput);
                ApplyNoBackpedalCap(__instance, ref moveInput);
                ApplyFaceMovementDirection(__instance, moveInput, toPlayerFlat);
                UpdateApproachStateForDash(ai, moveInput, toPlayerFlat);
                CapMoveInputByMinDistanceFromPlayer(ref moveInput, __instance);
                EnsureMinMoveAfterCap(ref moveInput, __instance);
                StoreLastMoveInput(__instance, ref moveInput);
                return;
            }
            if (lowHealth && !playerReloading)
                {
                    float safeMin = playerRange + StandOffMargin;
                    if (dist < safeMin && dist >= RetreatSuppressWhenCloserThan)
                    {
                        float retreatBlend = AdaptiveAISettings.RetreatBlendStrength * retreatStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                        Vector3 blended = Vector3.Lerp(moveFlat.normalized, retreatDir, retreatBlend);
                        blended.y = moveInput.y;
                        moveInput = blended;
                    }
                }
                else if (profile.IsShortOrMelee && dist >= RetreatSuppressWhenCloserThan && dist < PlayerShortRangeRetreatDistMax)
                {
                    // 플레이어 단거리/근접: 거리 벌리기 유도
                    float retreatBlend = AdaptiveAISettings.RetreatBlendStrength * retreatStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 blended = Vector3.Lerp(moveFlat.normalized, retreatDir, retreatBlend);
                    blended.y = moveInput.y;
                    moveInput = blended;
                }
                else if (playerReloading && dist > 0.5f)
                {
                    float baseBlend = Mathf.Clamp01(AdaptiveAISettings.ApproachWhenPlayerReloadingBlend);
                    float riskScale = summary != null
                        ? Mathf.Lerp(AdaptiveAISettings.ReloadRiskBlendMin, AdaptiveAISettings.ReloadRiskBlendMax, summary.ReloadRiskRatioEma)
                        : 1f;
                    float blend = Mathf.Clamp01(baseBlend * riskScale) * approachStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 blended = Vector3.Lerp(moveFlat.normalized, toApproachTargetFlat, blend);
                    blended.y = moveInput.y;
                    moveInput = blended;
                }
                ApplyPostHitPushIfRetreatType(ref moveInput, toPlayerFlat, summary);
                ApplyRetreatCap(ref moveInput, toPlayerFlat);
                ApplySoftEvasionIfThreat(__instance, ref moveInput, toPlayerFlat, ai);
                ApplyMovementPattern(__instance, ref moveInput, toPlayerFlat);
                ApplyMoveOutOfPlayerView(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                ApplyMoveToPlayerBackWhenClose(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                ApplyOrbitAroundPlayer(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
                ApplyMoveToTargetBackWhenAtApproachDistance(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, desiredMinDist, ai, __instance);
                ApplyPlayerAsObstacleDeflection(ref moveInput, __instance, toPlayerFlat, liveDist, aiPos, livePlayerPos);
                ApplyStopWhenReachedPlayerBack(ref moveInput, aiPos, livePlayerPos, liveDist, mainForRetreat, __instance);
                ApplyPostDashPauseIfActive(__instance, ai, ref moveInput);
                ApplyMovePauseIfActive(__instance, ref moveInput);
                ApplyNoBackpedalCap(__instance, ref moveInput);
                ApplyFaceMovementDirection(__instance, moveInput, toPlayerFlat);
                UpdateApproachStateForDash(ai, moveInput, toPlayerFlat);
                CapMoveInputByMinDistanceFromPlayer(ref moveInput, __instance);
                EnsureMinMoveAfterCap(ref moveInput, __instance);
                StoreLastMoveInput(__instance, ref moveInput);
                return;
            }

            // AI 사거리 열위(또는 근접): 플레이어 단거리이고 가까우면 거리 벌리기, 아니면 접근 유도. 근처 엄폐물이 있으면 엄폐물을 끼면서 접근
            if (aiRange < playerRange - RangeSimilarTolerance || enemy.IsMelee)
            {
                if (profile.IsShortOrMelee && dist >= RetreatSuppressWhenCloserThan && dist < PlayerShortRangeRetreatDistMax)
                {
                    // 플레이어 단거리/근접: AI도 접근보다 거리 벌리기 우선
                    float retreatBlend = AdaptiveAISettings.RetreatBlendStrength * retreatStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 blended = Vector3.Lerp(moveFlat.normalized, retreatDir, retreatBlend);
                    blended.y = moveInput.y;
                    moveInput = blended;
                }
                else if (dist >= RetreatSuppressWhenCloserThan && (enemy.IsMelee || dist >= RangedTooCloseRetreatDist))
                {
                    // 이미 근접(4m 미만)이면 이 블록 스킵 → 궤도/등뒤만 적용해 앞뒤 왓다갓다 방지. 총기 적은 RangedTooCloseRetreatDist 미만에서 접근 스킵.
                    float approachStrength = (aiRange > 0.5f && dist > aiRange + StandOffMargin
                        ? ApproachBlendStrengthOutOfRange
                        : ApproachBlendStrengthInRange) * approachStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 approachDir = toApproachTargetFlat;

                    // 엄폐물 끼며 접근: 플레이어–AI 사이에 엄폐가 오는 위치(장애물 뒤) 방향으로 블렌드
                    if (TryGetCoverDirection(aiPos, playerPos, out Vector3 toCoverApproach))
                    {
                        float coverBlend = Mathf.Clamp01(AdaptiveAISettings.CoverApproachBlendStrength);
                        approachDir = Vector3.Lerp(toApproachTargetFlat, toCoverApproach, coverBlend).normalized;
                    }

                    Vector3 blended = Vector3.Lerp(moveFlat.normalized, approachDir, approachStrength);
                    blended.y = moveInput.y;
                    moveInput = blended;
                }
                ApplyRetreatCap(ref moveInput, toPlayerFlat);
            }
            else
            {
                // 비슷한 사거리: 플레이어 장거리면 접근 유도(엄폐 징검다리), 플레이어 단거리+가까우면 거리 벌리기. 이미 근접(4m 미만)이면 접근 스킵. 총기 적은 너무 가까우면 접근 스킵.
                if (profile.IsLongRange && dist > 0.5f && dist >= RetreatSuppressWhenCloserThan && (enemy.IsMelee || dist >= RangedTooCloseRetreatDist))
                {
                    float blend = ApproachBlendStrengthInRange * approachStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 approachDir = GetApproachDirectionWithOptionalCover(aiPos, playerPos, toApproachTargetFlat, useCoverAsSteppingStones: true);
                    Vector3 blended = Vector3.Lerp(moveFlat.normalized, approachDir, blend);
                    blended.y = moveInput.y;
                    moveInput = blended;
                }
                else if (profile.IsShortOrMelee && dist >= RetreatSuppressWhenCloserThan && dist < PlayerShortRangeRetreatDistMax)
                {
                    float retreatBlend = AdaptiveAISettings.RetreatBlendStrength * retreatStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 blended = Vector3.Lerp(moveFlat.normalized, retreatDir, retreatBlend);
                    blended.y = moveInput.y;
                    moveInput = blended;
                }
                ApplyRetreatCap(ref moveInput, toPlayerFlat);
            }

            // 총기 적 전용: 너무 가까우면(몸 겹침 직전~사거리 부족 구간) 후퇴만 유도. 접근 블렌드 스킵으로 밖/안 왓다갓다 방지.
            if (!enemy.IsMelee && dist > OverlapEscapeDist && dist < RangedTooCloseRetreatDist)
            {
                Vector3 moveFlatR = moveInput;
                moveFlatR.y = 0f;
                if (moveFlatR.sqrMagnitude < 0.0001f) moveFlatR = Vector3.forward;
                moveFlatR.Normalize();
                float retreatBlend = Mathf.Clamp01(AdaptiveAISettings.RetreatBlendStrength * retreatStanceMult * 1.2f);
                Vector3 blended = Vector3.Lerp(moveFlatR, retreatDir, retreatBlend);
                blended.y = moveInput.y;
                moveInput = blended;
            }

            // 교전 중 엄폐 추구: 노출 상태에서 플레이어 사격/최근 피격 시 엄폐물 쪽으로 이동 블렌드(눈에 띄게 엄폐 이용).
            ApplySeekCoverWhenUnderFire(ref moveInput, toPlayerFlat, aiPos, playerPos, ai);
            // 엄폐 대기: 플레이어 무기 유리 + AI 엄폐 뒤 + 플레이어 멀면 엄폐 유지(대기). 가까이 오면 대기 해제.
            ApplyCoverWaitIfPlayerAdvantage(ref moveInput, toPlayerFlat, aiPos, playerPos, dist, playerRange, aiRange, ai);
            // 엄폐 복병 대기: 플레이어 인지 후 "플레이어가 올 것 같다"고 판단(거리 구간·판단력)하면 엄폐물 뒤에서 대기, 가까이 오면 교전.
            ApplyCoverAmbushWait(ref moveInput, toPlayerFlat, aiPos, playerPos, dist, ai);
            // 피킹: 플레이어 사격 중이면 엄폐 방향(숨기), 미사격/재장전이면 플레이어 방향(피크 아웃).
            ApplyCoverPeek(ref moveInput, toPlayerFlat, aiPos, playerPos, ai);
            // 판단력 높을 때 엄폐물에서 나와 플레이어 공격 유도(플레이어 방향 블렌드). 저체력이면 대신 엄폐 회복 유도.
            ApplyLeaveCoverToEngage(ref moveInput, toPlayerFlat, aiPos, playerPos, ai, __instance);
            ApplyCoverRecoveryWhenLowHP(ref moveInput, toPlayerFlat, aiPos, playerPos, ai, __instance);

            // 무기별 선호 사거리: 산탄총 등은 선호 구간(예: 3~8m)보다 너무 가까우면 소폭 후퇴해 유효 사거리 유지.
            // 장비 격차 시: 플레이어 사거리가 훨씬 크면 선호거리 후퇴 미적용(적이 계속 도주만 하지 않도록).
            // 플레이어 재장전 중이면 선호거리 후퇴 대신 접근 유도(후퇴→접근 패턴).
            bool playerOutguns = playerRange > aiRange + AdaptiveAISettings.PreferredRangeRetreatPlayerAdvantageThreshold;
            bool playerReloadingPref = AdaptiveAISettings.ApproachWhenPlayerReloadingEnabled && IsPlayerReloading();
            if (!playerOutguns && WeaponPreferredRange.TryGet(enemy.GunTypeTag, out var preferred) && dist < preferred.OptimalMin && dist > 0.5f)
            {
                Vector3 moveFlat2 = moveInput;
                moveFlat2.y = 0f;
                if (moveFlat2.sqrMagnitude < 0.0001f) moveFlat2 = toPlayerFlat;
                moveFlat2.Normalize();
                if (playerReloadingPref)
                {
                    float baseBlend = Mathf.Clamp01(AdaptiveAISettings.ApproachWhenPlayerReloadingBlend);
                    float riskScale = summary != null
                        ? Mathf.Lerp(AdaptiveAISettings.ReloadRiskBlendMin, AdaptiveAISettings.ReloadRiskBlendMax, summary.ReloadRiskRatioEma)
                        : 1f;
                    float blend = Mathf.Clamp01(baseBlend * riskScale) * approachStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 blended = Vector3.Lerp(moveFlat2, toPlayerFlat, blend);
                    blended.y = moveInput.y;
                    moveInput = blended;
                }
                else
                {
                    if (DebugLogPreferredRangeRetreat && Time.time - _lastPreferredRetreatLogTime >= PreferredRetreatLogInterval)
                    {
                        _lastPreferredRetreatLogTime = Time.time;
                        string gunLabel = string.IsNullOrEmpty(enemy.GunTypeTag) ? "?" : enemy.GunTypeTag.Replace("Tag_GunType_", "");
                        Debug.Log($"[AdaptiveEnemyAI] 선호거리 후퇴: 무기={gunLabel} 거리={dist:F1}m (OptimalMin={preferred.OptimalMin:F0}m)");
                    }
                    float prefRetreatBlend = AdaptiveAISettings.PreferredRangeRetreatBlend * retreatStanceMult * GetTacticalBlendScaleForPathFollowing(__instance);
                    Vector3 blendedRetreat = Vector3.Lerp(moveFlat2, retreatDir, prefRetreatBlend);
                    blendedRetreat.y = moveInput.y;
                    moveInput = blendedRetreat;
                }
            }

            ApplyPostHitPushIfRetreatType(ref moveInput, toPlayerFlat, summary);
            ApplyRetreatCap(ref moveInput, toPlayerFlat);
            ApplySoftEvasionIfThreat(__instance, ref moveInput, toPlayerFlat, ai);
            if (!AICharacterControllerPatches.TryGetIncomingProjectileThreat(ai, out _, out _))
                ApplyCounterMoveFromFirePattern(__instance, ref moveInput, toPlayerFlat, ai);
            ApplyMovementPattern(__instance, ref moveInput, toPlayerFlat);
            ApplyMoveOutOfPlayerView(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
            ApplyMoveToPlayerBackWhenClose(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
            ApplyOrbitAroundPlayer(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, ai, __instance);
            ApplyMoveToTargetBackWhenAtApproachDistance(ref moveInput, toPlayerFlat, liveDist, aiPos, livePlayerPos, desiredMinDist, ai, __instance);
            ApplyPlayerAsObstacleDeflection(ref moveInput, __instance, toPlayerFlat, liveDist, aiPos, livePlayerPos);
            ApplyStopWhenReachedPlayerBack(ref moveInput, aiPos, livePlayerPos, liveDist, mainForRetreat, __instance);
            ApplyPostDashPauseIfActive(__instance, ai, ref moveInput);
            ApplyMovePauseIfActive(__instance, ref moveInput);
            ApplyNoBackpedalCap(__instance, ref moveInput);
            ApplyFaceMovementDirection(__instance, moveInput, toPlayerFlat);
            UpdateApproachStateForDash(ai, moveInput, toPlayerFlat);
            CapMoveInputByMinDistanceFromPlayer(ref moveInput, __instance);
            EnsureMinMoveAfterCap(ref moveInput, __instance);
            StoreLastMoveInput(__instance, ref moveInput);
        }

        /// <summary>대시 종료 직후 짧은 구간에서 이동을 0으로 둠. 대시→잠깐 멈춤→사격 패턴으로 기계적 느낌 완화.</summary>
        private static void ApplyPostDashPauseIfActive(CharacterMainControl __instance, global::AICharacterController ai, ref Vector3 moveInput)
        {
            if (ai == null || !AdaptiveAISettings.PostDashPauseEnabled) return;
            float lastDodge = AICharacterControllerPatches.GetLastAnyDodgeTime(ai);
            if (lastDodge < 0f) return;
            float elapsed = Time.time - lastDodge;
            float start = AdaptiveAISettings.PostDashPauseStartAfter;
            float dur = Mathf.Max(0.01f, AdaptiveAISettings.PostDashPauseDuration);
            if (elapsed >= start && elapsed < start + dur)
            {
                moveInput.x = 0f;
                moveInput.z = 0f;
                moveInput.y = 0f;
            }
        }

        /// <summary>대시와 무관하게 가끔 짧게 멈췄다가 다시 움직이게 함. 자연스러운 리듬감.</summary>
        private static void ApplyMovePauseIfActive(CharacterMainControl __instance, ref Vector3 moveInput)
        {
            if (__instance == null || !AdaptiveAISettings.MovePauseEnabled) return;
            float now = Time.time;
            float until = _movePauseUntilTime.TryGetValue(__instance, out float u) ? u : -1f;
            if (now < until)
            {
                moveInput.x = 0f;
                moveInput.z = 0f;
                moveInput.y = 0f;
                return;
            }
            float nextCheck = _movePauseNextCheckTime.TryGetValue(__instance, out float n) ? n : -1f;
            if (now < nextCheck) return;
            _movePauseNextCheckTime[__instance] = now + Mathf.Max(0.5f, AdaptiveAISettings.MovePauseCheckInterval);
            float chance = Mathf.Clamp01(AdaptiveAISettings.MovePauseChance);
            if (UnityEngine.Random.value >= chance) return;
            float minD = Mathf.Max(0.05f, AdaptiveAISettings.MovePauseMinDuration);
            float maxD = Mathf.Max(minD, AdaptiveAISettings.MovePauseMaxDuration);
            float duration = UnityEngine.Random.Range(minD, maxD);
            _movePauseUntilTime[__instance] = now + duration;
            moveInput.x = 0f;
            moveInput.z = 0f;
            moveInput.y = 0f;
        }

        /// <summary>v1.1.8: 후퇴형 플레이어가 최근 피격됐을 때 접근 블렌드 일시 강화(푸시).</summary>
        private static void ApplyPostHitPushIfRetreatType(ref Vector3 moveInput, Vector3 toPlayerFlat, Data.BehaviorProfileSummary? summary)
        {
            if (!AdaptiveAISettings.PostHitPushEnabled || summary == null) return;
            float now = Time.time;
            if (now - summary.LastEnemyToPlayerHitTime > AdaptiveAISettings.PostHitPushDuration) return;
            if (summary.ThreatResponseRetreatTendencyEma < AdaptiveAISettings.ThreatResponseRetreatThreshold) return;
            float extra = Mathf.Clamp((AdaptiveAISettings.PostHitPushBlendMultiplier - 1f) * 0.5f, 0f, 0.25f);
            float y = moveInput.y;
            Vector3 flat = moveInput;
            flat.y = 0f;
            float origMag = flat.magnitude;
            if (flat.sqrMagnitude < 0.0001f) flat = toPlayerFlat;
            else flat.Normalize();
            Vector3 blended = Vector3.Lerp(flat, toPlayerFlat, extra);
            if (blended.sqrMagnitude > 0.0001f)
            {
                blended.Normalize();
                blended *= Mathf.Max(0.01f, origMag);
            }
            moveInput = blended;
            moveInput.y = y;
        }

        /// <summary>비선공몹·미인지 시 호출. moveInput에서 플레이어 방향 성분을 제거해 회피/무빙 패턴으로 플레이어 쪽으로 걷는 현상을 완전 차단.</summary>
        private static void BlockMoveTowardPlayerForNonAggroUnnoticed(CharacterMainControl c, ref Vector3 moveInput)
        {
            var main = CharacterMainControl.Main;
            if (main == null) return;
            Vector3 toPlayer = main.transform.position - c.transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.0001f) return;
            Vector3 toPlayerFlat = toPlayer.normalized;

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) return;

            float dot = Vector3.Dot(moveFlat.normalized, toPlayerFlat);
            if (dot <= 0.1f) return;

            Vector3 towardComponent = toPlayerFlat * Vector3.Dot(moveFlat, toPlayerFlat);
            moveFlat -= towardComponent;
            if (moveFlat.sqrMagnitude < 0.01f)
            {
                moveInput.x = 0f;
                moveInput.z = 0f;
                moveInput.y = 0f;
            }
            else
            {
                moveInput.x = moveFlat.x;
                moveInput.z = moveFlat.z;
                moveInput.y = 0f;
            }
        }

        /// <summary>후퇴가 과할 때(완전 뒤로 걷기) 옆 방향으로 캡해 뒤로 걷기 완화.</summary>
        private static void ApplyRetreatCap(ref Vector3 moveInput, Vector3 toPlayerFlat)
        {
            if (!AdaptiveAISettings.RetreatCapEnabled) return;
            Vector3 flat = moveInput;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f) return;
            flat.Normalize();
            float dot = Vector3.Dot(flat, toPlayerFlat);
            if (dot >= RetreatCapDotThreshold) return;
            Vector3 right = Vector3.Cross(Vector3.up, toPlayerFlat);
            if (right.sqrMagnitude < 0.01f) return;
            right.Normalize();
            // 목표: 접근+옆 방향(dot ≈ RetreatCapTargetDot). 순수 옆(right)이면 dot=0으로 평행 접근처럼 보임 → 플레이어 방향 성분 포함.
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - RetreatCapTargetDot * RetreatCapTargetDot));
            Vector3 targetDir = (toPlayerFlat * RetreatCapTargetDot + right * r).normalized;
            float t = Mathf.Clamp01((RetreatCapDotThreshold - dot) / (RetreatCapDotThreshold - RetreatCapTargetDot));
            Vector3 capped = Vector3.Lerp(flat, targetDir, t);
            if (capped.sqrMagnitude > 0.0001f)
            {
                capped.Normalize();
                capped *= Mathf.Max(0.01f, moveInput.magnitude);
                capped.y = moveInput.y;
                moveInput = capped;
            }
        }

        /// <summary>캐릭터 시야각(전방 원뿔) 안에서만 이동하도록 제한. 이동 방향이 전방 기준 허용 각도를 넘으면 원뿔 경계로 보정. 구르기(대시) 중에는 적용하지 않음.</summary>
        private static void ApplyNoBackpedalCap(CharacterMainControl c, ref Vector3 moveInput)
        {
            if (c == null || !AdaptiveAISettings.NoBackpedalEnabled) return;
            // 구르기(대시) 방향은 제한하지 않음. 이동 방향 제한은 일반 보행에만 적용.
            if (c.dashAction != null && c.dashAction.Running) return;
            Vector3 flat = moveInput;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f) return;
            flat.Normalize();

            Vector3 charForward = c.transform.forward;
            charForward.y = 0f;
            if (charForward.sqrMagnitude < 0.0001f) return;
            charForward.Normalize();

            float halfAngleDeg = Mathf.Clamp(AdaptiveAISettings.NoBackpedalViewConeHalfAngleDeg, 1f, 180f);
            float minDot = Mathf.Cos(halfAngleDeg * Mathf.Deg2Rad);
            float dot = Vector3.Dot(flat, charForward);
            if (dot >= minDot) return;

            Vector3 perp = flat - charForward * dot;
            if (perp.sqrMagnitude < 0.0001f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, charForward);
                if (right.sqrMagnitude > 0.01f) right.Normalize();
                else return;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - minDot * minDot));
                flat = (charForward * minDot + right * r).normalized;
            }
            else
            {
                perp.Normalize();
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - minDot * minDot));
                flat = (charForward * minDot + perp * r).normalized;
            }
            float mag = Mathf.Max(0.01f, new Vector3(moveInput.x, 0f, moveInput.z).magnitude);
            float preserveY = moveInput.y;
            moveInput = flat * mag;
            moveInput.y = preserveY;
        }

        /// <summary>이동할 때 바라보는 방향을 이동 방향과 일치시킴. 접근·후퇴·옆걸음 모두 적용.</summary>
        private static void ApplyFaceMovementDirection(CharacterMainControl c, Vector3 moveInput, Vector3 toPlayerFlat)
        {
            if (c == null || !AdaptiveAISettings.FaceMovementDirectionWhenApproaching) return;
            if (c.movementControl == null) return;
            Vector3 flat = moveInput;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) return;
            flat.Normalize();
            try { c.movementControl.ForceTurnTo(flat); } catch { }
        }

        /// <summary>접근/엄폐 이탈 대시 조건용: 이번 프레임 이동이 플레이어 방향(접근)이면 LastApproachTime 갱신.</summary>
        private static void UpdateApproachStateForDash(global::AICharacterController ai, Vector3 moveInput, Vector3 toPlayerFlat)
        {
            if (ai == null || toPlayerFlat.sqrMagnitude < 0.0001f) return;
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) return;
            moveFlat.Normalize();
            toPlayerFlat.Normalize();
            if (Vector3.Dot(moveFlat, toPlayerFlat) > 0.35f)
            {
                var holder = _approachStateByAi.GetOrCreateValue(ai);
                holder.LastApproachTime = Time.time;
            }
        }

        /// <summary>이 AI가 최근 withinSeconds(기본 0.6초) 이내에 플레이어 방향(접근) 이동을 했으면 true. 접근 대시/전술 대시 조건용.</summary>
        internal static bool IsApproachingPlayer(global::AICharacterController ai, float withinSeconds = 0.6f)
        {
            if (ai == null || withinSeconds <= 0f) return false;
            if (!_approachStateByAi.TryGetValue(ai, out var holder)) return false;
            return (Time.time - holder.LastApproachTime) <= withinSeconds;
        }

        /// <summary>이 AI에 ApplyLeaveCoverToEngage가 최근 withinSeconds(기본 0.5초) 이내에 적용됐으면 true. 엄폐 이탈 대시/전술 대시 조건용.</summary>
        internal static bool WasLeaveCoverEngageRecently(global::AICharacterController ai, float withinSeconds = 0.5f)
        {
            if (ai == null || withinSeconds <= 0f) return false;
            if (!_approachStateByAi.TryGetValue(ai, out var holder)) return false;
            return (Time.time - holder.LastLeaveCoverEngageTime) <= withinSeconds;
        }

        /// <summary>모드 엄폐 판정(낮은 레이 등): AI↔플레이어 사이에 장애물이 있으면 true. BlockFireWhenInCover에서 hasObsticleToTarget과 함께 사용.</summary>
        internal static bool IsCoverBetweenForBlockFire(Vector3 aiPos, Vector3 playerPos)
        {
            return TryGetCoverDirection(aiPos, playerPos, out _);
        }

        /// <summary>근접 시(약 1m 이내) 또는 판단력이 높을 때 플레이어 시야 밖으로 이동하려는 무빙을 블렌드. 위치 겹침 시 제자리 멈춤 완화·똑똑한 AI는 시야 이탈 움직임.</summary>
        private static void ApplyMoveOutOfPlayerView(ref Vector3 moveInput, Vector3 toPlayerFlat, float dist, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.MoveOutOfPlayerViewEnabled || ai == null) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            float closeDist = Mathf.Max(0.1f, AdaptiveAISettings.MoveOutOfPlayerViewCloseDist);
            float judgmentDistMax = Mathf.Max(closeDist, AdaptiveAISettings.MoveOutOfPlayerViewJudgmentDistMax);
            if (dist >= judgmentDistMax) return;

            Vector3 playerForward = main.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.0001f) return;
            playerForward.Normalize();
            Vector3 playerToAi = aiPos - playerPos;
            playerToAi.y = 0f;
            if (playerToAi.sqrMagnitude < 0.0001f) return;
            playerToAi.Normalize();
            Vector3 outOfViewDir = playerToAi - playerForward * Vector3.Dot(playerToAi, playerForward);
            if (outOfViewDir.sqrMagnitude < 0.01f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, playerForward);
                if (right.sqrMagnitude > 0.01f) outOfViewDir = right.normalized;
                else return;
            }
            else
                outOfViewDir.Normalize();

            float blend = 0f;
            if (dist <= closeDist)
            {
                float t = 1f - dist / closeDist;
                blend = t * Mathf.Clamp01(AdaptiveAISettings.MoveOutOfPlayerViewBlendAtClose);
            }
            else if (AdaptiveAISettings.MoveOutOfPlayerViewByJudgmentEnabled && dist < judgmentDistMax)
            {
                float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
                float range = judgmentDistMax - closeDist;
                if (range > 0.0001f)
                {
                    float t = 1f - (dist - closeDist) / range;
                    blend = judgmentNorm * t * Mathf.Clamp01(AdaptiveAISettings.MoveOutOfPlayerViewBlendByJudgment);
                }
            }
            if (blend <= 0.0001f) return;

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            float mag = moveFlat.magnitude;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlat, outOfViewDir, blend).normalized;
            moveInput.x = blended.x * Mathf.Max(mag, 0.5f);
            moveInput.z = blended.z * Mathf.Max(mag, 0.5f);
            moveInput.y = 0f;
        }

        /// <summary>ApplyMoveOutOfPlayerView 호출 시 캐릭터를 넘겨 무빙 패턴 활성 표시(가속 배율 적용).</summary>
        private static void ApplyMoveOutOfPlayerView(ref Vector3 moveInput, Vector3 toPlayerFlat, float dist, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai, CharacterMainControl c)
        {
            ApplyMoveOutOfPlayerView(ref moveInput, toPlayerFlat, dist, aiPos, playerPos, ai);
            if (c != null) MarkMovementPatternActive(c);
        }

        /// <summary>뒤쪽에 도달했을 때 이동을 완전히 멈추지 않고 약하게 유지. 플레이어 방향이 바뀌면 등뒤 목표가 움직이므로 실시간 추적 가능. 경계선 구간(2.5m 초과)에서는 감속만 완화해 왓다갓다·원 도는 현상 완화.</summary>
        private static void ApplyStopWhenReachedPlayerBack(ref Vector3 moveInput, Vector3 aiPos, Vector3 playerPos, float dist, CharacterMainControl main, CharacterMainControl aiCharacter = null)
        {
            if (main == null) return;
            if (dist > StopWhenReachedBackDistMax || dist < 0.01f) return;
            if (!IsAIBehindPlayer(aiPos, playerPos, main)) return;
            if (aiCharacter != null)
            {
                var ai = aiCharacter.GetComponent<global::AICharacterController>() ?? aiCharacter.GetComponentInParent<global::AICharacterController>();
                if (ai != null)
                {
                    var enemy = AICharacterControllerPatches.GetEnemyLoadout(ai);
                    if (!enemy.IsMelee && dist < RangedTooCloseRetreatDist)
                        return;
                }
            }
            // 경계선 근처(2.5m 초과)에서는 감속 완화해 뒤쪽에 머무르되 과도한 정지·떨림 방지
            float reduce = dist > 2.5f ? 0.52f : 0.28f;
            moveInput.x *= reduce;
            moveInput.z *= reduce;
            moveInput.y *= reduce;
        }

        /// <summary>접근 대시 시 플레이어 뒤쪽으로 굴릴 때 사용. 판단능력 높을 때 등뒤 대시 패턴용.</summary>
        internal static bool GetDirectionTowardPlayerBackForDash(CharacterMainControl main, Vector3 aiPos, out Vector3 dirToBack)
        {
            return GetDirectionTowardPlayerBack(Vector3.zero, aiPos, main, out dirToBack);
        }

        /// <summary>접근 대시 시 이미 플레이어 뒤에 있으면 등뒤 대시 스킵(옆/정면만).</summary>
        internal static bool IsAIBehindPlayerForDash(CharacterMainControl main, Vector3 aiPos)
        {
            return main != null && IsAIBehindPlayer(aiPos, main.transform.position, main);
        }

        /// <summary>플레이어 기준으로 AI가 등 뒤 쪽에 있으면 true. 뒤에선 궤도/등뒤 블렌드 약화해 왓다갓다 방지.</summary>
        private static bool IsAIBehindPlayer(Vector3 aiPos, Vector3 playerPos, CharacterMainControl main)
        {
            if (main == null) return false;
            Vector3 toPlayer = playerPos - aiPos;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.0001f) return false;
            toPlayer.Normalize();
            Vector3 playerForward = main.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f) return false;
            playerForward.Normalize();
            return Vector3.Dot(playerForward, toPlayer) < BehindPlayerDotThreshold;
        }

        /// <summary>접근 시 "절대 이 거리보다 가까이 가지 않을" 최소 유지 거리(m). 근접=OverlapEscapeDist, 총기=Max(MinDistanceFromPlayer, OverlapEscapeDist). MinDistanceFromPlayer로 접근 거리 조정 가능.</summary>
        private static float GetDesiredMinDistanceFromPlayer(CharacterMainControl c, bool isMelee)
        {
            if (isMelee) return OverlapEscapeDist;
            float minFromSettings = Mathf.Max(0f, AdaptiveAISettings.MinDistanceFromPlayer);
            return Mathf.Max(minFromSettings, OverlapEscapeDist);
        }

        /// <summary>접근 목표 위치: 플레이어로부터 minDist(m) 떨어진, AI 방향의 한 점. 이 점을 목표로 하면 minDist 안으로 들어가지 않음(겹침 근본 방지).</summary>
        private static Vector3 GetApproachTargetPosition(Vector3 aiPos, Vector3 playerPos, float minDist)
        {
            Vector3 toAi = aiPos - playerPos;
            toAi.y = 0f;
            if (toAi.sqrMagnitude < 0.0001f) return playerPos;
            return playerPos + toAi.normalized * Mathf.Max(0f, minDist);
        }

        /// <summary>이동/접근 목표의 기준점. ApproachTargetOffsetBehindPlayerMeters &gt; 0이면 플레이어가 바라보는 방향의 뒤쪽 해당 거리(m) 지점을 반환(겹침 완화). 0이면 fallbackPlayerPos 그대로.</summary>
        private static Vector3 GetMovementTargetCenterPosition(CharacterMainControl main, Vector3 fallbackPlayerPos, float offsetBehindMeters)
        {
            if (offsetBehindMeters <= 0f || main == null) return fallbackPlayerPos;
            Vector3 p = main.transform.position;
            Vector3 f = main.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) return fallbackPlayerPos;
            f.Normalize();
            Vector3 behind = p - f * Mathf.Max(0f, offsetBehindMeters);
            behind.y = fallbackPlayerPos.y;
            return behind;
        }

        /// <summary>플레이어가 바라보는 방향의 뒤쪽에 있는 웨이포인트 월드 위치. 경계선 도달 시 "멈춤 → 뒤로 이동" 명령용.</summary>
        private static Vector3 GetWaypointPositionBehindPlayer(CharacterMainControl main, float distanceFromPlayer)
        {
            if (main == null) return Vector3.zero;
            Vector3 p = main.transform.position;
            Vector3 f = main.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) return p;
            f.Normalize();
            Vector3 behind = p - f * Mathf.Max(distanceFromPlayer, 2f);
            behind.y = p.y;
            return behind;
        }

        /// <summary>경계선(최소 유지 거리) 근처에서 접근/후퇴 전환 시 왓다갓다 방지용 히스테리시스 밴드(m). 이 거리 안이면 "원 위"로 보고 궤도/등뒤만 사용. 1.5로 확대해 접근 거리 도달 후 멈춤·무반응 완화.</summary>
        private const float ApproachBoundaryHysteresisBand = 1.5f;
        /// <summary>경계선 도달 시 앞뒤 진동 방지: 이 거리(m) 안이면 Cap(접근/후퇴 성분 제거)를 적용하지 않음. 궤도만 유지해 프레임마다 in/out 전환으로 인한 흔들림 제거.</summary>
        private const float ApproachBoundaryRadialDeadZone = 0.5f;
        /// <summary>권총 등 단거리 총기: 궤도 블렌드 보정. 1보다 크면 뒤쪽으로 궤도 그리며 접근하는 강도 증가.</summary>
        private const float ShortRangeOrbitBlendBoost = 1.4f;
        /// <summary>권총 등 단거리 총기: 등뒤 무빙 적용 거리 배율. 이 배율을 곱해 더 멀리서부터 등뒤로 궤도 그리며 접근.</summary>
        private const float ShortRangeMoveToBackDistScale = 1.35f;

        /// <summary>궤도 좌/우 전환 주기(초). 이마다 궤도 방향이 바뀌어 한쪽 대각선에만 수렴·멈춤 방지.</summary>
        private const float OrbitDirectionFlipPeriod = 4f;

        /// <summary>플레이어 주변 궤도(옆/뒤로 도는) + 뒤쪽 서성임 방향 계산. orbitCharacter 넣으면 주기적으로 좌/우 전환해 한쪽에만 수렴하지 않음. 이미 뒤에 있으면 전환 없이 등뒤 쏠림만 해 원 도는 현상 완화.</summary>
        private static bool GetOrbitDirection(Vector3 playerPos, Vector3 aiPos, CharacterMainControl main, out Vector3 orbitDir, CharacterMainControl orbitCharacter = null)
        {
            orbitDir = Vector3.zero;
            if (main == null) return false;
            Vector3 toPlayer = playerPos - aiPos;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.0001f) return false;
            toPlayer.Normalize();
            Vector3 playerForward = main.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.0001f) return false;
            playerForward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, toPlayer);
            if (right.sqrMagnitude < 0.01f) return false;
            right.Normalize();
            float sign = Mathf.Sign(Vector3.Dot(right, -playerForward));
            if (Mathf.Abs(sign) < 0.001f) sign = 1f;
            bool alreadyBehind = IsAIBehindPlayer(aiPos, playerPos, main);
            if (alreadyBehind)
            {
                // 뒤에 있을 때는 좌/우 전환 없이 등뒤 방향만 강하게 → 계속 원 그리지 않고 뒤쪽에 머무름
                orbitCharacter = null;
            }
            if (orbitCharacter != null)
            {
                int phase = (int)(Time.time / OrbitDirectionFlipPeriod) + orbitCharacter.GetInstanceID();
                sign *= ((phase % 2 == 0) ? 1f : -1f);
            }
            Vector3 tangent = (sign * right).normalized;
            float behindBias = alreadyBehind ? 0.9f : Mathf.Clamp01(AdaptiveAISettings.OrbitPreferBehind);
            orbitDir = (tangent + behindBias * (-playerForward)).normalized;
            return orbitDir.sqrMagnitude > 0.01f;
        }

        /// <summary>플레이어 등 뒤 한 점(플레이어 현재 위치 - 전방*오프셋)을 향하는 방향. 항상 main의 실시간 position/forward 사용해 고정된 위치에서 멈추는 현상 방지.</summary>
        private static bool GetDirectionTowardPlayerBack(Vector3 playerPos, Vector3 aiPos, CharacterMainControl main, out Vector3 dirToBack)
        {
            dirToBack = Vector3.zero;
            if (main == null) return false;
            Vector3 livePos = main.transform.position;
            Vector3 playerForward = main.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f) return false;
            playerForward.Normalize();
            Vector3 behindPoint = livePos - playerForward * 2f;
            behindPoint.y = aiPos.y;
            Vector3 toBack = behindPoint - aiPos;
            toBack.y = 0f;
            if (toBack.sqrMagnitude < 0.0001f) return false;
            dirToBack = toBack.normalized;
            return true;
        }

        /// <summary>2m 이하일 때 타겟과 거리를 벌리며 플레이어 등 뒤로 가려는 무빙 블렌드. 판단능력이 높을수록 등뒤 이동 경향 강화. 권총 등 단거리는 적용 거리 확대.</summary>
        private static void ApplyMoveToPlayerBackWhenClose(ref Vector3 moveInput, Vector3 toPlayerFlat, float dist, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.MoveToPlayerBackWhenCloseEnabled || ai == null) return;
            float closeDist = Mathf.Max(0.5f, AdaptiveAISettings.MoveToPlayerBackWhenCloseDist);
            var enemyBack = AICharacterControllerPatches.GetEnemyLoadout(ai);
            if (!enemyBack.IsMelee && enemyBack.WeaponRange > 0f && enemyBack.WeaponRange <= PlayerLoadoutProfile.ShortRangeThreshold)
                closeDist *= ShortRangeMoveToBackDistScale;
            if (dist > closeDist) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            if (!GetDirectionTowardPlayerBack(playerPos, aiPos, main, out Vector3 dirToBack)) return;
            float blend = (1f - dist / closeDist) * Mathf.Clamp01(AdaptiveAISettings.MoveToPlayerBackBlend);
            if (!enemyBack.IsMelee && enemyBack.WeaponRange > 0f && enemyBack.WeaponRange <= PlayerLoadoutProfile.ShortRangeThreshold)
                blend *= Mathf.Clamp(ShortRangeOrbitBlendBoost, 1f, 1.5f);
            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            blend *= Mathf.Lerp(0.7f, 1f, judgmentNorm);
            // 이미 플레이어 뒤에 있으면 블렌드 대폭 축소 → 뒤쪽에서 왓다갓다/정신없는 이동 방지
            if (IsAIBehindPlayer(aiPos, playerPos, main)) blend *= 0.15f;
            if (blend <= 0.0001f) return;
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            float mag = moveFlat.magnitude;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlat, dirToBack, blend).normalized;
            mag = Mathf.Max(mag, 0.5f);
            moveInput.x = blended.x * mag;
            moveInput.z = blended.z * mag;
            moveInput.y = 0f;
        }

        /// <summary>ApplyMoveToPlayerBackWhenClose + 무빙 패턴 활성 표시.</summary>
        private static void ApplyMoveToPlayerBackWhenClose(ref Vector3 moveInput, Vector3 toPlayerFlat, float dist, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai, CharacterMainControl c)
        {
            // 경계선 구간에서는 경계선 블록이 이미 이동을 적용함. 덮어쓰면 진동(오버랩) 발생 → 스킵
            if (c != null)
            {
                var loadout = AICharacterControllerPatches.GetEnemyLoadout(ai);
                float desiredMin = GetDesiredMinDistanceFromPlayer(c, loadout.IsMelee);
                bool inBoundary = dist >= desiredMin - ApproachBoundaryHysteresisBand && dist <= desiredMin + ApproachBoundaryHysteresisBand;
                if (inBoundary) return;
            }
            ApplyMoveToPlayerBackWhenClose(ref moveInput, toPlayerFlat, dist, aiPos, playerPos, ai);
            if (c != null) MarkMovementPatternActive(c);
        }

        /// <summary>접근 거리(경계선)에 도달했을 때 타겟(플레이어)이 바라보는 방향의 뒤쪽으로 이동하도록 블렌드. 판단능력이 높을수록 등뒤 이동 경향 강화.</summary>
        private static void ApplyMoveToTargetBackWhenAtApproachDistance(ref Vector3 moveInput, Vector3 toPlayerFlat, float liveDist, Vector3 aiPos, Vector3 playerPos, float desiredMinDist, global::AICharacterController ai)
        {
            if (ai == null) return;
            const float approachReachBand = 0.6f;
            bool atApproachDist = liveDist >= desiredMinDist - approachReachBand && liveDist <= desiredMinDist + 2f;
            if (!atApproachDist) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            if (!GetDirectionTowardPlayerBack(playerPos, aiPos, main, out Vector3 dirToBack)) return;
            if (IsAIBehindPlayer(aiPos, playerPos, main)) return;
            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            float blend = Mathf.Lerp(0.5f, 0.92f, judgmentNorm);
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            float mag = moveFlat.magnitude;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlat, dirToBack, blend).normalized;
            mag = Mathf.Max(mag, 0.55f);
            moveInput.x = blended.x * mag;
            moveInput.z = blended.z * mag;
            moveInput.y = 0f;
        }

        /// <summary>ApplyMoveToTargetBackWhenAtApproachDistance + 무빙 패턴 활성 표시.</summary>
        private static void ApplyMoveToTargetBackWhenAtApproachDistance(ref Vector3 moveInput, Vector3 toPlayerFlat, float liveDist, Vector3 aiPos, Vector3 playerPos, float desiredMinDist, global::AICharacterController ai, CharacterMainControl c)
        {
            // 경계선 구간에서는 경계선 블록이 이미 등뒤/궤도 방향을 적용함. 여기서 다시 덮어쓰면 진동(오버랩) 발생 → 스킵
            if (c != null)
            {
                bool inBoundary = liveDist >= desiredMinDist - ApproachBoundaryHysteresisBand && liveDist <= desiredMinDist + ApproachBoundaryHysteresisBand;
                if (inBoundary) return;
            }
            ApplyMoveToTargetBackWhenAtApproachDistance(ref moveInput, toPlayerFlat, liveDist, aiPos, playerPos, desiredMinDist, ai);
            if (c != null) MarkMovementPatternActive(c);
        }

        /// <summary>근접 시 플레이어 주변을 도는 궤도 + 뒤쪽 서성임 블렌드. 겹침·멈춤 방지, 360도 시야에서도 거리 유지하며 공격.</summary>
        /// <param name="orbitDistMaxOverride">null이 아니면 이 값을 궤도 적용 최대 거리로 사용. 접근 목표 원(최대 궤적)에 도달했을 때도 궤도/등뒤 무빙 유지용.</param>
        /// <param name="orbitCharacter">넣으면 궤도 좌/우가 주기적으로 전환되어 한쪽 대각선에만 수렴하지 않음.</param>
        private static void ApplyOrbitAroundPlayer(ref Vector3 moveInput, Vector3 toPlayerFlat, float dist, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai, float? orbitDistMaxOverride = null, CharacterMainControl orbitCharacter = null)
        {
            if (!AdaptiveAISettings.OrbitAroundPlayerEnabled || ai == null) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            float distMin = Mathf.Max(0.1f, AdaptiveAISettings.OrbitDistMin);
            float distMax = Mathf.Max(distMin, AdaptiveAISettings.OrbitDistMax);
            // 최소 유지 거리(접근 목표 원) 근처에서도 궤도 적용: distMax를 RangedTooCloseRetreatDist+1 이상으로 확대해 "주변을 돌며 뒤로" 무빙 유지
            float distMaxEffective = orbitDistMaxOverride ?? Mathf.Max(distMax, RangedTooCloseRetreatDist + 1f);
            if (dist > distMaxEffective) return;
            if (!GetOrbitDirection(playerPos, aiPos, main, out Vector3 orbitDir, orbitCharacter)) return;

            float blend = 0f;
            float minMag = 0f;
            if (dist <= distMin)
            {
                blend = Mathf.Lerp(0.85f, 0.65f, dist / distMin);
                minMag = AdaptiveAISettings.OrbitMinMoveMagnitude;
            }
            else
            {
                float t = 1f - (dist - distMin) / Mathf.Max(0.01f, distMaxEffective - distMin);
                blend = Mathf.Clamp01(t) * Mathf.Clamp01(AdaptiveAISettings.OrbitBlendStrength);
                // 궤도 무빙이 아예 안 보이지 않도록 적용 구간 내에서는 최소 블렌드 보장
                blend = Mathf.Max(blend, 0.38f);
            }
            // 이미 플레이어 뒤에 있으면 궤도 블렌드·최소 이동 일부만 축소 → 뒤쪽에서도 궤도 패턴이 보이도록
            if (IsAIBehindPlayer(aiPos, playerPos, main)) { blend *= 0.4f; minMag *= 0.5f; }
            // 권총 등 단거리 총기: 궤도 블렌드 강화 → 뒤쪽으로 궤도를 그리며 접근
            var enemyOrbit = AICharacterControllerPatches.GetEnemyLoadout(ai);
            if (!enemyOrbit.IsMelee && enemyOrbit.WeaponRange > 0f && enemyOrbit.WeaponRange <= PlayerLoadoutProfile.ShortRangeThreshold)
                blend = Mathf.Clamp01(blend * ShortRangeOrbitBlendBoost);
            if (blend <= 0.0001f) return;

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            float mag = moveFlat.magnitude;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlat, orbitDir, blend).normalized;
            mag = minMag > 0f ? Mathf.Max(mag, minMag) : mag;
            moveInput.x = blended.x * Mathf.Max(mag, 0.35f);
            moveInput.z = blended.z * Mathf.Max(mag, 0.35f);
            moveInput.y = 0f;
        }

        /// <summary>ApplyOrbitAroundPlayer + 무빙 패턴 활성 표시. 접근 목표 원(최대 궤적) 구간에서도 궤도 적용되도록 distMax 확대.</summary>
        private static void ApplyOrbitAroundPlayer(ref Vector3 moveInput, Vector3 toPlayerFlat, float dist, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai, CharacterMainControl c)
        {
            // 경계선 구간에서는 경계선 블록이 이미 궤도/등뒤 방향을 적용함. 여기서 다시 덮어쓰면 진동(오버랩) 발생 → 스킵
            if (c != null)
            {
                var loadout = AICharacterControllerPatches.GetEnemyLoadout(ai);
                float desiredMin = GetDesiredMinDistanceFromPlayer(c, loadout.IsMelee);
                bool inBoundary = dist >= desiredMin - ApproachBoundaryHysteresisBand && dist <= desiredMin + ApproachBoundaryHysteresisBand;
                if (inBoundary) return;
            }
            float? orbitDistMaxOverride = null;
            if (c != null)
            {
                var enemyLoadout = AICharacterControllerPatches.GetEnemyLoadout(ai);
                float desiredMinDist = GetDesiredMinDistanceFromPlayer(c, enemyLoadout.IsMelee);
                float distMaxBase = Mathf.Max(Mathf.Max(0.1f, AdaptiveAISettings.OrbitDistMin), AdaptiveAISettings.OrbitDistMax);
                orbitDistMaxOverride = Mathf.Max(distMaxBase, RangedTooCloseRetreatDist + 1f, desiredMinDist + 1f);
            }
            ApplyOrbitAroundPlayer(ref moveInput, toPlayerFlat, dist, aiPos, playerPos, ai, orbitDistMaxOverride, c);
            if (c != null) MarkMovementPatternActive(c);
        }

        /// <summary>Cap 적용 후 매우 가까운데 이동이 거의 0이면 궤도 방향으로 최소 이동을 넣어 멈춤·겹침 방지. 경계선(접근 목표 원) 구간에서도 적용해 도달 후 멈춤 방지.</summary>
        private static void EnsureMinMoveAfterCap(ref Vector3 moveInput, CharacterMainControl c)
        {
            if (c == null || AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude <= 0f) return;
            // 플레이어 뒤 웨이포인트 이동 중: 궤도 주입 스킵(경로 방향 유지)
            if (AI_PathControlPatches.IsCharacterPathFollowing(c) && _boundaryWaypointPlayerForwardByCharacter.ContainsKey(c)) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            Vector3 aiPos = c.transform.position;
            Vector3 playerPos = GetSmoothedPlayerPositionForMovement(c);
            Vector3 toPlayer = playerPos - aiPos;
            toPlayer.y = 0f;
            float dist = toPlayer.magnitude;
            if (dist < 0.001f) return;
            float closeThreshold = Mathf.Max(AdaptiveAISettings.OrbitDistMin, AdaptiveAISettings.OrbitDistMax);
            var ai = c.GetComponent<global::AICharacterController>() ?? c.GetComponentInParent<global::AICharacterController>();
            bool isMelee = ai != null && AICharacterControllerPatches.GetEnemyLoadout(ai).IsMelee;
            float desiredMinDist = GetDesiredMinDistanceFromPlayer(c, isMelee);
            bool inBoundaryBand = dist >= desiredMinDist - ApproachBoundaryHysteresisBand && dist <= desiredMinDist + ApproachBoundaryHysteresisBand;
            bool inOrbitOrBoundaryRange = dist < closeThreshold || inBoundaryBand;
            if (!inOrbitOrBoundaryRange) return;
            // 경계선에서는 이미 경계선 블록이 이동을 설정함. 여기서 다시 궤도로 덮어쓰면 진동(위아래/방향 오버랩) 발생 → 스킵
            if (inBoundaryBand) return;
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude >= 0.12f) return;
            bool behindPlayer = IsAIBehindPlayer(aiPos, playerPos, main);
            if (behindPlayer && !inBoundaryBand) return;
            Vector3 orbitDir;
            if (inBoundaryBand && ai != null && TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover))
            {
                Vector3 toApproachFlat = toPlayer.normalized;
                if (GetOrbitDirection(playerPos, aiPos, main, out orbitDir, c))
                    toApproachFlat = orbitDir;
                orbitDir = Vector3.Lerp(toApproachFlat, toCover, Mathf.Clamp01(AdaptiveAISettings.CoverApproachBlendStrength)).normalized;
            }
            else if (!GetOrbitDirection(playerPos, aiPos, main, out orbitDir, c))
            {
                // 경계선 구간에서는 궤도 실패 시 등뒤 방향으로라도 최소 이동 주입해 멈춤 방지
                if (inBoundaryBand && GetDirectionTowardPlayerBack(playerPos, aiPos, main, out Vector3 dirToBack))
                    orbitDir = dirToBack;
                else
                    return;
            }
            if (orbitDir.sqrMagnitude < 0.01f) return;
            float mag = Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude, 0.2f, 1f);
            if (behindPlayer && inBoundaryBand)
                mag *= 0.45f;
            moveInput.x = orbitDir.x * mag;
            moveInput.z = orbitDir.z * mag;
            moveInput.y = 0f;
        }

        /// <summary>플레이어를 장애물로 간주: 가까울 때 플레이어 쪽으로 가는 이동을 옆(접선) 방향으로 치우쳐 겹치지 않고 돌아가게 함.</summary>
        private static void ApplyPlayerAsObstacleDeflection(ref Vector3 moveInput, CharacterMainControl c, Vector3 toPlayerFlat, float dist, Vector3 aiPos, Vector3 playerPos)
        {
            if (c == null || dist >= PlayerAsObstacleDist || dist < 0.01f) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.01f) return;
            moveFlat.Normalize();
            if (Vector3.Dot(moveFlat, toPlayerFlat) <= 0.2f) return; // 이미 옆/뒤로만 가면 무시
            if (!GetOrbitDirection(playerPos, aiPos, main, out Vector3 orbitDir, c)) return;
            // 플레이어 쪽 성분을 줄이고 궤도(옆) 방향으로 블렌드 → 장애물을 돌아가는 느낌
            float blend = Mathf.Clamp01((1f - dist / PlayerAsObstacleDist) * 0.85f);
            Vector3 deflected = Vector3.Slerp(moveFlat, orbitDir, blend).normalized;
            float mag = Mathf.Max(0.01f, new Vector3(moveInput.x, 0f, moveInput.z).magnitude);
            moveInput.x = deflected.x * mag;
            moveInput.z = deflected.z * mag;
            moveInput.y = 0f;
        }

        /// <summary>플레이어와 너무 가까우면 접근 방향 이동 성분을 제거해 좌표 겹침/비비기 방지. 겹침 구간(OverlapEscapeDist 미만)에서는 플레이어 반대 방향으로 밀어냄. 엄폐물 우회 경로 추종 중에는 최소 거리 캡 생략(경로 관통 허용, 최종 위치만 만족).</summary>
        private static void CapMoveInputByMinDistanceFromPlayer(ref Vector3 moveInput, CharacterMainControl c)
        {
            if (c == null || AdaptiveAISettings.MinDistanceFromPlayer <= 0f) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            // 플레이어 뒤 웨이포인트로 이동 중: 접근거리 제한 전부 스킵 → 경로대로 지나가서 뒤로 도달 가능하게 함
            if (AI_PathControlPatches.IsCharacterPathFollowing(c) && _boundaryWaypointPlayerForwardByCharacter.ContainsKey(c))
                return;
            Vector3 toPlayer = GetSmoothedPlayerPositionForMovement(c) - c.transform.position;
            toPlayer.y = 0f;
            float dist = toPlayer.magnitude;
            // 가까울 때는 실시간 플레이어 위치로 겹침/비비기 판정해 즉시 반응
            if (dist < 6f)
            {
                Vector3 toLive = main.transform.position - c.transform.position;
                toLive.y = 0f;
                float liveMag = toLive.magnitude;
                if (liveMag >= 0.001f) { toPlayer = toLive; dist = liveMag; toPlayer.Normalize(); }
            }
            else if (toPlayer.sqrMagnitude >= 0.0001f)
                toPlayer.Normalize();
            var ai = c.GetComponent<global::AICharacterController>() ?? c.GetComponentInParent<global::AICharacterController>();
            bool isMelee = ai != null && AICharacterControllerPatches.GetEnemyLoadout(ai).IsMelee;
            float effectiveMinDist = GetDesiredMinDistanceFromPlayer(c, isMelee);
            if (dist >= effectiveMinDist || dist < 0.001f) return;
            // 경계선 데드존: 원 위에 있을 때 Cap을 건드리지 않음 → 접근/후퇴 전환이 프레임마다 바뀌며 앞뒤로 진동하는 현상 방지
            if (dist >= effectiveMinDist - ApproachBoundaryRadialDeadZone && dist <= effectiveMinDist + ApproachBoundaryRadialDeadZone) return;
            // 엄폐물 우회용으로 요청한 경로 추종 중: 최소 거리 구간 관통 허용(최종 도착지만 만족). 겹침(OverlapEscapeDist 미만)만 강제 해소.
            bool weRequestedFlankingPath = AI_PathControlPatches.IsCharacterPathFollowing(c)
                && _lastPathRequestToPlayerTime.TryGetValue(c, out float flankReqTime) && (Time.time - flankReqTime) < PathRequestToPlayerGraceSeconds;
            if (weRequestedFlankingPath && dist >= OverlapEscapeDist) return;
            // 겹침 구간: 플레이어 반대 방향으로 이동 강제해 겹침 해소
            if (dist < OverlapEscapeDist)
            {
                moveInput.x = -toPlayer.x * OverlapEscapeMagnitude;
                moveInput.z = -toPlayer.z * OverlapEscapeMagnitude;
                moveInput.y = 0f;
                return;
            }
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f)
            {
                Vector3 playerPos = GetSmoothedPlayerPositionForMovement(c);
                Vector3 aiPos = c.transform.position;
                var enemy = ai != null ? AICharacterControllerPatches.GetEnemyLoadout(ai) : default;
                // 뒤쪽 도달 상태(이동 정지 중)면 궤도 주입하지 않음. 단 총기 적이 RangedTooCloseRetreatDist 미만이면 후퇴 주입하므로 스킵하지 않음.
                if (IsAIBehindPlayer(aiPos, playerPos, main) && dist <= StopWhenReachedBackDistMax && (ai == null || enemy.IsMelee || dist >= RangedTooCloseRetreatDist)) return;
                // 총기 적: 너무 가까운데 이동이 0이면 후퇴(밖으로) 방향 주입 → 사거리 확보 후 공격 가능. 근접은 궤도만.
                if (ai != null && !enemy.IsMelee && dist > OverlapEscapeDist && dist < RangedTooCloseRetreatDist)
                {
                    float mag = Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude > 0f ? AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude : 0.55f, 0.4f, 1f);
                    moveInput.x = -toPlayer.x * mag;
                    moveInput.z = -toPlayer.z * mag;
                    moveInput.y = 0f;
                    return;
                }
                // 근접 또는 궤도 구간: BT가 aim/shoot 등으로 moveInput=0일 때 궤도 방향으로 최소 이동 주입
                if (AdaptiveAISettings.OrbitAroundPlayerEnabled && AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude > 0f)
                {
                    if (GetOrbitDirection(playerPos, aiPos, main, out Vector3 orbitDir, c))
                    {
                        float mag = Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude, 0.2f, 1f);
                        moveInput.x = orbitDir.x * mag;
                        moveInput.z = orbitDir.z * mag;
                        moveInput.y = 0f;
                    }
                }
                return;
            }
            float dot = Vector3.Dot(moveFlat.normalized, toPlayer);
            if (dot <= 0f) return; // 플레이어 반대/옆으로만 이동 중이면 유지
            Vector3 towardPart = toPlayer * Vector3.Dot(moveFlat, toPlayer);
            moveFlat -= towardPart;
            moveInput.x = moveFlat.x;
            moveInput.z = moveFlat.z;
            moveInput.y = 0f;
        }

        /// <summary>다음 프레임 발사 중 이동 복원용으로 현재 이동 입력 저장. 제안3: 이동 방향 프록시 갱신 및 aimTarget 설정. 방향 전환 시 가속용 판정. 근접 시 방향 스무딩으로 흔들림 완화.</summary>
        private static void StoreLastMoveInput(CharacterMainControl c, ref Vector3 moveInput)
        {
            if (c == null) return;
            Vector3 v = moveInput;
            v.y = 0f;
            if (v.sqrMagnitude < 0.0001f) return;

            // 근접 시 이동 방향을 직전 프레임과 블렌드해 뒤로 가기/궤도 등이 섞여 흔들리는 현상 완화
            if (c != CharacterMainControl.Main && _lastMoveInputByCharacter.TryGetValue(c, out Vector3 prevStored))
            {
                Vector3 toPlayer = GetSmoothedPlayerPositionForMovement(c) - c.transform.position;
                toPlayer.y = 0f;
                float dist = toPlayer.magnitude;
                if (dist < CloseRangeMoveSmoothDist && prevStored.sqrMagnitude > 0.0001f)
                {
                    Vector3 prevFlat = prevStored;
                    prevFlat.y = 0f;
                    prevFlat.Normalize();
                    Vector3 curFlat = v.normalized;
                    Vector3 blendedDir = Vector3.Slerp(prevFlat, curFlat, CloseRangeMoveSmoothFactor);
                    float mag = Mathf.Max(v.magnitude, prevStored.magnitude * 0.5f);
                    moveInput.x = blendedDir.x * mag;
                    moveInput.z = blendedDir.z * mag;
                    moveInput.y = 0f;
                    v = moveInput;
                    v.y = 0f;
                }
            }

            if (AdaptiveAISettings.MovementPatternAccelOnDirectionChange && _lastMoveInputByCharacter.TryGetValue(c, out Vector3 prev))
            {
                Vector3 prevFlat = prev;
                prevFlat.y = 0f;
                if (prevFlat.sqrMagnitude > 0.0001f)
                {
                    prevFlat.Normalize();
                    float dot = Vector3.Dot(prevFlat, v.normalized);
                    float th = Mathf.Clamp(AdaptiveAISettings.MovementPatternDirectionChangeDotThreshold, 0.1f, 1f);
                    if (dot < th)
                        _directionChangeThisFrame.Add(c);
                }
            }
            _lastMoveInputByCharacter[c] = moveInput;
            UpdateAimProxyAndSetTarget(c, moveInput);
        }

        /// <summary>제안3: 이동 방향을 가리키는 프록시 Transform을 갱신하고 ai.aimTarget으로 설정. 해당 방향으로 이동할 때 조준도 해당 방향으로 변경.
        /// 플레이어를 인식했을 때에는 호출하지 않음 — 어그로를 플레이어 우선으로 유지하고, 플레이어를 향해 다가가며 조준하도록 게임 BT의 target 유지.</summary>
        private static void UpdateAimProxyAndSetTarget(CharacterMainControl c, Vector3 moveInput)
        {
            if (c == null || !AdaptiveAISettings.FaceMovementDirectionWhenApproaching) return;
            if (c.dashAction != null && c.dashAction.Running) return;
            var ai = c.GetComponent<global::AICharacterController>() ?? c.GetComponentInParent<global::AICharacterController>();
            if (ai == null) return;
            if (AICharacterControllerPatches.IsAggroOnPlayer(ai)) return;
            Vector3 flat = moveInput;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) return;
            flat.Normalize();
            Transform proxy = GetOrCreateAimProxy(c);
            if (proxy == null) return;
            proxy.position = c.transform.position + flat * 50f;
            try { ai.SetTarget(proxy); } catch { }
        }

        private static Transform GetOrCreateAimProxy(CharacterMainControl c)
        {
            if (c == null) return null;
            if (_aimProxyByCharacter.TryGetValue(c, out Transform t) && t != null) return t;
            var go = new GameObject("AdaptiveEnemyAI_AimProxy");
            go.transform.SetParent(c.transform, worldPositionStays: false);
            t = go.transform;
            _aimProxyByCharacter[c] = t;
            return t;
        }

        /// <summary>저장된 이동 방향(수평 정규화). LateUpdate/SetAimPoint 보정에서 조준을 이동 방향과 동기화할 때 사용.</summary>
        internal static bool TryGetLastMoveDirectionFlat(CharacterMainControl c, out Vector3 flatDir)
        {
            flatDir = Vector3.zero;
            if (c == null || !_lastMoveInputByCharacter.TryGetValue(c, out Vector3 stored)) return false;
            flatDir = stored;
            flatDir.y = 0f;
            if (flatDir.sqrMagnitude < 0.01f) return false;
            flatDir.Normalize();
            return true;
        }

        /// <summary>에임 추적 디버그 로그용 AI 표시 이름 (프리셋명 또는 GameObject명, 없으면 InstanceID).</summary>
        private static string GetAimLogName(CharacterMainControl c)
        {
            if (c == null) return "null";
            string preset = c.characterPreset?.name;
            if (!string.IsNullOrEmpty(preset)) return preset;
            if (c.gameObject != null && !string.IsNullOrEmpty(c.gameObject.name)) return c.gameObject.name;
            return $"id={c.GetInstanceID()}";
        }

        /// <summary>터렛 프리셋 여부 (에임 갱신 지연 원인 확인용 디버그 로그에서 사용).</summary>
        private static bool IsTurretPreset(CharacterMainControl c)
        {
            if (c?.characterPreset?.name == null) return false;
            return string.Equals(c.characterPreset.name, CharacterMainControlDashPatches.PresetNameNoDash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>현재 사격 중인지. attackAction.Running 또는 CurrentAction==attackAction 둘 다 확인 (게임/타이밍에 따라 한쪽만 true인 경우 대비).</summary>
        private static bool IsAttackRunning(CharacterMainControl c)
        {
            if (c == null) return false;
            if (c.attackAction != null && c.attackAction.Running) return true;
            if (c.CurrentAction != null && c.attackAction != null && c.CurrentAction == c.attackAction && c.CurrentAction.Running) return true;
            return false;
        }

        /// <summary>근접 멈춤 판정용 타겟 위치. aimTarget → searchedEnemy → 플레이어 순. 없으면 Vector3.zero.</summary>
        private static Vector3 GetMeleeTargetPositionForStop(global::AICharacterController ai)
        {
            if (ai == null) return Vector3.zero;
            if (ai.aimTarget != null) return ai.aimTarget.position;
            if (ai.searchedEnemy != null) return ai.searchedEnemy.transform.position;
            var main = CharacterMainControl.Main;
            var pr = main != null ? AICharacterControllerPatches.GetPlayerDamageReceiver(main) : null;
            if (pr != null) return pr.transform.position;
            return Vector3.zero;
        }

        /// <summary>에임 추적 숙련도(0~1). 인지 경과 시간 램프 + (옵션) 판단력 보정. 스무딩 반응속도에 사용.</summary>
        private static float GetAimTrackingProficiency(global::AICharacterController ai)
        {
            if (ai == null) return 1f;
            float timeSinceNoticed = GetTimeSinceNoticedForAim(ai);
            float timeRamp = Mathf.Clamp01(timeSinceNoticed / Mathf.Max(0.01f, AdaptiveAISettings.AimTrackingRampDurationSeconds));
            if (!AdaptiveAISettings.AimTrackingUseJudgment)
                return timeRamp;
            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            float w = Mathf.Clamp01(AdaptiveAISettings.AimTrackingJudgmentWeight);
            return Mathf.Clamp01(timeRamp * (1f - w) + judgmentNorm * w);
        }

        /// <summary>해당 AI가 플레이어를 인지한 이후 경과 시간(초). noticeTimeMarker 리플렉션 사용.</summary>
        private static float GetTimeSinceNoticedForAim(global::AICharacterController ai)
        {
            if (ai == null) return 0f;
            if (_noticeTimeMarkerFieldForAim == null)
                _noticeTimeMarkerFieldForAim = AccessTools.Field(typeof(global::AICharacterController), "noticeTimeMarker");
            float t = 0f;
            try { if (_noticeTimeMarkerFieldForAim != null) t = (float)(_noticeTimeMarkerFieldForAim.GetValue(ai) ?? 0f); } catch { }
            return t > 0f ? Mathf.Max(0f, (float)Time.time - t) : 0f;
        }

        /// <summary>이동용 스무딩: 에임과 동일한 반응속도/숙련도로 목표 위치를 보간. ApplyPlayerAimSyncForNoticed 내부에서 어그로 플레이어인 AI마다 호출.</summary>
        private static void UpdateSmoothedMovementTargetFor(global::AICharacterController ai, Vector3 targetPosition)
        {
            if (ai == null || !AdaptiveAISettings.AimTrackingSmoothEnabled) return;
            var holder = _smoothedMovementTargetByAi.GetOrCreateValue(ai);
            if (!holder.Valid) { holder.Position = targetPosition; holder.Valid = true; return; }
            float proficiency = GetAimTrackingProficiency(ai);
            float reactMin = Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMin);
            float reactMax = Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMax);
            if (AdaptiveAISettings.DodgeAndAimScaleByPlayerFamiliarity)
            {
                float fam = PlayerBehaviorCollector.GetCurrentMapFamiliarity();
                reactMin = Mathf.Lerp(reactMin, Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMinAtMaxFamiliarity), fam);
                reactMax = Mathf.Lerp(reactMax, Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMaxAtMaxFamiliarity), fam);
            }
            float reactionSpeed = Mathf.Lerp(reactMin, reactMax, proficiency);
            float lerpT = Mathf.Clamp01(reactionSpeed * (float)Time.deltaTime);
            holder.Position = Vector3.Lerp(holder.Position, targetPosition, lerpT);
        }

        /// <summary>대시 중 사용할 목표 위치. 시작~현재를 Lerp하여 대시 방향으로 조금 추적되는 느낌. forAim이면 y에 0.5 보정.</summary>
        private static Vector3 GetEffectiveDashTargetPosition(Vector3 currentPlayerPos, bool forAim)
        {
            float t = Mathf.Clamp01(AdaptiveAISettings.AimTrackingDashLerpFactor);
            Vector3 p = Vector3.Lerp(_playerDashStartPosition, currentPlayerPos, t);
            if (forAim) p.y = currentPlayerPos.y + 0.5f;
            return p;
        }

        /// <summary>이동 판단용 플레이어 위치. 스무딩 사용 시 AI별 보간 위치(에임과 동일 패턴). 대시 중에는 근접이 아니면 대시 시작~현재 보간(대시 방향으로 일부 추적), 근접은 항상 추적.</summary>
        internal static Vector3 GetSmoothedPlayerPositionForMovement(CharacterMainControl c)
        {
            var main = CharacterMainControl.Main;
            if (main == null) return Vector3.zero;
            if (c == null || c == main) return main.transform.position;
            bool playerDashing = main.dashAction != null && main.dashAction.Running;
            if (AdaptiveAISettings.AimTrackingLoseTargetOnPlayerDash && playerDashing && _playerDashStartValid)
            {
                if (c.GetMeleeWeapon() != null && !AdaptiveAISettings.MeleeUseOriginalBehavior)
                    return main.transform.position;
                return GetEffectiveDashTargetPosition(main.transform.position, forAim: false);
            }
            var ai = c.GetComponent<global::AICharacterController>() ?? c.GetComponentInParent<global::AICharacterController>();
            if (ai == null || !AdaptiveAISettings.AimTrackingSmoothEnabled)
                return main.transform.position;
            if (!_smoothedMovementTargetByAi.TryGetValue(ai, out var holder) || !holder.Valid)
                return main.transform.position;
            return holder.Position;
        }

        /// <summary>인지 후 전투 시 플레이어 위치 실시간 추적. 플레이어를 인식한 적 AI(noticed 또는 최근 플레이어에게 피격)의 에임을 매 프레임 현재 플레이어 위치로 갱신. MovementAimSyncRunner.LateUpdate에서 호출. FaceMovementDirection 설정과 무관하게 항상 실행.</summary>
        internal static void ApplyPlayerAimSyncForNoticed()
        {
            var main = CharacterMainControl.Main;
            if (main == null) return;
            float now = Time.time;

            // 플레이어 대시 시 "에임 놓침" 연출: 대시 시작 순간 위치를 저장해, 대시 중에는 그 위치를 목표로 유지.
            bool playerDashing = main.dashAction != null && main.dashAction.Running;
            if (AdaptiveAISettings.AimTrackingLoseTargetOnPlayerDash && playerDashing)
            {
                if (!_playerWasDashingLastFrame)
                {
                    _playerDashStartPosition = main.transform.position + Vector3.up * 0.5f;
                    _playerDashStartValid = true;
                }
                _playerWasDashingLastFrame = true;
            }
            else
            {
                _playerWasDashingLastFrame = false;
            }

            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                var c = ai.CharacterMainControl;
                if (c == null || c == main) return;
                if (!Team.IsEnemy(c.Team, Teams.player)) return;
                if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c)) return;
                if (c.dashAction != null && c.dashAction.Running) return;
                // 터렛: 바라보는 방향 반원 안에 플레이어가 들어오면 인지(SetMoveInput이 호출되지 않을 수 있어 LateUpdate에서도 적용). 시야 옵션 시 벽 너머 인지 방지. 1.3.5 대응: GetPlayerDamageReceiver 폴백 사용.
                if (CharacterMainControlDashPatches.IsDashBlockedForPreset(c) && !ai.noticed && AICharacterControllerPatches.GetPlayerDamageReceiver(main) != null && !AICharacterControllerPatches.IsAggroOnlyWhenHurtPreset(ai))
                {
                    var loadoutTurret = AICharacterControllerPatches.GetEnemyLoadout(ai);
                    float minRange = AdaptiveAISettings.TurretNoticeMinRangeFallback > 0f ? AdaptiveAISettings.TurretNoticeMinRangeFallback : 1f;
                    float turretRange = Mathf.Max(loadoutTurret.WeaponRange, minRange);
                    Vector3 toPlayerTurret = main.transform.position - c.transform.position;
                    toPlayerTurret.y = 0f;
                    float distSqTurret = toPlayerTurret.sqrMagnitude;
                    float maxRangeSqTurret = turretRange * turretRange;
                    bool inConeAndRange = distSqTurret <= maxRangeSqTurret && AICharacterControllerPatches.IsPlayerInTurretFrontCone(ai, main);
                    if (inConeAndRange)
                    {
                        // 터렛: 레이 원점을 눈높이(1.2m)로 올려 지면에 막혀 멀리 있는 플레이어를 못 보는 문제 완화.
                        Vector3 fromForLOS = c.transform.position + (IsTurretPreset(c) ? Vector3.up * 1.2f : Vector3.zero);
                        bool canNotice = !AdaptiveAISettings.TurretNoticeRequireLineOfSight || !IsObstacleBetween(fromForLOS, main.transform.position);
                        if (canNotice)
                            AICharacterControllerPatches.ForceNoticedState(ai, main);
                    }
                }
                // 이동용 스무딩: 어그로 플레이어인 AI는 에임과 동일한 반응속도로 "인지한 플레이어 위치" 보간. 대시 중엔 근접은 계속 추적, 원거리는 대시 시작~현재 보간.
                if (AICharacterControllerPatches.IsAggroOnPlayer(ai))
                {
                    Vector3 moveTarget = main.transform.position;
                    if (playerDashing && _playerDashStartValid && AdaptiveAISettings.AimTrackingLoseTargetOnPlayerDash && c.GetMeleeWeapon() == null)
                        moveTarget = GetEffectiveDashTargetPosition(main.transform.position, forAim: false);
                    UpdateSmoothedMovementTargetFor(ai, moveTarget);
                }
                bool noticed = ai.noticed;
                bool recentlyHurt = AICharacterControllerPatches.IsRecentlyHurtByPlayer(ai);
                bool isTurret = IsTurretPreset(c);
                // 실시간 추적: 매 AI마다 현재 프레임의 플레이어 위치 사용(캐시 없음).
                Vector3 playerPos = main.transform.position;
                float distSq = (playerPos - c.transform.position).sqrMagnitude;
                // 터렛 포함: 미인식·최근 피격·어그로 비플레이어면 에임 갱신 스킵. 거리로 searchedEnemy만 플레이어로 잡힌 경우(어그로 플레이어)도 추적 허용.
                if (!noticed && !recentlyHurt && !AICharacterControllerPatches.IsAggroOnPlayer(ai))
                {
                    if (DebugLogAimTracking && isTurret && (!_lastAimTrackingLogByCharacter.TryGetValue(c, out float t) || now - t >= AimTrackingLogInterval))
                    {
                        _lastAimTrackingLogByCharacter[c] = now;
                        Debug.Log($"[AdaptiveEnemyAI] [터렛에임] 에임추적(LateUpdate) 스킵: noticed={noticed} recentlyHurt={recentlyHurt} → 플레이어 미인식이라 플레이어위치 갱신 안 함");
                    }
                    return;
                }
                // 어그로가 플레이어가 아닐 때(다른 AI에게 피격된 경우 등) 플레이어 방향 에임·타겟 고정 적용하지 않음.
                if (!AICharacterControllerPatches.IsAggroOnPlayer(ai))
                    return;
                if (distSq > MaxAimAtPlayerDistSq)
                {
                    if (DebugLogAimTracking && isTurret && (!_lastAimTrackingLogByCharacter.TryGetValue(c, out float t) || now - t >= AimTrackingLogInterval))
                    {
                        _lastAimTrackingLogByCharacter[c] = now;
                        Debug.Log($"[AdaptiveEnemyAI] [터렛에임] 에임추적(LateUpdate) 스킵: dist={Mathf.Sqrt(distSq):F1}m > 50m → 거리초과로 플레이어위치 갱신 안 함");
                    }
                    return;
                }
                // 벽 너머 에임/타겟 갱신 방지: 시야 없으면 에임 동기화 스킵(隔墙瞄人·拉出来直接开枪 완화).
                if (AdaptiveAISettings.AimSyncRequireLineOfSight)
                {
                    bool obstacleBetween = IsObstacleBetween(c.transform.position, playerPos);
                    var losHolder = _losStateByAi.GetOrCreateValue(ai);
                    if (obstacleBetween)
                    {
                        losHolder.HadObstacleLastFrame = true;
                        return;
                    }
                    if (losHolder.HadObstacleLastFrame)
                    {
                        losHolder.TimeWhenGainedLOS = now;
                        losHolder.HadObstacleLastFrame = false;
                    }
                    float delaySec = Mathf.Max(0f, AdaptiveAISettings.AimAcquisitionDelayAfterLOSSeconds);
                    if (AdaptiveAISettings.DodgeAndAimScaleByPlayerFamiliarity)
                    {
                        float fam = PlayerBehaviorCollector.GetCurrentMapFamiliarity();
                        delaySec = Mathf.Lerp(delaySec, Mathf.Max(0f, AdaptiveAISettings.AimAcquisitionDelayAfterLOSAtMaxFamiliarity), fam);
                    }
                    if (delaySec > 0.0001f && (now - losHolder.TimeWhenGainedLOS) < delaySec)
                        return;
                }
                float lastLogTime = 0f;
                bool shooting = IsAttackRunning(c);
                if (DebugLogAimTracking && (!_lastAimTrackingLogByCharacter.TryGetValue(c, out lastLogTime) || now - lastLogTime >= AimTrackingLogInterval))
                {
                    _lastAimTrackingLogByCharacter[c] = now;
                    string aiName = GetAimLogName(c);
                    string turretTag = isTurret ? " [터렛에임]" : "";
                    Debug.Log($"[AdaptiveEnemyAI]{turretTag} 에임추적(LateUpdate) 적용: AI=\"{aiName}\" dist={Mathf.Sqrt(distSq):F1}m noticed={noticed} recentlyHurt={recentlyHurt} shooting={shooting} → SetAimPoint(플레이어)");
                }
                // 총 쏠 때(shooting=True) 전용 로그: 발사 중인 순간이 로그에 확실히 보이도록 별도 쓰로틀로 출력
                if (DebugLogAimTracking && shooting && (!_lastShootingTrueLogByCharacter.TryGetValue(c, out float tShoot) || now - tShoot >= ShootingTrueLogInterval))
                {
                    _lastShootingTrueLogByCharacter[c] = now;
                    string aiName = GetAimLogName(c);
                    Debug.Log($"[AdaptiveEnemyAI] 에임추적(발사 중): AI=\"{aiName}\" dist={Mathf.Sqrt(distSq):F1}m shooting=True → SetAimPoint(플레이어)");
                }
                // 어그로가 플레이어일 때만: 게임이 aimTarget을 근처 다른 적으로 둔 경우를 덮어써 플레이어로 고정. 1.3.5 대응: GetPlayerDamageReceiver 사용.
                var playerReceiver = AICharacterControllerPatches.GetPlayerDamageReceiver(main);
                if (AICharacterControllerPatches.IsAggroOnPlayer(ai) && playerReceiver != null && (ai.aimTarget == null || ai.aimTarget.gameObject != playerReceiver.gameObject))
                {
                    try { ai.SetTarget(playerReceiver.transform); } catch { }
                }
                // 실시간 추적: 근접은 대시 중에도 플레이어 위치. 원거리는 대시 중에 시작~현재 보간(대시 방향으로 조금 추적).
                Vector3 playerAimPoint = playerPos + Vector3.up * 0.5f;
                if (AdaptiveAISettings.AimTrackingLoseTargetOnPlayerDash && playerDashing && _playerDashStartValid && c.GetMeleeWeapon() == null)
                    playerAimPoint = GetEffectiveDashTargetPosition(playerPos, forAim: true);
                if (AdaptiveAISettings.AimTrackingSmoothEnabled)
                {
                    var holder = _smoothedAimByAi.GetOrCreateValue(ai);
                    if (!holder.Valid) { holder.Position = playerAimPoint; holder.Valid = true; }
                    float proficiency = GetAimTrackingProficiency(ai);
                    float reactMin = Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMin);
                    float reactMax = Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMax);
                    if (AdaptiveAISettings.DodgeAndAimScaleByPlayerFamiliarity)
                    {
                        float fam = PlayerBehaviorCollector.GetCurrentMapFamiliarity();
                        reactMin = Mathf.Lerp(reactMin, Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMinAtMaxFamiliarity), fam);
                        reactMax = Mathf.Lerp(reactMax, Mathf.Max(0.1f, AdaptiveAISettings.AimTrackingReactionSpeedMaxAtMaxFamiliarity), fam);
                    }
                    float reactionSpeed = Mathf.Lerp(reactMin, reactMax, proficiency);
                    float lerpT = Mathf.Clamp01(reactionSpeed * (float)Time.deltaTime);
                    holder.Position = Vector3.Lerp(holder.Position, playerAimPoint, lerpT);
                    try { c.SetAimPoint(holder.Position); } catch { }
                }
                else
                {
                    try { c.SetAimPoint(playerAimPoint); } catch { }
                }
            });
        }

        /// <summary>근접몹이 플레이어에게 다가올 때 매 프레임 SetRunInput(true) 호출. 원본 게임/BT가 SetRunInput을 호출하지 않으면 항상 걸어오므로, 접근 중일 때만 직접 달리기로 덮어씀.</summary>
        internal static void ApplyMeleeRunWhenApproaching()
        {
            var main = CharacterMainControl.Main;
            if (main == null) return;
            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                var c = ai.CharacterMainControl;
                if (c == null || c == main) return;
                if (!Team.IsEnemy(c.Team, Teams.player)) return;
                if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c)) return;
                var melee = c.GetMeleeWeapon();
                if (melee == null) return;
                if (PerAIPatchControl.IsExcludedFromAdaptivePatches(c)) return;
                if (AdaptiveAISettings.MeleeUseOriginalBehavior) return; // 근접 원작 동작: 모드의 달리기 강제 미적용
                if (!ai.noticed || !AICharacterControllerPatches.IsAggroOnPlayer(ai)) return;
                if (c.attackAction != null && c.attackAction.Running) return; // 휘두르는 중에는 달리기 강제 해제
                Vector3 toPlayer = GetSmoothedPlayerPositionForMovement(c) - c.transform.position;
                toPlayer.y = 0f;
                float dist = toPlayer.magnitude;
                float runUntilDist = melee.AttackRange * Mathf.Clamp01(AdaptiveAISettings.MeleeAttackStartRangeRatio);
                if (dist > runUntilDist)
                    try { c.SetRunInput(true); } catch { }
            });
        }

        /// <summary>인지·어그로 플레이어인데 경로 추종 중이 아닐 때(패트롤 경로를 끊은 직후 등) 접근 이동 주입. pathControl.Update가 path==null이면 SetMoveInput을 호출하지 않아서 Postfix가 안 불리므로 LateUpdate에서 호출. AI와 플레이어 사이에 장애물이 있으면 직선 접근 대신 PathControl.MoveToPos(플레이어 위치)로 경로를 요청해 우회 이동하도록 함.</summary>
        internal static void ApplyApproachWhenNoticedAndNoPath()
        {
            var main = CharacterMainControl.Main;
            if (main == null) return;
            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                var c = ai.CharacterMainControl;
                if (c == null || c == main) return;
                if (!Team.IsEnemy(c.Team, Teams.player)) return;
                if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c)) return;
                if (CharacterMainControlDashPatches.IsDashBlockedForPreset(c)) return; // 터렛: 접근 이동 주입 안 함
                if (c.dashAction != null && c.dashAction.Running) return;
                if (!ai.noticed || !AICharacterControllerPatches.IsAggroOnPlayer(ai)) return;
                // 경계선 도달 후 플레이어 뒤 웨이포인트 대기/이동 중: 접근 이동 주입 금지. 대기 중이면 멈춤 유지, 경로 추종 중이면 경로 방향 유지(진동·덮어쓰기 방지).
                if (_boundaryWaypointPlayerForwardByCharacter.ContainsKey(c))
                {
                    if (!AI_PathControlPatches.IsCharacterPathFollowing(c))
                        try { c.SetMoveInput(Vector3.zero); } catch { }
                    return;
                }
                Vector3 aiPos = c.transform.position;
                // 근접도 플레이어 추적(이동+공격)을 위해 접근 이동 주입 적용. MeleeUseOriginalBehavior여도 BT만으로는 제자리 공격만 하므로 주입 필요.
                // 경로 추종 중: 근접은 목표 주기 갱신, 엄폐물 있으면 우회 경로 재요청(궤도/접근 불가 시 좁은 길목 등).
                // IsObstacleBetween 호출을 캐릭터별 주기로 제한해 프레임 드랍 완화(플레이어 추적·경로 갱신은 주기 내에서만 판정).
                if (AI_PathControlPatches.IsCharacterPathFollowing(c))
                {
                    float now = Time.time;
                    if (_lastPathFollowCheckTime.TryGetValue(c, out float lastPathCheck) && (now - lastPathCheck) < PathFollowCheckInterval)
                        return;
                    _lastPathFollowCheckTime[c] = now;

                    Vector3 playerPosForObstacle = GetSmoothedPlayerPositionForMovement(c);
                    bool obstacleBetweenPath = IsObstacleBetween(aiPos, playerPosForObstacle);
                    if (c.GetMeleeWeapon() != null)
                    {
                        if (!_lastMeleePathRefreshTime.TryGetValue(c, out float lastRefresh) || (now - lastRefresh) >= MeleePathRefreshIntervalSeconds)
                        {
                            _lastMeleePathRefreshTime[c] = now;
                            var enemyMelee = AICharacterControllerPatches.GetEnemyLoadout(ai);
                            Vector3 meleePlayerPos = GetSmoothedPlayerPositionForMovement(c);
                            float meleeMinDist = GetDesiredMinDistanceFromPlayer(c, enemyMelee.IsMelee);
                            Vector3 meleeCenter = GetMovementTargetCenterPosition(main, meleePlayerPos, AdaptiveAISettings.ApproachTargetOffsetBehindPlayerMeters);
                            Vector3 meleePathTarget = GetApproachTargetPosition(aiPos, meleeCenter, meleeMinDist);
                            if (AI_PathControlPatches.RequestPathToPosition(c, meleePathTarget))
                            {
                                _lastPathRequestToPlayerTime[c] = now;
                                _boundaryWaypointPlayerForwardByCharacter.Remove(c);
                            }
                        }
                    }
                    else if (obstacleBetweenPath)
                    {
                        // 궤도/접근 시 엄폐물로 정해진 경로로 못 갈 때: 엄폐물을 피해 접근 목표(최소 거리 원)로 가는 경로 재요청. 주기 제한으로 스팸 방지.
                        if (!_lastPathRequestToPlayerTime.TryGetValue(c, out float lastReq) || (now - lastReq) >= PathRefreshAfterFollowingSeconds)
                        {
                            if (!AI_PathControlPatches.IsCharacterWaitingForPathResult(c) && (!_lastPathFailTime.TryGetValue(c, out float ft) || (now - ft) >= PathFailCooldownSeconds))
                            {
                                var enemyPath = AICharacterControllerPatches.GetEnemyLoadout(ai);
                                Vector3 livePosForPath = main.transform.position;
                                float desiredMinDistPath = GetDesiredMinDistanceFromPlayer(c, enemyPath.IsMelee);
                                Vector3 pathCenter = GetMovementTargetCenterPosition(main, livePosForPath, AdaptiveAISettings.ApproachTargetOffsetBehindPlayerMeters);
                                Vector3 pathTarget = GetApproachTargetPosition(aiPos, pathCenter, desiredMinDistPath);
                                if (AI_PathControlPatches.RequestPathToPosition(c, pathTarget))
                                {
                                    _lastPathRequestToPlayerTime[c] = now;
                                    _lastPathFailTime.Remove(c);
                                    _boundaryWaypointPlayerForwardByCharacter.Remove(c);
                                }
                            }
                        }
                    }
                    return;
                }
                // 원거리만: 공격 중에는 접근 이동 주입 안 함(이동 중 공격 트리거 방지). 근접은 공격 중에도 플레이어 방향 이동 주입 → 추적하며 공격.
                if (IsAttackRunning(c) && c.GetMeleeWeapon() == null) return;

                Vector3 playerPos = GetSmoothedPlayerPositionForMovement(c);
                bool obstacleBetween = IsObstacleBetween(aiPos, playerPos);
                if (obstacleBetween)
                {
                    if (AI_PathControlPatches.IsCharacterWaitingForPathResult(c))
                    {
                        try { c.SetMoveInput(Vector3.zero); } catch { }
                        return;
                    }
                    // 제자리 걸음으로 경로 끊은 직후에는 재요청 대신 폴백만 적용
                    if (_lastStuckBreakTime.TryGetValue(c, out float stuckBreakTime) && (Time.time - stuckBreakTime) < StuckBreakFallbackSeconds)
                    {
                        ApplyObstacleFallbackMove(c, aiPos, playerPos);
                        return;
                    }
                    // 경로 계산이 방금 실패했으면 쿨다운 동안 재요청하지 않고 폴백 이동으로 끼임 방지
                    if (AI_PathControlPatches.GetAndClearLastPathFailedForCharacter(c))
                        _lastPathFailTime[c] = Time.time;
                    if (_lastPathFailTime.TryGetValue(c, out float failTime) && (Time.time - failTime) < PathFailCooldownSeconds)
                    {
                        ApplyObstacleFallbackMove(c, aiPos, playerPos);
                        return;
                    }
                    var enemyApproachPath = AICharacterControllerPatches.GetEnemyLoadout(ai);
                    Vector3 livePosForPath = main.transform.position;
                    float desiredMinDistPath = GetDesiredMinDistanceFromPlayer(c, enemyApproachPath.IsMelee);
                    Vector3 pathCenter = GetMovementTargetCenterPosition(main, livePosForPath, AdaptiveAISettings.ApproachTargetOffsetBehindPlayerMeters);
                    Vector3 pathTarget = GetApproachTargetPosition(aiPos, pathCenter, desiredMinDistPath);
                    if (AI_PathControlPatches.RequestPathToPosition(c, pathTarget))
                    {
                        _lastPathRequestToPlayerTime[c] = Time.time;
                        _lastPathFailTime.Remove(c);
                        _boundaryWaypointPlayerForwardByCharacter.Remove(c);
                        return;
                    }
                    // 요청 자체가 실패한 경우(컴포넌트 없음 등) 폴백 이동
                    ApplyObstacleFallbackMove(c, aiPos, playerPos);
                    return;
                }

                // 겹침 근본 방지: 접근 목표를 "플레이어 위치"가 아닌 "최소 유지 거리 위의 한 점"으로 통일. 옵션으로 목표 중심을 플레이어 등 뒤 Nm로 둠.
                var enemyApproach = AICharacterControllerPatches.GetEnemyLoadout(ai);
                Vector3 livePlayerPosApproach = main.transform.position;
                float desiredMinDistApproach = GetDesiredMinDistanceFromPlayer(c, enemyApproach.IsMelee);
                Vector3 approachCenter = GetMovementTargetCenterPosition(main, livePlayerPosApproach, AdaptiveAISettings.ApproachTargetOffsetBehindPlayerMeters);
                Vector3 approachTargetPos = GetApproachTargetPosition(aiPos, approachCenter, desiredMinDistApproach);
                Vector3 toTarget = approachTargetPos - aiPos;
                toTarget.y = 0f;
                Vector3 approachDir;
                if (toTarget.sqrMagnitude < 0.01f)
                {
                    // 이미 목표점에 도달: 궤도 또는 제자리
                    if (AdaptiveAISettings.OrbitAroundPlayerEnabled && GetOrbitDirection(livePlayerPosApproach, aiPos, main, out Vector3 orbitDir, c))
                    {
                        float mag = Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude, 0.2f, 1f);
                        approachDir = orbitDir * mag;
                    }
                    else
                        approachDir = Vector3.zero;
                }
                else
                {
                    Vector3 toTargetNorm = (toTarget / toTarget.magnitude);
                    Vector3 dirFromStyle = GetApproachDirectionFromStyle(c, toTargetNorm, aiPos, livePlayerPosApproach, main);
                    approachDir = dirFromStyle * 0.85f;
                }
                try { c.SetMoveInput(approachDir); } catch { }
            });
        }

        /// <summary>플레이어 근처인데 현재 이동이 거의 0인 적에게 매 프레임 궤도 이동 강제. SetMoveInput이 호출되지 않는 구간(경로 도착·사격 등)에서도 멈춤 방지.</summary>
        internal static void ApplyOrbitWhenCloseAndStandingStill()
        {
            if (!AdaptiveAISettings.OrbitAroundPlayerEnabled || AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude <= 0f) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            float maxDist = Mathf.Max(AdaptiveAISettings.OrbitDistMin, AdaptiveAISettings.OrbitDistMax);
            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                var c = ai.CharacterMainControl;
                if (c == null || c == main) return;
                if (!Team.IsEnemy(c.Team, Teams.player)) return;
                if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c)) return;
                if (c.dashAction != null && c.dashAction.Running) return;
                if (!AICharacterControllerPatches.IsAggroOnPlayer(ai)) return;
                // 경계선 플레이어 뒤 웨이포인트 대기/이동 중이면 궤도 주입 안 함(멈춤 또는 경로 방향 유지)
                if (_boundaryWaypointPlayerForwardByCharacter.ContainsKey(c)) return;
                Vector3 aiPos = c.transform.position;
                Vector3 playerPos = main.transform.position;
                Vector3 toPlayer = playerPos - aiPos;
                toPlayer.y = 0f;
                float dist = toPlayer.magnitude;
                if (dist >= maxDist || dist < 0.001f) return;
                // 뒤쪽 도달 상태면 궤도 이동 주입 안 함(제자리 유지). 뒤가 아니게 되면 다시 움직임
                if (IsAIBehindPlayer(aiPos, playerPos, main) && dist <= StopWhenReachedBackDistMax) return;
                Vector3 currentMove = c.MoveInput;
                currentMove.y = 0f;
                if (currentMove.sqrMagnitude >= 0.02f) return;
                float closeDist = Mathf.Max(0.5f, AdaptiveAISettings.MoveToPlayerBackWhenCloseDist);
                Vector3 dir;
                if (AdaptiveAISettings.MoveToPlayerBackWhenCloseEnabled && dist <= closeDist && GetDirectionTowardPlayerBack(playerPos, aiPos, main, out Vector3 dirToBack))
                    dir = dirToBack;
                else if (GetOrbitDirection(playerPos, aiPos, main, out Vector3 orbitDir, c))
                    dir = orbitDir;
                else
                    return;
                float mag = Mathf.Clamp(AdaptiveAISettings.OrbitMinMoveAfterCapMagnitude, 0.2f, 1f);
                try { c.SetMoveInput(new Vector3(dir.x * mag, 0f, dir.z * mag)); } catch { }
            });
        }

        /// <summary>저장된 이동이 있는 모든 적 AI에 대해 조준을 이동 방향으로 덮어씀. MovementAimSyncRunner.LateUpdate에서 호출. 제안3 프록시 정리(파괴된 캐릭터). 이슈2: 사격 중·플레이어 조준 중에는 덮어쓰지 않음.</summary>
        internal static void ApplyMovementAimSyncToAllStored()
        {
            CleanupDestroyedAimProxies();
            if (_lastMoveInputByCharacter.Count == 0) return;
            _cachedKeysForAimSync.Clear();
            foreach (var k in _lastMoveInputByCharacter.Keys)
                _cachedKeysForAimSync.Add(k);
            var main = CharacterMainControl.Main;
            foreach (var c in _cachedKeysForAimSync)
            {
                if (c == null) continue;
                if (c == CharacterMainControl.Main) continue;
                if (!Team.IsEnemy(c.Team, Teams.player)) continue;
                if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(c)) continue;
                if (c.dashAction != null && c.dashAction.Running) continue;
                if (AdaptiveAISettings.SkipAimOverwriteWhenShooting && IsAttackRunning(c)) continue;
                var ai = c.GetComponent<global::AICharacterController>() ?? c.GetComponentInParent<global::AICharacterController>();
                if (ai != null && AICharacterControllerPatches.IsAggroOnPlayer(ai)) continue;
                if (!TryGetLastMoveDirectionFlat(c, out Vector3 moveDir)) continue;
                try { c.SetAimPoint(c.transform.position + moveDir * 50f); } catch { }
                // 무빙 시 바라보는 방향도 이동 방향으로: LateUpdate에서 회전까지 적용해 같은 프레임 다른 로직에 덮어쓰이지 않게 함.
                if (c.movementControl != null)
                { try { c.movementControl.ForceTurnTo(moveDir); } catch { } }
            }
        }

        /// <summary>파괴된 캐릭터에 대한 프록시 제거 및 GameObject 파괴.</summary>
        private static readonly List<CharacterMainControl> _proxyCleanupBuffer = new List<CharacterMainControl>();
        private static void CleanupDestroyedAimProxies()
        {
            _proxyCleanupBuffer.Clear();
            foreach (var kv in _aimProxyByCharacter)
            {
                if (kv.Key == null || kv.Value == null) _proxyCleanupBuffer.Add(kv.Key);
            }
            foreach (var c in _proxyCleanupBuffer)
            {
                if (_aimProxyByCharacter.TryGetValue(c, out Transform t) && t != null && t.gameObject != null)
                    UnityEngine.Object.Destroy(t.gameObject);
                _aimProxyByCharacter.Remove(c);
                _lastMoveInputByCharacter.Remove(c);
                _lastBoundaryDirByCharacter.Remove(c);
                _directionChangeThisFrame.Remove(c);
                _lastPathRequestToPlayerTime.Remove(c);
                _boundaryWaypointPlayerForwardByCharacter.Remove(c);
                _boundaryLockUntilByCharacter.Remove(c);
                _lastMeleePathRefreshTime.Remove(c);
                _lastPathFailTime.Remove(c);
                _positionWhenStuckCheck.Remove(c);
                _timeWhenStuckCheck.Remove(c);
                _lastStuckBreakTime.Remove(c);
            }
        }

        private static FieldInfo _inputAimPointField;

        /// <summary>조준점이 플레이어 위치 근처(이 반경 이내)면 "플레이어 조준"으로 간주해 이동 방향으로 덮어쓰지 않음.</summary>
        private const float AimPointNearPlayerRadiusSq = 100f; // 10m 반경
        /// <summary>플레이어 조준 허용 최대 거리 제곱(m²). 이 거리 밖이면 플레이어 위치로 에임 덮어쓰지 않아 허공 사격 방지.</summary>
        private const float MaxAimAtPlayerDistSq = 2500f; // 50m

        /// <summary>aimTarget이 우리가 만든 이동 방향 프록시인지 여부.</summary>
        private static bool IsAimTargetOurProxy(CharacterMainControl c, Transform aimTarget)
        {
            if (c == null || aimTarget == null) return false;
            return _aimProxyByCharacter.TryGetValue(c, out Transform proxy) && proxy == aimTarget;
        }

        /// <summary>aimTarget이 플레이어(본체·손상판정·자식)이면 true. 재인지 시 게임이 준 과거 좌표 대신 항상 현재 플레이어 위치 쓰기 위함.</summary>
        private static bool IsAimTargetPlayerOrPartOf(Transform aimTarget, CharacterMainControl main)
        {
            if (aimTarget == null || main == null) return false;
            if (aimTarget == main.transform) return true;
            var pr = AICharacterControllerPatches.GetPlayerDamageReceiver(main);
            if (pr != null && aimTarget == pr.transform) return true;
            try { return aimTarget.IsChildOf(main.transform); } catch { return false; }
        }

        /// <summary>SetAimPoint 호출 시: 우리가 이동을 제어한 적 AI면 원본 대신 이동 방향으로 조준 설정(제안4). 재귀 방지를 위해 필드 직접 설정 후 원본 스킵. 이슈2: 사격 중·플레이어 조준 중에는 덮어쓰지 않음. 조준점이 플레이어 근처면 덮어쓰지 않음(이상한 방향 사격 방지).
        /// 타겟 위치 고정 버그 방지: 플레이어 조준 시 호출자가 넘긴 _aimPoint가 과거 좌표일 수 있으므로, 항상 현재 타겟(플레이어) 위치로 갱신해 에임이 타겟 이동을 따라가도록 함.
        /// 플레이어를 인식했을 때 aimTarget이 이동 방향 프록시로 남아 있으면 프록시를 무시하고 플레이어 위치로 조준(허공 사격 방지).</summary>
        public static bool SetAimPoint_Prefix(CharacterMainControl __instance, Vector3 _aimPoint)
        {
            if (__instance == null || __instance == CharacterMainControl.Main) return true;
            if (PlayerBehaviorCollector.IsInBase()) return true;
            if (!Team.IsEnemy(__instance.Team, Teams.player)) return true;
            if (CharacterMainControlDashPatches.IsAdaptiveAIExcludedForPreset(__instance) && !CharacterMainControlDashPatches.IsDashBlockedForPreset(__instance)) return true;
            var ai = __instance.GetComponent<global::AICharacterController>() ?? __instance.GetComponentInParent<global::AICharacterController>();
            var main = CharacterMainControl.Main;
            if (CharacterMainControlDashPatches.IsDashBlockedForPreset(__instance))
            {
                Vector3 targetPos = (ai != null && ai.aimTarget != null) ? (ai.aimTarget.position + Vector3.up * 0.5f) : (main != null ? main.transform.position + Vector3.up * 1f : _aimPoint);
                if (_inputAimPointField == null) _inputAimPointField = AccessTools.Field(typeof(CharacterMainControl), "inputAimPoint");
                try { _inputAimPointField?.SetValue(__instance, targetPos); } catch { }
                Vector3 toTarget = targetPos - __instance.transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.01f)
                {
                    toTarget.Normalize();
                    Transform rotTarget = __instance.modelRoot != null ? __instance.modelRoot : __instance.transform;
                    rotTarget.rotation = Quaternion.LookRotation(toTarget);
                }
                return false;
            }
            if (__instance.dashAction != null && __instance.dashAction.Running)
            {
                LogTurretSetAimPointReturnTrueIf(__instance, "대시중", _aimPoint);
                return true;
            }
            if (_inputAimPointField == null) _inputAimPointField = AccessTools.Field(typeof(CharacterMainControl), "inputAimPoint");
            // 타겟 위치 고정 버그 방지: aimTarget이 있으면 호출자가 넘긴 _aimPoint 대신 현재 aimTarget 위치로 설정.
            // 단, 어그로가 플레이어일 때 aimTarget이 우리 이동 방향 프록시면 사용하지 않음 → 아래 플레이어 조준 블록으로 진행(허공 사격 방지).
            bool aggroOnPlayer = ai != null && AICharacterControllerPatches.IsAggroOnPlayer(ai);
            bool aimTargetIsOurProxy = ai != null && ai.aimTarget != null && IsAimTargetOurProxy(__instance, ai.aimTarget);
            if (ai != null && ai.aimTarget != null && _inputAimPointField != null && !(aggroOnPlayer && aimTargetIsOurProxy))
            {
                try
                {
                    // 재인지 시 그 전 위치로 에임 고정 방지: 타겟이 플레이어(본체/손상판정/자식)이거나, 어그로가 플레이어면 항상 현재 플레이어 위치 사용(처음 발견 위치 고정 버그 방지).
                    bool useLivePlayerPos = main != null && (
                        IsAimTargetPlayerOrPartOf(ai.aimTarget, main) ||
                        aggroOnPlayer);
                    Vector3 currentTargetPos = useLivePlayerPos
                        ? (main.transform.position + Vector3.up * 0.5f)
                        : (ai.aimTarget.transform.position + Vector3.up * 0.5f);
                    _inputAimPointField.SetValue(__instance, currentTargetPos);
                    if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
                    {
                        string turretTag = IsTurretPreset(__instance) ? " [터렛에임]" : "";
                        Debug.Log($"[AdaptiveEnemyAI]{turretTag} SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" → aimTarget위치(프록시아님) noticed={ai?.noticed} recentlyHurt={ai != null && AICharacterControllerPatches.IsRecentlyHurtByPlayer(ai)}");
                        if (IsTurretPreset(__instance))
                            Debug.Log($"[AdaptiveEnemyAI] [터렛에임] aimTarget위치 사용 → 설정한 좌표=({currentTargetPos.x:F1},{currentTargetPos.y:F1},{currentTargetPos.z:F1}), 호출자_aimPoint=({_aimPoint.x:F1},{_aimPoint.y:F1},{_aimPoint.z:F1})");
                    }
                    return false;
                }
                catch { }
            }
            // 어그로가 플레이어일 때만: aimTarget 없이 플레이어 근처를 조준하는 경우(예: 다른 코드 경로) 현재 플레이어 위치로 갱신해 고정 에임 방지. 시야 없으면 덮어쓰기 안 함.
            if (aggroOnPlayer && main != null && (main.transform.position - _aimPoint).sqrMagnitude <= AimPointNearPlayerRadiusSq)
            {
                if (AdaptiveAISettings.AimSyncRequireLineOfSight && IsObstacleBetween(__instance.transform.position, main.transform.position))
                {
                    if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
                        Debug.Log($"[AdaptiveEnemyAI] SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" → 벽 너머라 플레이어근처조준 갱신 스킵");
                    return true;
                }
                if (_inputAimPointField != null)
                {
                    try
                    {
                        _inputAimPointField.SetValue(__instance, main.transform.position + Vector3.up * 0.5f);
                        if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
                        {
                            string turretTag = IsTurretPreset(__instance) ? " [터렛에임]" : "";
                            Debug.Log($"[AdaptiveEnemyAI]{turretTag} SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" → 플레이어근처조준 갱신(현재플레이어위치)");
                        }
                        return false;
                    }
                    catch { }
                }
                LogTurretSetAimPointReturnTrueIf(__instance, "inputAimPoint필드 null", _aimPoint);
                return true;
            }
            // 어그로가 플레이어일 때만: 플레이어 방향으로 에임 고정. 거리/유효성 검사로 허공 사격 방지. 시야 옵션 시 벽 너머 에임 덮어쓰기 안 함.
            if (ai != null && main != null && aggroOnPlayer && _inputAimPointField != null)
            {
                float distSq = (main.transform.position - __instance.transform.position).sqrMagnitude;
                if (distSq <= MaxAimAtPlayerDistSq)
                {
                    if (AdaptiveAISettings.AimSyncRequireLineOfSight && IsObstacleBetween(__instance.transform.position, main.transform.position))
                    {
                        if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
                            Debug.Log($"[AdaptiveEnemyAI] SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" → 벽 너머라 플레이어 에임 덮어쓰기 안 함(원본 실행)");
                        return true;
                    }
                    try
                    {
                        _inputAimPointField.SetValue(__instance, main.transform.position + Vector3.up * 0.5f);
                        if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
                        {
                            string turretTag = IsTurretPreset(__instance) ? " [터렛에임]" : "";
                            Debug.Log($"[AdaptiveEnemyAI]{turretTag} SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" → 플레이어인식(noticed/recentlyHurt) 플레이어방향 에임 dist={Mathf.Sqrt(distSq):F1}m shooting={IsAttackRunning(__instance)}");
                        }
                        return false;
                    }
                    catch { }
                }
                else if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
                    Debug.Log($"[AdaptiveEnemyAI] SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" 플레이어인식이지만 거리초과 → 에임덮어쓰기안함 dist={Mathf.Sqrt(distSq):F1}m");
            }
            // 인지 후 전투 실시간 추적은 위에서 적용됨. 아래는 "이동 방향으로 조준 덮어쓰기"만 해당 → 설정 꺼짐이면 원본 실행.
            if (!AdaptiveAISettings.FaceMovementDirectionWhenApproaching)
            {
                LogTurretSetAimPointReturnTrueIf(__instance, "FaceMovementDirection 끔", _aimPoint);
                return true;
            }
            if (AdaptiveAISettings.SkipAimOverwriteWhenShooting && IsAttackRunning(__instance))
            {
                LogTurretSetAimPointReturnTrueIf(__instance, "사격중 원본실행", _aimPoint);
                return true;
            }
            if (!TryGetLastMoveDirectionFlat(__instance, out Vector3 moveDir))
            {
                LogTurretSetAimPointReturnTrueIf(__instance, "이동방향 없음 원본실행", _aimPoint);
                return true;
            }
            if (_inputAimPointField != null)
            {
                try
                {
                    _inputAimPointField.SetValue(__instance, __instance.transform.position + moveDir * 50f);
                    if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
                        Debug.Log($"[AdaptiveEnemyAI] SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" → 이동방향 에임 moveDir=({moveDir.x:F2},{moveDir.z:F2})");
                    return false;
                }
                catch { }
            }
            if (DebugLogAimTracking && ShouldLogAimTracking(__instance))
            {
                if (IsTurretPreset(__instance))
                    Debug.Log($"[AdaptiveEnemyAI] [터렛에임] SetAimPoint_Prefix → 원본실행(덮어쓰기없음), 호출자_aimPoint=({_aimPoint.x:F1},{_aimPoint.y:F1},{_aimPoint.z:F1})");
                else
                    Debug.Log($"[AdaptiveEnemyAI] SetAimPoint_Prefix: AI=\"{GetAimLogName(__instance)}\" → 원본실행(덮어쓰기없음)");
            }
            return true;
        }

        private static bool ShouldLogAimTracking(CharacterMainControl c)
        {
            if (c == null) return false;
            float now = Time.time;
            if (!_lastAimTrackingLogByCharacter.TryGetValue(c, out float last) || now - last >= AimTrackingLogInterval)
            {
                _lastAimTrackingLogByCharacter[c] = now;
                return true;
            }
            return false;
        }

        /// <summary>터렛이 SetAimPoint 원본실행(또는 특정 이유)으로 넘어갈 때 호출자 _aimPoint 로그. 같은 곳 계속 조준하는 원인 확인용.</summary>
        private static void LogTurretSetAimPointReturnTrueIf(CharacterMainControl c, string reason, Vector3 callerAimPoint)
        {
            if (c == null || !IsTurretPreset(c) || !DebugLogAimTracking || !ShouldLogAimTracking(c)) return;
            Debug.Log($"[AdaptiveEnemyAI] [터렛에임] SetAimPoint_Prefix → {reason}, 호출자_aimPoint=({callerAimPoint.x:F1},{callerAimPoint.y:F1},{callerAimPoint.z:F1})");
        }

        /// <summary>행동 공격성 0~1에 비례한 패턴 보정 강도 계수. 무빙 패턴용.</summary>
        private static float GetAggressionScaleFor(CharacterMainControl c)
        {
            var ai = c.GetComponent<global::AICharacterController>() ?? c.GetComponentInParent<global::AICharacterController>();
            if (ai == null) return 0f;
            float agg = AICharacterControllerPatches.GetBehaviorAggressionFor(ai);
            float minAgg = AdaptiveAISettings.PatternReadMinAggression;
            float maxAgg = AdaptiveAISettings.PatternReadMaxAggression;
            if (maxAgg <= minAgg) maxAgg = minAgg + 0.01f;
            return Mathf.Clamp01((agg - minAgg) / (maxAgg - minAgg));
        }

        /// <summary>캐릭터별 phase 오프셋이 적용된 시간. 무빙 패턴(스트라핑·회피·카운터) phase 계산에 사용해 여러 AI가 동시에 같은 방향으로 움직이는 느낌을 줄임.</summary>
        private static float GetMovementPhaseTime(CharacterMainControl c)
        {
            if (c == null) return Time.time;
            return Time.time + (c.GetInstanceID() % 1000) * 0.0013f;
        }

        /// <summary>스트라핑 성향 6단계: 0=가장 보수적, 3=보통, 5=가장 적극적. 수식어처럼 객체별로 고정된 성격 부여.</summary>
        private const int StrafingTendencyLevels = 6;

        /// <summary>해당 캐릭터의 스트라핑 성향(0~5). 객체별로 결정적 부여(InstanceID 기반)해 같은 개체는 항상 같은 성향.</summary>
        private static int GetStrafingTendencyFor(CharacterMainControl c)
        {
            if (c == null) return StrafingTendencyLevels / 2;
            int id = c.GetInstanceID();
            return ((id % StrafingTendencyLevels) + StrafingTendencyLevels) % StrafingTendencyLevels;
        }

        /// <summary>스트라핑 성향(0~5)을 배율로 변환. 0→Min, 5→Max, 그 사이 선형 보간.</summary>
        private static float GetStrafingTendencyMultiplier(int tendency)
        {
            float minM = Mathf.Clamp(AdaptiveAISettings.MovementPatternPersonalityMultMin, 0.2f, 1f);
            float maxM = Mathf.Clamp(AdaptiveAISettings.MovementPatternPersonalityMultMax, 1f, 2.5f);
            if (tendency <= 0) return minM;
            if (tendency >= StrafingTendencyLevels - 1) return maxM;
            float t = tendency / (float)(StrafingTendencyLevels - 1);
            return Mathf.Lerp(minM, maxM, t);
        }

        /// <summary>경로 추종 중일 때 전술(접근/후퇴) 블렌드 강도에 곱할 배율. 경로를 더 신뢰하게 함.</summary>
        private static float GetTacticalBlendScaleForPathFollowing(CharacterMainControl c)
        {
            if (c == null) return 1f;
            if (!AI_PathControlPatches.IsCharacterPathFollowing(c)) return 1f;
            return Mathf.Clamp01(AdaptiveAISettings.TacticalBlendWhenPathFollowingMultiplier);
        }

        /// <summary>플레이어 총기 타입에 따른 무빙 패턴 주기·블렌드 배율. 연사형(SMG/AR/MAG)이면 주기 단축·블렌드 강화, 단발/저격(SNP/BR)이면 주기 연장. 옵션 꺼져 있으면 1,1 반환.</summary>
        private static void GetPlayerWeaponMovementMultipliers(out float cycleMult, out float blendMult)
        {
            cycleMult = 1f;
            blendMult = 1f;
            if (!AdaptiveAISettings.MovementPatternScaleByPlayerWeapon || !PlayerLoadoutService.IsValid) return;
            string? tag = PlayerLoadoutService.Current?.GunTypeTag;
            if (string.IsNullOrEmpty(tag)) return;
            switch (tag)
            {
                case "Tag_GunType_SMG":
                case "Tag_GunType_MAG":
                case "Tag_GunType_AR":
                    cycleMult = 0.85f;
                    blendMult = 1.1f;
                    break;
                case "Tag_GunType_SNP":
                case "Tag_GunType_BR":
                    cycleMult = 1.15f;
                    blendMult = 0.95f;
                    break;
                case "Tag_GunType_SHT":
                    cycleMult = 0.9f;
                    blendMult = 1.05f;
                    break;
                case "Tag_GunType_PST":
                case "Tag_GunType_PWS":
                case "Tag_GunType_ARR":
                case "Tag_GunType_Rocket":
                case "Melee":
                default:
                    break;
            }
        }

        private static void CharacterRunAcc_Postfix(CharacterMainControl __instance, ref float __result)
        {
            if (__instance == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (AI_PathControlPatches.IsCharacterPathFollowing(__instance))
            {
                float pathMult = Mathf.Clamp(AdaptiveAISettings.PathFollowingRunAccMultiplier, 0.01f, 5f);
                if (pathMult != 1f) __result *= pathMult;
            }
            bool patternActive = _charactersWithPatternActiveThisFrame.Contains(__instance);
            bool lateralMove = HasSignificantLateralMovement(__instance);
            if (!patternActive && !lateralMove) return;
            float scale = GetAggressionScaleFor(__instance);
            if (scale < 0.01f) return;
            float mult = Mathf.Lerp(1f, AdaptiveAISettings.MovementPatternRunAccMultiplier, scale);
            if (AdaptiveAISettings.MovementPatternAccelOnDirectionChange && _directionChangeThisFrame.Contains(__instance))
                mult *= Mathf.Clamp(AdaptiveAISettings.MovementPatternAccelOnDirectionChangeMult, 1f, 1.2f);
            if (mult != 1f && mult > 0f) __result *= mult;
        }

        private static void CharacterWalkAcc_Postfix(CharacterMainControl __instance, ref float __result)
        {
            if (__instance == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (AI_PathControlPatches.IsCharacterPathFollowing(__instance))
            {
                float pathMult = Mathf.Clamp(AdaptiveAISettings.PathFollowingWalkAccMultiplier, 0.01f, 5f);
                if (pathMult != 1f) __result *= pathMult;
            }
            bool patternActive = _charactersWithPatternActiveThisFrame.Contains(__instance);
            bool lateralMove = HasSignificantLateralMovement(__instance);
            if (!patternActive && !lateralMove) return;
            float scale = GetAggressionScaleFor(__instance);
            if (scale < 0.01f) return;
            float mult = Mathf.Lerp(1f, AdaptiveAISettings.MovementPatternWalkAccMultiplier, scale);
            if (AdaptiveAISettings.MovementPatternAccelOnDirectionChange && _directionChangeThisFrame.Contains(__instance))
                mult *= Mathf.Clamp(AdaptiveAISettings.MovementPatternAccelOnDirectionChangeMult, 1f, 1.2f);
            if (mult != 1f && mult > 0f) __result *= mult;
        }

        /// <summary>투사체 위협이 있고 대시 중이 아닐 때만, 발사궤적 예측(수직 방향) 기반 지그재그/다이아몬드 무빙을 블렌드. 플레이어 패턴(LateralMoveRatioEma)에 따라 강도 조절.</summary>
        private static void ApplySoftEvasionIfThreat(CharacterMainControl c, ref Vector3 moveInput, Vector3 toPlayerFlat, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.SoftEvasionOnThreatEnabled) return;
            if (CharacterMainControlDashPatches.DashIsIncomingDodge) return;
            if (!AICharacterControllerPatches.TryGetIncomingProjectileThreat(ai, out Vector3 projDir, out Vector3 _)) return;

            Vector3 projFlat = new Vector3(projDir.x, 0f, projDir.z);
            if (projFlat.sqrMagnitude < 0.01f) return;
            projFlat.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, projFlat);
            if (right.sqrMagnitude < 0.01f) return;
            right.Normalize();

            GetPlayerWeaponMovementMultipliers(out float weaponCycleMult, out float weaponBlendMult);
            float baseZigzagCycle = Mathf.Max(0.2f, AdaptiveAISettings.SoftEvasionZigzagCycle);
            float baseDiamondCycle = AdaptiveAISettings.SoftEvasionDiamondCycle;
            float evasionCycleZigzag = baseZigzagCycle * weaponCycleMult;
            float evasionCycleDiamond = baseDiamondCycle > 0.01f ? baseDiamondCycle * weaponCycleMult : 0f;

            float phaseTime = GetMovementPhaseTime(c);
            Vector3 evasionDir;
            if (AdaptiveAISettings.SoftEvasionUseDiamondStep && evasionCycleDiamond > 0.01f)
            {
                int phase = (int)(phaseTime / evasionCycleDiamond) % 4;
                switch (phase)
                {
                    case 0: evasionDir = (toPlayerFlat + right).normalized; break;
                    case 1: evasionDir = (toPlayerFlat - right).normalized; break;
                    case 2: evasionDir = (-toPlayerFlat + right).normalized; break;
                    default: evasionDir = (-toPlayerFlat - right).normalized; break;
                }
            }
            else
            {
                float cycle = Mathf.Max(0.2f, evasionCycleZigzag);
                int phaseIndex = (int)(phaseTime / cycle) % 2;
                float sign = phaseIndex == 0 ? 1f : -1f;
                evasionDir = (toPlayerFlat + sign * right * 0.8f).normalized;
            }

            var summary = PlayerBehaviorCollector.GetCurrentSummary();
            float lateralEma = summary != null ? Mathf.Clamp01(summary.LateralMoveRatioEma) : 0.3f;
            float blend = Mathf.Clamp(AdaptiveAISettings.SoftEvasionBlendMax * (0.2f + 0.6f * lateralEma), 0f, AdaptiveAISettings.SoftEvasionBlendMax) * weaponBlendMult;
            if (AdaptiveAISettings.MovementPatternStrafeScaleByPersonality)
                blend *= GetStrafingTendencyMultiplier(GetStrafingTendencyFor(c));
            if (AdaptiveAISettings.MovementPatternStrafeScaleByLowHealth && IsHealthRatioAtOrBelow(c, GetLowHealthRatioThresholdForStrafe(c, ai)))
                blend *= Mathf.Clamp(AdaptiveAISettings.MovementPatternLowHealthStrafeMult, 1f, 1.3f);
            // 경로 추종 중일 때 회피 블렌드 완화: 경로를 너무 덮어쓰지 않도록.
            if (AI_PathControlPatches.IsCharacterPathFollowing(c))
                blend *= Mathf.Clamp01(AdaptiveAISettings.MovementPatternStrafeWhenPathFollowingMultiplier);

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlat, evasionDir, blend);
            if (blended.sqrMagnitude > 0.0001f)
            {
                blended.Normalize();
                blended.y = moveInput.y;
                moveInput = blended;
                MarkMovementPatternActive(c);
            }
        }

        /// <summary>플레이어 학습 무빙 패턴(스트라핑·지그재그)을 이동 입력에 블렌드. 공격성 스케일 옵션 시 적극적일수록 스트라핑 강화. 접근 시에는 학습값이 0이어도 최소 좌우 무빙(ApproachMinStrafe) 적용. EngageDistance 밖에서는 스트라핑을 완전히 끄지 않고 거리에 따라 약하게 적용해 멀리서도 직선만 보이지 않게 함. 근접무기는 스트라핑/지그재그 미적용(직선 접근만).</summary>
        private static void ApplyMovementPattern(CharacterMainControl c, ref Vector3 moveInput, Vector3 toPlayerFlat)
        {
            if (!AdaptiveAISettings.MovementPatternEnabled) return;
            if (c.GetMeleeWeapon() != null) return; // 근접: 직선으로만 다가가도록 무빙 패턴 미적용
            float engageDist = Mathf.Max(0f, AdaptiveAISettings.MovementPatternEngageDistance);
            float distToPlayer = 0f;
            float farScale = 1f; // EngageDistance 밖이면 0.25~1 구간으로 스트라핑 약하게 적용
            if (engageDist > 0.01f && CharacterMainControl.Main != null)
            {
                Vector3 toPlayer = GetSmoothedPlayerPositionForMovement(c) - c.transform.position;
                toPlayer.y = 0f;
                distToPlayer = toPlayer.magnitude;
                if (distToPlayer > engageDist)
                    farScale = Mathf.Clamp(engageDist / distToPlayer, 0.25f, 1f);
            }
            var summary = PlayerBehaviorCollector.GetCurrentSummary();
            if (summary == null) return;
            float lateralEma = summary.LateralMoveRatioEma;
            // 접근 중(플레이어 방향)일 때는 학습값이 없어도 최소 스트라핑 적용 → 직선 무빙 완화
            Vector3 moveFlatForApproach = moveInput;
            moveFlatForApproach.y = 0f;
            if (moveFlatForApproach.sqrMagnitude < 0.0001f) moveFlatForApproach = toPlayerFlat;
            else moveFlatForApproach.Normalize();
            float approachDotForMin = Vector3.Dot(moveFlatForApproach, toPlayerFlat);
            float approachMinStrafe = Mathf.Clamp01(AdaptiveAISettings.MovementPatternApproachMinStrafe);
            float effectiveLateral = (approachDotForMin > 0.5f && approachMinStrafe > 0f)
                ? Mathf.Max(lateralEma, approachMinStrafe)
                : lateralEma;
            if (effectiveLateral < 0.01f) return;

            Vector3 right = Vector3.Cross(Vector3.up, toPlayerFlat);
            if (right.sqrMagnitude < 0.01f) return;
            right.Normalize();
            if (AdaptiveAISettings.MovementPatternAlignStrafeToCover && AdaptiveAISettings.MovementPatternCoverAlignBlend > 0.0001f)
            {
                var main = CharacterMainControl.Main;
                if (main != null)
                {
                    Vector3 aiPos = c.transform.position;
                    Vector3 playerPos = GetSmoothedPlayerPositionForMovement(c);
                    if (TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover))
                    {
                        toCover.y = 0f;
                        if (toCover.sqrMagnitude > 0.01f)
                        {
                            toCover.Normalize();
                            Vector3 coverTangent = Vector3.Cross(Vector3.up, toCover);
                            if (coverTangent.sqrMagnitude > 0.01f)
                            {
                                coverTangent.Normalize();
                                float blendCover = Mathf.Clamp01(AdaptiveAISettings.MovementPatternCoverAlignBlend);
                                right = Vector3.Slerp(right, coverTangent, blendCover).normalized;
                            }
                        }
                    }
                }
            }

            float variance = summary.MoveDirDotVariance;
            float cycleBase = AdaptiveAISettings.MovementPatternZigzagCycleBase;
            float varRef = Mathf.Max(0.01f, AdaptiveAISettings.MovementPatternVarianceRef);
            float cycle = Mathf.Max(0.3f, cycleBase - variance / varRef * 0.4f);
            if (AdaptiveAISettings.MovementPatternCycleScaleByHitRate && summary.HasCombatSamplesP2E)
            {
                float shoots = Mathf.Max(summary.ShootCountPerMin * 0.5f, 1f);
                float hitRate = Mathf.Clamp01(summary.PlayerToEnemyHitsPerMin / shoots);
                float minMult = Mathf.Clamp(AdaptiveAISettings.MovementPatternCycleMinMultiplierAtHighHitRate, 0.4f, 1f);
                float mult = Mathf.Lerp(1f, minMult, hitRate);
                cycle *= mult;
            }
            GetPlayerWeaponMovementMultipliers(out float weaponCycleMult, out float weaponBlendMult);
            cycle *= weaponCycleMult;
            float phaseTime = GetMovementPhaseTime(c);
            int phaseIndex = (int)(phaseTime / cycle) % 2;
            float sign = phaseIndex == 0 ? 1f : -1f;
            Vector3 strafeDir = sign * right;

            float blend = Mathf.Clamp01(effectiveLateral * AdaptiveAISettings.MovementPatternStrafeStrength) * weaponBlendMult;
            if (AdaptiveAISettings.MovementPatternStrafeScaleByAggression)
            {
                float aggScale = GetAggressionScaleFor(c);
                blend *= Mathf.Lerp(0.7f, 1f, aggScale);
            }
            if (AdaptiveAISettings.MovementPatternStrafeScaleByPersonality)
                blend *= GetStrafingTendencyMultiplier(GetStrafingTendencyFor(c));
            if (AdaptiveAISettings.MovementPatternStrafeScaleByLowHealth && IsHealthRatioAtOrBelow(c, GetLowHealthRatioThresholdForStrafe(c, null)))
                blend *= Mathf.Clamp(AdaptiveAISettings.MovementPatternLowHealthStrafeMult, 1f, 1.3f);
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            // 접근 중(플레이어 방향)일 때 스트라핑을 줄여 평행 접근(옆으로만 다가옴) 방지.
            float approachDot = Vector3.Dot(moveFlat, toPlayerFlat);
            float approachThreshold = Mathf.Clamp01(AdaptiveAISettings.MovementPatternApproachStrafeReduceThreshold);
            float denom = Mathf.Max(0.01f, 1f - approachThreshold);
            if (approachDot > approachThreshold)
                blend *= Mathf.Lerp(1f, AdaptiveAISettings.MovementPatternApproachStrafeMultiplier, (approachDot - approachThreshold) / denom);
            if (approachDot < 0f)
                blend *= Mathf.Clamp01(AdaptiveAISettings.MovementPatternStrafeWhenRetreatingMultiplier);
            if (AdaptiveAISettings.MovementPatternStrafeScaleByDistance && engageDist > 0.01f && CharacterMainControl.Main != null)
            {
                float dist = (CharacterMainControl.Main.transform.position - c.transform.position).magnitude;
                if (dist <= AdaptiveAISettings.MovementPatternNearDist && dist > 0.01f)
                    blend *= Mathf.Clamp(AdaptiveAISettings.MovementPatternNearStrafeMultiplier, 1f, 2f);
            }
            // 경로 추종 중일 때 스트라핑 완화: 경로 방향을 우선해 목표 도달이 자연스럽게.
            if (AI_PathControlPatches.IsCharacterPathFollowing(c))
                blend *= Mathf.Clamp01(AdaptiveAISettings.MovementPatternStrafeWhenPathFollowingMultiplier);
            // 경로 끝 구간(도착 직전)일 때 추가 완화로 정확히 서게 함.
            if (AI_PathControlPatches.IsCharacterReachedEndOfPath(c))
                blend *= Mathf.Clamp01(AdaptiveAISettings.MovementPatternStrafeWhenNearPathEndMultiplier);
            blend *= farScale;
            // waypoint(경로) 추종 중이면 moveFlat = 원본 waypoint 방향 → waypoint 우선, 그 위에 스트라핑만 가산
            // 스트라핑 강도가 눈에 보이도록: (moveFlat + strafeDir*blend) 정규화로 lateral을 blend 비율로 적용
            Vector3 blended = (moveFlat + strafeDir * blend).normalized;
            if (blended.sqrMagnitude > 0.0001f)
            {
                blended.Normalize();
                blended.y = moveInput.y;
                moveInput = blended;
                MarkMovementPatternActive(c);
            }
        }

        /// <summary>투사체가 없을 때, 플레이어 발사 패턴(버스트/다음 발 예상)에 맞춰 카운터 무빙 블렌드. 공격성 절대값이 높을수록 패턴 읽기 능력(블렌드 강도)이 올라감.</summary>
        private static void ApplyCounterMoveFromFirePattern(CharacterMainControl c, ref Vector3 moveInput, Vector3 toPlayerFlat, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.CounterMoveOnFirePatternEnabled) return;
            if (CharacterMainControlDashPatches.DashIsIncomingDodge) return;

            float agg = AICharacterControllerPatches.GetBehaviorAggressionFor(ai);
            float minAgg = AdaptiveAISettings.PatternReadMinAggression;
            float maxAgg = AdaptiveAISettings.PatternReadMaxAggression;
            if (maxAgg <= minAgg) maxAgg = minAgg + 0.01f;
            float patternReadT = Mathf.Clamp01((agg - minAgg) / (maxAgg - minAgg));
            if (patternReadT < 0.01f) return;

            if (!PlayerBehaviorCollector.GetRecentShootPattern(out float avgInterval, out bool inBurst, out float nextShotExpected))
                return;

            float now = Time.time;
            float window = AdaptiveAISettings.CounterMoveNextShotWindow;
            bool nearNextShot = now >= nextShotExpected - window && now <= nextShotExpected + window;
            if (!inBurst && !nearNextShot) return;

            float baseBlend = inBurst
                ? AdaptiveAISettings.CounterMoveBlendWhenBurst
                : AdaptiveAISettings.CounterMoveBlendWhenNextShotExpected;
            float blend = Mathf.Clamp(baseBlend * patternReadT, 0f, baseBlend);
            if (AdaptiveAISettings.MovementPatternStrafeScaleByPersonality)
                blend *= GetStrafingTendencyMultiplier(GetStrafingTendencyFor(c));
            if (AdaptiveAISettings.MovementPatternStrafeScaleByLowHealth && IsHealthRatioAtOrBelow(c, GetLowHealthRatioThresholdForStrafe(c, ai)))
                blend *= Mathf.Clamp(AdaptiveAISettings.MovementPatternLowHealthStrafeMult, 1f, 1.3f);
            if (AI_PathControlPatches.IsCharacterPathFollowing(c))
                blend *= Mathf.Clamp01(AdaptiveAISettings.MovementPatternStrafeWhenPathFollowingMultiplier);

            Vector3 projFlat = toPlayerFlat;
            Vector3 right = Vector3.Cross(Vector3.up, projFlat);
            if (right.sqrMagnitude < 0.01f) return;
            right.Normalize();

            float phaseTime = GetMovementPhaseTime(c);
            Vector3 evasionDir;
            if (AdaptiveAISettings.SoftEvasionUseDiamondStep && AdaptiveAISettings.SoftEvasionDiamondCycle > 0.01f)
            {
                float diamondCycle = AdaptiveAISettings.SoftEvasionDiamondCycle;
                int phase = (int)(phaseTime / diamondCycle) % 4;
                switch (phase)
                {
                    case 0: evasionDir = (toPlayerFlat + right).normalized; break;
                    case 1: evasionDir = (toPlayerFlat - right).normalized; break;
                    case 2: evasionDir = (-toPlayerFlat + right).normalized; break;
                    default: evasionDir = (-toPlayerFlat - right).normalized; break;
                }
            }
            else
            {
                float cycle = Mathf.Max(0.2f, AdaptiveAISettings.SoftEvasionZigzagCycle);
                int phaseIndex = (int)(phaseTime / cycle) % 2;
                float sign = phaseIndex == 0 ? 1f : -1f;
                evasionDir = (toPlayerFlat + sign * right * 0.8f).normalized;
            }

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlat, evasionDir, blend);
            if (blended.sqrMagnitude > 0.0001f)
            {
                blended.Normalize();
                blended.y = moveInput.y;
                moveInput = blended;
                MarkMovementPatternActive(c);
            }
        }

        /// <summary>플레이어(메인 캐릭터)가 재장전 중인지. 후퇴→접근 패턴에서 사용.</summary>
        private static bool IsPlayerReloading()
        {
            var main = CharacterMainControl.Main;
            return main != null && CharacterMainControlDashPatches.IsCharacterReloading(main);
        }

        /// <summary>해당 캐릭터의 현재 체력 비율이 ratio 이하인지. Health 없거나 MaxHealth 0이면 false(거리 유지 미적용).</summary>
        private static bool IsHealthRatioAtOrBelow(CharacterMainControl c, float ratio)
        {
            if (c?.Health == null) return false;
            float max = c.Health.MaxHealth;
            if (max <= 0f) return false;
            return (c.Health.CurrentHealth / max) <= ratio;
        }

        /// <summary>저체력 스트라핑/회피 강화 발동 임계치(체력 비율). 판단 능력(0~2)이 높을수록 더 높은 체력에서 발동(예: 판단 최대 50%, 판단 0이면 1%).</summary>
        private static float GetLowHealthRatioThresholdForStrafe(CharacterMainControl c, global::AICharacterController ai)
        {
            float judgment = 0f;
            if (ai != null)
                judgment = Mathf.Clamp(AICharacterControllerPatches.GetCachedAggressionFor(ai), 0f, 2f);
            else if (c != null)
            {
                var a = c.GetComponent<global::AICharacterController>() ?? c.GetComponentInParent<global::AICharacterController>();
                if (a != null) judgment = Mathf.Clamp(AICharacterControllerPatches.GetCachedAggressionFor(a), 0f, 2f);
            }
            float norm = Mathf.Clamp01(judgment / 2f);
            float minR = Mathf.Clamp01(AdaptiveAISettings.MovementPatternLowHealthRatioAtJudgmentMin);
            float maxR = Mathf.Clamp01(AdaptiveAISettings.MovementPatternLowHealthRatioAtJudgmentMax);
            return Mathf.Lerp(minR, maxR, norm);
        }

        /// <summary>재장전 중일 때 플레이어와 AI 사이(플레이어-엄폐물-AI) 엄폐물 방향으로 이동 보정. 판단능력이 높을수록 블렌드 강도 상승.</summary>
        private static void ApplyCoverToReload(ref Vector3 moveInput, ref Vector3 moveFlat, CharacterMainControl c, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai, Vector3 toPlayerFlat)
        {
            if (!AdaptiveAISettings.CoverToReloadEnabled || ai == null) return;
            if (!CharacterMainControlDashPatches.IsCharacterReloading(c)) return;
            if (!TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover)) return;
            // 판단력 0~2(GetCachedAggressionFor) → 0~1 정규화. 높을수록 엄폐 이동 수행.
            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            float blend = Mathf.Lerp(AdaptiveAISettings.CoverToReloadBlendMin, AdaptiveAISettings.CoverToReloadBlendMax, judgmentNorm);
            if (blend <= 0.0001f) return;
            Vector3 moveFlatIn = moveInput;
            moveFlatIn.y = 0f;
            if (moveFlatIn.sqrMagnitude < 0.0001f) moveFlatIn = toPlayerFlat;
            moveFlatIn.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlatIn, toCover, blend).normalized;
            float mag = Mathf.Clamp01(AdaptiveAISettings.CoverToReloadMoveMagnitude);
            moveInput.x = blended.x * mag;
            moveInput.z = blended.z * mag;
            moveInput.y = 0f;
            moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = Vector3.forward;
        }

        /// <summary>AI→플레이어 직선 레이에 장애물이 없을 때, 플레이어 방향 기준 좌우 아크(180°~270°)로 레이를 쏴 가까운 엄폐 후보를 찾음.</summary>
        private static bool TryGetCoverDirectionFromArc(Vector3 aiPos, Vector3 playerPos, out Vector3 toCoverFlat)
        {
            toCoverFlat = Vector3.zero;
            if (!AdaptiveAISettings.CoverArcScanEnabled || !TryGetObstacleLayerMask(out int obstacleMask)) return false;
            Vector3 toPlayerFlat = playerPos - aiPos;
            toPlayerFlat.y = 0f;
            if (toPlayerFlat.sqrMagnitude < 0.0001f) return false;
            toPlayerFlat.Normalize();
            float halfDeg = Mathf.Clamp(AdaptiveAISettings.CoverArcScanHalfAngleDeg, 5f, 175f);
            float stepDeg = Mathf.Max(2f, AdaptiveAISettings.CoverArcScanStepDeg);
            float maxDist = Mathf.Max(1f, AdaptiveAISettings.CoverArcScanMaxDist);
            Vector3 rayOrigin = aiPos + Vector3.up * CoverRaycastOriginHeight;
            float bestDist = float.MaxValue;
            bool bestHit = false;
            RaycastHit bestHitResult = default;
            for (float angleDeg = -halfDeg; angleDeg <= halfDeg; angleDeg += stepDeg)
            {
                Vector3 dir = Quaternion.AngleAxis(angleDeg, Vector3.up) * toPlayerFlat;
                if (dir.sqrMagnitude < 0.0001f) continue;
                if (!Physics.Raycast(rayOrigin, dir, out RaycastHit hit, maxDist, obstacleMask, QueryTriggerInteraction.Ignore))
                    continue;
                if (hit.collider != null && CharacterMainControl.Main != null && hit.collider.transform.IsChildOf(CharacterMainControl.Main.transform))
                    continue;
                float hitDistFlat = new Vector3(hit.point.x - aiPos.x, 0f, hit.point.z - aiPos.z).magnitude;
                if (hitDistFlat <= 0.5f || hitDistFlat > maxDist) continue;
                if (hitDistFlat < bestDist)
                {
                    bestDist = hitDistFlat;
                    bestHit = true;
                    bestHitResult = hit;
                }
            }
            if (!bestHit) return false;
            return TryGetCoverDirectionFromHit(aiPos, bestHitResult, out toCoverFlat);
        }

        /// <summary>레이/SphereCast/아크에 걸리지 않지만 AI↔플레이어 구간에 있는 콜라이더(총알차단·얇은 장애물 등)를 OverlapSphere로 찾아 엄폐 방향 계산. CoverOverlapSphereLayerMask가 0이 아니고 CoverDetectOverlapSphereEnabled일 때만 사용.</summary>
        private static bool TryGetCoverDirectionFromOverlapSphere(Vector3 aiPos, Vector3 playerPos, float distToPlayer, out Vector3 toCoverFlat)
        {
            toCoverFlat = Vector3.zero;
            int mask = AdaptiveAISettings.CoverOverlapSphereLayerMask;
            if (mask == 0 || distToPlayer < 0.5f) return false;
            int sampleCount = Mathf.Clamp(AdaptiveAISettings.CoverOverlapSphereSampleCount, 2, 20);
            float radius = Mathf.Max(0.2f, AdaptiveAISettings.CoverOverlapSphereRadius);
            Vector3 toPlayer = (playerPos - aiPos).normalized;
            float rayDist = Mathf.Min(distToPlayer + 1f, CoverRaycastMaxDist);

            if (_coverOverlapSphereBuffer == null || _coverOverlapSphereBuffer.Length < CoverOverlapSphereBufferSize)
                _coverOverlapSphereBuffer = new Collider[CoverOverlapSphereBufferSize];
            Collider[] buffer = _coverOverlapSphereBuffer;
            float bestDist = float.MaxValue;
            Collider bestCollider = null;
            Vector3 bestPoint = Vector3.zero;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (i + 1) / (float)(sampleCount + 1);
                Vector3 sampleOrigin = aiPos + toPlayer * (t * rayDist);
                int n = Physics.OverlapSphereNonAlloc(sampleOrigin, radius, buffer, mask, QueryTriggerInteraction.Ignore);
                for (int j = 0; j < n; j++)
                {
                    Collider c = buffer[j];
                    if (c == null || !c.enabled) continue;
                    if (CharacterMainControl.Main != null && c.transform.IsChildOf(CharacterMainControl.Main.transform))
                        continue;
                    Vector3 surfacePoint = c.ClosestPoint(aiPos);
                    Vector3 flatToSurface = surfacePoint - aiPos;
                    flatToSurface.y = 0f;
                    float distFlat = flatToSurface.magnitude;
                    if (distFlat < 0.5f) continue;
                    if (distFlat >= distToPlayer - 0.2f) continue;
                    if (distFlat < bestDist)
                    {
                        bestDist = distFlat;
                        bestCollider = c;
                        bestPoint = surfacePoint;
                    }
                }
            }

            if (bestCollider == null) return false;
            Vector3 toAiFromSurface = aiPos - bestPoint;
            toAiFromSurface.y = 0f;
            if (toAiFromSurface.sqrMagnitude < 0.0001f) return false;
            var syntheticHit = new RaycastHit
            {
                point = bestPoint,
                normal = toAiFromSurface.normalized,
                distance = bestDist
            };
            return TryGetCoverDirectionFromHit(aiPos, syntheticHit, out toCoverFlat);
        }

        /// <summary>레이 히트 결과(장애물 표면)로 "플레이어–AI 사이에 엄폐가 오는" 목표 방향을 계산. 두꺼운 장애물 보정 포함.</summary>
        private static bool TryGetCoverDirectionFromHit(Vector3 aiPos, RaycastHit hit, out Vector3 toCoverFlat)
        {
            toCoverFlat = Vector3.zero;
            Vector3 toAiFromHit = aiPos - hit.point;
            toAiFromHit.y = 0f;
            if (toAiFromHit.sqrMagnitude < 0.0001f) return false;
            toAiFromHit.Normalize();
            float offset = Mathf.Max(CoverTargetOffsetBehindObstacle, CoverTargetOffsetMin);
            float maxOffset = offset;
            if (TryGetObstacleLayerMask(out int mask))
            {
                float rayToAi = (aiPos - hit.point).magnitude;
                if (Physics.Raycast(hit.point, toAiFromHit, out RaycastHit hitBack, Mathf.Min(offset + 0.5f, rayToAi), mask, QueryTriggerInteraction.Ignore))
                {
                    float backDist = new Vector3(hitBack.point.x - hit.point.x, 0f, hitBack.point.z - hit.point.z).magnitude;
                    if (backDist > 0.1f)
                        maxOffset = Mathf.Max(CoverTargetOffsetMin, Mathf.Min(offset, backDist - 0.1f));
                }
            }
            Vector3 coverTarget = hit.point + toAiFromHit * maxOffset;
            toCoverFlat = coverTarget - aiPos;
            toCoverFlat.y = 0f;
            if (toCoverFlat.sqrMagnitude < 0.0001f) return false;
            toCoverFlat.Normalize();
            return true;
        }

        /// <summary>AI→플레이어 레이에 장애물이 있으면 "플레이어–AI 사이에 엄폐가 오는" 방향 반환. 없으면 SphereCast·좌우 오프셋 레이로 보조 감지 후 아크 스캔으로 엄폐 탐색. 감지 일관화로 우회/비비기 차이 완화.</summary>
        private static bool TryGetCoverDirection(Vector3 aiPos, Vector3 playerPos, out Vector3 toCoverFlat)
        {
            toCoverFlat = Vector3.zero;
            if (!TryGetObstacleLayerMask(out int obstacleMask)) return false;
            Vector3 baseOrigin = aiPos + Vector3.up * CoverRaycastOriginHeight;
            Vector3 lowOrigin = aiPos + Vector3.up * CoverRaycastOriginHeightLow;
            Vector3 toPlayer3d = (playerPos - aiPos).normalized;
            float dist = (playerPos - aiPos).magnitude;
            float rayDist = Mathf.Min(dist + 2f, CoverRaycastMaxDist);
            Vector3 coverResult = Vector3.zero;
            bool tryHit(Vector3 origin, RaycastHit h)
            {
                if (h.collider != null && CharacterMainControl.Main != null && h.collider.transform.IsChildOf(CharacterMainControl.Main.transform))
                    return false;
                float hitDistFlat = new Vector3(h.point.x - aiPos.x, 0f, h.point.z - aiPos.z).magnitude;
                return hitDistFlat > 0.5f && hitDistFlat < dist && TryGetCoverDirectionFromHit(aiPos, h, out coverResult);
            }
            // 메인 높이(무릎~가슴) 레이
            if (Physics.Raycast(baseOrigin, toPlayer3d, out RaycastHit hit, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(baseOrigin, hit))
            {
                toCoverFlat = coverResult;
                return true;
            }
            // 낮은 높이 보조 레이: 이동 방해되는 낮은 장애물(턱, 낮은 벽) 감지
            if (Physics.Raycast(lowOrigin, toPlayer3d, out RaycastHit lowHit, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(lowOrigin, lowHit))
            {
                toCoverFlat = coverResult;
                return true;
            }
            // 레이에 막히지 않지만 캐릭터 몸이 막히는 엄폐물(얇은 벽·울타리 등) 감지: SphereCast로 보조
            if (dist > 0.8f && rayDist > CoverSphereCastRadius && Physics.SphereCast(baseOrigin, CoverSphereCastRadius, toPlayer3d, out RaycastHit sphereHit, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(baseOrigin, sphereHit))
            {
                toCoverFlat = coverResult;
                return true;
            }
            if (dist > 0.8f && rayDist > CoverSphereCastRadius && Physics.SphereCast(lowOrigin, CoverSphereCastRadius, toPlayer3d, out RaycastHit sphereLowHit, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(lowOrigin, sphereLowHit))
            {
                toCoverFlat = coverResult;
                return true;
            }
            // 위치/각도 차이로 중심 레이가 놓치는 경우: 좌우 오프셋 레이로 동일 방향 추가 감지 (메인·낮은 높이 둘 다)
            Vector3 right = Vector3.Cross(Vector3.up, toPlayer3d);
            if (right.sqrMagnitude < 0.01f) right = Quaternion.AngleAxis(90f, Vector3.up) * toPlayer3d;
            right.Normalize();
            float off = Mathf.Min(CoverLateralRayOffset, rayDist * 0.5f);
            if (off > 0.01f)
            {
                if (Physics.Raycast(baseOrigin + right * off, toPlayer3d, out RaycastHit hitR, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(baseOrigin + right * off, hitR))
                {
                    toCoverFlat = coverResult;
                    return true;
                }
                if (Physics.Raycast(baseOrigin - right * off, toPlayer3d, out RaycastHit hitL, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(baseOrigin - right * off, hitL))
                {
                    toCoverFlat = coverResult;
                    return true;
                }
                if (Physics.Raycast(lowOrigin + right * off, toPlayer3d, out RaycastHit lowHitR, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(lowOrigin + right * off, lowHitR))
                {
                    toCoverFlat = coverResult;
                    return true;
                }
                if (Physics.Raycast(lowOrigin - right * off, toPlayer3d, out RaycastHit lowHitL, rayDist, obstacleMask, QueryTriggerInteraction.Ignore) && tryHit(lowOrigin - right * off, lowHitL))
                {
                    toCoverFlat = coverResult;
                    return true;
                }
            }
            if (TryGetCoverDirectionFromArc(aiPos, playerPos, out toCoverFlat))
                return true;
            // 레이/SphereCast/아크에 걸리지 않지만 총알을 막는 장애물(우회·엄폐 대상) 보조 탐지
            if (AdaptiveAISettings.CoverDetectOverlapSphereEnabled && AdaptiveAISettings.CoverOverlapSphereLayerMask != 0 &&
                TryGetCoverDirectionFromOverlapSphere(aiPos, playerPos, dist, out toCoverFlat))
                return true;
            return false;
        }

        /// <summary>접근 방향. 플레이어 장거리일 때(useCoverAsSteppingStones) AI→플레이어 레이에 장애물이 있으면 엄폐물 쪽으로 블렌드해 징검다리 이동.</summary>
        private static Vector3 GetApproachDirectionWithOptionalCover(Vector3 aiPos, Vector3 playerPos, Vector3 toPlayerFlat, bool useCoverAsSteppingStones)
        {
            if (!useCoverAsSteppingStones) return toPlayerFlat;
            if (!TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover)) return toPlayerFlat;
            float coverBlend = Mathf.Clamp01(AdaptiveAISettings.CoverApproachBlendStrength);
            Vector3 approachDir = Vector3.Lerp(toPlayerFlat, toCover, coverBlend).normalized;
            approachDir.y = 0f;
            return approachDir.sqrMagnitude > 0.0001f ? approachDir : toPlayerFlat;
        }

        /// <summary>노출 상태에서 플레이어 사격 중이거나 최근 피격 시, 플레이어–AI 사이 엄폐물이 있으면 그 방향으로 이동 블렌드(엄폐 추구).</summary>
        private static void ApplySeekCoverWhenUnderFire(ref Vector3 moveInput, Vector3 toPlayerFlat, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.SeekCoverWhenUnderFireEnabled || ai == null) return;
            if (ai.hasObsticleToTarget) return;
            if (!TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover)) return;

            var main = CharacterMainControl.Main;
            bool playerShooting = main != null && main.attackAction != null && main.attackAction.Running;
            bool recentlyHurt = AICharacterControllerPatches.IsRecentlyHurtByPlayer(ai);
            if (!playerShooting && !recentlyHurt) return;

            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            if (judgmentNorm < AdaptiveAISettings.SeekCoverWhenUnderFireJudgmentMin) return;

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            float blend = Mathf.Clamp01(AdaptiveAISettings.SeekCoverWhenUnderFireBlendStrength);
            if (AdaptiveAISettings.TacticalStanceByPlayerPatternEnabled)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null)
                    blend *= (1f + summary.GetDefensiveStanceFactor() * Mathf.Clamp01(AdaptiveAISettings.DefensiveStanceWeightRetreat));
            }
            if (AdaptiveAISettings.TacticalModeTierEnabled && ai != null)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null)
                {
                    int mode = Data.BehaviorProfileSummary.GetTacticalModeIndex(Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f), summary.GetDefensiveStanceFactor());
                    blend *= AdaptiveAISettings.GetTacticalModeCoverWaitBlendMult(mode);
                }
            }
            blend = Mathf.Clamp01(blend);
            Vector3 blended = Vector3.Lerp(moveFlat, toCover, blend).normalized;
            float mag = Mathf.Max(0.15f, new Vector3(moveInput.x, 0f, moveInput.z).magnitude);
            moveInput.x = blended.x * mag;
            moveInput.z = blended.z * mag;
        }

        /// <summary>플레이어 무기 유리 + AI 엄폐 뒤 + 플레이어가 멀 때: 엄폐 유지(대기). 가까이 오면 대기 해제.</summary>
        private static void ApplyCoverWaitIfPlayerAdvantage(ref Vector3 moveInput, Vector3 toPlayerFlat, Vector3 aiPos, Vector3 playerPos, float dist, float playerRange, float aiRange, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.CoverWaitByPlayerAdvantageEnabled || ai == null) return;
            bool playerAdvantage = playerRange > aiRange + AdaptiveAISettings.CoverWaitPlayerAdvantageThreshold;
            if (!playerAdvantage || !ai.hasObsticleToTarget) return;
            if (dist <= AdaptiveAISettings.CoverWaitMaxDistToEngage) return;

            if (!TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover)) return;
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            float coverWaitBlend = Mathf.Clamp01(AdaptiveAISettings.CoverWaitBlendStrength);
            if (AdaptiveAISettings.TacticalStanceByPlayerPatternEnabled)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null)
                    coverWaitBlend *= (1f + summary.GetDefensiveStanceFactor() * Mathf.Clamp01(AdaptiveAISettings.DefensiveStanceWeightRetreat));
            }
            if (AdaptiveAISettings.TacticalModeTierEnabled && ai != null)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null)
                {
                    int mode = Data.BehaviorProfileSummary.GetTacticalModeIndex(Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f), summary.GetDefensiveStanceFactor());
                    coverWaitBlend *= AdaptiveAISettings.GetTacticalModeCoverWaitBlendMult(mode);
                }
            }
            coverWaitBlend = Mathf.Clamp01(coverWaitBlend);
            Vector3 blendedDir = Vector3.Lerp(moveFlat, toCover, coverWaitBlend).normalized;
            float cap = Mathf.Clamp01(AdaptiveAISettings.CoverWaitMoveMagnitudeCap);
            moveInput.x = blendedDir.x * cap;
            moveInput.z = blendedDir.z * cap;
        }

        /// <summary>플레이어 인지 후 "플레이어가 올 것 같다"고 판단할 때(거리 구간 + 판단력) 엄폐물 뒤에서 대기. 가까이 오면 대기 해제 후 교전.</summary>
        private static void ApplyCoverAmbushWait(ref Vector3 moveInput, Vector3 toPlayerFlat, Vector3 aiPos, Vector3 playerPos, float dist, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.CoverAmbushWaitEnabled || ai == null) return;
            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            if (judgmentNorm < AdaptiveAISettings.CoverAmbushWaitJudgmentMin) return;
            if (dist < AdaptiveAISettings.CoverAmbushWaitDistMin || dist > AdaptiveAISettings.CoverAmbushWaitDistMax) return;
            if (!TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover)) return;

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            float ambushBlend = Mathf.Clamp01(AdaptiveAISettings.CoverAmbushWaitBlendStrength);
            if (AdaptiveAISettings.TacticalStanceByPlayerPatternEnabled)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null)
                    ambushBlend *= (1f + summary.GetDefensiveStanceFactor() * Mathf.Clamp01(AdaptiveAISettings.DefensiveStanceWeightRetreat));
            }
            if (AdaptiveAISettings.TacticalModeTierEnabled && ai != null)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null)
                {
                    int mode = Data.BehaviorProfileSummary.GetTacticalModeIndex(Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f), summary.GetDefensiveStanceFactor());
                    ambushBlend *= AdaptiveAISettings.GetTacticalModeCoverWaitBlendMult(mode);
                }
            }
            ambushBlend = Mathf.Clamp01(ambushBlend);
            Vector3 blendedDir = Vector3.Lerp(moveFlat, toCover, ambushBlend).normalized;
            float cap = Mathf.Clamp01(AdaptiveAISettings.CoverAmbushWaitMoveMagnitudeCap);
            moveInput.x = blendedDir.x * cap;
            moveInput.z = blendedDir.z * cap;
        }

        /// <summary>피킹: 플레이어 사격 중이면 엄폐 방향(숨기), 미사격/재장전이면 플레이어 방향(피크 아웃).</summary>
        private static void ApplyCoverPeek(ref Vector3 moveInput, Vector3 toPlayerFlat, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai)
        {
            if (!AdaptiveAISettings.CoverPeekEnabled || ai == null) return;
            bool inCover = ai.hasObsticleToTarget || TryGetCoverDirection(aiPos, playerPos, out _);
            if (!inCover) return;

            var main = CharacterMainControl.Main;
            bool playerShooting = main != null && main.attackAction != null && main.attackAction.Running;
            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();

            if (playerShooting)
            {
                if (!TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover)) return;
                Vector3 blended = Vector3.Lerp(moveFlat, toCover, Mathf.Clamp01(AdaptiveAISettings.CoverPeekHideBlendWhenPlayerShooting)).normalized;
                float mag = Mathf.Max(0.01f, new Vector3(moveInput.x, 0f, moveInput.z).magnitude);
                moveInput.x = blended.x * mag;
                moveInput.z = blended.z * mag;
            }
            else
            {
                float blend = Mathf.Clamp01(AdaptiveAISettings.CoverPeekOutBlendWhenPlayerNotShooting);
                Vector3 blended = Vector3.Lerp(moveFlat, toPlayerFlat, blend).normalized;
                float mag = Mathf.Max(0.01f, new Vector3(moveInput.x, 0f, moveInput.z).magnitude);
                moveInput.x = blended.x * mag;
                moveInput.z = blended.z * mag;
            }
        }

        /// <summary>판단력이 높을 때 엄폐물에서 나와 플레이어를 공격하도록 이동을 플레이어 방향으로 블렌드. (재장전/저체력이 아니면 적용)</summary>
        private static void ApplyLeaveCoverToEngage(ref Vector3 moveInput, Vector3 toPlayerFlat, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai, CharacterMainControl c)
        {
            if (!AdaptiveAISettings.LeaveCoverToEngageEnabled || ai == null || c == null) return;
            bool inCover = ai.hasObsticleToTarget || TryGetCoverDirection(aiPos, playerPos, out _);
            if (!inCover) return;

            // 이미 근접이면 플레이어 방향 블렌드 스킵(궤도/등뒤만 쓰여 앞뒤 흔들림 방지)
            Vector3 toP = playerPos - aiPos;
            toP.y = 0f;
            if (toP.sqrMagnitude < RetreatSuppressWhenCloserThan * RetreatSuppressWhenCloserThan) return;

            if (AdaptiveAISettings.TacticalStanceByPlayerPatternEnabled)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null && summary.GetDefensiveStanceFactor() >= AdaptiveAISettings.LeaveCoverToEngageDefensiveStanceCap)
                    return;
            }

            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            if (judgmentNorm < AdaptiveAISettings.LeaveCoverToEngageJudgmentMin) return;

            // 재장전 중이거나 저체력이면 엄폐 유지(나가서 공격 적용 안 함).
            if (CharacterMainControlDashPatches.IsCharacterReloading(c)) return;
            if (AdaptiveAISettings.CoverRecoveryWhenLowHPEnabled && IsHealthRatioAtOrBelow(c, AdaptiveAISettings.CoverRecoveryHPRatioThreshold)) return;

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            float blend = Mathf.Clamp01(AdaptiveAISettings.LeaveCoverToEngageBlendStrength);
            Vector3 blended = Vector3.Lerp(moveFlat, toPlayerFlat, blend).normalized;
            float mag = Mathf.Max(0.01f, new Vector3(moveInput.x, 0f, moveInput.z).magnitude);
            moveInput.x = blended.x * mag;
            moveInput.z = blended.z * mag;
            // 엄폐 이탈 대시 조건: 이번 프레임에 엄폐에서 나오는 블렌드가 적용됐음을 기록
            var holder = _approachStateByAi.GetOrCreateValue(ai);
            holder.LastLeaveCoverEngageTime = Time.time;
        }

        /// <summary>저체력일 때 엄폐물 뒤에서 숨기(회복 유도). 판단력 높을수록 엄폐 방향 블렌드 강화.</summary>
        private static void ApplyCoverRecoveryWhenLowHP(ref Vector3 moveInput, Vector3 toPlayerFlat, Vector3 aiPos, Vector3 playerPos, global::AICharacterController ai, CharacterMainControl c)
        {
            if (!AdaptiveAISettings.CoverRecoveryWhenLowHPEnabled || ai == null || c == null) return;
            if (!IsHealthRatioAtOrBelow(c, AdaptiveAISettings.CoverRecoveryHPRatioThreshold)) return;
            if (!TryGetCoverDirection(aiPos, playerPos, out Vector3 toCover)) return;

            float judgmentNorm = Mathf.Clamp01(AICharacterControllerPatches.GetCachedAggressionFor(ai) / 2f);
            float blend = Mathf.Clamp01(AdaptiveAISettings.CoverRecoveryBlendStrength) * (0.3f + 0.7f * judgmentNorm);
            if (blend <= 0.0001f) return;

            Vector3 moveFlat = moveInput;
            moveFlat.y = 0f;
            if (moveFlat.sqrMagnitude < 0.0001f) moveFlat = toPlayerFlat;
            moveFlat.Normalize();
            Vector3 blended = Vector3.Lerp(moveFlat, toCover, blend).normalized;
            float mag = Mathf.Max(0.01f, new Vector3(moveInput.x, 0f, moveInput.z).magnitude);
            moveInput.x = blended.x * mag;
            moveInput.z = blended.z * mag;
        }

        /// <summary>접근 스타일(0=직선, 1=지그재그, 2=궤도)에 따라 접근 방향 벡터 반환. 정규화된 방향만 반환. 근접 무기는 항상 직선.</summary>
        private static Vector3 GetApproachDirectionFromStyle(CharacterMainControl c, Vector3 toTargetNorm, Vector3 aiPos, Vector3 playerPos, CharacterMainControl main)
        {
            if (c == null || main == null || toTargetNorm.sqrMagnitude < 0.01f) return toTargetNorm;
            if (c.GetMeleeWeapon() != null) return toTargetNorm; // 근접: 직선 접근만
            if (!AdaptiveAISettings.MovementPatternEnabled) return toTargetNorm;
            int style = GetApproachStyleForCharacter(c);
            if (style == 0) return toTargetNorm;

            Vector3 right = Vector3.Cross(Vector3.up, toTargetNorm);
            if (right.sqrMagnitude < 0.01f) return toTargetNorm;
            right.Normalize();

            if (style == 1)
            {
                // 지그재그: 주기적으로 좌우 전환하며 접근
                float cycle = Mathf.Max(0.2f, AdaptiveAISettings.MovementPatternZigzagCycleBase);
                int phase = (int)(Time.time / cycle) + c.GetInstanceID();
                float sign = (phase % 2 == 0) ? 1f : -1f;
                float zigzagStrength = 0.55f;
                Vector3 zigzagDir = (toTargetNorm + right * sign * zigzagStrength).normalized;
                return zigzagDir;
            }

            // style == 2: 궤도(플레이어 쪽으로 돌며 접근)
            Vector3 toPlayer = playerPos - aiPos;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.0001f) return toTargetNorm;
            toPlayer.Normalize();
            Vector3 tangentRight = Vector3.Cross(Vector3.up, toPlayer);
            if (tangentRight.sqrMagnitude < 0.01f) return toTargetNorm;
            tangentRight.Normalize();
            float orbitCycle = AdaptiveAISettings.ApproachOrbitalDirectionCycle > 0.01f
                ? AdaptiveAISettings.ApproachOrbitalDirectionCycle
                : AdaptiveAISettings.MovementPatternZigzagCycleBase;
            int orbitPhase = (int)(Time.time / orbitCycle) + c.GetInstanceID();
            float orbitSign = (orbitPhase % 2 == 0) ? 1f : -1f;
            Vector3 tangent = (orbitSign * tangentRight).normalized;
            float tangentRatio = Mathf.Clamp01(AdaptiveAISettings.ApproachOrbitalTangentRatio);
            Vector3 orbitApproachDir = (tangent * tangentRatio + toTargetNorm * (1f - tangentRatio)).normalized;
            return orbitApproachDir;
        }

        /// <summary>접근 스타일: 0=직선, 1=지그재그, 2=궤도(플레이어 주변 돌며 접근). 플레이어 행동에 따라 다이나믹 비율 적용 시 직선 접근倾向이면 직선 비율 감소(적은 지그재그/궤도 더 선택). 일정 거리 밖이면 직선(0)만 반환.</summary>
        private static int GetApproachStyleForCharacter(CharacterMainControl c)
        {
            if (c == null) return 1;
            float engageDist = AdaptiveAISettings.MovementPatternEngageDistance;
            if (engageDist > 0.01f)
            {
                var main = CharacterMainControl.Main;
                if (main != null)
                {
                    Vector3 toPlayer = GetSmoothedPlayerPositionForMovement(c) - c.transform.position;
                    toPlayer.y = 0f;
                    if (toPlayer.sqrMagnitude > engageDist * engageDist)
                        return 0;
                }
            }
            float straight = Mathf.Clamp01(AdaptiveAISettings.ApproachStyleStraightWeight);
            float zigzag = Mathf.Clamp01(AdaptiveAISettings.ApproachStyleZigzagWeight);
            float orbit = 1f - straight - zigzag;
            if (orbit < 0f) orbit = 0f;

            if (AdaptiveAISettings.MovementPatternApproachStyleDynamicByPlayer)
            {
                var summary = PlayerBehaviorCollector.GetCurrentSummary();
                if (summary != null)
                {
                    float moveDot = Mathf.Clamp01(summary.MoveDirDotToEnemyEma);
                    float lateral = Mathf.Clamp01(summary.LateralMoveRatioEma);
                    float variance = Mathf.Max(0f, summary.MoveDirDotVariance);
                    float varRef = Mathf.Max(0.01f, AdaptiveAISettings.MovementPatternVarianceRef);
                    float varianceNorm = Mathf.Clamp01(variance / varRef);
                    // 카운터 무빙: 플레이어 직선 접근 많으면 AI는 지그재그/궤도 비율 상승, 플레이어 옆돌기·지그재그 많으면 AI는 직선 비율 상승
                    float straightMin = Mathf.Min(0.15f, AdaptiveAISettings.ApproachStyleStraightWeight);
                    float straightMax = Mathf.Max(0.65f, AdaptiveAISettings.ApproachStyleStraightWeight);
                    straight = Mathf.Lerp(straightMax, straightMin, moveDot);
                    float remainder = 1f - straight;
                    if (remainder > 0.001f)
                    {
                        float playerDynamic = Mathf.Clamp01((varianceNorm + lateral) * 0.5f);
                        float zigzagRatio = 1f - playerDynamic;
                        zigzag = remainder * zigzagRatio;
                        orbit = remainder * playerDynamic;
                    }
                }
            }

            float cycle = Mathf.Max(1f, AdaptiveAISettings.ApproachStyleCycleSeconds);
            int phase = (int)(Time.time / cycle);
            int hash = (c.GetInstanceID() * 31 + phase) % 1000;
            if (hash < 0) hash += 1000;
            float t = hash / 1000f;
            if (t < straight) return 0;
            if (t < straight + zigzag) return 1;
            return 2;
        }

        /// <summary>장애물 구간에서 경로 실패/쿨다운/제자리 걸음 해제 후 등에, 플레이어 방향+옆 방향으로 이동 입력 적용해 제자리 걸음만 반복하지 않게 함.</summary>
        private static void ApplyObstacleFallbackMove(CharacterMainControl c, Vector3 aiPos, Vector3 playerPos)
        {
            Vector3 toPlayer = playerPos - aiPos;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.0001f) return;
            toPlayer.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, toPlayer);
            if (right.sqrMagnitude < 0.01f) right = Quaternion.AngleAxis(90f, Vector3.up) * toPlayer;
            right.Normalize();
            // 시간에 따라 좌우를 바꿔 가며 옆으로도 시도해 장애물 끝으로 빠져나올 기회를 줌
            float sideSign = (Mathf.FloorToInt(Time.time * 0.4f) % 2 == 0) ? 1f : -1f;
            Vector3 fallbackDir = (toPlayer + right * sideSign * 0.4f).normalized;
            try { c.SetMoveInput(fallbackDir * 0.65f); } catch { }
        }

        /// <summary>이동 방향 바로 앞(지정 거리 이내)에 장애물이 있으면 true. 플레이어도 가까우면 장애물로 간주해 겹침 방지. 경로 추종 중 벽에 낑기기 전에 경로 끊기용.</summary>
        private static bool IsObstacleInMoveDirection(CharacterMainControl c, Vector3 moveDirFlat, float maxDist)
        {
            if (c == null || moveDirFlat.sqrMagnitude < 0.01f || maxDist <= 0f) return false;
            // 플레이어를 장애물로: 이동 방향이 플레이어 쪽이고 거리 이내면 "앞에 장애물 있음"으로 간주
            var main = CharacterMainControl.Main;
            if (main != null && c != main)
            {
                Vector3 toPlayer = main.transform.position - c.transform.position;
                toPlayer.y = 0f;
                float distToPlayer = toPlayer.magnitude;
                if (distToPlayer < PlayerAsObstacleDist && distToPlayer > 0.01f)
                {
                    toPlayer.Normalize();
                    Vector3 moveDir = moveDirFlat.normalized;
                    if (Vector3.Dot(moveDir, toPlayer) > 0.25f)
                        return true;
                }
            }
            if (!TryGetObstacleLayerMask(out int obstacleMask)) return false;
            Vector3 origin = c.transform.position + Vector3.up * CoverRaycastOriginHeight;
            Vector3 lowOrigin = c.transform.position + Vector3.up * CoverRaycastOriginHeightLow;
            Vector3 dir = moveDirFlat.normalized;
            bool hitIsPlayer(RaycastHit rh) => rh.collider != null && CharacterMainControl.Main != null && rh.collider.transform.IsChildOf(CharacterMainControl.Main.transform);
            if (Physics.Raycast(origin, dir, out RaycastHit hit1, maxDist, obstacleMask, QueryTriggerInteraction.Ignore) && !hitIsPlayer(hit1))
                return true;
            if (Physics.Raycast(lowOrigin, dir, out RaycastHit hit2, maxDist, obstacleMask, QueryTriggerInteraction.Ignore) && !hitIsPlayer(hit2))
                return true;
            if (maxDist > CoverSphereCastRadius && Physics.SphereCast(origin, CoverSphereCastRadius, dir, out RaycastHit sh1, maxDist, obstacleMask, QueryTriggerInteraction.Ignore) && !hitIsPlayer(sh1))
                return true;
            if (maxDist > CoverSphereCastRadius && Physics.SphereCast(lowOrigin, CoverSphereCastRadius, dir, out RaycastHit sh2, maxDist, obstacleMask, QueryTriggerInteraction.Ignore) && !hitIsPlayer(sh2))
                return true;
            return false;
        }

        /// <summary>두 점 사이 직선에 장애물이 있는지(레이 또는 몸통 SphereCast에 막히는지) 반환. AI→플레이어 직선 접근 시 우회 필요 여부 판단에 사용. 좌우 오프셋 레이로 위치/각도 차이에 따른 감지 누락을 줄임.</summary>
        private static bool IsObstacleBetween(Vector3 from, Vector3 to)
        {
            if (!TryGetObstacleLayerMask(out int obstacleMask)) return false;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.5f) return false;
            dir.Normalize();
            float rayDist = Mathf.Min(dist - 0.3f, CoverRaycastMaxDist);
            if (rayDist <= 0f) return false;
            Vector3 baseOrigin = from + Vector3.up * CoverRaycastOriginHeight;
            Vector3 lowOrigin = from + Vector3.up * CoverRaycastOriginHeightLow;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            if (right.sqrMagnitude < 0.01f) right = Quaternion.AngleAxis(90f, Vector3.up) * dir;
            right.Normalize();
            float off = Mathf.Min(CoverLateralRayOffset, rayDist * 0.5f);
            if (Physics.Raycast(baseOrigin, dir, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                return true;
            if (Physics.Raycast(lowOrigin, dir, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                return true;
            if (off > 0.01f)
            {
                if (Physics.Raycast(baseOrigin + right * off, dir, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                    return true;
                if (Physics.Raycast(baseOrigin - right * off, dir, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                    return true;
                if (Physics.Raycast(lowOrigin + right * off, dir, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                    return true;
                if (Physics.Raycast(lowOrigin - right * off, dir, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                    return true;
            }
            if (rayDist > CoverSphereCastRadius && Physics.SphereCast(baseOrigin, CoverSphereCastRadius, dir, out _, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                return true;
            if (rayDist > CoverSphereCastRadius && Physics.SphereCast(lowOrigin, CoverSphereCastRadius, dir, out _, rayDist, obstacleMask, QueryTriggerInteraction.Ignore))
                return true;
            // 레이/SphereCast에 안 걸리지만 총알을 막는 장애물(우회 대상) 보조: 구간 내 OverlapSphere
            if (AdaptiveAISettings.CoverDetectOverlapSphereEnabled && AdaptiveAISettings.CoverOverlapSphereLayerMask != 0)
            {
                int mask = AdaptiveAISettings.CoverOverlapSphereLayerMask;
                float radius = Mathf.Max(0.2f, AdaptiveAISettings.CoverOverlapSphereRadius);
                if (_coverOverlapSphereBuffer == null || _coverOverlapSphereBuffer.Length < CoverOverlapSphereBufferSize)
                    _coverOverlapSphereBuffer = new Collider[CoverOverlapSphereBufferSize];
                for (int i = 1; i <= 3; i++)
                {
                    Vector3 sampleOrigin = from + dir * (rayDist * i / 4f);
                    int n = Physics.OverlapSphereNonAlloc(sampleOrigin, radius, _coverOverlapSphereBuffer, mask, QueryTriggerInteraction.Ignore);
                    for (int j = 0; j < n; j++)
                    {
                        Collider c = _coverOverlapSphereBuffer[j];
                        if (c == null || !c.enabled) continue;
                        float d = (c.ClosestPoint(from) - from).magnitude;
                        if (d > 0.5f && d < dist - 0.2f) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>게임 설정의 벽·반쯤 장애물 레이어 마스크. CoverIncludeDamageReceiverLayer면 DamageReceiver(부서지는 벽 등) 병합. CoverBulletBlockerLayerMask가 0이 아니면 병합. (게임 로드 전이면 0)</summary>
        private static bool TryGetObstacleLayerMask(out int layerMask)
        {
            layerMask = 0;
            try
            {
                var layers = GameplayDataSettings.Layers;
                layerMask = (int)layers.wallLayerMask | (int)layers.halfObsticleLayer;
                if (AdaptiveAISettings.CoverIncludeDamageReceiverLayer)
                    layerMask |= (int)layers.damageReceiverLayerMask;
                if (AdaptiveAISettings.CoverBulletBlockerLayerMask != 0)
                    layerMask |= AdaptiveAISettings.CoverBulletBlockerLayerMask;
                return layerMask != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
