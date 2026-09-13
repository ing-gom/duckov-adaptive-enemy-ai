using UnityEngine;
using AdaptiveEnemyAI.Data;
using AdaptiveEnemyAI.Patches;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Services
{
    /// <summary>
    /// Test debug overlay. F9=toggle, F10=extreme aggression (0.8), F11=extreme caution (0.2), F12=clear.
    /// Aggression 0~1 (0=cautious, 1=aggressive). Head label shows cached 0~1.
    /// </summary>
    public sealed class DebugOverlay : MonoBehaviour
    {
        private bool _visible;
        private const float OverlayWidth = 320f;
        private const float LineHeight = 20f;
        private const float Pad = 8f;
        /// <summary>Height offset for head label (relative to character root).</summary>
        private const float HeadLabelHeight = 3.2f;
        private const float HeadLabelWidth = 56f;
        private const float HeadLabelHeightPx = 22f;
        /// <summary>Dodge reaction delay label is shown just below aggression label.</summary>
        private const float HeadLabelDelayOffsetY = 24f;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9)) _visible = !_visible;
            if (Input.GetKeyDown(KeyCode.F8)) AICharacterControllerPatches.DebugLogIncomingDodge = !AICharacterControllerPatches.DebugLogIncomingDodge;
            if (Input.GetKeyDown(KeyCode.F6)) AdaptiveAISettings.DebugLogDash = !AdaptiveAISettings.DebugLogDash;
            if (Input.GetKeyDown(KeyCode.F5)) CharacterMainControlSetMoveInputPatches.DebugLogAimTracking = !CharacterMainControlSetMoveInputPatches.DebugLogAimTracking;
#if DEBUG
            if (Input.GetKeyDown(KeyCode.F7)) CharacterPresetListHelper.LogAllCharacterPresetsToConsole(forceRefresh: true);
            if (Input.GetKeyDown(KeyCode.F10)) AICharacterControllerPatches.DebugAggressionOverride = 0.8f;
            if (Input.GetKeyDown(KeyCode.F11)) AICharacterControllerPatches.DebugAggressionOverride = 0.2f;
            if (Input.GetKeyDown(KeyCode.F12)) AICharacterControllerPatches.DebugAggressionOverride = 0f;
