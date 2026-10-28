//! Порт `Network/NetworkBuilder.Connectors.cs`: рукава узлов (по часовой стрелке) и connector-полосы для манёвров «въезд -> выезд».

use crate::api::desc::{NodeControl, RoadKind};
use crate::util::Vec2;
use crate::util::detmath::{max_net, min_net};

use super::builder::{MAX_CONNECTORS_PER_NODE, NetworkBuilder};
use super::store::NetworkStore;
use super::types::{ConnectorRules, LaneData, LaneKind, Maneuver, ManeuverContext, NetworkStatus};

/// cos(30 град): поворот до 30 град - «прямо».
pub const STRAIGHT_COS: f32 = 0.8660254;
/// Бит `ArmFlags`: рукав второстепенный.
pub const ARM_MINOR_BIT: u8 = 1;

const LATERAL_ACCEL: f32 = 2.0; // м/с2, intersections.md §1
const MIN_CONNECTOR_SPEED: f32 = 2.5;
const COS_TEN_DEGREES: f32 = 0.9848078;

/// Монотонная по углу функция [0, 4) без тригонометрии (кросс-платформенный детерминизм).
fn pseudo_angle(v: Vec2) -> f32 {
    let p = v.z / (v.x.abs() + v.z.abs());
    if v.x < 0.0 {
        return 2.0 - p;
    }
    if v.z < 0.0 { 4.0 + p } else { p }
}

fn dist(a: Vec2, b: Vec2) -> f32 {
    let dx = b.x - a.x;
    let dz = b.z - a.z;
    ((dx * dx) + (dz * dz)).sqrt()
}

fn normalize(v: Vec2) -> Vec2 {
    let inv = 1.0_f32 / ((v.x * v.x) + (v.z * v.z)).sqrt();
    v * inv
}

/// Класс манёвра по направлениям рукавов от узла (intersections.md §2.1): |turn| <= 30 град - прямо, иначе по знаку cross
/// вправо/влево. На узле из двух рукавов (изгиб) любой проезд - `Straight`.
pub fn classify_turn(arm_dir_in: Vec2, arm_dir_out: Vec2, two_arms: bool) -> Maneuver {
    if two_arms {
        return Maneuver::Straight;
    }
    let u = arm_dir_in * -1.0_f32;
    let dot = (u.x * arm_dir_out.x) + (u.z * arm_dir_out.z);
    if dot >= STRAIGHT_COS {
        return Maneuver::Straight;
    }
    let cross = (u.x * arm_dir_out.z) - (u.z * arm_dir_out.x);
    if cross >= 0.0 { Maneuver::Right } else { Maneuver::Left }
}

impl<R: ConnectorRules> NetworkBuilder<R> {
    /// `build_connectors` для всех узлов, где он ещё не вызывался; первая ошибка прерывает обход.
    pub fn build_all_connectors(&mut self, s: &mut NetworkStore) -> NetworkStatus {
        for n in 0..s.node_count {
            if s.nodes[n as usize].first_arm >= 0 {
                continue;
            }
            let st = self.build_connectors(s, n);
            if st != NetworkStatus::Ok {
                return st;
            }
        }
        NetworkStatus::Ok
    }

