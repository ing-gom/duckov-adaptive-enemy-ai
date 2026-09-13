# Adaptive Enemy AI

> **⚠️ Requirement**: This mod requires the **Harmony mod**. Please install Harmony first.

A mod that analyzes the player's loadout and behavior to change how enemies fight. When no initial behavior pattern exists, enemies mainly act according to the player's loadout. As patterns accumulate, it creates a more challenging combat environment for the player.

## 🔄 What's new in v1.4.0

- **Sound no longer leads enemies to you**: a gunshot starts one search leg toward where that shot came from. Further shots don't redirect it until the leg is over (8s); then the enemy moves on to the newest sound. It walks to where you *were*, not where you are.
- **No more hugging**: gun-armed enemies now hold their weapon's minimum range (pistol/SMG 2m, shotgun 2.5m, AR 3m, BR/LMG 4m, sniper 5m) instead of closing to touching distance. Melee is unchanged.
- **A glance doesn't trigger a charge**: an alerted enemy with no target needs a confirmed look (past the reaction delay) before the mod starts steering it toward you — the same bar as pulling the trigger.
## ✨ Key Features

- **Enemy AI calibration**: Enemy **combat judgment** (aggressive judgment ↔ defensive judgment) changes in real time based on loadout and behavior.

**Factors that influence it**
- **Loadout**: Weapons and armor equipped
- **Combat behavior**: Learns and reflects **how you move and attack** when combat happens.
  - **Movement**: Whether to walk, dash in, or how long/far to move
  - **Movement pattern learning**: Learns how you approach enemies and applies it to enemy AI movement adjustment.
  - **Combat**: Reaction speed, fire rate (spray vs. careful shots), aim accuracy, how long they track or forget the player
  - **Evasion**: Whether they attempt a dodge dash when they detect incoming bullets or melee swings, and how often
  - **Fire pattern counter**: Reads when you are about to shoot and moves beforehand to make aiming harder.
  - **Time decay**: Reduces the weight of older saved patterns so your recent play style is reflected more.

## 🎯 Loadout-based enemy response

Enemies adjust their **combat judgment** based on the player's weapons and **armor**. (They respond in a way that is tactically favorable to the AI.)

- When the player has an **advantageous weapon** → enemies **aggressive judgment** (apply pressure)
- When the player has a **disadvantageous weapon** → enemies **defensive judgment** (maintain distance, prioritize evasion)
- When the player's armor is **high** → enemies **defensive judgment**
- When the player's armor is **low** → enemies **aggressive judgment**

## 🔧 Source code

The full source is on GitHub: https://github.com/ing-gom/duckov-adaptive-enemy-ai

It is MIT licensed. Bug reports and pull requests are welcome — an issue there can hold a repro, a log and a discussion thread, which works better than a Steam comment.

## 📋 Notes

- This mod is still experimental; enemy combat judgment may feel more aggressive or less aggressive than expected. Your feedback after playing would be very helpful for the next version.
- All code and assets were created with AI assistance.
- Please report any bugs or balance suggestions.

**Enjoy the game! 🎮**
