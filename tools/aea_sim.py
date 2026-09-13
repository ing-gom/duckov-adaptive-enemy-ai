#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AdaptiveEnemyAI 인지·수색·접근 로직 시뮬레이터.

게임을 켜지 않고 수치로 확인하기 위한 도구다. 모드의 판정 경로를 그대로 옮겨서
v1.3.9 와 v1.4.0 을 같은 시나리오에 돌리고 결과를 비교한다.

옮긴 경로 (src/AdaptiveEnemyAI/Patches/):
  AICharacterControllerPatches.ComputeRawSight            → Sight.compute_raw
  AICharacterControllerPatches.HasRawSightToPlayer        → Sight.has_raw
  AICharacterControllerPatches.HasConfirmedSightToPlayer  → Sight.has_confirmed
  AICharacterControllerPatches.CanSensePlayer             → Sight.can_sense
  AICharacterControllerPatches.IsAggroOnPlayer (마지막 분기) → Enemy.aggro_on_player
  AICharacterControllerPatches.RecordHeardPlayerSound     → Enemy.on_sound
  AICharacterControllerPatches.TickSightMemory            → Enemy.tick_search
  CharacterMainControlSetMoveInputPatches
      .GetDesiredMinDistanceFromPlayer                    → Enemy.desired_min_dist

옮기지 않은 것 (결과를 볼 때 감안할 것):
  - 비헤이비어 트리 자체. 게임 BT 가 searchedEnemy 를 직접 잡는 경로는 모드 밖이라,
    여기서는 "모드가 어그로로 볼지" 만 본다.
  - 이동 가속·경로 탐색·엄폐. 직선으로 걷는다고 본다. 실제로는 더 느리다.
  - 적응형 공격성이 반응 시간에 주는 보정. reaction_delay 를 고정값으로 둔다.

