using UnityEngine;
using Duckov;
using AdaptiveEnemyAI.Patches;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Systems
{
    /// <summary>
    /// Sync move direction to aim in LateUpdate. Runs at frame end so same-frame movement is reflected in facing; mitigates backpedal cause (move/aim timing mismatch).
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public class MovementAimSyncRunner : MonoBehaviour
    {
        private void LateUpdate()
        {
            // Skip all during scene change/loading or in base (avoid lag before map load)
            if (!LevelManager.LevelInited || LevelManager.Instance == null || PlayerBehaviorCollector.IsInBase())
                return;

            // Post-notice combat: real-time player position tracking (always, regardless of settings)
            CharacterMainControlSetMoveInputPatches.ApplyPlayerAimSyncForNoticed();
            // After patrol path broken (noticed, aggro player, no path): inject approach move (otherwise move stays 0)
            CharacterMainControlSetMoveInputPatches.ApplyApproachWhenNoticedAndNoPath();
            // Force orbit move on enemies near player with move=0 (prevents standing still when SetMoveInput not called e.g. path arrived, shooting)
            CharacterMainControlSetMoveInputPatches.ApplyOrbitWhenCloseAndStandingStill();
            // Melee: force run every frame when approaching player (original may not call SetRunInput)
            CharacterMainControlSetMoveInputPatches.ApplyMeleeRunWhenApproaching();
            // Move direction → aim sync only when setting enabled
            if (AdaptiveAISettings.FaceMovementDirectionWhenApproaching)
                CharacterMainControlSetMoveInputPatches.ApplyMovementAimSyncToAllStored();
        }
    }
}