    /// Строит рукава узла и connector-полосы для всех манёвров «въезд -> выезд» по `ConnectorRules`; разворот - только на тупике.
    /// Connector - кубическая Безье, сэмплируется в `connector_samples` точек. Атомарно: при ошибке хранилище не меняется.
    pub fn build_connectors(&mut self, s: &mut NetworkStore, node: i32) -> NetworkStatus {
        if node < 0 || node >= s.node_count {
            return NetworkStatus::InvalidArgument;
        }
        let ni = node as usize;
        if s.nodes[ni].first_arm >= 0 {
            return NetworkStatus::NodeAlreadyBuilt;
        }

        let arms = self.gather_arms(s, node);
        if arms < 0 {
            return NetworkStatus::TooManyArms;
        }
        if s.node_arm_count as usize + arms as usize > s.node_arms.len() {
            return NetworkStatus::PointsFull;
        }

        let control = s.nodes[ni].control;
        let plan = self.plan_connectors(s, control, arms);
        if plan < 0 {
            return NetworkStatus::TooManyConnectors;
        }

        let samples = self.o.connector_samples;
        if s.lane_count as usize + plan as usize > s.lanes.len() {
            return NetworkStatus::LanesFull;
        }
        if s.lane_point_count as usize + (plan as usize * samples as usize) > s.lane_points.len()
            || s.lane_next_count as usize + (2 * plan as usize) > s.lane_next.len()
        {
            return NetworkStatus::PointsFull;
        }

        // ---- запись (ошибок дальше нет) ----
        let first_arm = s.node_arm_count;
        for a in 0..arms {
            s.node_arms[(first_arm + a) as usize] = self.arm_seg[a as usize];
        }
        s.node_arm_count += arms;

        let base_lane = s.lane_count;
        let block_in = s.lane_next_count;
        let block_own = block_in + plan;
        for p in 0..plan {
            let in_lane = self.plan_in[p as usize];
            let out_lane = self.plan_out[p as usize];
            let lane_idx = base_lane + p;

            {
                let li = &mut s.lanes[in_lane as usize];
                if li.next_count == 0 {
                    li.first_next = block_in + p;
                }
                li.next_count += 1;
            }
            s.lane_next[(block_in + p) as usize] = lane_idx;
            s.lane_next[(block_own + p) as usize] = out_lane;

            let first = s.lane_point_count + (p * samples);
            let li = s.lanes[in_lane as usize];
            let lo = s.lanes[out_lane as usize];
            let (length, speed) = self.emit_bezier(
                s,
                &li,
                &lo,
                first as usize,
                samples as usize,
                self.plan_man[p as usize] == Maneuver::UTurn,
            );
            s.lane_length[lane_idx as usize] = length;
            s.lanes[lane_idx as usize] = LaneData {
                kind: LaneKind::Connector,
                lane_no: 0,
                direction: 0,
                flags: 0,
                segment: -1,
                node,
                from_lane: in_lane,
                to_lane: out_lane,
                length,
                speed_limit: speed,
                first_point: first,
                point_count: samples,
                left: -1,
                right: -1,
                first_next: block_own + p,
                next_count: 1,
                conflict_group: -1,
                generation: 1,
                width: 0.0,
            };
        }

        s.lane_next_count += 2 * plan;
        s.lane_count += plan;
        s.lane_point_count += plan * samples;

        for a in 0..arms {
            s.arm_dir[(first_arm + a) as usize] = self.arm_dir[a as usize];
        }

        let nd = &mut s.nodes[ni];
        nd.first_arm = first_arm;
        nd.arm_count = arms;
        nd.first_connector = base_lane;
        nd.connector_count = plan;
        self.refresh_arm_flags(s, ni);
        s.refresh_lane_gate_node(ni);
        s.version += 1;
        NetworkStatus::Ok
    }

    /// Собирает рукава узла в `arm_*` по возрастанию «псевдоугла» (по часовой стрелке в X-Z); -1 при переполнении.
    fn gather_arms(&mut self, s: &NetworkStore, node: i32) -> i32 {
        let mut arms = 0usize;
        let mut code = s.inc_head[node as usize];
        while code >= 0 {
            if arms == NetworkStore::MAX_ARMS {
                return -1;
            }
            let sd = s.segments[(code >> 1) as usize];
            let p0 = sd.first_point as usize;
            let mut v = if (code & 1) == 0 {
                s.segment_points[p0 + 1] - s.segment_points[p0]
            } else {
                s.segment_points[p0 + sd.point_count as usize - 2] - s.segment_points[p0 + sd.point_count as usize - 1]
            };
            let inv = 1.0_f32 / ((v.x * v.x) + (v.z * v.z)).sqrt();
            v = v * inv;
            let key = pseudo_angle(v);

            let mut pos = arms;
            arms += 1;
            while pos > 0 && (self.arm_key[pos - 1] > key || (self.arm_key[pos - 1] == key && self.arm_code[pos - 1] > code)) {
                self.arm_key[pos] = self.arm_key[pos - 1];
                self.arm_code[pos] = self.arm_code[pos - 1];
                self.arm_seg[pos] = self.arm_seg[pos - 1];
                self.arm_dir[pos] = self.arm_dir[pos - 1];
                pos -= 1;
            }
            self.arm_key[pos] = key;
            self.arm_code[pos] = code;
            self.arm_seg[pos] = code >> 1;
            self.arm_dir[pos] = v;

            code = s.inc_next[code as usize];
        }
        arms as i32
    }

