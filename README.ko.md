# Adaptive Enemy AI (적응형 적 AI)

[English](README.md) · **한국어** · [简体中文](README.zh.md)

**Escape from Duckov**용 적 AI 모드입니다. 적이 플레이어의 무장과 전투 방식을 읽고 자기 전술을
실시간으로 바꿉니다.

[![Steam Workshop](https://img.shields.io/badge/Steam%20Workshop-Adaptive%20Enemy%20AI-1b2838)](https://steamcommunity.com/sharedfiles/filedetails/?id=3661223437)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

> ⚠️ **이 모드는 Harmony 모드가 필요합니다.**

![preview](assets/preview.png)

---

## 기능

**무장 반영** — 무기와 방어구에 따라 적의 판단이 공격적 ↔ 방어적으로 이동합니다. 현재 거리에서
유리한 무기를 들고 있으면 적이 압박해 오고, 방어구가 두꺼우면 거리를 유지하며 회피를 우선합니다.

**행동 학습** — 이동 강도, 조준 변화, 대시·사격 빈도를 샘플링해 요약 통계로 유지하고 AI 파라미터에
되먹입니다: 반응 속도, 연사 성향, 조준 정확도, 플레이어를 얼마나 오래 쫓다 포기하는지. 오래된
표본은 가중치가 줄어 최근 플레이가 더 반영됩니다.

**사격 패턴 대응** — 플레이어가 쏘려는 시점을 읽고 미리 움직여, 고정된 조준이 통하지 않게 합니다.

**회피 대시** — 날아오는 총알과 근접 휘두름에 대해 회피 대시를 시도합니다. 확률은 플레이어 명중률,
사격 패턴, 엄폐 상태, 교전 경과 시간으로 상·하한이 묶여 있어 확정 회피가 되지 않습니다.

**시야 모델 (v1.3.9)** — 게임의 단일 `noticed` 플래그 대신 시야를 3단계로 나눕니다: 원본 시야
(거리·시야각·벽 레이), 감지(원본 ∪ 3초 기억 ∪ 최근 피격), 교전(반응 지연을 통과한 확정 시야).
벽 너머로 인지·추적·사격하지 않습니다.

**소리는 추적이 아니라 수색** — 총성을 들으면 *소리가 난 지점까지* 걸어가 훑습니다. 소리만으로
플레이어의 실시간 위치를 따라가지 않습니다.

**세이브 연동** — 행동 프로파일을 게임 세이브와 함께 저장합니다 (SavesSystem + ES3).

## 동작 방식

전부 Harmony로 게임의 기존 AI 위에 얹혀 있습니다. 비헤이비어 트리를 대체하지 않고, 게이트를 걸고
값을 밀어 주는 방식입니다.

| 영역 | 후킹 지점 |
|---|---|
| 인지·어그로·조준 게이트 | `AICharacterController` 패치 |
| 시야 없을 때 방아쇠 차단 | `CharacterMainControl.Trigger` 프리픽스 |
| 회피 대시 | `CA_Dash` / `CharacterMainControl` 대시 패치 |
| 날아오는 투사체 감지 | `Projectile` 패치 |
| 이동·경로 추종 | `AI_PathControl` 패치 |
| 프리셋별 제외 | `PerAIPatchControl` |

코드를 읽을 때 알아 두면 좋은 것: 원작에서 `AICharacterController.noticed` 는 "봤다"가 아니라
**"들었다 / 맞았다"** 입니다(`OnSound`·`OnHurt` 에서만 true). 실제 시야 탐지는 비헤이비어 트리의
`SearchEnemyAround` + `CheckObsticle` 이 하고, `Update` 는 `forceTracePlayerDistance` 안이면 벽과
무관하게 `searchedEnemy` 를 플레이어로 둡니다. 이 둘을 "인지"로 취급한 것이 v1.3.9 이전의 벽 너머
어그로 원인이었습니다.

## 설정

튜닝 값은 전부 `Settings/AdaptiveAISettings.cs` 에 정적 프로퍼티 + 기본값으로 있습니다 — 무장 비중,
전역 AI 강도, 회피 확률 상·하한, 전투 램프, 시야각·시야 거리, 시야 기억, 소리 수색 반경·기억,
반응 지연. 디버그 오버레이(`Services/DebugOverlay.cs`)로 일부를 런타임에 토글할 수 있습니다.

**아직 게임 내 옵션 UI 나 설정 파일은 없습니다.** 가장 많이 요청받은 기능이고, 기여하기에 가장
좋은 자리이기도 합니다.

## 빌드

```bash
dotnet build AdaptiveEnemyAI.csproj
```

기본 Steam 경로가 다르면 `Local.props.example` 을 `Local.props` 로 복사해 게임 설치 경로를
지정하세요. `Local.props` 는 gitignore 되어 있습니다.

의존성은 NuGet 패키지(`Ducky.Sdk`, `Lib.Harmony`)이며, 게임 DLL 은 이 저장소에 포함되지 않습니다.

## 기여

이슈와 풀 리퀘스트 환영합니다. 재현 절차·로그·토론을 함께 담을 수 있어서 스팀 댓글보다 버그 리포트
쪽이 훨씬 쓸모 있습니다. 게임 로그 위치:

```
%USERPROFILE%\AppData\LocalLow\TeamSoda\Duckov\Player.log
```

## 라이선스

[MIT](LICENSE). 서드파티·팬 프로젝트 고지는 [NOTICE.md](NOTICE.md) 를 참고하세요.