수치 출처: Settings/AdaptiveAISettings.cs 기본값. 게임에서 오는 값(이동 속도,
총성 반경, sightDistance)은 CLI 로 바꿀 수 있게 두었다 — 기본값은 근사다.
"""

import argparse
import math
from dataclasses import dataclass, field

# ---------------------------------------------------------------- 설정값
# AdaptiveAISettings.cs 기본값 그대로.
SIGHT_DISTANCE_MINIMUM_M = 22.0       # SightDistanceMinimumM
SIGHT_LOS_RANGE_MULT = 1.0            # SightLosRangeMultiplier
SIGHT_ANGLE_MINIMUM_DEG = 120.0       # SightAngleMinimumDeg
SIGHT_PERIPHERAL_EXTRA_DEG = 45.0     # SightPeripheralExtraDeg
SIGHT_PERIPHERAL_ACQUIRE_MULT = 1.6   # SightPeripheralAcquireMultiplier
SIGHT_ACQUIRE_DELAY = 0.12            # SightAcquireDelaySeconds
SIGHT_MEMORY_SECONDS = 3.0            # SightMemorySeconds
SIGHT_CHECK_INTERVAL = 0.15           # SightCheckIntervalSeconds
SIGHT_SEARCH_DURATION = 8.0           # SightSearchDurationSeconds
SOUND_TRACK_MEMORY = 12.0             # SoundTrackMemorySeconds
SOUND_RETARGET_COOLDOWN = 8.0         # SoundSearchRetargetCooldownSeconds  (v1.4.0 신규)
OVERLAP_ESCAPE_DIST = 1.2             # CharacterMainControlSetMoveInputPatches.OverlapEscapeDist
MIN_DISTANCE_FROM_PLAYER = 0.0        # MinDistanceFromPlayer

# WeaponPreferredRange.cs 의 OptimalMin
WEAPON_OPTIMAL_MIN = {
    "PST": 2.0, "SMG": 2.0, "SHT": 2.5, "AR": 3.0,
    "BR": 4.0, "SNP": 5.0, "MAG": 4.0, "PWS": 3.0,
    "ARR": 3.0, "Rocket": 4.0,
}


# ---------------------------------------------------------------- 기하
def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def seg_blocks(p, q, walls):
    """p→q 직선이 벽 선분과 교차하면 True (시야 레이캐스트 대역)."""
    for (x1, y1, x2, y2) in walls:
        if _seg_intersect(p, q, (x1, y1), (x2, y2)):
            return True
    return False


def _ccw(a, b, c):
    return (c[1] - a[1]) * (b[0] - a[0]) > (b[1] - a[1]) * (c[0] - a[0])


def _seg_intersect(a, b, c, d):
    return _ccw(a, c, d) != _ccw(b, c, d) and _ccw(a, b, c) != _ccw(a, b, d)


def angle_between(forward, to_target):
    fa = math.atan2(forward[1], forward[0])
    ta = math.atan2(to_target[1], to_target[0])
    d = math.degrees(abs(fa - ta)) % 360.0
    return d if d <= 180.0 else 360.0 - d


# ---------------------------------------------------------------- 시야 상태
@dataclass
class Sight:
    """AICharacterControllerPatches.SightStateHolder 대응."""
    checked_at: float = -999.0
    raw_visible: bool = False
    raw_peripheral: bool = False
    raw_visible_since: float = -999.0
    last_seen_at: float = -999.0
    last_seen_pos: tuple = (0.0, 0.0)
    has_last_seen: bool = False
    last_engaged_at: float = -999.0
    search_handed_off: bool = False
    search_started_at: float = -999.0
    last_sound_at: float = -999.0
    last_sound_pos: tuple = (0.0, 0.0)
    has_sound: bool = False


# ---------------------------------------------------------------- 적
@dataclass
class Enemy:
    pos: tuple
    facing: tuple = (1.0, 0.0)
    gun: str = "AR"
    version: str = "1.4.0"          # "1.3.9" | "1.4.0"
    speed: float = 2.6              # m/s, 게임 값 근사 (CLI 로 조정)
    sight_distance: float = SIGHT_DISTANCE_MINIMUM_M
    hearing_radius: float = 15.0    # sound.radius × hearingAbility 근사
    reaction_delay: float = 0.30    # ai.reactionTime 근사 (0.12~0.5 클램프 안)
    st: Sight = field(default_factory=Sight)

    noticed: bool = False
    search_target: tuple = None     # 수색 중 향하는 지점
    log: list = field(default_factory=list)

    # ---- 시야 ----
    def compute_raw(self, player, walls):
        """ComputeRawSight. (보이는가, 주변시인가)"""
        d = dist(self.pos, player)
        if d < 0.01:
            return True, False
        if d > self.sight_distance * SIGHT_LOS_RANGE_MULT:
            return False, False
        to_t = (player[0] - self.pos[0], player[1] - self.pos[1])
        ang = angle_between(self.facing, to_t)
        half = SIGHT_ANGLE_MINIMUM_DEG * 0.5
        half_periph = half + SIGHT_PERIPHERAL_EXTRA_DEG
        if ang > half_periph:
            return False, False
        peripheral = ang > half
        if seg_blocks(self.pos, player, walls):
            return False, False
        return True, peripheral

    def has_raw(self, now, player, walls):
        """HasRawSightToPlayer. 캐시 간격·rising edge 보정 포함."""
        st = self.st
        if SIGHT_CHECK_INTERVAL <= 0 or now - st.checked_at >= SIGHT_CHECK_INTERVAL:
            was = st.raw_visible
            st.checked_at = now
            raw, periph = self.compute_raw(player, walls)
            if raw:
                if not was or st.raw_visible_since < 0:
                    # 캐시 간격 보정: 지난 검사와 이번 사이 어딘가에서 보이기 시작했다
                    st.raw_visible_since = now - SIGHT_CHECK_INTERVAL * 0.5
            else:
                st.raw_visible_since = -999.0
            st.raw_visible = raw
            st.raw_peripheral = periph
            if raw:
                st.last_seen_at = now
                st.last_seen_pos = player
                st.has_last_seen = True
                st.search_handed_off = False
                st.search_started_at = -999.0
        return st.raw_visible

    def has_confirmed(self, now, player, walls):
        """HasConfirmedSightToPlayer. 획득 지연과 반응 지연 중 큰 쪽."""
        if not self.has_raw(now, player, walls):
            return False
        st = self.st
        cold = (now - st.last_engaged_at) > SIGHT_MEMORY_SECONDS
        acquire = SIGHT_ACQUIRE_DELAY
        if st.raw_peripheral:
            acquire *= SIGHT_PERIPHERAL_ACQUIRE_MULT
        need = acquire
        if cold:
            need = max(need, self.reaction_delay)   # 합이 아니라 큰 쪽
        if now - st.raw_visible_since < need:
            return False
        st.last_engaged_at = now
        return True

    def can_sense(self, now, player, walls):
        """CanSensePlayer. 원본 시야 ∪ 기억(3s). (피격 예외는 시나리오에 없음)"""
        if self.has_raw(now, player, walls):
            return True
        if SIGHT_MEMORY_SECONDS > 0 and (now - self.st.last_seen_at) <= SIGHT_MEMORY_SECONDS:
            return True
        return False

    def aggro_on_player(self, now, player, walls):
        """IsAggroOnPlayer 의 마지막 분기 — noticed 인데 타겟이 없는 상태.
        게임 BT 가 직접 플레이어를 타겟으로 잡은 경우는 두 버전 모두 즉시 어그로라 제외."""
        if not self.noticed:
            return False
        if self.version == "1.3.9":
            return self.can_sense(now, player, walls)
        return self.can_sense(now, player, walls) and self.has_confirmed(now, player, walls)

    # ---- 소리 ----
    def on_sound(self, now, sound_pos):
        """RecordHeardPlayerSound."""
        if dist(self.pos, sound_pos) >= self.hearing_radius:
            return
        st = self.st
        st.last_sound_at = now
        st.last_sound_pos = sound_pos
        st.has_sound = True
        self.noticed = True
        if self.version == "1.4.0":
            # 수색 중이면 지점만 기억하고 방향을 바꾸지 않는다
            if st.search_handed_off and now - st.search_started_at < SOUND_RETARGET_COOLDOWN:
                return
        st.search_handed_off = False
        st.search_started_at = -999.0

    # ---- 수색 ----
    def tick_search(self, now, player, walls):
        """TickSightMemory."""
        st = self.st
        if self.can_sense(now, player, walls):
            return
        sight_usable = st.has_last_seen and (now - st.last_seen_at) >= SIGHT_MEMORY_SECONDS
        sound_usable = st.has_sound and (now - st.last_sound_at) <= SOUND_TRACK_MEMORY

        # 구간이 진행 중이면 만료 처리부터 — 단서가 만료됐더라도 상태는 반드시 정리한다
        if st.search_handed_off:
            if SIGHT_SEARCH_DURATION <= 0 or now - st.search_started_at < SIGHT_SEARCH_DURATION:
                return
            if self.version == "1.4.0" and sound_usable and st.last_sound_at > st.search_started_at:
                st.search_handed_off = False
                st.search_started_at = -999.0
                self.log.append((now, "구간종료→다음소리", st.last_sound_pos, ""))
                return
            st.has_last_seen = False
            st.has_sound = False
            st.search_handed_off = False
            st.search_started_at = -999.0
            self.noticed = False
            self.search_target = None
            self.log.append((now, "포기→순찰", self.pos, ""))
            return

        if not sight_usable and not sound_usable:
            return
        use_sound = sound_usable and (not sight_usable or st.last_sound_at > st.last_seen_at)
        search_pos = st.last_sound_pos if use_sound else st.last_seen_pos
        st.search_handed_off = True
        st.search_started_at = now
        self.search_target = search_pos
        self.noticed = True
        self.log.append((now, "수색시작", search_pos, "소리" if use_sound else "목격"))

    # ---- 이동 ----
    def desired_min_dist(self):
        """GetDesiredMinDistanceFromPlayer (총기 적)."""
        m = max(0.0, MIN_DISTANCE_FROM_PLAYER)
        if self.version == "1.4.0":
            m = max(m, WEAPON_OPTIMAL_MIN.get(self.gun, 3.0))
        return max(m, OVERLAP_ESCAPE_DIST)

    def step(self, now, dt, player, walls):
        aggro = self.aggro_on_player(now, player, walls)
        if aggro:
            self.search_target = None
            target = player
            stop_at = self.desired_min_dist()
        elif self.search_target is not None:
            target = self.search_target
            stop_at = 0.5
        else:
            return aggro   # 순찰 복귀 — 여기서는 제자리

        d = dist(self.pos, target)
        if d > stop_at:
            step = min(self.speed * dt, d - stop_at)
            ux = (target[0] - self.pos[0]) / d
            uy = (target[1] - self.pos[1]) / d
            moved = self._move_with_walls(ux, uy, step, walls)
            if moved is not None:
                self.pos, self.facing = moved
        return aggro

    def _move_with_walls(self, ux, uy, step, walls):
        """벽을 통과하지 않는다. 막히면 벽을 따라 미끄러진다 — 경로 탐색 대신
        모퉁이를 돌아 나가는 정도만 흉내낸다. 실제 게임 경로보다 비효율적일 수 있다."""
        cand = (self.pos[0] + ux * step, self.pos[1] + uy * step)
        if not seg_blocks(self.pos, cand, walls):
            return cand, (ux, uy)
        # 미끄러짐: 막은 벽의 방향으로 투영해 두 방향 중 목표에 가까운 쪽으로
        for (x1, y1, x2, y2) in walls:
            if not _seg_intersect(self.pos, cand, (x1, y1), (x2, y2)):
                continue
            wx, wy = x2 - x1, y2 - y1
            wl = math.hypot(wx, wy)
            if wl < 1e-6:
                continue
            wx, wy = wx / wl, wy / wl
            proj = ux * wx + uy * wy
            sx, sy = (wx, wy) if proj >= 0 else (-wx, -wy)
            slid = (self.pos[0] + sx * step, self.pos[1] + sy * step)
            if not seg_blocks(self.pos, slid, walls):
                return slid, (sx, sy)
        return None


# ---------------------------------------------------------------- 시나리오
@dataclass
class Scenario:
    name: str
    desc: str
    walls: list
    enemy_pos: tuple
    enemy_facing: tuple
    gun: str
    duration: float
    player_at: callable       # t -> (x, y)
    shots_at: list            # 발사 시각 목록
    question: str
    start_noticed: bool = False   # 이미 경계 상태(수색 중)로 시작하는가


def scen_wall_then_move():
    # 벽 뒤에서 이동하며 세 발 쏘고, 그 뒤로는 조용히 계속 빠진다.
    wall = [(-6.0, 6.0, 12.0, 6.0)]      # 동쪽 끝 x=12 로 돌아가야 한다

    def player_at(t):
        return (min(t * 2.0, 24.0), 0.0)

    return Scenario(
        name="사격 후 이탈",
        desc="플레이어가 벽 뒤에서 0/0.6/1.2초에 세 발 쏘고(그때 x=0/1.2/2.4), 이후 조용히 동진한다.",
        walls=wall,
        enemy_pos=(0.0, 13.0),
        enemy_facing=(0.0, 1.0),      # 반대쪽(북)을 보고 있다
        gun="AR",
        duration=30.0,
        player_at=player_at,
        shots_at=[0.0, 0.6, 1.2],
        question="적이 '쏜 자리'로 갔다가 포기하는가, 아니면 플레이어를 잡아내는가",
    )


def scen_sustained_fire():
    # 벽 뒤에서 계속 쏘면서 천천히 옆으로 이동한다 (교전 중 상황).
    wall = [(-6.0, 6.0, 40.0, 6.0)]

    def player_at(t):
        return (t * 1.1, 0.0)          # 초당 1.1m 로 동진하며 계속 사격

    return Scenario(
        name="이동하며 지속 사격",
        desc="플레이어가 벽 뒤에서 0.5초마다 쏘며 초당 1.1m 로 동진한다(벽이 길어 시야는 계속 막힘).",
        walls=wall,
        enemy_pos=(0.0, 13.0),
        enemy_facing=(0.0, 1.0),
        gun="AR",
        duration=26.0,
        player_at=player_at,
        shots_at=[i * 0.5 for i in range(52)],
        question="수색 지점이 플레이어 현재 위치를 따라붙는가 (추적 오차)",
    )


def scen_brief_exposure():
    # 출입구를 가로질러 순간적으로 노출된다. 노출 창 ≈ 0.25초.
    walls = [(12.0, -30.0, 12.0, -1.0), (12.0, 1.0, 12.0, 30.0)]   # y∈(-1,1) 만 뚫린 문

    def player_at(t):
        return (16.0, -7.0 + t * 10.0)   # 문 너머를 10m/s 로 가로지른다

    return Scenario(
        name="순간 노출(문틈 가로지르기)",
        desc="적이 이미 경계 상태(수색 중)이고, 플레이어가 문틈 너머를 빠르게 가로질러 0.27초쯤 노출된다.",
        walls=walls,
        enemy_pos=(0.0, 0.0),
        enemy_facing=(1.0, 0.0),
        gun="AR",
        duration=12.0,
        player_at=player_at,
        shots_at=[],
        question="스치듯 보인 것만으로 어그로가 붙는가",
        start_noticed=True,
    )


def scen_standoff(gun):
    # 트인 공간에서 정면으로 접근 — 어디서 멈추는가.
    def player_at(t):
        return (0.0, 0.0)

    return Scenario(
        name=f"접근 정지 거리 ({gun})",
        desc=f"{gun} 을 든 적이 트인 공간에서 12m 밖에서 정면으로 접근한다.",
        walls=[],
        enemy_pos=(12.0, 0.0),
        enemy_facing=(-1.0, 0.0),
        gun=gun,
        duration=14.0,
        player_at=player_at,
        shots_at=[0.0],
        question="플레이어에게서 몇 m 에서 멈추는가",
    )


# ---------------------------------------------------------------- 실행
def run(scen, version, speed, dt=0.05, trace=False):
    e = Enemy(pos=scen.enemy_pos, facing=scen.enemy_facing, gun=scen.gun,
              version=version, speed=speed)
    e.noticed = scen.start_noticed
    shots = sorted(scen.shots_at)
    si = 0
    t = 0.0
    track_err = []          # 수색 목표와 플레이어 실제 위치의 차이
    first_aggro = None
    first_glance = None
    min_gap = 1e9
    rows = []

    while t <= scen.duration + 1e-9:
        player = scen.player_at(t)

        while si < len(shots) and shots[si] <= t + 1e-9:
            e.on_sound(t, scen.player_at(shots[si]))
            si += 1

        e.tick_search(t, player, scen.walls)
        aggro = e.step(t, dt, player, scen.walls)

        raw, periph = e.compute_raw(player, scen.walls)
        if raw and first_glance is None:
            first_glance = (t, periph)
        if aggro and first_aggro is None:
            first_aggro = t

        gap = dist(e.pos, player)
        min_gap = min(min_gap, gap)
        if e.search_target is not None:
            track_err.append(dist(e.search_target, player))

        if trace and abs((t / 0.5) - round(t / 0.5)) < 1e-9:
            rows.append((t, e.pos, gap, aggro, e.search_target))
        t += dt

    return {
        "enemy": e,
        "first_glance": first_glance,
        "first_aggro": first_aggro,
        "min_gap": min_gap,
        "final_gap": dist(e.pos, scen.player_at(scen.duration)),
        "track_err_avg": (sum(track_err) / len(track_err)) if track_err else None,
        "track_err_max": max(track_err) if track_err else None,
        "rows": rows,
    }


def fmt(v, unit="", nd=2):
    return "—" if v is None else f"{v:.{nd}f}{unit}"


def sweep_cooldown(speed, values):
    """SoundSearchRetargetCooldownSeconds 를 바꿔가며 소리 추적 지연을 본다.
    0 = v1.3.9 동작(총성마다 재조준)."""
    global SOUND_RETARGET_COOLDOWN
    s = scen_sustained_fire()
    keep = SOUND_RETARGET_COOLDOWN
    print("[스윕] SoundSearchRetargetCooldownSeconds — 시나리오: 이동하며 지속 사격")
    print("  쿨다운은 '수색 한 구간이 끝나기 전에는 새 총성으로 방향을 바꾸지 않는 시간'이다.")
    print(f"  {'쿨다운(s)':>10}{'오차 평균(m)':>14}{'오차 최대(m)':>14}{'최소 접근(m)':>14}{'수색 구간 수':>13}")
    for v in values:
        SOUND_RETARGET_COOLDOWN = v
        r = run(s, "1.4.0", speed)
        legs = sum(1 for _, k, _, _ in r["enemy"].log if k == "수색시작")
        print(f"  {v:>10.1f}{fmt(r['track_err_avg']):>14}{fmt(r['track_err_max']):>14}"
              f"{fmt(r['min_gap']):>14}{legs:>13}")
    SOUND_RETARGET_COOLDOWN = keep
    print()


def sweep_standoff(speed, values):
    """MinDistanceWeaponPreferredRangeScale 을 바꿔가며 총기별 정지 거리를 본다."""
    print("[스윕] MinDistanceWeaponPreferredRangeScale — 시나리오: 접근 정지 거리")
    print("  표의 OptimalMin 에 곱하는 값. 1 = 표 그대로. 낮출수록 적이 더 붙는다.")
    guns = ["PST", "SMG", "SHT", "AR", "BR", "SNP"]
    print(f"  {'배율':>6}" + "".join(f"{g:>8}" for g in guns))
    for v in values:
        row = []
        for g in guns:
            base = WEAPON_OPTIMAL_MIN[g] * v
            row.append(max(base, OVERLAP_ESCAPE_DIST))
        print(f"  {v:>6.2f}" + "".join(f"{d:>8.2f}" for d in row))
    print("  (v1.3.9 는 무기와 무관하게 1.20m — 겹침 해소 거리뿐)")
    print()


def main():
    ap = argparse.ArgumentParser(description="AdaptiveEnemyAI 인지·수색·접근 시뮬레이터")
    ap.add_argument("--speed", type=float, default=2.6, help="적 이동 속도 m/s (기본 2.6, 게임 값 근사)")
    ap.add_argument("--trace", action="store_true", help="0.5초 간격 타임라인 출력")
    ap.add_argument("--only", type=str, default=None, help="시나리오 이름 일부로 필터")
    ap.add_argument("--sweep", action="store_true", help="파라미터 스윕 표만 출력")
    args = ap.parse_args()

    if args.sweep:
        print(f"이동 속도 {args.speed} m/s · dt 0.05s\n")
        sweep_cooldown(args.speed, [0.0, 2.0, 4.0, 6.0, 8.0, 12.0])
        sweep_standoff(args.speed, [0.6, 0.8, 1.0, 1.2, 1.5])
        return

    scens = [
        scen_wall_then_move(),
        scen_sustained_fire(),
        scen_brief_exposure(),
        scen_standoff("SHT"),
        scen_standoff("AR"),
        scen_standoff("SNP"),
    ]
    if args.only:
        scens = [s for s in scens if args.only in s.name]

    print(f"이동 속도 {args.speed} m/s · dt 0.05s · 수치는 AdaptiveAISettings.cs 기본값\n")

    for s in scens:
        print("=" * 72)
        print(f"[{s.name}]  {s.desc}")
        print(f"  묻는 것: {s.question}")
        old = run(s, "1.3.9", args.speed, trace=args.trace)
        new = run(s, "1.4.0", args.speed, trace=args.trace)

        print(f"  {'':22}{'v1.3.9':>14}{'v1.4.0':>14}")
        print(f"  {'첫 목격(초)':22}{fmt(old['first_glance'][0] if old['first_glance'] else None,'s'):>14}"
              f"{fmt(new['first_glance'][0] if new['first_glance'] else None,'s'):>14}")
        print(f"  {'어그로 성립(초)':22}{fmt(old['first_aggro'],'s'):>14}{fmt(new['first_aggro'],'s'):>14}")
        print(f"  {'최소 접근 거리(m)':22}{fmt(old['min_gap'],'m'):>14}{fmt(new['min_gap'],'m'):>14}")
        print(f"  {'종료 시점 거리(m)':22}{fmt(old['final_gap'],'m'):>14}{fmt(new['final_gap'],'m'):>14}")
        if s.shots_at and (old["track_err_avg"] is not None or new["track_err_avg"] is not None):
            print(f"  {'수색목표 오차 평균(m)':21}{fmt(old['track_err_avg'],'m'):>14}{fmt(new['track_err_avg'],'m'):>14}")
            print(f"  {'수색목표 오차 최대(m)':21}{fmt(old['track_err_max'],'m'):>14}{fmt(new['track_err_max'],'m'):>14}")

        for label, r in (("v1.3.9", old), ("v1.4.0", new)):
            if r["enemy"].log:
                events = "  ".join(f"{t:.1f}s {k}" + (f"({p[0]:.1f},{p[1]:.1f})" if k != "포기→순찰" else "")
                                   for t, k, p, _ in r["enemy"].log[:6])
                print(f"    {label} 이벤트: {events}")

        if args.trace:
            for label, r in (("v1.3.9", old), ("v1.4.0", new)):
                print(f"    --- {label} 타임라인")
                for t, pos, gap, aggro, tgt in r["rows"]:
                    tg = f" →({tgt[0]:.1f},{tgt[1]:.1f})" if tgt else ""
                    print(f"      {t:5.1f}s  적({pos[0]:6.2f},{pos[1]:6.2f})  거리{gap:6.2f}m  "
                          f"{'어그로' if aggro else '      '}{tg}")
        print()


if __name__ == "__main__":
    main()
