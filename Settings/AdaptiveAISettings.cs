namespace AdaptiveEnemyAI.Settings
{
    /// <summary>
    /// Adaptive AI tuning settings. Centralizes constants so values can be adjusted without code changes.
    /// Phase 1: static settings + defaults. config.ini / options UI integration possible later.
    /// </summary>
    public static class AdaptiveAISettings
    {
        /// <summary>Loadout vs behavior ratio (0~1). This value = loadout weight, (1-this) = behavior weight. Lower = more emphasis on player behavior pattern.</summary>
        public static float LoadoutWeight { get; set; } = 0.25f;

        /// <summary>If true, weapon matchup affects behavior params (reaction time, dodge chance, fire rate, etc.). If false, only player behavior pattern is used.</summary>
        public static bool LoadoutAffectsBehaviorParams { get; set; } = true;

        /// <summary>Global AI strength scale. Multiplied with all Strength factors to scale overall correction strength.</summary>
        public static float GlobalAIScale { get; set; } = 1f;

        /// <summary>Global minimum reaction time (seconds) after all multipliers. Prevents unrealistically fast reactions from multiplicative feature stacking. Default 0.25.</summary>
        public static float ReactionTimeGlobalFloor { get; set; } = 0.25f;

        /// <summary>Projectile dodge chance curve: when agg(0~1)=this value chance=Min, when agg=1 Max. Default 0.</summary>
        public static float IncomingDodgeAggressionThreshold { get; set; } = 0f;
        /// <summary>Projectile dodge chance lower bound (when agg is low, cautious enemy).</summary>
        public static float IncomingDodgeChanceMin { get; set; } = 0.25f;

        /// <summary>Projectile dodge chance upper bound (when agg is high, aggressive enemy).</summary>
        public static float IncomingDodgeChanceMax { get; set; } = 0.5f;
        /// <summary>Projectile dodge final chance cap (0~1). Final chance is capped by this value. Lower = less frequent rolling. Default 0.55. (When using closest-approach basis, up to 1 allowed)</summary>
        public static float IncomingDodgeChanceCap { get; set; } = 0.55f;
        /// <summary>Projectile dodge final chance floor (0~1). Final chance is kept at least this value. Default 0.1.</summary>
        public static float IncomingDodgeChanceFloor { get; set; } = 0.1f;
        /// <summary>Closest-approach-based dodge: base probability when closest approach is at threshold(1m). Closer to 0 = converges to 100%. Default 0.85.</summary>
        public static float IncomingDodgeChanceBaseWhenClosest { get; set; } = 0.85f;
        /// <summary>Whether to apply dodge chance cap by player hit rate pattern. If true, dodge is capped (dodge always relaxed) for all phases including first shot.</summary>
        public static bool DodgeChanceCapByPlayerHitRate { get; set; } = true;
        /// <summary>Pattern-based dodge cap minimum (0~1). Applied when hit rate is low. Also caps at or below this when no samples. Default 0.45.</summary>
        public static float DodgeChanceCapMin { get; set; } = 0.45f;
        /// <summary>Pattern-based dodge cap maximum (0~1). Applied when hit rate is high. Lower = less dodge even vs accurate players. Default 0.7.</summary>
        public static float DodgeChanceCapMax { get; set; } = 0.7f;
        /// <summary>Whether to vary dodge chance by player pattern (hit rate, fire style). If true, cap is kept and only the curve is shifted (sniper=dodge up, spray=dodge down).</summary>
        public static bool DodgeChanceScaleByPlayerPattern { get; set; } = true;
        /// <summary>Pattern scale minimum (0.85 = less dodge when spray/low threat). Only when DodgeChanceScaleByPlayerPattern is used.</summary>
        public static float DodgeChancePatternScaleMin { get; set; } = 0.85f;
        /// <summary>Pattern scale maximum (1.15 = more dodge when aim-focused/high hit rate). Only when DodgeChanceScaleByPlayerPattern is used.</summary>
        public static float DodgeChancePatternScaleMax { get; set; } = 1.15f;
        /// <summary>Whether to also reflect dash/aim variance from player pattern. If true, DashCountPerMin and AimChangeVariance fine-tune dodge multiplier (diversify dodge response vs aggressive/mobile players).</summary>
        public static bool DodgeChancePatternUseDashAim { get; set; } = false;

        // ---- Fire pattern (burst vs single) link: relax dodge when single shot / no pattern ----
        /// <summary>Whether to adjust dodge chance by player fire pattern (burst vs single). If true, dodge multiplier is reduced for single shot / no pattern (less reaction to first/single shot).</summary>
        public static bool DodgeChanceScaleByShootPattern { get; set; } = true;
        /// <summary>Dodge chance multiplier (0~1) when single shot (not burst, no pattern data). Lower = less dodge on first/single shot. Only when DodgeChanceScaleByShootPattern. Default 0.55.</summary>
        public static float DodgeChanceMultWhenSingleShot { get; set; } = 0.55f;

        // ---- First threat in this engagement for this AI (relax dodge on first hit) ----
        /// <summary>Whether to temporarily lower dodge chance only when this is the first threat detected for this AI in this engagement.</summary>
        public static bool DodgeFirstThreatScaleEnabled { get; set; } = true;
        /// <summary>Dodge chance multiplier (0~1) on first threat. Only the first shot uses this; afterward normal. Only when DodgeFirstThreatScaleEnabled. Default 0.55.</summary>
        public static float DodgeFirstThreatScale { get; set; } = 0.55f;

        // ---- Dodge cap ramp after combat start (low before/first hit, rises per second during combat) ----
        // To reduce overly reactive dodge in long fights, lower RampPerSecond or RampMax.
        /// <summary>Whether to use combat ramp. If true, dodge cap is 50% before/just after combat, then increases per second up to RampMax.</summary>
        public static bool DodgeChanceCombatRampEnabled { get; set; } = true;
        /// <summary>Dodge chance cap (0~1) before combat / right after combat start. Applied when player not spotted or right after combat starts. Default 0.5.</summary>
        public static float DodgeChancePreCombatCap { get; set; } = 0.5f;
        /// <summary>Per-second increase (0~1) of dodge cap after combat start. Lower = less dodge ramp in long engagements. Default 0.03 (3%).</summary>
        public static float DodgeChanceCombatRampPerSecond { get; set; } = 0.03f;
        /// <summary>Final dodge cap (0~1) when combat ramp is applied. Rises with time up to this value. Lower = less rolling in long fights. Default 0.52.</summary>
        public static float DodgeChanceCombatRampMax { get; set; } = 0.52f;

        // ---- Reduce dodge attempt chance when not noticed (only when player not detected) ----
        // The following values apply to the chance of actually attempting dodge, not the 'dodge chance' number.
        /// <summary>Whether to apply extra reduction to dodge attempt chance only when player is not noticed (noticed=false).</summary>
        public static bool DodgeScaleWhenNotNoticedEnabled { get; set; } = true;
        /// <summary>Dodge attempt scale minimum (0~1) when not noticed. Applied when not noticed and low aggression. Lower = fewer dodge attempts. Default 0.08.</summary>
        public static float DodgeScaleWhenNotNoticedMin { get; set; } = 0.08f;
        /// <summary>Dodge attempt scale maximum (0~1) when not noticed. Applied when not noticed and high aggression. Default 0.2.</summary>
        public static float DodgeScaleWhenNotNoticedMax { get; set; } = 0.2f;

        /// <summary>Suppression: dodge chance multiplier when hit by player within recent N seconds. &gt;1 = more dodge (suppressed, dodges more).</summary>
        public static float DodgeChanceMultWhenSuppressed { get; set; } = 1.2f;
        /// <summary>Suppression: dodge chance multiplier when enemy is currently shooting. &lt;1 = less dodge (dodge less while firing).</summary>
        public static float DodgeChanceMultWhenShooting { get; set; } = 0.7f;
        /// <summary>Cover: dodge chance multiplier when there is obstacle toward target (hasObsticleToTarget). &lt;1 = less dodge (in cover). When using cover block, this scale applies only at low aggression.</summary>
        public static float DodgeChanceMultWhenInCover { get; set; } = 0.85f;
        /// <summary>Whether to block dodge when in cover (obstacle between player and AI). If true, no dodge at high aggression; at low aggression only the scale applies.</summary>
        public static bool InCoverDodgeBlockEnabled { get; set; } = true;
        /// <summary>Aggression threshold (0~1) for blocking dodge in cover. If aggression ≥ this, no dodge in cover (floor). Default 0.5.</summary>
        public static float InCoverDodgeAggressionThreshold { get; set; } = 0.5f;
        /// <summary>Dodge multiplier (0~1) in cover when aggression is low. When aggression &lt; threshold, chance *= this. Default 0.2.</summary>
        public static float InCoverDodgeChanceMultWhenLowAgg { get; set; } = 0.2f;

        /// <summary>In cover, branch dodge by whether player is shooting. If true, dodge strength is determined by "is player shooting now" instead of aggression (hold hide / allow peek).</summary>
        public static bool CoverDodgeByPlayerShootingEnabled { get; set; } = true;
        /// <summary>Dodge chance cap (0~1) when player shooting + in cover. Lower = stay hidden (roll out less). Default 0.25.</summary>
        public static float InCoverDodgeCapWhenPlayerShooting { get; set; } = 0.25f;
        /// <summary>Dodge multiplier (0~1) when player shooting + in cover. Used when using multiplier instead of cap. Default 0.3.</summary>
        public static float InCoverDodgeMultWhenPlayerShooting { get; set; } = 0.3f;
        /// <summary>Dodge cap (0~1) when player not shooting + in cover. Allows peek out. 0 = not applied (existing scale only). Default 0.7.</summary>
        public static float InCoverDodgeCapWhenPlayerNotShooting { get; set; } = 0.7f;

        /// <summary>Enemy with player not noticed (noticed=false): dodge attempt scale minimum when agg&lt;0.5 (cautious). If aggressive (≥0.5), only Floor applied. Default 0.1.</summary>
        public static float DodgeChanceScaleWhenPlayerNotNoticed { get; set; } = 0.1f;
        /// <summary>Dodge scale interpolation when unnoticed (0~1). Deprecated, kept for config backward compatibility; not read by current logic.</summary>
        public static float DodgeNotNoticedAggressionThreshold { get; set; } = 0.35f;
        /// <summary>Cap (0~1) on dodge attempt chance for enemies that have not yet spotted the player. Lower = unnoticed enemies rarely attempt dodge. Default 0.06 (6%).</summary>
        public static float DodgeChanceMaxWhenPlayerNotNoticed { get; set; } = 0.06f;
        /// <summary>Final multiplier (0~1) for dodge attempt chance for unnoticed (noticed=false) enemies. Applied once more after all above. Lower = unnoticed enemies rarely attempt. Default 0.12.</summary>
        public static float DodgeChanceMultWhenPlayerNotNoticed { get; set; } = 0.12f;

        // ---- 플레이어 익숙도에 따른 구르기/에임 완화 스케일 (익숙해질수록 상한까지 도달) ----
        /// <summary>If true, dodge and cover snap-aim use relaxed values at low map familiarity and lerp toward "at max familiarity" caps as player gets familiar with the map. Keeps upper limits unchanged.</summary>
        public static bool DodgeAndAimScaleByPlayerFamiliarity { get; set; } = true;
        /// <summary>Dodge chance cap (0~1) when player familiarity = 1 (upper limit). Lerped from IncomingDodgeChanceCap. Default 0.7.</summary>
        public static float IncomingDodgeChanceCapAtMaxFamiliarity { get; set; } = 0.7f;
        /// <summary>Pattern-based dodge cap max when familiarity = 1. Lerped from DodgeChanceCapMax. Default 0.85.</summary>
        public static float DodgeChanceCapMaxAtMaxFamiliarity { get; set; } = 0.85f;
        /// <summary>Combat ramp dodge cap max when familiarity = 1. Lerped from DodgeChanceCombatRampMax. Default 0.62.</summary>
        public static float DodgeChanceCombatRampMaxAtMaxFamiliarity { get; set; } = 0.62f;
        /// <summary>Dodge cooldown max/min (s) when familiarity = 1. Lerped from IncomingDodgeCooldownMax/Min. Default 4 / 3.</summary>
        public static float IncomingDodgeCooldownMaxAtMaxFamiliarity { get; set; } = 4f;
        public static float IncomingDodgeCooldownMinAtMaxFamiliarity { get; set; } = 3f;
        public static float IncomingDodgeCooldownBossEliteMaxAtMaxFamiliarity { get; set; } = 3.5f;
        public static float IncomingDodgeCooldownBossEliteMinAtMaxFamiliarity { get; set; } = 2.5f;
        public static float IncomingDodgeCooldownWhenReloadingMaxAtMaxFamiliarity { get; set; } = 5f;
        public static float IncomingDodgeCooldownWhenReloadingMinAtMaxFamiliarity { get; set; } = 3f;
        public static float IncomingDodgeCooldownWhenReloadingBossEliteMaxAtMaxFamiliarity { get; set; } = 3f;
        public static float IncomingDodgeCooldownWhenReloadingBossEliteMinAtMaxFamiliarity { get; set; } = 2f;
        /// <summary>LOS acquisition delay (s) when familiarity = 1. Lerped from AimAcquisitionDelayAfterLOSSeconds. Default 0.25.</summary>
        public static float AimAcquisitionDelayAfterLOSAtMaxFamiliarity { get; set; } = 0.25f;
        /// <summary>Aim tracking reaction speed min/max when familiarity = 1. Lerped from AimTrackingReactionSpeedMin/Max. Default 3 / 18.</summary>
        public static float AimTrackingReactionSpeedMinAtMaxFamiliarity { get; set; } = 3f;
        public static float AimTrackingReactionSpeedMaxAtMaxFamiliarity { get; set; } = 18f;
        /// <summary>Dodge reaction delay (seconds) for unnoticed (noticed=false) enemies. Also used as minimum when noticed. 0 = no delay (original behavior). Default 0.12.</summary>
        public static float DodgeReactionDelayWhenNotNoticed { get; set; } = 0.12f;
        /// <summary>Minimum dodge reaction delay (seconds) when unnoticed. When applying judgment-aggression correction, delay does not go below this when unnoticed. Default 0.5.</summary>
        public static float DodgeReactionDelayWhenNotNoticedMin { get; set; } = 0.5f;
        /// <summary>Whether to scale dodge reaction delay by judgment-aggression. If true, delay decreases with map familiarity (judgment) - aggression.</summary>
        public static bool DodgeReactionDelayScaleByJudgmentEnabled { get; set; } = true;
        /// <summary>Maximum dodge reaction delay (seconds) when unnoticed. Applied when judgment low and aggression high. Only when DodgeReactionDelayScaleByJudgmentEnabled. Default 2.</summary>
        public static float DodgeReactionDelayWhenNotNoticedMax { get; set; } = 2f;
        /// <summary>Maximum dodge reaction delay (seconds) when noticed (noticed=true). Same judgment-aggression correction; delay applied up to this after threat detection. 0 = no delay when noticed. Default 0.25.</summary>
        public static float DodgeReactionDelayWhenNoticedMax { get; set; } = 0.25f;

        // ---- Dodge success chance after attempt (second gate, player fairness) ----
        /// <summary>Base probability (0~1) that a dodge attempt actually executes a dash. Center value before judgment correction. Default 0.5 (50%).</summary>
        public static float DodgeSuccessChance { get; set; } = 0.5f;
        /// <summary>Whether to adjust dodge success chance by judgment (0~2: player behavior + loadout, map familiarity). If true, higher judgment = higher success chance.</summary>
        public static bool DodgeSuccessChanceScaleByJudgmentEnabled { get; set; } = true;
        /// <summary>Dodge success chance (0~1) at judgment 0. Default 0.35.</summary>
        public static float DodgeSuccessChanceAtLowJudgment { get; set; } = 0.35f;
        /// <summary>Dodge success chance (0~1) at judgment 2. Default 0.65.</summary>
        public static float DodgeSuccessChanceAtHighJudgment { get; set; } = 0.65f;
        /// <summary>Dodge success chance (stage 2) floor (0~1). Result is not allowed below this. Default 0.5 (50%).</summary>
        public static float DodgeSuccessChanceFloor { get; set; } = 0.5f;
        /// <summary>Dodge success chance (stage 2) cap (0~1). Result is capped by this. Default 0.9 (90%).</summary>
        public static float DodgeSuccessChanceCap { get; set; } = 0.9f;

        // ---- Dodge cooldown 4~3s range + judgment correction (judgment 0~2 = player behavior + loadout, map familiarity) ----
        /// <summary>Whether to reduce dodge cooldown by judgment (0~2). If true, higher judgment = shorter cooldown.</summary>
        public static bool IncomingDodgeCooldownScaleByJudgmentEnabled { get; set; } = true;
        /// <summary>Dodge cooldown maximum (seconds). Applied at judgment 0. Higher = less frequent rolls. Default 4.25.</summary>
        public static float IncomingDodgeCooldownMax { get; set; } = 4.25f;
        /// <summary>Dodge cooldown minimum (seconds). Applied at judgment 2. Default 3.25.</summary>
        public static float IncomingDodgeCooldownMin { get; set; } = 3.25f;
        /// <summary>Boss/elite dodge cooldown maximum (seconds). At judgment 0. Default 3.5.</summary>
        public static float IncomingDodgeCooldownBossEliteMax { get; set; } = 3.5f;
        /// <summary>Boss/elite dodge cooldown minimum (seconds). At judgment 2. Default 2.5.</summary>
        public static float IncomingDodgeCooldownBossEliteMin { get; set; } = 2.5f;
        /// <summary>Dodge cooldown maximum (seconds) when reloading. At judgment 0. Default 5.</summary>
        public static float IncomingDodgeCooldownWhenReloadingMax { get; set; } = 5f;
        /// <summary>Dodge cooldown minimum (seconds) when reloading. At judgment 2. Default 3.</summary>
        public static float IncomingDodgeCooldownWhenReloadingMin { get; set; } = 3f;
        /// <summary>Boss/elite dodge cooldown maximum (seconds) when reloading. At judgment 0. Default 3.</summary>
        public static float IncomingDodgeCooldownWhenReloadingBossEliteMax { get; set; } = 3f;
        /// <summary>Boss/elite dodge cooldown minimum (seconds) when reloading. At judgment 2. Default 2.</summary>
        public static float IncomingDodgeCooldownWhenReloadingBossEliteMin { get; set; } = 2f;

        /// <summary>Whether enemy AI only treats player-fired projectiles as dodge targets. If true, only PlayerTeamProjectiles (player shots). LOS not required for dodge; bullet detection counts as threat awareness.</summary>
        public static bool IncomingDodgePlayerProjectilesOnly { get; set; } = true;
        /// <summary>Dodge detection range (meters) minimum. At high aggression, only detect within this range (dodge only when close → fewer false dodges). 3~5m recommended.</summary>
        public static float IncomingDodgeDetectionRangeMin { get; set; } = 5f;
        /// <summary>Dodge detection range (meters) maximum. At low aggression, detect up to this range (react from farther).</summary>
        public static float IncomingDodgeDetectionRangeMax { get; set; } = 22f;
        /// <summary>Projectile closest approach distance (meters). Only treat as threat and dodge when passing within this (trajectory that would hit or graze). ~1m (character hitbox) recommended. Larger = react to near-miss rounds.</summary>
        public static float IncomingDodgeClosestApproachMeters { get; set; } = 1f;
        /// <summary>Minimum dot (0~1) between projectile direction and AI direction. Higher = only react to trajectories that would actually hit (0.7≈within 45°, 0.5≈60°).</summary>
        public static float IncomingDodgeDotMin { get; set; } = 0.7f;
        /// <summary>Allowed deviation (degrees) for melee enemy dodge dash toward player. 0=forward only, 45=max ±45° from player direction.</summary>
        public static float MeleeDodgeTowardPlayerMaxAngleDeg { get; set; } = 45f;

        // ---- Prefer direction out of player view when dashing ----
        /// <summary>Whether to favor direction out of player view (side/behind) for dodge/approach/tactical dash. If true, blend dash direction toward out-of-view.</summary>
        public static bool DashPreferOutOfPlayerViewEnabled { get; set; } = true;
        /// <summary>Blend (0~1) between original dodge dash direction (e.g. perpendicular to projectile) and out-of-view direction. 0.5=half, 1=always out of view. Default 0.5.</summary>
        public static float DodgeDashOutOfViewBlend { get; set; } = 0.5f;
        /// <summary>Blend (0~1) of out-of-view component toward player for approach dash and leave-cover dash. 0=forward only, 0.4=slightly sideways. Default 0.4.</summary>
        public static float ApproachDashOutOfViewBlend { get; set; } = 0.4f;
        /// <summary>When judgment ability ≥ this (0~1), probability of dashing behind player during approach dash. 0 = no behind-player dash. Default 0.38.</summary>
        public static float ApproachDashToBackChance { get; set; } = 0.38f;
        /// <summary>Minimum judgment ability (0~1) to attempt behind-player dash. Below this, only forward/out-of-view blend. Default 0.55.</summary>
        public static float ApproachDashToBackJudgmentMin { get; set; } = 0.55f;

        // ---- Distance-based dodge correction (per-weapon far/close hit rate) ----
        /// <summary>Whether to use distance-based dodge correction. e.g. sniper=less dodge at far range, more at close.</summary>
        public static bool DodgeDistanceCorrectionEnabled { get; set; } = true;
        /// <summary>If distance/range ratio is at or below this, treated as "close range". Unset weapons use this value.</summary>
        public static float DodgeDistCloseRatioDefault { get; set; } = 0.25f;
        /// <summary>If distance/range ratio is at or above this, treated as "far range". Unset weapons use this value.</summary>
        public static float DodgeDistFarRatioDefault { get; set; } = 0.55f;
        /// <summary>Dodge multiplier at close range (harder for player to hit → enemy dodges more). Default for unset weapons.</summary>
        public static float DodgeDistMultAtCloseDefault { get; set; } = 1.2f;
        /// <summary>Dodge multiplier at far range (player hits well → less enemy dodge). Default for unset weapons.</summary>
        public static float DodgeDistMultAtFarDefault { get; set; } = 0.8f;

        // ---- Aggression range and per-entity offset ----
        /// <summary>Base aggression clamp absolute value after loadout/matchup/distance correction. Larger = wider aggression range (default 0.6 → 0.85).</summary>
        public static float BaseAggressionRange { get; set; } = 0.85f;
        /// <summary>Clamp absolute value for behavior profile (GetBehaviorAggressionFactor) result. 0 = use fixed 0.5. Larger = more visible aggression spread (aggressive vs conservative).</summary>
        public static float BehaviorAggressionRange { get; set; } = 0.75f;
        /// <summary>Per-entity aggression offset minimum. Each enemy gets one value in [Min, Max]. (Legacy: old ±Range = Min=-0.3, Max=0.3)</summary>
        public static float PerEntityAggressionOffsetMin { get; set; } = -0.3f;
        /// <summary>Per-entity aggression offset maximum.</summary>
        public static float PerEntityAggressionOffsetMax { get; set; } = 0.3f;

        // ---- Reduce clustering near aggression 0 (root fix) ----
        /// <summary>Loadout/matchup aggression sensitivity. 1=original, 1.5=larger absolute value in same situation (neutral stays 0).</summary>
        public static float LoadoutAggressionSensitivity { get; set; } = 1.5f;
        /// <summary>If true, raise judgment more when player has advantage, moderately when disadvantage. If false, original: lower when player advantage.</summary>
        public static bool JudgmentRisesWhenPlayerAdvantage { get; set; } = true;
        /// <summary>Judgment increase multiplier when player has advantage (raise more). 1.3=+30%. Only when JudgmentRisesWhenPlayerAdvantage.</summary>
        public static float PlayerAdvantageJudgmentMultiplier { get; set; } = 1.3f;
        /// <summary>Judgment rise ratio (0~1) when player at disadvantage (moderate rise). Only this fraction applied as positive. Default 0.4.</summary>
        public static float PlayerDisadvantageJudgmentScale { get; set; } = 0.4f;
        /// <summary>Remap exponent for blend result. 1=no change, &lt;1 pushes values near 0 outward (e.g. 0.85).</summary>
        public static float AggressionRemapExponent { get; set; } = 0.85f;
        /// <summary>Minimum absolute value after blend. If non-zero, when |agg|&lt;this, clamp to sign(agg)*this (reduce excessive neutrality). 0 = not applied.</summary>
        public static float MinAbsAggression { get; set; } = 0f;

        // ---- Judgment → scatter (GunScatterMultiplier) correction ----
        /// <summary>Whether to lower GunScatterMultiplier as judgment increases (accuracy up). If true, scatter multiplier reduced by judgment 0~1.</summary>
        public static bool ScatterScaleByJudgmentEnabled { get; set; } = true;
        /// <summary>Scatter multiplier reduction (0~1) at judgment 1. Applied via PercentageMultiply; e.g. 0.25 = up to 25% extra reduction at max judgment. Only when ScatterScaleByJudgmentEnabled.</summary>
        public static float ScatterJudgmentStrength { get; set; } = 0.25f;

        // ---- Behavior input reference constants ----

        /// <summary>Reference for normalizing aim change variance. AimChangeVariance / this then Clamp01.</summary>
        public static float AimVarianceRef { get; set; } = 1.5f;

        /// <summary>Reference for normalizing shots per minute. ShootCountPerMin / this then Clamp01.</summary>
        public static float ShootCountPerMinRef { get; set; } = 60f;

        // ---- Player disadvantage (HP, number of enemies in combat → aggression correction) ----
        /// <summary>Weight of player HP ratio (lower = disadvantage) contribution. (1 - PlayerHealthRatioEma) * this.</summary>
        public static float PlayerDisadvantageHealthWeight { get; set; } = 0.15f;
        /// <summary>Reference for normalizing number of enemies in combat. (Legacy; ignored when EnemyCountAggression is used)</summary>
        public static float EnemyCountNearPlayerRef { get; set; } = 3f;
        /// <summary>Aggression increase per enemy (0~1 scale). e.g. 0.05 = 5% per enemy.</summary>
        public static float EnemyCountAggressionPerEnemy { get; set; } = 0.05f;
        /// <summary>Cap on aggression increase from enemy count (0~1 scale). e.g. 0.5 = max 50%.</summary>
        public static float EnemyCountAggressionCap { get; set; } = 0.5f;
        /// <summary>Weight of number of enemies in combat (many vs one). (Legacy; ignored when EnemyCountAggression is used)</summary>
        public static float PlayerDisadvantageEnemyCountWeight { get; set; } = 0.08f;

        // ---- Movement pattern (player learning → enemy strafe/zigzag) ----

        /// <summary>Whether to apply movement pattern (strafe, zigzag).</summary>
        public static bool MovementPatternEnabled { get; set; } = true;

        /// <summary>Whether to dynamically adjust approach style (straight/zigzag/orbit) ratio by player behavior. If true, more player straight approach → higher enemy zigzag/orbit ratio.</summary>
        public static bool MovementPatternApproachStyleDynamicByPlayer { get; set; } = true;
        /// <summary>Whether to apply zigzag/dodge period/strength multiplier by player weapon type. Auto = shorter period, stronger dodge; single/sniper = longer period.</summary>
        public static bool MovementPatternScaleByPlayerWeapon { get; set; } = true;

        /// <summary>Strafe blend strength (0~1). LateralMoveRatioEma * this for lateral component. Higher = enemy moves more sideways, more threatening.</summary>
        public static float MovementPatternStrafeStrength { get; set; } = 0.6f;
        /// <summary>When approach direction (dot to player) exceeds this, reduce strafe multiplier (avoid parallel approach). 0.5 = less sidestep when approaching forward.</summary>
        public static float MovementPatternApproachStrafeReduceThreshold { get; set; } = 0.5f;
        /// <summary>Multiplier (0~1) for strafe while approaching. 0.5 = half sidestep when approaching.</summary>
        public static float MovementPatternApproachStrafeMultiplier { get; set; } = 0.5f;
        /// <summary>Minimum lateral movement ratio (0~1) when approaching (moving toward player). Even if LateralMoveRatioEma is 0, apply this much zigzag to soften straight approach. 0 = use learned value only.</summary>
        public static float MovementPatternApproachMinStrafe { get; set; } = 0.22f;

        /// <summary>Distance (m) at which to start blending in movement pattern (zigzag, orbit, strafe). Outside this, approach player in straight line only; inside, mix straight + zigzag + orbit. ≤0 = ignore distance (always apply). Default 6.</summary>
        public static float MovementPatternEngageDistance { get; set; } = 6f;

        // ---- Approach style (straight / zigzag / orbit) ----
        /// <summary>Approach style switch period (seconds). Every this long, each enemy picks one of straight/zigzag/orbit. 0 = blend at fixed ratio every frame.</summary>
        public static float ApproachStyleCycleSeconds { get; set; } = 10f;
        /// <summary>Straight approach ratio (0~1). Rest goes to zigzag and orbit.</summary>
        public static float ApproachStyleStraightWeight { get; set; } = 0.25f;
        /// <summary>Zigzag approach ratio (0~1). Of the remainder after straight, this fraction is zigzag, rest is orbit.</summary>
        public static float ApproachStyleZigzagWeight { get; set; } = 0.5f;
        /// <summary>Orbit (approach while circling player) tangent direction ratio (0~1). 0.7 = 70% tangential, 30% toward player.</summary>
        public static float ApproachOrbitalTangentRatio { get; set; } = 0.7f;
        /// <summary>Orbit direction (left/right) switch period (seconds). 0 = use ZigzagCycleBase.</summary>
        public static float ApproachOrbitalDirectionCycle { get; set; } = 1.5f;

        /// <summary>Multiplier (0~1) for blending movement pattern (strafe) on top of waypoint direction during path following. 0.65 = waypoint priority + 65% strafe strength. Higher = more visible sidestep while following path.</summary>
        public static float MovementPatternStrafeWhenPathFollowingMultiplier { get; set; } = 0.65f;
        /// <summary>Multiplier (0~1) for strafe when in path-end segment (ReachedEndOfPath). Reduces sidestep near arrival so the agent stops precisely.</summary>
        public static float MovementPatternStrafeWhenNearPathEndMultiplier { get; set; } = 0.5f;

        /// <summary>Multiplier (0~1) for tactical blend strength from other path sources when not path following. During path following only waypoint + movement pattern apply (tactical blend skipped), so this value is used only when not path following.</summary>
        public static float TacticalBlendWhenPathFollowingMultiplier { get; set; } = 0.7f;

        /// <summary>Run acceleration (RunAcc) multiplier applied only during path following. 1 = default. Tunes responsiveness for path-only movement separately from movement pattern multiplier.</summary>
        public static float PathFollowingRunAccMultiplier { get; set; } = 1f;
        /// <summary>Walk acceleration (WalkAcc) multiplier applied only during path following. 1 = default.</summary>
        public static float PathFollowingWalkAccMultiplier { get; set; } = 1f;

        /// <summary>Zigzag direction switch period (seconds). Left↔right strafe sign flips every this duration. Shorter = more frequent direction changes, harder to predict. Default 0.5 (0.5s per direction → 1s full cycle).</summary>
        public static float MovementPatternZigzagCycleBase { get; set; } = 0.5f;

        /// <summary>Reference for normalizing MoveDirDotVariance. Zigzag period is adjusted by variance/this value (higher variance = shorter period).</summary>
        public static float MovementPatternVarianceRef { get; set; } = 0.4f;

        /// <summary>Whether to scale strafe strength by aggression when movement pattern is active. If true, more aggressive = stronger sidestep.</summary>
        public static bool MovementPatternStrafeScaleByAggression { get; set; } = true;
        /// <summary>Whether to apply per-entity strafe tendency (6-tier personality). If true, each character gets strafe multiplier ± by 0~5 tendency.</summary>
        public static bool MovementPatternStrafeScaleByPersonality { get; set; } = true;
        /// <summary>Multiplier when strafe tendency is 0 (most conservative). Minimum of 6 tiers. Default 0.55.</summary>
        public static float MovementPatternPersonalityMultMin { get; set; } = 0.55f;
        /// <summary>Multiplier when strafe tendency is 5 (most aggressive). Maximum of 6 tiers. Default 1.6.</summary>
        public static float MovementPatternPersonalityMultMax { get; set; } = 1.6f;
        /// <summary>Whether to increase movement pattern (direction change) frequency as player hit rate rises. If true, at high hit rate period is shortened so enemies switch left/right more often to dodge better.</summary>
        public static bool MovementPatternCycleScaleByHitRate { get; set; } = true;
        /// <summary>Period multiplier at 100% hit rate (&lt;1 = shorter period → more frequent direction changes). 0.7 = at high hit rate period becomes 70%, frequency increases. Only when MovementPatternCycleScaleByHitRate is used.</summary>
        public static float MovementPatternCycleMinMultiplierAtHighHitRate { get; set; } = 0.7f;

        /// <summary>Run acceleration (RunAcc) multiplier when movement pattern (strafe, zigzag, soft evasion, counter movement) is active. 1 = default, 1.2 = more threatening dynamic movement.</summary>
        public static float MovementPatternRunAccMultiplier { get; set; } = 1.22f;

        /// <summary>Walk acceleration (WalkAcc) multiplier when movement pattern is active. 1 = default.</summary>
        public static float MovementPatternWalkAccMultiplier { get; set; } = 1f;

        /// <summary>Whether to strengthen strafe/evasion blend at low health. If true, multiplier applies when health ratio is at or below judgment-based threshold (more evasive as they take damage).</summary>
        public static bool MovementPatternStrafeScaleByLowHealth { get; set; } = true;
        /// <summary>Health ratio (0~1) threshold for low-health boost at judgment 0. Boost only when at or below this ratio. Default 0.01 (1%).</summary>
        public static float MovementPatternLowHealthRatioAtJudgmentMin { get; set; } = 0.01f;
        /// <summary>Health ratio (0~1) threshold for low-health boost at max judgment (2). Default 0.5 (50%). Higher judgment = boost kicks in at higher health.</summary>
        public static float MovementPatternLowHealthRatioAtJudgmentMax { get; set; } = 0.5f;
        /// <summary>Multiplier (1.0~1.3) for strafe and soft evasion blend when at low health. Default 1.15.</summary>
        public static float MovementPatternLowHealthStrafeMult { get; set; } = 1.15f;

        /// <summary>Multiplier (0~1) for strafe when retreating (moving away from player). Eases by allowing only lateral movement while backing off. Default 0.7.</summary>
        public static float MovementPatternStrafeWhenRetreatingMultiplier { get; set; } = 0.7f;

        /// <summary>Distance band: whether to strengthen strafe when very close. If true, MovementPatternNearStrafeMultiplier applies within MovementPatternNearDist.</summary>
        public static bool MovementPatternStrafeScaleByDistance { get; set; } = false;
        /// <summary>Close-range distance (m). Strafe is strengthened when within this distance. When MovementPatternStrafeScaleByDistance is used.</summary>
        public static float MovementPatternNearDist { get; set; } = 3f;
        /// <summary>Multiplier (&gt;1) for strafe in close range. Default 1.2. When MovementPatternStrafeScaleByDistance is used.</summary>
        public static float MovementPatternNearStrafeMultiplier { get; set; } = 1.2f;

        /// <summary>Whether to align strafe direction to cover tangent (sidestep along wall) when near cover. If true, on TryGetCoverDirection success right is snapped to cover axis.</summary>
        public static bool MovementPatternAlignStrafeToCover { get; set; } = true;
        /// <summary>Blend ratio (0 = original only, 1 = cover tangent only) when aligning to cover axis. Default 1.</summary>
        public static float MovementPatternCoverAlignBlend { get; set; } = 1f;

        /// <summary>Whether to apply temporary RunAcc/WalkAcc boost on frames when direction changes due to movement pattern. Improves perceived responsiveness when turning sideways.</summary>
        public static bool MovementPatternAccelOnDirectionChange { get; set; } = true;
        /// <summary>Acceleration multiplier (1.0~1.2) on direction change. Default 1.1. When MovementPatternAccelOnDirectionChange is used.</summary>
        public static float MovementPatternAccelOnDirectionChangeMult { get; set; } = 1.1f;
        /// <summary>Direction change detection: if dot between previous and current move direction is below this value, treat as a turn (0.9 ≈ ~25°).</summary>
        public static float MovementPatternDirectionChangeDotThreshold { get; set; } = 0.9f;

        /// <summary>When at or beyond this distance (m) from player and approaching, correct SetRunInput so AI runs. 0 = not applied.</summary>
        public static float RunWhenApproachingFromDistance { get; set; } = 18f;
        /// <summary>Approach detection: if dot between move direction and player direction is at or above this value, treat as "approaching" and apply run-from-distance. Default 0.35.</summary>
        public static float RunWhenApproachingFromDistanceDot { get; set; } = 0.35f;
        /// <summary>When closer than this distance (m) to player, remove approach component from move to prevent overlap/collision. Approach target circle (boundary) also uses this. 0 = not applied (approach distance limit disabled). Default 0.</summary>
        public static float MinDistanceFromPlayer { get; set; } = 0f;
        /// <summary>If true, a gun-armed enemy never closes past its weapon's preferred minimum range (WeaponPreferredRange.OptimalMin: pistol/SMG 2m, shotgun 2.5m, AR 3m, BR/LMG 4m, sniper 5m).
        /// Takes the larger of this and MinDistanceFromPlayer. Melee is unaffected. Without it the only floor is the 1.2m overlap escape, so riflemen walk up and rub against the player. Default true.</summary>
        public static bool MinDistanceFromWeaponPreferredRange { get; set; } = true;
        /// <summary>Scale applied to the weapon's OptimalMin when used as the approach floor. 1 = as tabled. Default 1.</summary>
        public static float MinDistanceWeaponPreferredRangeScale { get; set; } = 1f;
        /// <summary>Whether to use as approach/move target not player position but "this distance (m) behind the direction the player is facing". 0 = use player position (original). 2.5 = use point 2.5m behind player as target center when approaching to reduce overlap. Updated each time movement logic runs, relative to player facing.</summary>
        public static float ApproachTargetOffsetBehindPlayerMeters { get; set; } = 2.5f;

        // ---- Move out of player view (when close / high judgment: move out of sight) ----
        /// <summary>Whether to blend in movement toward out-of-player-view when close (overlap-prevention range). If true, add sidestep-out behavior within ~1m.</summary>
        public static bool MoveOutOfPlayerViewEnabled { get; set; } = true;
        /// <summary>When within this distance (m), apply out-of-view movement blend (eases standing still when close). Default 3.</summary>
        public static float MoveOutOfPlayerViewCloseDist { get; set; } = 3f;
        /// <summary>Blend strength (0~1) toward out-of-view direction in close range. Higher = more pronounced sidestep-out. Default 0.6.</summary>
        public static float MoveOutOfPlayerViewBlendAtClose { get; set; } = 0.6f;
        /// <summary>Whether to do more out-of-player-view movement at mid range as judgment increases. If true, high-judgment AI shows more sight-breaking movement.</summary>
        public static bool MoveOutOfPlayerViewByJudgmentEnabled { get; set; } = true;
        /// <summary>Max distance (m) at which judgment-based out-of-view movement applies. Blend by judgment between CloseDist and this value. Default 3.</summary>
        public static float MoveOutOfPlayerViewJudgmentDistMax { get; set; } = 3f;
        /// <summary>Out-of-view blend cap (0~1) at mid range (CloseDist~JudgmentDistMax) when judgment is high. Default 0.35.</summary>
        public static float MoveOutOfPlayerViewBlendByJudgment { get; set; } = 0.35f;

        // ---- Orbit around player + loiter behind (when close: reduce overlap/stall, harassing movement) ----
        /// <summary>When close, use orbit around player + loiter-behind pattern. Reduces overlap and standing still; maintains distance and attacks even in 360° view.</summary>
        public static bool OrbitAroundPlayerEnabled { get; set; } = true;
        /// <summary>Minimum distance (m) for orbit movement. Below this, orbit+behind blend is applied strongly to avoid stalling. Default 3 (consider weapon length etc. to avoid collision).</summary>
        public static float OrbitDistMin { get; set; } = 3f;
        /// <summary>Maximum distance (m) for orbit movement. Within this distance the agent orbits and loiters around player. Default 3.5.</summary>
        public static float OrbitDistMax { get; set; } = 3.5f;
        /// <summary>Orbit direction (move lateral/back with back to player) blend strength (0~1). Higher = more pronounced loitering. Default 0.55.</summary>
        public static float OrbitBlendStrength { get; set; } = 0.55f;
        /// <summary>Tendency (0~1) to move toward "behind player" during orbit. Higher = more movement loitering behind. Default 0.4.</summary>
        public static float OrbitPreferBehind { get; set; } = 0.4f;
        /// <summary>Minimum move magnitude in close range (within OrbitDistMin). Move at least this much to prevent overlap/stall. Too low can look like oscillation. 0 = not applied. Default 0.58.</summary>
        public static float OrbitMinMoveMagnitude { get; set; } = 0.58f;
        /// <summary>When very close and move is nearly 0 after cap, reinject this magnitude in orbit direction to prevent stall. Default 0.5.</summary>
        public static float OrbitMinMoveAfterCapMagnitude { get; set; } = 0.5f;

        // ---- When within 2m: create distance and move toward player's back ----
        /// <summary>When close, apply movement that creates distance from target and goes toward player's back. If true, applies at or below MoveToPlayerBackWhenCloseDist.</summary>
        public static bool MoveToPlayerBackWhenCloseEnabled { get; set; } = true;
        /// <summary>MoveToPlayerBack apply distance (m). Blend toward behind-player at or below this distance. Real-time distance so movement toward back can start at 4~5m. Default 5.</summary>
        public static float MoveToPlayerBackWhenCloseDist { get; set; } = 5f;
        /// <summary>Blend strength (0~1) toward behind-player. Higher = more clearly creates distance and moves back. Default 0.75.</summary>
        public static float MoveToPlayerBackBlend { get; set; } = 0.75f;

        // ---- Movement pattern under projectile threat (zigzag/diamond evasion when dash unavailable or not triggered) ----

        /// <summary>Whether to use movement pattern (zigzag/diamond) blend instead of dash under projectile threat.</summary>
        public static bool SoftEvasionOnThreatEnabled { get; set; } = true;

        /// <summary>Soft evasion blend cap (0~1). Higher = more pronounced lateral/diagonal movement under ballistic threat.</summary>
        public static float SoftEvasionBlendMax { get; set; } = 0.7f;

        /// <summary>Zigzag approach left/right switch period (seconds). Shorter = more frequent evasion direction changes.</summary>
        public static float SoftEvasionZigzagCycle { get; set; } = 0.75f;

        /// <summary>Diamond step 4-direction switch period (seconds). 0 = use zigzag only.</summary>
        public static float SoftEvasionDiamondCycle { get; set; } = 0.42f;

        /// <summary>If true use diamond step (4 dir), if false use zigzag approach (2 dir) only.</summary>
        public static bool SoftEvasionUseDiamondStep { get; set; } = true;

        // ---- Counter movement to player fire pattern (evade at predicted timing when no projectile) ----

        /// <summary>Whether to use counter movement based on player fire timing pattern (burst / next shot expected).</summary>
        public static bool CounterMoveOnFirePatternEnabled { get; set; } = true;

        /// <summary>Counter blend cap (0~1) when in burst. Applied after aggression scale.</summary>
        public static float CounterMoveBlendWhenBurst { get; set; } = 0.58f;

        /// <summary>Counter blend cap (0~1) when near expected next shot time. Applied after aggression scale.</summary>
        public static float CounterMoveBlendWhenNextShotExpected { get; set; } = 0.48f;

        /// <summary>Time window (seconds) to count as burst. Two or more shots within this = burst.</summary>
        public static float CounterMoveBurstWindow { get; set; } = 0.5f;

        // ---- Tactical dash (disorient player during movement, not bullet evasion) ----
        /// <summary>Whether to use tactical dash (to disrupt player aim/focus) during movement. If true, occasional left/right dash by player pattern.</summary>
        public static bool TacticalDashEnabled { get; set; } = true;
        /// <summary>Tactical dash minimum cooldown (seconds). After last dash (including dodge/approach dash), must wait this long before tactical dash. Default 2.5.</summary>
        public static float TacticalDashCooldownAfterDodge { get; set; } = 2.5f;
        /// <summary>Tactical dash base attempt chance (0~1). After player-pattern multiplier, chance is at or below this. Default 0.18.</summary>
        public static float TacticalDashChanceBase { get; set; } = 0.18f;
        /// <summary>Whether to raise tactical dash chance when player aim is stable (low aim variance). If true, aim-focused players get dashed at more often.</summary>
        public static bool TacticalDashScaleByAimStability { get; set; } = true;
        /// <summary>Whether to raise tactical dash chance when player hit rate is high. If true, accurate players get dashed at more often.</summary>
        public static bool TacticalDashScaleByPlayerHitRate { get; set; } = true;
        /// <summary>Probability multiplier cap when aim is stable. When AimChangeVariance is near 0, chance *= this (max 1.6). When TacticalDashScaleByAimStability is used.</summary>
        public static float TacticalDashMaxMultWhenAimStable { get; set; } = 1.6f;
        /// <summary>Probability multiplier cap at high hit rate. When player→enemy hit rate is high, chance *= this (max 1.4). When TacticalDashScaleByPlayerHitRate is used.</summary>
        public static float TacticalDashMaxMultWhenHighHitRate { get; set; } = 1.4f;
        /// <summary>If true, tactical dash is only attempted when "approaching" or "right after leaving cover". If false, attempt periodically as before (can be frequent). Default true.</summary>
        public static bool TacticalDashOnlyWhenApproachingOrLeavingCover { get; set; } = true;
        /// <summary>If true, approach dash is only attempted when "approaching toward player". If false, attempt by chance when aware. Default true.</summary>
        public static bool ApproachDashOnlyWhenApproaching { get; set; } = true;
        /// <summary>Chance to dash out when blending from cover toward player (right after ApplyLeaveCoverToEngage). Shares cooldown with dodge/approach dash. Default true.</summary>
        public static bool LeaveCoverDashEnabled { get; set; } = true;

        // ---- Post-dash pause (reduce mechanical feel; dash → brief pause → fire pattern) ----
        /// <summary>If true, briefly stop movement right after dash so agent aims/shoots in place. Less mechanical than constant movement. Default true.</summary>
        public static bool PostDashPauseEnabled { get; set; } = true;
        /// <summary>Pause phase starts this many seconds after dash start. Slightly longer than dash animation (e.g. 0.35). Default 0.35.</summary>
        public static float PostDashPauseStartAfter { get; set; } = 0.35f;
        /// <summary>Pause phase duration (seconds). Move input is zero during this. Default 0.25.</summary>
        public static float PostDashPauseDuration { get; set; } = 0.25f;

        // ---- Natural pause and resume (independent of dash; occasional brief stop then move) ----
        /// <summary>If true, apply pattern of occasionally pausing briefly then moving again. Adds rhythm and natural feel, independent of dash. Default true.</summary>
        public static bool MovePauseEnabled { get; set; } = true;
        /// <summary>Pause attempt probability (0~1). Each CheckInterval, enter pause with this chance. Default 0.28.</summary>
        public static float MovePauseChance { get; set; } = 0.28f;
        /// <summary>Pause check interval (seconds). Every this interval, decide whether to pause via MovePauseChance. Default 2.5.</summary>
        public static float MovePauseCheckInterval { get; set; } = 2.5f;
        /// <summary>Minimum pause duration (seconds). Default 0.18.</summary>
        public static float MovePauseMinDuration { get; set; } = 0.18f;
        /// <summary>Maximum pause duration (seconds). Default 0.5.</summary>
        public static float MovePauseMaxDuration { get; set; } = 0.5f;

        /// <summary>Player range advantage limit (m). If player range exceeds enemy range + this, preferred-range retreat is not applied (no endless retreat on gear gap).</summary>
        public static float PreferredRangeRetreatPlayerAdvantageThreshold { get; set; } = 10f;

        // ---- Prevent infinite retreat (reduce endless fleeing by weapon/state) ----
        /// <summary>When AI has range advantage, stand-off (retreat) applies only at or below this health ratio (0~1). Lower = stricter retreat condition, less fleeing (e.g. 0.35 = 35% or below).</summary>
        public static float StandOffHealthRatioMax { get; set; } = 0.35f;
        /// <summary>Blend strength (0~1) for stand-off retreat. Lower = weaker retreat. Higher = more visible "turn and retreat".</summary>
        public static float RetreatBlendStrength { get; set; } = 0.55f;

        // ---- Tactical stance by player pattern (defense vs attack judgment) ----
        /// <summary>Compute defensive stance (0~1) from player pattern (approach/retreat, hit rate, exchange ratio) and apply to approach/retreat and leave cover. If true, use it.</summary>
        public static bool TacticalStanceByPlayerPatternEnabled { get; set; } = true;
        /// <summary>Weight (0~1) of defensive stance on approach blend. Higher = more visible difference: defensive = less approach, aggressive = more rush.</summary>
        public static float DefensiveStanceWeightApproach { get; set; } = 0.65f;
        /// <summary>Weight (0~1) of defensive stance on retreat blend. Higher = more visible difference: defensive = stronger retreat/cover hold.</summary>
        public static float DefensiveStanceWeightRetreat { get; set; } = 0.65f;
        /// <summary>If defensive stance is at or above this value (0~1), LeaveCoverToEngage blend is not applied. Default 0.7.</summary>
        public static float LeaveCoverToEngageDefensiveStanceCap { get; set; } = 0.7f;
        /// <summary>Preferred-range (e.g. shotgun) retreat blend strength (0~1). Lower = weaker preferred-range retreat.</summary>
        public static float PreferredRangeRetreatBlend { get; set; } = 0.25f;

        // ---- Cover approach strength (approach while staying near cover) ----
        /// <summary>When approaching, if there is cover between player and AI, blend strength (0~1) toward that direction. Higher = more visible approach along cover. Default 0.55.</summary>
        public static float CoverApproachBlendStrength { get; set; } = 0.55f;

        // ---- Cover arc scan (obstacle search in left/right directions besides forward) ----
        /// <summary>When forward ray has no cover, cast rays left/right to find cover candidates. If true, use it. Default true.</summary>
        public static bool CoverArcScanEnabled { get; set; } = true;
        /// <summary>Arc scan: half-angle (degrees) left/right from player direction. 90 = ±90° (180° total), 135 = 270° scan. Default 90.</summary>
        public static float CoverArcScanHalfAngleDeg { get; set; } = 90f;
        /// <summary>Arc scan: angle step (degrees) between rays. Smaller = more precise, higher cost. Default 15.</summary>
        public static float CoverArcScanStepDeg { get; set; } = 15f;
        /// <summary>Arc scan: max distance (m) for obstacle search. Only cover within this range is candidate. Default 22.</summary>
        public static float CoverArcScanMaxDist { get; set; } = 22f;

        // ---- Cover assist: obstacles that block bullets but may not be hit by raycast (flanking/cover judgment) ----
        /// <summary>If true, include DamageReceiver layer in cover/shot-block check. Destructible walls etc. may use this layer (block bullets but not default wall layer). Default true.</summary>
        public static bool CoverIncludeDamageReceiverLayer { get; set; } = true;
        /// <summary>Additional layer mask for objects that block bullets but are not on default wall/half-obstacle layers. 0 = not used. Bitmask can include multiple layers.</summary>
        public static int CoverBulletBlockerLayerMask { get; set; } = 0;
        /// <summary>Whether to additionally detect obstacles (not hit by ray/SphereCast/arc) on AI–player segment via OverlapSphere. If true, secondary detection only when CoverOverlapSphereLayerMask is non-zero.</summary>
        public static bool CoverDetectOverlapSphereEnabled { get; set; } = true;
        /// <summary>Layer mask for OverlapSphere secondary detection. 0 = no secondary detection. Include only bullet-blocking and environment layers; exclude character layer.</summary>
        public static int CoverOverlapSphereLayerMask { get; set; } = 0;
        /// <summary>OverlapSphere secondary detection: sample sphere radius (m) along segment. Colliders within this radius are treated as cover candidates. Default 0.5.</summary>
        public static float CoverOverlapSphereRadius { get; set; } = 0.5f;
        /// <summary>OverlapSphere secondary detection: number of samples along AI→player segment. More = more precise, higher cost. Default 6.</summary>
        public static int CoverOverlapSphereSampleCount { get; set; } = 6;

        // ---- Seek cover during combat (move toward cover when exposed and player firing or recently hit) ----
        /// <summary>When player is firing or AI was recently hit, and not yet behind cover, blend movement toward cover. If true, apply.</summary>
        public static bool SeekCoverWhenUnderFireEnabled { get; set; } = true;
        /// <summary>Seek cover: blend strength (0~1) toward cover direction. Default 0.5.</summary>
        public static float SeekCoverWhenUnderFireBlendStrength { get; set; } = 0.5f;
        /// <summary>Seek cover: only performed when judgment (0~1 normalized) is at or above this. Lower = more enemies seek cover. Default 0.25.</summary>
        public static float SeekCoverWhenUnderFireJudgmentMin { get; set; } = 0.25f;

        // ---- Cover wait (when player has advantage, wait in cover then engage when close) ----
        /// <summary>When player has weapon advantage, wait behind cover then approach/fire when in range. If true, apply.</summary>
        public static bool CoverWaitByPlayerAdvantageEnabled { get; set; } = true;
        /// <summary>Cover wait: if player range - AI range &gt; this (m), treat as player advantage. Default 6.</summary>
        public static float CoverWaitPlayerAdvantageThreshold { get; set; } = 6f;
        /// <summary>Cover wait: when player enters this distance (m), stop waiting and encourage approach/fire. Default 12.</summary>
        public static float CoverWaitMaxDistToEngage { get; set; } = 12f;
        /// <summary>Cover wait: blend strength (0~1) toward staying in cover. Default 0.6.</summary>
        public static float CoverWaitBlendStrength { get; set; } = 0.6f;
        /// <summary>Cover wait: move magnitude cap (0~1). Near 0 = stay in place. Default 0.35.</summary>
        public static float CoverWaitMoveMagnitudeCap { get; set; } = 0.35f;

        // ---- Cover ambush wait (after spotting player, wait behind cover expecting approach, then attack when close) ----
        /// <summary>When judgment is high, wait behind cover expecting player to come, then attack when close. If true, apply.</summary>
        public static bool CoverAmbushWaitEnabled { get; set; } = true;
        /// <summary>Cover ambush wait: only when judgment (0~1 normalized) is at or above this. Higher = only smarter enemies wait. Default 0.4.</summary>
        public static float CoverAmbushWaitJudgmentMin { get; set; } = 0.4f;
        /// <summary>Cover ambush wait: only wait when player is at or beyond this distance (m) (too close = already in combat). Default 6.</summary>
        public static float CoverAmbushWaitDistMin { get; set; } = 6f;
        /// <summary>Cover ambush wait: only wait when player is at or within this distance (m) (too far = no reason to wait). Default 22.</summary>
        public static float CoverAmbushWaitDistMax { get; set; } = 22f;
        /// <summary>Cover ambush wait: blend strength (0~1) toward staying in cover. Default 0.55.</summary>
        public static float CoverAmbushWaitBlendStrength { get; set; } = 0.55f;
        /// <summary>Cover ambush wait: move magnitude cap (0~1). Near 0 = stay in place. Default 0.3.</summary>
        public static float CoverAmbushWaitMoveMagnitudeCap { get; set; } = 0.3f;

        // ---- Peeking (hide when player shoots, peek out when they stop) ----
        /// <summary>Peek: blend toward cover when player shooting; peek out when not shooting/reloading. If true, apply.</summary>
        public static bool CoverPeekEnabled { get; set; } = true;
        /// <summary>Peek hide: blend strength (0~1) toward cover when player is shooting. Default 0.7.</summary>
        public static float CoverPeekHideBlendWhenPlayerShooting { get; set; } = 0.7f;
        /// <summary>Peek out: blend strength (0~1) toward player when player not shooting/reloading. Default 0.35.</summary>
        public static float CoverPeekOutBlendWhenPlayerNotShooting { get; set; } = 0.35f;

        // ---- Cover accuracy penalty (prevent instant headshots from cover) ----
        /// <summary>If true, AI in cover has increased scatter (less accurate). Prevents instant headshots while peeking. Default true.</summary>
        public static bool CoverScatterPenaltyEnabled { get; set; } = true;
        /// <summary>Scatter increase (0~1) applied while AI is in cover. Higher = less accurate from cover. Default 0.2.</summary>
        public static float CoverScatterPenaltyStrength { get; set; } = 0.2f;

        // ---- Cover exit aim delay (settling time after leaving cover) ----
        /// <summary>If true, AI has temporarily reduced accuracy right after leaving cover. Simulates aim settling time. Default true.</summary>
        public static bool CoverExitAimDelayEnabled { get; set; } = true;
        /// <summary>Duration (seconds) of reduced accuracy after leaving cover. Scatter decays linearly to normal over this period. Default 0.6.</summary>
        public static float CoverExitAimDelayDuration { get; set; } = 0.6f;
        /// <summary>Peak scatter boost (0~1) at the moment of leaving cover. Decays to 0 over CoverExitAimDelayDuration. Default 0.35.</summary>
        public static float CoverExitAimDelayScatterBoost { get; set; } = 0.35f;

        // ---- Cover when reloading (move to cover between player and AI; more when judgment is high) ----
        /// <summary>When reloading, move to cover between player and AI to hide. Higher judgment = do it more. If true, apply.</summary>
        public static bool CoverToReloadEnabled { get; set; } = true;
        /// <summary>Cover when reloading: blend strength (0~1) toward cover at low judgment. 0 = no cover movement at low judgment. Default 0.</summary>
        public static float CoverToReloadBlendMin { get; set; } = 0f;
        /// <summary>Cover when reloading: blend strength (0~1) toward cover at high judgment. Default 0.65.</summary>
        public static float CoverToReloadBlendMax { get; set; } = 0.65f;
        /// <summary>Move magnitude (0~1) when moving to cover while reloading. Default 0.5.</summary>
        public static float CoverToReloadMoveMagnitude { get; set; } = 0.5f;

        /// <summary>When holding melee weapon, 10% move speed buff for enemy AI. If true, apply.</summary>
        public static bool MeleeHoldSpeedBuffEnabled { get; set; } = true;
        // ---- Block fire when in cover + judgment-based leave cover / reload·recovery ----
        /// <summary>When directly behind cover (hasObsticleToTarget), block starting to fire toward player. If true, apply.</summary>
        public static bool BlockFireWhenInCoverEnabled { get; set; } = true;

        // ---- Global skip for attack logic patches ----
        /// <summary>If true, do not apply attack-logic patches (Attack/StartAction/CA_Attack/melee tree·movement etc.) to any AI; use original game logic only. Takes precedence over PerAIPatchControl exclude logic.</summary>
        public static bool SkipAttackLogicPatchesGlobally { get; set; } = false;

        /// <summary>Melee: whether to behave like original. If true, skip all melee-specific blocking·move removal in Attack/StartAction/IsReady/SetMoveInput (original code). If false, apply mod distance·angle·trigger·stop logic. Default true (original-like).</summary>
        public static bool MeleeUseOriginalBehavior { get; set; } = false;
        /// <summary>Melee: whether to allow trigger at all. If false, block all melee attack start in Attack()/IsReady/StartAction(attackAction) (prevent trigger every cooldown). If true, apply existing distance·angle etc. Default true.</summary>
        public static bool MeleeAttackTriggerEnabled { get; set; } = true;
        /// <summary>Melee: whether to use extra control (MinTimeInRange). Distance/angle checks always apply regardless. false = distance/angle only. true = also apply MinTimeInRange. Attack timing (shootDelay) is not modified by mod. Default false.</summary>
        public static bool MeleeAttackControlEnabled { get; set; } = false;
        /// <summary>Melee: attack start allowed distance = AttackRange * this multiplier (0~1). 1 = only start attack within weapon range (same as original). &lt;1 = must be closer to start.</summary>
        public static float MeleeAttackStartMaxDistanceMultiplier { get; set; } = 1f;
        /// <summary>Melee: attack allowed distance = (AttackRange+0.05f)*this ratio (0~1). Swing allowed only when player is at or within this distance (can swing every cooldown). 1 = same as weapon attack range. &lt;1 = must be closer to swing. Default 1.</summary>
        public static float MeleeAttackStartRangeRatio { get; set; } = 1f;
        /// <summary>Melee: trigger distance ratio (0~1). Attack allowed only within this ratio of distance (prevents cooldown burn at range). Lower = must be closer to swing. Default 1 (used when MeleeAttackControlEnabled).</summary>
        public static float MeleeTriggerDistanceRatio { get; set; } = 1f;
        /// <summary>Melee: minimum time (seconds) target must be in allowed distance+angle before attack allowed. 0 = not applied. Default 0 (allow immediately).</summary>
        public static float MeleeAttackMinTimeInRange { get; set; } = 0f;
        /// <summary>Melee: absolute cap (meters) applied to distance result. Even with AttackRange bug/overestimate, block attack if over this distance. ≤0 = no cap. Default 0 (not applied).</summary>
        public static float MeleeAttackMaxDistanceCap { get; set; } = 0f;
        /// <summary>Melee: clear BT attack tree and have mod call Attack() only when in range (remove trigger). If true, keep combat_Attack_Tree=null and call Attack() every 0.1s only when in range. Default true.</summary>
        public static bool MeleeDriveAttackOurselves { get; set; } = true;
        /// <summary>Melee: block Attack() call at reflection Invoke. Unnecessary when MeleeDriveAttackOurselves is used. Default false.</summary>
        public static bool MeleeBlockAttackAtInvoke { get; set; } = false;
        /// <summary>Melee: set combat_Attack_Tree to null only when out of range. Not used when MeleeDriveAttackOurselves. Default false.</summary>
        public static bool MeleeDisableAttackTreeWhenOutOfRange { get; set; } = false;
        /// <summary>[Unused] Melee: attempt to suppress attack call via shootDelay. This game's BT is not controlled by shootDelay. Default false.</summary>
        public static bool MeleeSuppressAttackCallWhenOutOfRange { get; set; } = false;
        /// <summary>Melee: when out of range, extend cooldown (cd) so IsReady() is false. If true, set cd to MeleeCooldownWhenOutOfRange (seconds) when out of range. (Suppress retry after first attack. Out-of-range trigger suppression always applied in CA_Attack_IsReady_MeleeRangePostfix.) Default true.</summary>
        public static bool MeleeCooldownByRangeEnabled { get; set; } = true;
        /// <summary>Melee: cooldown (seconds) when out of range. This value makes attack effectively impossible. Default 99999.</summary>
        public static float MeleeCooldownWhenOutOfRange { get; set; } = 99999f;
        /// <summary>If true, when melee enemy enters attack range (or MeleeStopRangeMultiplier times it), remove move input to stop then swing. Prevents swinging while running. Default true.</summary>
        public static bool MeleeStopBeforeAttackEnabled { get; set; } = true;
        /// <summary>Melee: remove movement (stop) when within this multiplier * (AttackRange+0.05) distance. 1.2 = stop at 120% of range. Only when MeleeStopBeforeAttackEnabled. Default 1.2.</summary>
        public static float MeleeStopRangeMultiplier { get; set; } = 1.2f;
        /// <summary>[Unused] Melee distance-based shootDelay. Attack timing kept as original so mod does not touch shootDelay. Default 0.25.</summary>
        public static float MeleeShootDelayFarMargin { get; set; } = 0.25f;
        /// <summary>[Unused] Melee shootDelay value when 'far' is judged. Attack timing kept as original. Default 60.</summary>
        public static float MeleeShootDelayWhenFar { get; set; } = 60f;
        /// <summary>When judgment is high, encourage leaving cover to attack player (blend toward player). If true, apply.</summary>
        public static bool LeaveCoverToEngageEnabled { get; set; } = true;
        /// <summary>Leave cover to engage: only move toward player when judgment (0~1 normalized) is at or above this. Default 0.45.</summary>
        public static float LeaveCoverToEngageJudgmentMin { get; set; } = 0.45f;
        /// <summary>Leave cover to engage: strength (0~1) to blend current move toward player. Higher = more aggressive exit. Default 0.5.</summary>
        public static float LeaveCoverToEngageBlendStrength { get; set; } = 0.5f;
        /// <summary>At low health, hide behind cover (encourage recovery). Higher judgment = stay in cover more. If true, apply. Default true.</summary>
        public static bool CoverRecoveryWhenLowHPEnabled { get; set; } = true;
        /// <summary>Low-health cover: apply blend toward cover when health ratio is at or below this. Default 0.4 (40%).</summary>
        public static float CoverRecoveryHPRatioThreshold { get; set; } = 0.4f;
        /// <summary>Low-health cover: blend strength (0~1) toward cover. Interpolated by judgment. Default 0.5.</summary>
        public static float CoverRecoveryBlendStrength { get; set; } = 0.5f;

        /// <summary>If true, when retreat blend result is full backward walk (dot &lt; -0.4), cap sideways to ease backward walk.</summary>
        public static bool RetreatCapEnabled { get; set; } = true;
        /// <summary>If true, restrict movement to character facing direction and diagonals. Prevents backpedaling toward player.</summary>
        public static bool NoBackpedalEnabled { get; set; } = true;
        /// <summary>Restrict move direction to within this half-angle (degrees) from character forward. 90 = front hemisphere (no backward step), 60 = 120° cone, 45 = 90° cone. Walk only within view cone.</summary>
        public static float NoBackpedalViewConeHalfAngleDeg { get; set; } = 90f;
        /// <summary>If true, always rotate to face move direction when moving. Approach, retreat, sidestep all align facing with move direction.</summary>
        public static bool FaceMovementDirectionWhenApproaching { get; set; } = true;
        /// <summary>If true, enable enemy AI shootCanMove so they can move while firing (original game behavior). If false, keep preset/default.</summary>
        public static bool MoveWhileFiringEnabled { get; set; } = true;
        /// <summary>Global default multiplier (0.01~5) for move input during path following (AI_PathControl). 1 = no change. Per-AI override via AI_PathControlPatches.SetSpeedMultiplier.</summary>
        public static float PathSpeedMultiplier { get; set; } = 1f;
        /// <summary>If true, when move input is 0 while reloading, restore previous move so agent moves while reloading (pattern movement can apply).</summary>
        public static bool MoveWhileReloadingEnabled { get; set; } = true;
        /// <summary>Move magnitude (0~1) when restoring move during reload. 0.5 = slow walk level.</summary>
        public static float MoveWhileReloadingMagnitude { get; set; } = 0.5f;
        /// <summary>If true, apply approach/retreat/sidestep correction only to enemies that have noticed player. If false, also when unnoticed (can cause backward/side step while patrolling). Default true.</summary>
        public static bool MoveInputBlendOnlyWhenNoticed { get; set; } = true;
        /// <summary>If true, remove approach component from input so unnoticed (noticed=false) enemies cannot move toward player. Natural flow: wander then approach after spotting. Default true.</summary>
        public static bool BlockApproachWhenUnnoticed { get; set; } = true;
        /// <summary>If true, enforce "spot then approach": when unnoticed, block·stop intentional path (MoveToPos) toward player so approach-before-spot does not occur. Default true.</summary>
        public static bool BlockPathToPlayerWhenUnnoticed { get; set; } = true;
        /// <summary>If true, block SetTarget so unnoticed enemies cannot set player as aim target. Prevents eyes on player while body moves elsewhere. Default true.</summary>
        public static bool BlockAimAtPlayerWhenUnnoticed { get; set; } = true;

        // ---- Aim tracking reaction speed ("getting better" feel when tracking player in real time) ----
        /// <summary>If true, set aim to smoothed position by reaction speed rather than exact player position each frame. Slow follow right after spot, faster tracking over time. Default true.</summary>
        public static bool AimTrackingSmoothEnabled { get; set; } = true;
        /// <summary>Aim tracking reaction speed lower bound (convergence per second). Lower = slower tracking right after spot. Default 2.5.</summary>
        public static float AimTrackingReactionSpeedMin { get; set; } = 2.5f;
        /// <summary>Aim tracking reaction speed upper bound (convergence per second). Lower = less violent snap when peeking from cover. Default 14.</summary>
        public static float AimTrackingReactionSpeedMax { get; set; } = 14f;
        /// <summary>After spotting, reaction speed ramps linearly from Min to Max over this duration (seconds). "Getting skilled" window. Default 5.</summary>
        public static float AimTrackingRampDurationSeconds { get; set; } = 5f;
        /// <summary>If true, higher judgment (map familiarity, player pattern) also improves initial reaction speed. If false, only time ramp. Default true.</summary>
        public static bool AimTrackingUseJudgment { get; set; } = true;
        /// <summary>Weight (0~1) of judgment on reaction speed. 0.5 = 50% time ramp + 50% judgment. Only when AimTrackingUseJudgment. Default 0.35.</summary>
        public static float AimTrackingJudgmentWeight { get; set; } = 0.35f;

        /// <summary>If true, when player is dashing limit aim/move target toward dash start so AI briefly "loses" target. Melee AI always track. Default true.</summary>
        public static bool AimTrackingLoseTargetOnPlayerDash { get; set; } = true;
        /// <summary>Target position during dash (0 = fixed at start, 1 = full tracking). 0.4 = lerp toward 40% from start to current, slight tracking in dash direction. Not applied to melee AI. Default 0.45.</summary>
        public static float AimTrackingDashLerpFactor { get; set; } = 0.45f;

        // ---- Through-wall combat mitigation (隔墙进战斗/瞄人/拉出来直接开枪 缓和) ----
        /// <summary>If true, do not sync aim to player when there is obstacle (wall) between AI and player. Prevents "aim through wall then step out and shoot". Default true.</summary>
        public static bool AimSyncRequireLineOfSight { get; set; } = true;
        /// <summary>When LOS required: delay (seconds) after gaining line of sight before full aim sync. Reduces instant snap-and-shoot when stepping out of cover. Higher = less "lock head and pull" from cover. Default 0.42.</summary>
        public static float AimAcquisitionDelayAfterLOSSeconds { get; set; } = 0.42f;
        /// <summary>If true, turret-style AI does not set noticed=true when player is in cone but blocked by obstacle. Prevents "shout/notice through wall". Default true.</summary>
        public static bool TurretNoticeRequireLineOfSight { get; set; } = true;
        /// <summary>터렛이 GetGun()/WeaponRange를 못 쓸 때 사용하는 최소 인지 사거리(m). 0이면 기존대로 1m. 터렛이 플레이어를 멀리서 인지하려면 15~25 권장. Default 18.</summary>
        public static float TurretNoticeMinRangeFallback { get; set; } = 18f;

        /// <summary>If true, when AI fires align bullet direction to current visual aim (CurrentAimDirection) not aim point (inputAimPoint), so aim and bullet match. Default true.</summary>
        public static bool AlignBulletToAimVisual { get; set; } = true;
        /// <summary>When unnoticed, after removing approach component if remaining move is near 0, minimum move magnitude (0~1) to keep patrol. 0 = stay in place as before. Default 0.35.</summary>
        public static float UnnoticedMinMoveMagnitude { get; set; } = 0.35f;
        /// <summary>For this duration (seconds) after noticed becomes false, allow search/track (relax path·approach·aim blocking). 0 = not applied. Default 25.</summary>
        public static float RecentCombatGraceSeconds { get; set; } = 25f;
        /// <summary>For this duration (seconds) after being hit by player, treat as "attack aware" and do not block approach·aim (approach and aim at player even if noticed still false). 0 = not applied. Default 10. If 0, target-hold on ADS/LOS false positive may not work.</summary>
        public static float RecentHurtByPlayerGraceSeconds { get; set; } = 10f;
        /// <summary>When shooting (attackAction.Running), do not overwrite aim with move direction. Default true.</summary>
        public static bool SkipAimOverwriteWhenShooting { get; set; } = true;
        /// <summary>Do not run dodge dash when player–enemy distance exceeds this (m). ≤0 = no limit (can dodge and approach from far). Default 0.</summary>
        public static float MaxIncomingDodgeDistance { get; set; } = 0f;
        /// <summary>Fallback speed when enemy AI dash has DashSpeed stat 0 (no characterItem etc.). Keep moderate to match original dash feel. Default 8.</summary>
        public static float DodgeDashSpeedFallback { get; set; } = 8f;
        /// <summary>On dodge dash manual move is removed (game built-in move used). Next two values are legacy/debug. ≤0 = not applied.</summary>
        public static float DodgeDashMaxDeltaTime { get; set; } = 0.05f;
        public static float DodgeDashMaxMovePerFrame { get; set; } = 0.6f;

        // ---- Enemy sight (cone) and view distance correction ----
        /// <summary>Enemy sight distance (sightDistance) multiplier. 1 = original. 1.2 = detect 20% farther (player-like view distance). Default 1.25.</summary>
        public static float SightDistanceMultiplier { get; set; } = 1.25f;
        /// <summary>Enemy sight angle (sightAngle) multiplier. 1 = original (e.g. 100°). 1.8 ≈ 180° (±90° from forward, player-like cone). Default 1.8.</summary>
        public static float SightAngleMultiplier { get; set; } = 1.8f;
        /// <summary>Minimum degrees for sight angle. After multiplier, if below this clamp up. 0 = not applied. Default 120 (v1.3.8: was 180, which left enemies with no blind spot behind them).</summary>
        public static float SightAngleMinimumDeg { get; set; } = 120f;
        /// <summary>Minimum meters for sight distance. After multiplier, if below this clamp up. 0 = not applied. Default 22.</summary>
        public static float SightDistanceMinimumM { get; set; } = 22f;

        // ---- No fire when only sound heard (original: growl + recon only, no fire) ----
        /// <summary>If true, when obstacle between player and enemy (no line of sight) clear searchedEnemy/aimTarget so they do not fire. When only sound heard, recon only, no fire (original behavior). Default true.</summary>
        public static bool NoFireWhenNoLineOfSight { get; set; } = true;

        // ---- Non-aggro sound / dodge awareness ----
        /// <summary>Whether non-aggro (forceTracePlayerDistance≤0.5) can notice player by gunfight sound only. If true, notice within sound range like original. If false, non-aggro notice only when hit by player. Default true (enemies react to gunfire etc.).</summary>
        public static bool AllowSoundNoticeForNonAggro { get; set; } = true;
        /// <summary>Whether non-aggro (forceTracePlayerDistance≤0.5) set player as noticed and target (searchedEnemy) when doing dodge dash. If false, dodge only without notice so no "only orbit, no attack" bug. If true, previous behavior (attempt attack on dodge; some presets may orbit only). Default false.</summary>
        public static bool NonAggroDodgeSetsNoticed { get; set; } = false;
        /// <summary>When player is target but unnoticed, set noticed=true if within "distance at which sound can cause notice". Reference radius (m)×AI.hearingAbility, same as game sound detection. 0 = not applied. Default 15 (same as debug sound circle).</summary>
        public static float VisionNoticeSoundRadiusReference { get; set; } = 15f;

        // ---- Line-of-sight gated awareness ----
        /// <summary>If true, enemies never notice / chase / attack the player through walls. Force-notice, player-aggro checks and attack start all require line of sight.
        /// Enemies alerted by sound still search toward the sound like the original, but only aim/approach/fire once they actually see the player. Being hit by the player is an exception (they may chase back). Default true.</summary>
        public static bool RequireLineOfSightForAggro { get; set; } = true;
        /// <summary>Extra multiplier applied only to the line-of-sight gate, on top of ai.sightDistance (which already includes SightDistanceMultiplier). Default 1.</summary>
        public static float SightLosRangeMultiplier { get; set; } = 1f;
        /// <summary>Seconds after losing sight during which the player still counts as seen (so enemies can chase around a corner). 0 = lose instantly. Default 3.</summary>
        public static float SightMemorySeconds { get; set; } = 3f;
        /// <summary>Cache interval (seconds) for the sight raycast, to keep the cost low with many AIs. 0 = check every call. Default 0.15.</summary>
        public static float SightCheckIntervalSeconds { get; set; } = 0.15f;
        /// <summary>Whether half-height obstacles (halfObsticleLayer) also block sight. False means enemies can see over crates etc. (closer to the original). Default false.</summary>
        public static bool SightBlockedByHalfObstacle { get; set; } = false;
        /// <summary>Sight ray origin height (m, AI eye level). 1.5, same as the original SearchEnemyAround.</summary>
        public static float SightEyeHeight { get; set; } = 1.5f;
        /// <summary>Sight ray target height (m, player torso). Default 1.</summary>
        public static float SightTargetHeight { get; set; } = 1f;
        /// <summary>If true, log sight check results (debug).</summary>
        /// <summary>Seconds the player must stay continuously visible before the enemy may open fire (anti-flicker around doorways and pillars). Default 0.12.
        /// v1.3.9: this no longer delays target acquisition, only the trigger, and it no longer stacks with the reaction delay (the larger of the two wins).</summary>
        public static float SightAcquireDelaySeconds { get; set; } = 0.12f;
        /// <summary>If true, a cold first sighting additionally waits the AI reaction time before the enemy engages, so there is a visible beat between "spotted you" and "opens fire". Re-acquiring a target you were already fighting skips it. Default true.</summary>
        public static bool SightUseReactionDelayOnFirstSight { get; set; } = true;
        /// <summary>Multiplier on the reaction time used by the first-sight delay. Higher = enemies hesitate longer before engaging. Default 1.</summary>
        public static float SightReactionDelayScale { get; set; } = 1f;
        /// <summary>Lower clamp (seconds) for the first-sight reaction delay. Keeps highly adapted enemies from feeling like an aimbot. Default 0.12.</summary>
        public static float SightReactionDelayMin { get; set; } = 0.12f;
        /// <summary>Upper clamp (seconds) for the first-sight reaction delay. Default 0.5.</summary>
        public static float SightReactionDelayMax { get; set; } = 0.5f;
        /// <summary>If true, the first-sight reaction delay also picks up the multi-axis Reactivity axis and the short-term reactivity boost, so a higher adaptive score visibly shortens the hesitation. Default true.</summary>
        public static bool SightReactionUsesAdaptiveValue { get; set; } = true;

        /// <summary>If true, apply the sight cone (ai.sightAngle around CharacterMainControl.CurrentAimDirection) as well, so flanking works. Default true.</summary>
        public static bool SightAngleEnabled { get; set; } = true;
        /// <summary>Extra degrees beyond the sight cone that still count as peripheral vision. Detection there is slower (SightPeripheralAcquireMultiplier). Default 45.</summary>
        public static float SightPeripheralExtraDeg { get; set; } = 45f;
        /// <summary>Acquire-delay multiplier inside the peripheral band. Default 1.6.</summary>
        public static float SightPeripheralAcquireMultiplier { get; set; } = 1.6f;

        /// <summary>If true, when an enemy loses sight it walks to the last position it actually saw the player and searches there instead of returning straight to patrol. Default true.</summary>
        public static bool SightSearchLastSeenEnabled { get; set; } = true;
        /// <summary>Seconds to keep searching around the last seen position before giving up and returning to patrol. 0 = never give up. Default 8.</summary>
        public static float SightSearchDurationSeconds { get; set; } = 8f;

        // ---- Sound-driven tracking (A) ----
        /// <summary>If true, an enemy that hears the player walks to WHERE THE SOUND CAME FROM and searches there.
        /// It never tracks the player's live position by sound alone and never aims or fires without sight — that is what sight is for. Default true.</summary>
        public static bool SoundTrackingEnabled { get; set; } = true;
        /// <summary>Seconds a heard player position stays worth investigating. Default 12.</summary>
        public static float SoundTrackMemorySeconds { get; set; } = 12f;
        /// <summary>While a search leg is running, a new gunshot does NOT redirect it until this many seconds have passed since the leg started.
        /// One search = one point: without this, every shot re-aimed the search at the player's live position, which is a charge with extra steps.
        /// When the leg ends, a fresher sound heard meanwhile starts the next leg instead of returning to patrol. Default 8 (= SightSearchDurationSeconds, so legs never retarget mid-way).</summary>
        public static float SoundSearchRetargetCooldownSeconds { get; set; } = 8f;
        public static bool DebugLogSight { get; set; } = false;

        // ---- Retreat→approach pattern (encourage approach when player vulnerable) ----
        /// <summary>Whether to use approach blend instead of retreat when player is reloading.</summary>
        public static bool ApproachWhenPlayerReloadingEnabled { get; set; } = true;
        /// <summary>Approach direction blend strength (0~1) when player reloading (or vulnerable). Higher = enemy approaches more aggressively.</summary>
        public static float ApproachWhenPlayerReloadingBlend { get; set; } = 0.55f;

        // ---- v1.1.8: Reload timing (dangerous reload ratio) ----
        /// <summary>Approach blend multiplier lower bound when reload risk ratio is low. blend = base * (Min + (Max-Min)*ReloadRiskRatioEma). Default 0.5.</summary>
        public static float ReloadRiskBlendMin { get; set; } = 0.5f;
        /// <summary>Approach blend multiplier upper bound when reload risk ratio is high. Default 1.2.</summary>
        public static float ReloadRiskBlendMax { get; set; } = 1.2f;

        // ---- v1.1.8: Threat response (push right after hit when retreat-type) ----
        /// <summary>Whether to use strengthened push right after being hit.</summary>
        public static bool PostHitPushEnabled { get; set; } = true;
        /// <summary>Duration (seconds) of strengthened push after hit.</summary>
        public static float PostHitPushDuration { get; set; } = 3f;
        /// <summary>Extra multiplier for approach blend during push. 1.2 = 20% more toward approach.</summary>
        public static float PostHitPushBlendMultiplier { get; set; } = 1.2f;
        /// <summary>At or above this value, treat as retreat-type (apply push right after hit).</summary>
        public static float ThreatResponseRetreatThreshold { get; set; } = 0.5f;

        // ---- v1.1.8: Exchange ratio (combat trend) ----
        /// <summary>Aggression contribution when exchange ratio is unfavorable (player→enemy / enemy→player &lt; 1).</summary>
        public static float ExchangeRatioAggressionWeight { get; set; } = 0.08f;

        /// <summary>Minimum aggression (0~1) for pattern read to apply. If agg &lt; this, pattern counter not applied.</summary>
        public static float PatternReadMinAggression { get; set; } = 0.2f;

        /// <summary>Aggression (0~1) for full pattern read. If agg &gt;= this, apply up to blend cap.</summary>
        public static float PatternReadMaxAggression { get; set; } = 0.7f;

        /// <summary>Window (seconds) around expected next shot for counter. Expected time ± this value.</summary>
        public static float CounterMoveNextShotWindow { get; set; } = 0.2f;

        // ---- Short-term response (recent N seconds; "this engagement" layer) ----
        /// <summary>Time window (seconds) for short-term behavior correction. Only last N seconds affect short-term aggression/defensive boost.</summary>
        public static float ShortTermWindowSeconds { get; set; } = 12f;
        /// <summary>Cap on short-term aggression correction (additive, ±). Higher = stronger immediate reaction to dash/reload in last window.</summary>
        public static float ShortTermAggressionCorrectionCap { get; set; } = 0.15f;
        /// <summary>Cap on short-term defensive stance correction (additive, ±). Higher = stronger immediate reaction to straight approach in last window.</summary>
        public static float ShortTermDefensiveStanceCorrectionCap { get; set; } = 0.2f;

        // ---- Tactical mode tier (0=conservative, 1=neutral, 2=aggressive); multipliers by mode ----
        /// <summary>If true, apply tactical mode tier multipliers (retreat/approach/cover wait/approach dash) by aggression+defensive stance.</summary>
        public static bool TacticalModeTierEnabled { get; set; } = true;
        /// <summary>Retreat blend multiplier in conservative mode (0). &gt;1 = more retreat.</summary>
        public static float TacticalModeRetreatBlendMultConservative { get; set; } = 1.25f;
        /// <summary>Retreat blend multiplier in neutral mode (1).</summary>
        public static float TacticalModeRetreatBlendMultNeutral { get; set; } = 1f;
        /// <summary>Retreat blend multiplier in aggressive mode (2). &lt;1 = less retreat.</summary>
        public static float TacticalModeRetreatBlendMultAggressive { get; set; } = 0.75f;
        /// <summary>Approach blend multiplier in conservative mode (0). &lt;1 = less approach.</summary>
        public static float TacticalModeApproachBlendMultConservative { get; set; } = 0.75f;
        /// <summary>Approach blend multiplier in neutral mode (1).</summary>
        public static float TacticalModeApproachBlendMultNeutral { get; set; } = 1f;
        /// <summary>Approach blend multiplier in aggressive mode (2). &gt;1 = more approach/rush.</summary>
        public static float TacticalModeApproachBlendMultAggressive { get; set; } = 1.25f;
        /// <summary>Cover wait (hold cover) blend multiplier in conservative mode (0). &gt;1 = wait more in cover.</summary>
        public static float TacticalModeCoverWaitBlendMultConservative { get; set; } = 1.2f;
        /// <summary>Cover wait blend multiplier in neutral mode (1).</summary>
        public static float TacticalModeCoverWaitBlendMultNeutral { get; set; } = 1f;
        /// <summary>Cover wait blend multiplier in aggressive mode (2). &lt;1 = leave cover sooner.</summary>
        public static float TacticalModeCoverWaitBlendMultAggressive { get; set; } = 0.8f;
        /// <summary>Approach/tactical dash frequency multiplier in conservative mode (0). &lt;1 = fewer dashes.</summary>
        public static float TacticalModeApproachDashFreqMultConservative { get; set; } = 0.7f;
        /// <summary>Approach/tactical dash frequency multiplier in neutral mode (1).</summary>
        public static float TacticalModeApproachDashFreqMultNeutral { get; set; } = 1f;
        /// <summary>Approach/tactical dash frequency multiplier in aggressive mode (2). &gt;1 = more dashes.</summary>
        public static float TacticalModeApproachDashFreqMultAggressive { get; set; } = 1.3f;

        /// <summary>Get retreat blend multiplier for tactical mode index (0=conservative, 1=neutral, 2=aggressive).</summary>
        public static float GetTacticalModeRetreatBlendMult(int mode)
        {
            if (mode <= 0) return TacticalModeRetreatBlendMultConservative;
            if (mode >= 2) return TacticalModeRetreatBlendMultAggressive;
            return TacticalModeRetreatBlendMultNeutral;
        }
        /// <summary>Get approach blend multiplier for tactical mode index.</summary>
        public static float GetTacticalModeApproachBlendMult(int mode)
        {
            if (mode <= 0) return TacticalModeApproachBlendMultConservative;
            if (mode >= 2) return TacticalModeApproachBlendMultAggressive;
            return TacticalModeApproachBlendMultNeutral;
        }
        /// <summary>Get cover wait blend multiplier for tactical mode index.</summary>
        public static float GetTacticalModeCoverWaitBlendMult(int mode)
        {
            if (mode <= 0) return TacticalModeCoverWaitBlendMultConservative;
            if (mode >= 2) return TacticalModeCoverWaitBlendMultAggressive;
            return TacticalModeCoverWaitBlendMultNeutral;
        }
        /// <summary>Get approach/tactical dash frequency multiplier for tactical mode index.</summary>
        public static float GetTacticalModeApproachDashFreqMult(int mode)
        {
            if (mode <= 0) return TacticalModeApproachDashFreqMultConservative;
            if (mode >= 2) return TacticalModeApproachDashFreqMultAggressive;
            return TacticalModeApproachDashFreqMultNeutral;
        }

        // ---- Behavior pattern time decay (reduce weight of old patterns) ----

        /// <summary>Half-life (days) of stored pattern. After this many days loaded value reflects half toward neutral. 0 = no decay.</summary>
        public static float PatternHalfLifeDays { get; set; } = 14f;

        /// <summary>If true, output debug log to console for dash bottlenecks (canDash, turret block, dash block cause etc.). Toggleable in debug overlay.</summary>
        public static bool DebugLogDash { get; set; } = false;
        /// <summary>If true, log to console enemy AI melee weapon hold (GetMeleeWeapon/loadout.IsMelee) and on Attack() entry distance·block reason. For melee check verification.</summary>
        public static bool DebugLogMeleeWeapon { get; set; } = false;

        // ---- Debug: awareness/sound range visualization (enable Gizmos in Scene/Game view) ----
        /// <summary>If true, draw hearing range (sound awareness radius) as circle around enemy AI. Reference radius (e.g. 15m)×hearingAbility. Debug build/editor.</summary>
        public static bool DebugDrawEnemySoundRange { get; set; } = false;
        /// <summary>If true, draw player's sound radius (gunfire SoundRange, shout 15m etc.) as circle around player. Debug build/editor.</summary>
        public static bool DebugDrawPlayerSoundRange { get; set; } = false;
        /// <summary>If true, draw sight distance (sightDistance) as circle around enemy AI. Debug build/editor.</summary>
        public static bool DebugDrawEnemySightRange { get; set; } = false;

        // ---- Map familiarity (strength of reflecting player tendency by combat count on that map) ----
        // The more you play the same map, the more AI follows stored player behavior profile (aggressive/defensive, dash, fire, movement pattern). Play aggressive → AI more aggressive; play defensive → AI more cautious. Offset can be positive or negative (OffsetCap = absolute cap).
        /// <summary>Whether to use map familiarity (combat-count based) play tendency. If false, no stronger behavior profile reflection even after long play on same map.</summary>
        public static bool MapFamiliarityEnabled { get; set; } = true;
        /// <summary>MapFamiliarity=1 when this many combats have occurred on this map. Raising this increases combats needed for full reflection.</summary>
        public static float MapFamiliarityCombatCap { get; set; } = 180f;
        /// <summary>Strength (0~1) of map familiarity contribution to play tendency offset. Higher = “AI follows play style on this map” more.</summary>
        public static float MapFamiliarityStrength { get; set; } = 0.5f;
        /// <summary>Absolute cap on aggression offset from map familiarity. Aggressive player → +, defensive → −; result clamped to this.</summary>
        public static float MapFamiliarityOffsetCap { get; set; } = 0.12f;

        // ==== Feature 3: Short-term pattern detection (fast-reacting layer) ====

        /// <summary>If true, use short-term pattern detector for immediate tactical modifiers on top of EMA.</summary>
        public static bool ShortTermPatternEnabled { get; set; } = true;
        /// <summary>Maximum reactivity boost (0~0.5) from short-term behavior spikes. Reduces reaction time and dodge cooldown when player behavior changes rapidly.</summary>
        public static float ShortTermReactivityBoostCap { get; set; } = 0.35f;
        /// <summary>Maximum immediate aggression correction (0~0.3) from short-term detection. Applied additively to base aggression.</summary>
        public static float ShortTermImmediateAggressionCap { get; set; } = 0.2f;
        /// <summary>Dodge urgency multiplier cap from burst fire detection. 1.0=no change, 1.4=max boost during burst.</summary>
        public static float ShortTermDodgeUrgencyMultCap { get; set; } = 1.3f;
        /// <summary>If true, AI pushes (approach boost) when player is detected retreating or in fire lull.</summary>
        public static bool ShortTermPushWindowEnabled { get; set; } = true;
        /// <summary>Approach blend boost (0~0.3) when push window is active.</summary>
        public static float ShortTermPushWindowApproachBoost { get; set; } = 0.2f;

        // ==== Feature 2: Tactic outcome feedback loop (per-AI learning) ====

        /// <summary>If true, AI tracks success/failure of its own tactics and adjusts weights accordingly.</summary>
        public static bool TacticOutcomeTrackingEnabled { get; set; } = true;
        /// <summary>Weight multiplier range lower bound. Failing tactics get down to this multiplier. Default 0.65.</summary>
        public static float TacticWeightMin { get; set; } = 0.65f;
        /// <summary>Weight multiplier range upper bound. Successful tactics get up to this multiplier. Default 1.35.</summary>
        public static float TacticWeightMax { get; set; } = 1.35f;
        /// <summary>Overall effectiveness (0~1) threshold below which AI tries harder (lower dodge cooldown, higher accuracy). Default 0.4.</summary>
        public static float TacticEffectivenessBoostThreshold { get; set; } = 0.4f;
        /// <summary>Strength of effectiveness-based adjustment (0~0.3). When AI is ineffective, parameters shift by up to this amount. Default 0.15.</summary>
        public static float TacticEffectivenessBoostStrength { get; set; } = 0.15f;

        // ==== Feature 4: Player habit exploitation ====

        /// <summary>If true, AI detects and exploits repeating player habits (dash direction, post-shoot pattern, reload timing, position reuse).</summary>
        public static bool HabitExploitationEnabled { get; set; } = true;
        /// <summary>Minimum confidence (0~1) to act on a detected habit. Lower = exploit weaker signals. Default 0.35.</summary>
        public static float HabitConfidenceMin { get; set; } = 0.35f;
        /// <summary>Aim pre-lead strength (0~1) toward predicted dash direction. 0=none, 1=full prediction. Default 0.4.</summary>
        public static float HabitDashPredictionAimStrength { get; set; } = 0.4f;
        /// <summary>Approach boost (0~0.3) when reload timing prediction is confident. AI rushes during predicted reload. Default 0.2.</summary>
        public static float HabitReloadPushStrength { get; set; } = 0.2f;
        /// <summary>If true, AI prefers flanking toward player's frequent position to cut off retreat. Default true.</summary>
        public static bool HabitPositionFlankEnabled { get; set; } = true;

        // ==== Feature 5: Multi-axis adaptation ====

        /// <summary>If true, use multi-axis profile (Aggression/Precision/Reactivity) instead of single aggression for parameter computation.</summary>
        public static bool MultiAxisEnabled { get; set; } = true;
        /// <summary>Weight of Aggression axis when computing legacy single aggression value. Default 0.5.</summary>
        public static float MultiAxisAggressionWeight { get; set; } = 0.5f;
        /// <summary>Weight of Precision axis in legacy aggression blend. Default 0.25.</summary>
        public static float MultiAxisPrecisionWeight { get; set; } = 0.25f;
        /// <summary>Weight of Reactivity axis in legacy aggression blend. Default 0.25.</summary>
        public static float MultiAxisReactivityWeight { get; set; } = 0.25f;
        /// <summary>Strength (0~1) of multi-axis per-parameter overrides. 0=use legacy only, 1=full multi-axis. Default 0.6.</summary>
        public static float MultiAxisOverrideStrength { get; set; } = 0.6f;
    }
}