#endif
        }

        private void OnGUI()
        {
#if !DEBUG
            return;
#endif
            if (!_visible) return;

            float h = LineHeight * 28f + Pad * 2f;
            float x = Pad;
            float y = (Screen.height - h) * 0.5f;
            GUI.Box(new Rect(x, y, OverlayWidth, h), "");
            GUILayout.BeginArea(new Rect(x + Pad, y + Pad, OverlayWidth - Pad * 2f, h - Pad * 2f));

            GUI.color = Color.cyan;
            GUILayout.Label("[ Adaptive Enemy AI — Test ]");
            GUI.color = Color.white;
            GUILayout.Label("F5: Aim track  F6: Approach dash  F7: Preset  F8: Dodge  F9: Overlay");
            GUILayout.Label("F10: Extreme aggression (0.8)  F11: Extreme caution (0.2)  F12: Clear");
            bool dashLog = GUILayout.Toggle(AdaptiveAISettings.DebugLogDash, " F6: Approach dash (canDash)·turret block debug log (console)");
            if (dashLog != AdaptiveAISettings.DebugLogDash)
                AdaptiveAISettings.DebugLogDash = dashLog;
            bool dodgeLog = GUILayout.Toggle(AICharacterControllerPatches.DebugLogIncomingDodge, " F8: Dodge/dash debug log (console)");
            if (dodgeLog != AICharacterControllerPatches.DebugLogIncomingDodge)
                AICharacterControllerPatches.DebugLogIncomingDodge = dodgeLog;
            bool preferredRetreatLog = GUILayout.Toggle(CharacterMainControlSetMoveInputPatches.DebugLogPreferredRangeRetreat, " Preferred range retreat log (console, 1.5s throttle)");
            if (preferredRetreatLog != CharacterMainControlSetMoveInputPatches.DebugLogPreferredRangeRetreat)
                CharacterMainControlSetMoveInputPatches.DebugLogPreferredRangeRetreat = preferredRetreatLog;
            bool aimTrackingLog = GUILayout.Toggle(CharacterMainControlSetMoveInputPatches.DebugLogAimTracking, " F5: Aim tracking log (console, 0.6s throttle per AI)");
            if (aimTrackingLog != CharacterMainControlSetMoveInputPatches.DebugLogAimTracking)
                CharacterMainControlSetMoveInputPatches.DebugLogAimTracking = aimTrackingLog;
            bool meleeWeaponLog = GUILayout.Toggle(AdaptiveAISettings.DebugLogMeleeWeapon, " Melee weapon·Attack() decision log (console, 2s throttle per AI)");
            if (meleeWeaponLog != AdaptiveAISettings.DebugLogMeleeWeapon)
                AdaptiveAISettings.DebugLogMeleeWeapon = meleeWeaponLog;
            bool skipAttackPatches = GUILayout.Toggle(AdaptiveAISettings.SkipAttackLogicPatchesGlobally, " Skip attack logic patches (global: all AI use original attack logic)");
            if (skipAttackPatches != AdaptiveAISettings.SkipAttackLogicPatchesGlobally)
                AdaptiveAISettings.SkipAttackLogicPatchesGlobally = skipAttackPatches;
            bool drawEnemySound = GUILayout.Toggle(AdaptiveAISettings.DebugDrawEnemySoundRange, " Draw enemy sound range (cyan circle, need Gizmos for Game view)");
            if (drawEnemySound != AdaptiveAISettings.DebugDrawEnemySoundRange)
                AdaptiveAISettings.DebugDrawEnemySoundRange = drawEnemySound;
            bool drawPlayerSound = GUILayout.Toggle(AdaptiveAISettings.DebugDrawPlayerSoundRange, " Draw player sound range (gunfire/shot radius)");
            if (drawPlayerSound != AdaptiveAISettings.DebugDrawPlayerSoundRange)
                AdaptiveAISettings.DebugDrawPlayerSoundRange = drawPlayerSound;
            bool drawEnemySight = GUILayout.Toggle(AdaptiveAISettings.DebugDrawEnemySightRange, " Draw enemy sight range (sightDistance circle)");
            if (drawEnemySight != AdaptiveAISettings.DebugDrawEnemySightRange)
                AdaptiveAISettings.DebugDrawEnemySightRange = drawEnemySight;
            bool requireLos = GUILayout.Toggle(AdaptiveAISettings.RequireLineOfSightForAggro, " Require line of sight (no notice/chase/attack through walls)");
            if (requireLos != AdaptiveAISettings.RequireLineOfSightForAggro)
                AdaptiveAISettings.RequireLineOfSightForAggro = requireLos;
            bool logSight = GUILayout.Toggle(AdaptiveAISettings.DebugLogSight, " Log sight checks");
            if (logSight != AdaptiveAISettings.DebugLogSight)
                AdaptiveAISettings.DebugLogSight = logSight;
            GUILayout.Space(4f);

            float effAgg = AICharacterControllerPatches.GetDebugEffectiveAggression();
            bool overridden = AICharacterControllerPatches.DebugAggressionOverride != 0f;
            if (overridden)
            {
                GUI.color = Color.yellow;
                float override01 = Mathf.Clamp01(AICharacterControllerPatches.DebugAggressionOverride);
                GUILayout.Label($"*** Test override: {override01:0.00} (0=cautious 1=aggressive) ***");
                GUI.color = Color.white;
            }

            GUILayout.Label($"Applied aggression: {effAgg:0.00} (0=cautious 1=aggressive, reaction/move adjusted)");
            {
                // Shows how much the adaptive score actually feeds into reaction speed.
                float axisMult = 1f, shortBoost = 0f;
                if (AdaptiveAISettings.MultiAxisEnabled)
                {
                    var ma = PlayerBehaviorCollector.GetMultiAxisProfile();
                    if (ma != null && ma.IsValid) axisMult = ma.GetReactionTimeMult();
                }
                if (AdaptiveAISettings.ShortTermPatternEnabled)
                {
                    var det = PlayerBehaviorCollector.GetShortTermDetector();
                    if (det != null) shortBoost = det.ReactivityBoost;
                }
                GUILayout.Label($"Reaction adj: multi-axis x{axisMult:0.00}  short-term -{shortBoost * 100f:0}%  (hesitation after spotting {AdaptiveAISettings.SightReactionDelayMin:0.00}~{AdaptiveAISettings.SightReactionDelayMax:0.00}s)");
            }
            GUILayout.Space(4f);

            if (PlayerLoadoutService.IsValid)
            {
                var p = PlayerLoadoutService.Current;
                GUILayout.Label($"[ Loadout ] range={p.WeaponRange:F0} melee={p.IsMelee} armor={p.TotalArmor:F0}");
                GUILayout.Label($"  Loadout factor (internal): {p.GetAggressionFactor():+0.00} → mapped to 0~1");
            }
            else
                GUILayout.Label("[ Loadout ] not collected");

            var behavior = PlayerBehaviorCollector.GetCurrentSummary();
            if (behavior != null)
            {
                GUILayout.Label($"[ Behavior ] moveEMA={behavior.MoveStrengthEma:F2} runEMA={behavior.RunRatioEma:F2}");
                GUILayout.Label($"  dash/min={behavior.DashCountPerMin:F1} shoot/min={behavior.ShootCountPerMin:F1}");
                if (behavior.HasCombatSamplesP2E || behavior.HasCombatSamplesE2P)
                    GUILayout.Label($"  [Combat] PvE dist={behavior.PlayerToEnemyAvgDistanceEma:F1}m hits/min={behavior.PlayerToEnemyHitsPerMin:F0} crit/min={behavior.PlayerToEnemyCritsPerMin:F0}  EvP hits/min={behavior.EnemyToPlayerHitsPerMin:F0}");
                float behaviorFactor = behavior.GetBehaviorAggressionFactor();
                float behavior01 = Mathf.Clamp01((behaviorFactor + 0.5f));
                GUILayout.Label($"  Behavior factor: {behaviorFactor:+0.00} → 0~1: {behavior01:0.00}");
            }
            else
                GUILayout.Label("[ Behavior ] collecting");

            GUILayout.Space(4f);
            DrawNearestEnemyPreferredRange();
            GUILayout.Space(4f);
            GUILayout.Label("→ Enemy reaction/tracking/move adjusted in real time by above aggression");

            GUILayout.EndArea();

            DrawAggressionLabelsAboveHeads();
        }

        /// <summary>Shows distance, weapon, preferred-range modifier (distMod) for nearest enemy. For weapon matchup/preferred range testing.</summary>
        private void DrawNearestEnemyPreferredRange()
        {
            var main = CharacterMainControl.Main;
            if (main == null) return;
            Vector3 playerPos = main.transform.position;
            playerPos.y = 0f;

            global::AICharacterController nearest = null;
            float nearestDistSq = float.MaxValue;
            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai?.transform == null) return;
                Vector3 p = ai.transform.position;
                p.y = 0f;
                float dSq = (p - playerPos).sqrMagnitude;
                if (dSq < nearestDistSq) { nearestDistSq = dSq; nearest = ai; }
            });
            if (nearest == null) { GUILayout.Label("[ Preferred range ] No nearby enemy"); return; }

            float dist = Mathf.Sqrt(nearestDistSq);
            var enemy = AICharacterControllerPatches.GetEnemyLoadout(nearest);
            float distMod = WeaponPreferredRange.GetDistanceAggressionModifier(enemy.GunTypeTag, dist);
            string gunLabel = string.IsNullOrEmpty(enemy.GunTypeTag) ? "?" : enemy.GunTypeTag.Replace("Tag_GunType_", "");
            GUILayout.Label($"[ Preferred range ] Nearest enemy: {dist:F1}m");
            GUILayout.Label($"  weapon={gunLabel}  distMod={distMod:+0.00} (in range:+, far:-, over cap:-)");
        }

        /// <summary>Draws aggression value above each AI head when debug display is on.</summary>
        private void DrawAggressionLabelsAboveHeads()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            var prevColor = GUI.color;
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 0.7f);

            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                var t = ai.transform;
                if (t == null) return;

                // Show aggression fixed at spawn (cached). GetEffectiveAggressionFor varies per player so not used here.
                float agg = AICharacterControllerPatches.GetCachedAggressionFor(ai);
                Vector3 worldPos = t.position + Vector3.up * HeadLabelHeight;
                Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
                if (screenPos.z <= 0f) return;

                float guiX = screenPos.x - HeadLabelWidth * 0.5f;
                float guiY = Screen.height - screenPos.y - HeadLabelHeightPx * 0.5f;
                var rect = new Rect(guiX, guiY, HeadLabelWidth, HeadLabelHeightPx);

                GUI.color = Color.Lerp(Color.blue, Color.red, agg);
                GUI.Box(rect, "");
                GUI.color = Color.white;
                GUI.Label(rect, $"{agg:0.00}", CenteredLabel);

                // Dodge reaction delay: must wait N seconds after threat before dodge. If remaining time > 0, show above head (countdown).
                if (AICharacterControllerPatches.TryGetRemainingDodgeReactionDelay(ai, out float remaining, out float total) && remaining > 0f)
                {
                    var delayRect = new Rect(guiX, guiY + HeadLabelDelayOffsetY, HeadLabelWidth, HeadLabelHeightPx);
                    GUI.backgroundColor = new Color(0.2f, 0.2f, 0f, 0.85f);
                    GUI.Box(delayRect, "");
                    GUI.backgroundColor = prevBg;
                    GUI.color = Color.yellow;
                    GUI.Label(delayRect, $"{remaining:0.2f}s", CenteredLabel);
                    GUI.color = Color.white;
                }
            });

            GUI.color = prevColor;
            GUI.backgroundColor = prevBg;
        }

        private static GUIStyle _centeredLabel;

        private static GUIStyle CenteredLabel
        {
            get
            {
                if (_centeredLabel == null)
                {
                    _centeredLabel = new GUIStyle(GUI.skin.label);
                    _centeredLabel.alignment = TextAnchor.MiddleCenter;
                    _centeredLabel.fontSize = Mathf.Max(10, (int)(Screen.height * 0.018f));
                }
                return _centeredLabel;
            }
        }
    }
}
