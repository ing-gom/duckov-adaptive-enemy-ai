# Adaptive Enemy AI

**English** · [한국어](README.ko.md) · [简体中文](README.zh.md)

An enemy AI mod for **Escape from Duckov**. Enemies read what you are carrying and how you fight,
then adjust their own tactics in real time.

[![Steam Workshop](https://img.shields.io/badge/Steam%20Workshop-Adaptive%20Enemy%20AI-1b2838)](https://steamcommunity.com/sharedfiles/filedetails/?id=3661223437)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

> ⚠️ **This mod requires the Harmony mod.**

![preview](assets/preview.png)

---

## Features

**Loadout response** — Enemies shift between aggressive and defensive judgment based on your weapon
and armor. A weapon that beats them at the current range makes them press in; heavy armor on you
makes them keep their distance and prioritize evasion.

**Behavior learning** — Movement intensity, aim changes, dash and fire frequency are sampled into
summary statistics and fed back into AI parameters: reaction speed, fire rate, aim accuracy, how
long they track you before giving up. Older samples decay so recent play is weighted more.

**Fire-pattern counter** — Reads when you are about to shoot and moves beforehand, so a static aim
does not pay off.

**Dodge dash** — Enemies attempt an evasive dash against incoming bullets and melee swings. The
chance is bounded by your hit rate, shot pattern, cover state and how long the fight has run, so it
is not a guaranteed dodge.

**Sight model (v1.3.9)** — Sight is split into three gates instead of the game's single `noticed`
flag: raw sight (range, cone, wall ray), sense (raw ∪ 3s memory ∪ recently hurt) and engage
(confirmed sight past the reaction delay). Enemies no longer notice, chase or shoot through walls.

**Sound is a search, not a track** — A gunshot sends an enemy walking to *where the sound came from*
to look around. Sound alone never makes it follow your live position.

**Save integration** — Behavior profiles are stored with the game save (SavesSystem + ES3).

## How it works

Everything is grafted onto the game's own AI through Harmony — the mod does not replace the
behavior tree, it gates and nudges it.

| Area | Hook |
|---|---|
| Notice / aggro / aim gating | `AICharacterController` patches |
| Trigger suppression without line of sight | `CharacterMainControl.Trigger` prefix |
| Dodge dash | `CA_Dash` / `CharacterMainControl` dash patches |
| Incoming projectile detection | `Projectile` patches |
| Movement and path following | `AI_PathControl` patches |
| Per-preset opt-out | `PerAIPatchControl` |

`AICharacterController.noticed` is worth knowing about if you read the code: in the base game it
means "heard or was hit", **not** "saw". Sight detection lives in the behavior tree's
`SearchEnemyAround` + `CheckObsticle`, and `Update` sets `searchedEnemy` to the player inside
`forceTracePlayerDistance` regardless of walls. Treating either one as "noticed" is what produced
aggro through walls before v1.3.9.

## Settings

All tuning lives in `Settings/AdaptiveAISettings.cs` as static properties with defaults — loadout
weight, global AI scale, dodge chance floors and caps, combat ramp, sight cone and distance,
sight memory, sound search radius and memory, reaction delays. A debug overlay
(`Services/DebugOverlay.cs`) can toggle some of them at runtime.

There is **no in-game options UI or config file yet** — this is the most requested feature and the
most obvious place to contribute.

## Build

```bash
dotnet build AdaptiveEnemyAI.csproj
```

Copy `Local.props.example` to `Local.props` and point it at your game install if the default Steam
path is wrong. `Local.props` is gitignored.

Dependencies are NuGet packages (`Ducky.Sdk`, `Lib.Harmony`); no game DLLs are included here.

## Contributing

Issues and pull requests are welcome. Bug reports are more useful than Steam comments because they
can hold a repro, a log and a discussion thread. The game log lives at:

```
%USERPROFILE%\AppData\LocalLow\TeamSoda\Duckov\Player.log
```

## License

[MIT](LICENSE). See [NOTICE.md](NOTICE.md) for third-party and fan-project notices.