    /// Первая полоса и число полос рукава `arm` во входящем (`incoming`) или исходящем направлении.
    fn arm_lanes(&self, s: &NetworkStore, arm: usize, incoming: bool) -> (i32, i32) {
        let sd = &s.segments[self.arm_seg[arm] as usize];
        let at_a = (self.arm_code[arm] & 1) == 0;
        // Входящие в узел: у конца A едут B->A, у конца B - A->B. Исходящие - наоборот.
        let ab = at_a != incoming;
        if ab {
            (sd.first_lane_ab, i32::from(sd.lanes_ab))
        } else {
            (sd.first_lane_ba, i32::from(sd.lanes_ba))
        }
    }

    fn classify(&self, arm_in: usize, arm_out: usize, arms: i32) -> Maneuver {
        classify_turn(self.arm_dir[arm_in], self.arm_dir[arm_out], arms == 2)
    }

    /// `ArmMinor` рукавов и нормализация `control` (intersections.md §2.3). Для узлов с `arm_count <= 2` `control` не меняется.
    pub(crate) fn refresh_arm_flags(&self, s: &mut NetworkStore, node: usize) {
        let nd = s.nodes[node];
        let arms = nd.arm_count;
        let f = nd.first_arm;
        let mut any_arterial = false;
        let mut any_street = false;
        for a in 0..arms {
            if s.segments[s.node_arms[(f + a) as usize] as usize].kind == RoadKind::Arterial {
                any_arterial = true;
            } else {
                any_street = true;
            }
            s.arm_flags[(f + a) as usize] = 0;
        }

        let mut any_minor = false;
        if any_arterial && any_street {
            for a in 0..arms {
                if s.segments[s.node_arms[(f + a) as usize] as usize].kind == RoadKind::Street {
                    s.arm_flags[(f + a) as usize] = ARM_MINOR_BIT;
                    any_minor = true;
                }
            }
        } else if arms == 3 {
            let mut lonely = -1;
            let mut lonely_count = 0;
            for a in 0..arms {
                let u = s.arm_dir[(f + a) as usize] * -1.0_f32;
                let mut has_opposite = false;
                let mut b = 0;
                while b < arms && !has_opposite {
                    if b != a {
                        let v = s.arm_dir[(f + b) as usize];
                        has_opposite = (u.x * v.x) + (u.z * v.z) >= STRAIGHT_COS;
                    }
                    b += 1;
                }
                if !has_opposite {
                    lonely = a;
                    lonely_count += 1;
                }
            }
            if lonely_count == 1 {
                s.arm_flags[(f + lonely) as usize] = ARM_MINOR_BIT;
                any_minor = true;
            }
        }

        if arms >= 3 {
            let ctl = s.nodes[node].control;
            let no_rules = ctl == NodeControl::None;
            let need_minor = ctl == NodeControl::Yield || ctl == NodeControl::Stop;
            if no_rules || (need_minor && !any_minor) {
                s.nodes[node].control = NodeControl::RightHandRule;
            }
        }
    }

