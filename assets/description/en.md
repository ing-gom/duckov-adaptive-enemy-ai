# Adaptive Enemy AI

> **⚠️ Requirement**: This mod requires the **Harmony mod**. Please install Harmony first.

A mod that analyzes the player's loadout and behavior to change how enemies fight. When no initial behavior pattern exists, enemies mainly act according to the player's loadout. As patterns accumulate, it creates a more challenging combat environment for the player.

## 🔄 What's new in v1.3.9

- **Fixed seeing through walls**: enemies no longer notice, chase and shoot the player through walls. They now aim and fire **only when they can actually see you**.
- **Reacting to sound**: on hearing a gunshot an enemy **walks to where the sound came from and searches there**. Sound alone never makes it follow your current position.
- **Searching after losing sight**: when an enemy loses sight of you it heads to the **last place it saw you**, looks around for a while, then returns to patrol.
- **Sight cone**: enemies now have a forward cone and peripheral vision. Approaching from behind takes longer to be spotted.
- **Reaction time**: there is now a short beat between an enemy spotting you and opening fire. The more the mod has learned about you, the shorter that beat gets.

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

## 📋 Notes

- This mod is still experimental; enemy combat judgment may feel more aggressive or less aggressive than expected. Your feedback after playing would be very helpful for the next version.
- All code and assets were created with AI assistance.
- Please report any bugs or balance suggestions.

**Enjoy the game! 🎮**