    /// Заполняет `plan_*` в порядке (рукав, полоса, манёвр: прямо, направо, налево, разворот); -1 при переполнении.
    fn plan_connectors(&mut self, s: &NetworkStore, control: NodeControl, arms: i32) -> i32 {
        let mut plan = 0usize;
        for ai in 0..arms as usize {
            let (in_first, in_count) = self.arm_lanes(s, ai, true);
            let in_kind = s.segments[self.arm_seg[ai] as usize].kind;
            for li in 0..in_count {
                let before = plan;
                let mut pass = 0;
                while pass < 2 && plan == before {
                    let relaxed = pass == 1;
                    for m in Maneuver::ALL {
                        for aj in 0..arms as usize {
                            if aj == ai {
                                if m != Maneuver::UTurn || arms != 1 {
                                    continue;
                                }
                            } else if m == Maneuver::UTurn || self.classify(ai, aj, arms) != m {
                                continue;
                            }

                            let (out_first, out_count) = self.arm_lanes(s, aj, false);
                            if out_count == 0 {
                                continue;
                            }

                            let out_kind = s.segments[self.arm_seg[aj] as usize].kind;
                            let ctx = ManeuverContext {
                                maneuver: m,
                                in_lane_count: in_count,
                                out_lane_count: out_count,
                                control,
                                in_kind,
                                out_kind,
                                relaxed,
                            };
                            let target = self.rules.target_lane(&ctx, li);
                            if target < 0 || target >= out_count {
                                continue;
                            }

                            // Прямо при n_out > n_in: старшая входная полоса расходится (diverge) на все лишние выходные.
                            let hi = if m == Maneuver::Straight && li == in_count - 1 && out_count > in_count {
                                out_count - 1
                            } else {
                                target
                            };
                            for t in target..=hi {
                                if plan == MAX_CONNECTORS_PER_NODE {
                                    return -1;
                                }
                                self.plan_in[plan] = in_first + li;
                                self.plan_out[plan] = out_first + t;
                                self.plan_man[plan] = m;
                                plan += 1;
                            }
                        }
                    }
                    pass += 1;
                }
            }
        }
        plan as i32
    }

    /// Полилиния connector'а (Безье P0 = конец входной полосы, P3 = начало выходной) -> `lane_points`/`lane_point_s`/курсы;
    /// возвращает (длина, лимит скорости).
    fn emit_bezier(
        &self,
        s: &mut NetworkStore,
        lane_in: &LaneData,
        lane_out: &LaneData,
        first: usize,
        samples: usize,
        uturn: bool,
    ) -> (f32, f32) {
        let i_end = (lane_in.first_point + lane_in.point_count - 1) as usize;
        let p0 = s.lane_points[i_end];
        let t0 = normalize(p0 - s.lane_points[i_end - 1]);
        let fo = lane_out.first_point as usize;
        let p3 = s.lane_points[fo];
        let t3 = normalize(s.lane_points[fo + 1] - p3);
        let chord = dist(p0, p3);
        let d = if uturn {
            max_net(chord, 2.0 * lane_in.width)
        } else {
            chord * (1.0_f32 / 3.0_f32)
        };
        let p1 = p0 + (t0 * d);
        let p2 = p3 - (t3 * d);

        let mut sum = 0.0_f32;
        let mut prev = p0;
        for k in 0..samples {
            let pt = if k == 0 {
                p0
            } else if k == samples - 1 {
                p3
            } else {
                let t = k as f32 / (samples - 1) as f32;
                let u = 1.0_f32 - t;
                let b0 = u * u * u;
                let b1 = 3.0_f32 * u * u * t;
                let b2 = 3.0_f32 * u * t * t;
                let b3 = t * t * t;
                Vec2::new(
                    (b0 * p0.x) + (b1 * p1.x) + (b2 * p2.x) + (b3 * p3.x),
                    (b0 * p0.z) + (b1 * p1.z) + (b2 * p2.z) + (b3 * p3.z),
                )
            };
            sum += dist(prev, pt);
            prev = pt;
            s.lane_points[first + k] = pt;
            s.lane_point_s[first + k] = sum;
        }

        s.fill_piece_headings(first, samples);

        // Лимит скорости: sqrt(a_lat * R), R = chord / (2 sin(|turn|/2)) при |turn| >= 10 град; sin(turn/2) = sqrt((1 - cos)/2) без тригонометрии.
        let cos_turn = (t0.x * t3.x) + (t0.z * t3.z);
        let mut limit = lane_in.speed_limit;
        if uturn || cos_turn <= COS_TEN_DEGREES {
            let r = if uturn {
                lane_in.width
            } else {
                chord / (2.0_f32 * max_net(0.0, (1.0_f32 - cos_turn) * 0.5_f32).sqrt())
            };
            let v = max_net(MIN_CONNECTOR_SPEED, (LATERAL_ACCEL * r).sqrt());
            limit = min_net(limit, v);
        }
        (sum, limit)
    }
}
