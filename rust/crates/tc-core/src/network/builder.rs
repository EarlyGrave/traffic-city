//! Порт `Network/NetworkBuilder.cs`: построение сети в `NetworkStore` (узлы со snap, сегменты с N полосами в каждую сторону).
//! Исключений нет: нехватка ёмкости / неверные аргументы возвращаются как `NetworkStatus`. Рабочие буферы выделяются один раз в `new`.
//! Connector'ы - `connectors.rs` (`NetworkBuilder.Connectors.cs`).
//!
//! Float-правила (rust-pilot.md §6): без FMA, порядок операций - как в C#, `max/min` - только `detmath::*_net`.

use crate::api::desc::{NetworkOptions, NodeControl, RoadKind};
use crate::util::Vec2;

use super::connectors::STRAIGHT_COS;
use super::store::NetworkStore;
use super::types::{ConnectorRules, LaneData, LaneKind, Maneuver, NetworkStatus, NodeData, SegmentData};

pub const MAX_VIA: usize = 12;
pub const MAX_LANES_PER_DIRECTION: i32 = 8;
const MAX_AXIS: usize = MAX_VIA + 2;
/// Предел connector'ов на узел (`LocalBit` - 6 бит, `PhaseMask` - ulong; intersections.md §2.2).
pub const MAX_CONNECTORS_PER_NODE: usize = 64;

pub struct NetworkBuilder<R: ConnectorRules> {
    pub(crate) o: NetworkOptions,
    pub(crate) rules: R,

    raw: [Vec2; MAX_AXIS],
    raw_s: [f32; MAX_AXIS],
    axis: [Vec2; MAX_AXIS],
    axis_s: [f32; MAX_AXIS],
    miter: [Vec2; MAX_AXIS],

    pub(crate) arm_seg: [i32; NetworkStore::MAX_ARMS],
    pub(crate) arm_code: [i32; NetworkStore::MAX_ARMS],
    pub(crate) arm_key: [f32; NetworkStore::MAX_ARMS],
    pub(crate) arm_dir: [Vec2; NetworkStore::MAX_ARMS],

    pub(crate) plan_in: [i32; MAX_CONNECTORS_PER_NODE],
    pub(crate) plan_out: [i32; MAX_CONNECTORS_PER_NODE],
    pub(crate) plan_man: [Maneuver; MAX_CONNECTORS_PER_NODE],
}

fn is_finite(p: Vec2) -> bool {
    p.x.is_finite() && p.z.is_finite()
}

fn dist(a: Vec2, b: Vec2) -> f32 {
    let dx = b.x - a.x;
    let dz = b.z - a.z;
    ((dx * dx) + (dz * dz)).sqrt()
}

fn right_normal(a: Vec2, b: Vec2) -> Vec2 {
    let dx = b.x - a.x;
    let dz = b.z - a.z;
    let inv = 1.0_f32 / ((dx * dx) + (dz * dz)).sqrt();
    Vec2::new(-dz * inv, dx * inv)
}

impl<R: ConnectorRules> NetworkBuilder<R> {
    pub fn new(options: NetworkOptions, rules: R) -> Self {
        let mut o = options;
        if o.connector_samples < 2 {
            o.connector_samples = 2;
        }
        let z = Vec2::new(0.0, 0.0);
        Self {
            o,
            rules,
            raw: [z; MAX_AXIS],
            raw_s: [0.0; MAX_AXIS],
            axis: [z; MAX_AXIS],
            axis_s: [0.0; MAX_AXIS],
            miter: [z; MAX_AXIS],
            arm_seg: [0; NetworkStore::MAX_ARMS],
            arm_code: [0; NetworkStore::MAX_ARMS],
            arm_key: [0.0; NetworkStore::MAX_ARMS],
            arm_dir: [z; NetworkStore::MAX_ARMS],
            plan_in: [0; MAX_CONNECTORS_PER_NODE],
            plan_out: [0; MAX_CONNECTORS_PER_NODE],
            plan_man: [Maneuver::Straight; MAX_CONNECTORS_PER_NODE],
        }
    }

    pub fn options(&self) -> &NetworkOptions {
        &self.o
    }

    fn cell(&self, v: f32) -> i32 {
        (v / self.o.snap_radius).floor() as i32
    }

    fn cell_key(cx: i32, cz: i32) -> i64 {
        (i64::from(cx) << 32) ^ i64::from(cz as u32)
    }

    /// Индекс существующего узла в радиусе `snap_radius` (ближайший, при равенстве - меньший индекс) или -1.
    pub fn find_node(&self, s: &NetworkStore, p: Vec2) -> i32 {
        let cx = self.cell(p.x);
        let cz = self.cell(p.z);
        let r2 = self.o.snap_radius * self.o.snap_radius;
        let mut best = -1;
        let mut best_d = f32::MAX;
        for dx in -1..=1 {
            for dz in -1..=1 {
                let Some(mut n) = s.cells.get(Self::cell_key(cx.wrapping_add(dx), cz.wrapping_add(dz))) else {
                    continue;
                };
                while n >= 0 {
                    let q = s.nodes[n as usize].position;
                    let ex = q.x - p.x;
                    let ez = q.z - p.z;
                    let d = (ex * ex) + (ez * ez);
                    if d <= r2 && (d < best_d || (d == best_d && n < best)) {
                        best = n;
                        best_d = d;
                    }
                    n = s.cell_next[n as usize];
                }
            }
        }
        best
    }

    /// Добавляет узел или возвращает существующий в радиусе `snap_radius` (snap). `control` существующего не меняется.
    pub fn add_node(&self, s: &mut NetworkStore, position: Vec2, control: NodeControl) -> Result<i32, NetworkStatus> {
        if !is_finite(position) {
            return Err(NetworkStatus::InvalidArgument);
        }
        let found = self.find_node(s, position);
        if found >= 0 {
            return Ok(found);
        }
        if s.node_count as usize >= s.nodes.len() {
            return Err(NetworkStatus::NodesFull);
        }
        let idx = s.node_count;
        let key = Self::cell_key(self.cell(position.x), self.cell(position.z));
        let head = s.cells.get(key).unwrap_or(-1);
        if !s.cells.try_set(key, idx) {
            return Err(NetworkStatus::NodesFull);
        }
        s.cell_next[idx as usize] = head;
        s.inc_head[idx as usize] = -1;
        s.nodes[idx as usize] = NodeData {
            position,
            elevation: 0.0,
            control,
            first_arm: -1,
            arm_count: 0,
            first_connector: 0,
            connector_count: 0,
            signal: -1,
            zone: -1,
            radius: self.o.node_setback,
            generation: 1,
        };
        s.node_count = idx + 1;
        s.version += 1;
        Ok(idx)
    }

    /// Сегмент A->B, ось проходит через `via`, с отступами концов `setback_a`/`setback_b` от осей узлов (ADR-0028, C# `AddSegment` с отступами):
    /// ось короче `2 * node_setback + min_lane_length` - `SegmentTooShort`; если короче суммы отступов + `min_lane_length`, надбавки сверх `node_setback`
    /// сжимаются пропорционально. `radius` концов - максимум отступов их рукавов. `SegmentData::length` - длина обрезанной оси (= длина всех Normal-полос сегмента).
    #[allow(clippy::too_many_arguments, clippy::neg_cmp_op_on_partial_ord)]
    pub fn add_segment(
        &mut self,
        s: &mut NetworkStore,
        node_a: i32,
        node_b: i32,
        via: &[Vec2],
        lanes_ab: i32,
        lanes_ba: i32,
        speed_limit: f32,
        kind: RoadKind,
        setback_a: f32,
        setback_b: f32,
    ) -> Result<i32, NetworkStatus> {
        if node_a < 0
            || node_a >= s.node_count
            || node_b < 0
            || node_b >= s.node_count
            || node_a == node_b
            || lanes_ab < 0
            || lanes_ba < 0
            || lanes_ab > MAX_LANES_PER_DIRECTION
            || lanes_ba > MAX_LANES_PER_DIRECTION
            || lanes_ab + lanes_ba == 0
            || !(speed_limit > 0.0)
            || speed_limit.is_infinite()
            || via.len() > MAX_VIA
            || !setback_a.is_finite()
            || !setback_b.is_finite()
        {
            return Err(NetworkStatus::InvalidArgument);
        }

        // Сырая ось.
        let mut n = 0usize;
        self.raw[n] = s.nodes[node_a as usize].position;
        n += 1;
        for v in via {
            if !is_finite(*v) {
                return Err(NetworkStatus::InvalidArgument);
            }
            self.raw[n] = *v;
            n += 1;
        }
        self.raw[n] = s.nodes[node_b as usize].position;
        n += 1;
        self.raw_s[0] = 0.0;
        for i in 1..n {
            let len = dist(self.raw[i - 1], self.raw[i]);
            if len < 0.01 {
                return Err(NetworkStatus::InvalidArgument);
            }
            self.raw_s[i] = self.raw_s[i - 1] + len;
        }

        let total = self.raw_s[n - 1];
        let setback = self.o.node_setback;
        if total < (2.0 * setback) + self.o.min_lane_length {
            return Err(NetworkStatus::SegmentTooShort);
        }

        let (setback_a, setback_b) = self.clamp_setbacks(total, setback_a, setback_b);

        // Обрезка по отступам.
        let start = setback_a;
        let end = total - setback_b;
        let mut m = 0usize;
        self.axis[m] = self.point_at(start, n);
        self.axis_s[m] = 0.0;
        m += 1;
        for i in 1..n - 1 {
            if self.raw_s[i] > start + 0.01 && self.raw_s[i] < end - 0.01 {
                self.axis[m] = self.raw[i];
                self.axis_s[m] = self.raw_s[i] - start;
                m += 1;
            }
        }
        let length = end - start;
        self.axis[m] = self.point_at(end, n);
        self.axis_s[m] = length;
        m += 1;

        let lane_total = lanes_ab + lanes_ba;
        let point_total = lane_total as usize * m;
        if s.segment_count as usize >= s.segments.len() {
            return Err(NetworkStatus::SegmentsFull);
        }
        if s.lane_count as usize + lane_total as usize > s.lanes.len() {
            return Err(NetworkStatus::LanesFull);
        }
        if s.lane_point_count as usize + point_total > s.lane_points.len() || s.segment_point_count as usize + m > s.segment_points.len() {
            return Err(NetworkStatus::PointsFull);
        }

        // Miter-векторы оси: смещение вправо (по ходу A->B) на 1 м.
        self.compute_miters(m);

        let seg = s.segment_count;
        let first_lane = s.lane_count;
        let first_seg_point = s.segment_point_count;
        for i in 0..m {
            s.segment_points[first_seg_point as usize + i] = self.axis[i];
        }
        s.segment_point_count += m as i32;

        let half_width = lane_total as f32 * self.o.lane_width * 0.5;
        for k in 0..lanes_ab {
            let x = half_width - ((k as f32 + 0.5) * self.o.lane_width); // вправо от оси в системе A->B
            self.emit_lane(s, first_lane + k, seg, k, 0, lanes_ab, x, m, length, speed_limit);
        }
        for k in 0..lanes_ba {
            let x = -(half_width - ((k as f32 + 0.5) * self.o.lane_width));
            self.emit_lane(s, first_lane + lanes_ab + k, seg, k, 1, lanes_ba, x, m, length, speed_limit);
        }

        s.segments[seg as usize] = SegmentData {
            node_a,
            node_b,
            length,
            speed_limit,
            kind,
            lanes_ab: lanes_ab as u8,
            lanes_ba: lanes_ba as u8,
            reserved: 0,
            first_lane_ab: if lanes_ab > 0 { first_lane } else { -1 },
            first_lane_ba: if lanes_ba > 0 { first_lane + lanes_ab } else { -1 },
            first_point: first_seg_point,
            point_count: m as i32,
            generation: 1,
        };

        // Отступ стоп-линий узла (radius) - наибольший из отступов его рукавов.
        let (a, b) = (node_a as usize, node_b as usize);
        if setback_a > s.nodes[a].radius {
            s.nodes[a].radius = setback_a;
        }
        if setback_b > s.nodes[b].radius {
            s.nodes[b].radius = setback_b;
        }

        // Инцидентность (для build_connectors).
        s.inc_next[2 * seg as usize] = s.inc_head[a];
        s.inc_head[a] = 2 * seg;
        s.inc_next[(2 * seg as usize) + 1] = s.inc_head[b];
        s.inc_head[b] = (2 * seg) + 1;

        s.segment_count = seg + 1;
        s.lane_count += lane_total;
        s.lane_point_count += point_total as i32;
        s.version += 1;
        Ok(seg)
    }

    #[allow(clippy::too_many_arguments)]
    fn emit_lane(
        &self,
        s: &mut NetworkStore,
        lane_idx: i32,
        seg: i32,
        lane_no: i32,
        direction: i32,
        lane_count_in_dir: i32,
        x: f32,
        m: usize,
        length: f32,
        limit: f32,
    ) {
        let first = s.lane_point_count as usize + ((lane_idx - s.lane_count) as usize * m);
        for i in 0..m {
            // Направление B->A: те же точки в обратном порядке, s = length - s_оси.
            let src = if direction == 0 { i } else { m - 1 - i };
            s.lane_points[first + i] = self.axis[src] + (self.miter[src] * x);
            s.lane_point_s[first + i] = if direction == 0 {
                self.axis_s[src]
            } else {
                length - self.axis_s[src]
            };
        }
        s.lane_point_s[first] = 0.0;
        s.lane_point_s[first + m - 1] = length;
        s.fill_piece_headings(first, m);
        s.lane_length[lane_idx as usize] = length;
        s.lanes[lane_idx as usize] = LaneData {
            kind: LaneKind::Normal,
            lane_no: lane_no as u8,
            direction: direction as u8,
            flags: 0,
            segment: seg,
            node: -1,
            from_lane: -1,
            to_lane: -1,
            length,
            speed_limit: limit,
            first_point: first as i32,
            point_count: m as i32,
            right: if lane_no > 0 { lane_idx - 1 } else { -1 },
            left: if lane_no < lane_count_in_dir - 1 { lane_idx + 1 } else { -1 },
            first_next: 0,
            next_count: 0,
            conflict_group: -1,
            generation: 1,
        };
    }

    fn compute_miters(&mut self, m: usize) {
        // Правая нормаль направления d = (dx, dz): (-dz, dx) (в плоскости X-Z: для +X вправо = +Z).
        for i in 0..m {
            let n_prev = if i > 0 {
                right_normal(self.axis[i - 1], self.axis[i])
            } else {
                Vec2::new(0.0, 0.0)
            };
            let n_next = if i < m - 1 {
                right_normal(self.axis[i], self.axis[i + 1])
            } else {
                Vec2::new(0.0, 0.0)
            };
            if i == 0 {
                self.miter[i] = n_next;
            } else if i == m - 1 {
                self.miter[i] = n_prev;
            } else {
                let mut denom = 1.0_f32 + (n_prev.x * n_next.x) + (n_prev.z * n_next.z);
                if denom < 0.25 {
                    denom = 0.25;
                }
                self.miter[i] = (n_prev + n_next) * (1.0_f32 / denom);
            }
        }
    }

    // ---- отступы рукавов (ADR-0028, C# NetworkBuilder.Setback.cs) ----

    fn half_width(&self, lane_total: i32) -> f32 {
        lane_total as f32 * self.o.lane_width * 0.5
    }

    /// Отступ рукава узла степени `degree` при полуширине самой широкой поперечной дороги `cross_half` (0 - поперечных нет).
    #[allow(clippy::neg_cmp_op_on_partial_ord)]
    pub fn arm_setback(o: &NetworkOptions, degree: i32, cross_half: f32) -> f32 {
        if !(o.corner_radius > 0.0) || degree < 3 || !(cross_half > 0.0) {
            return o.node_setback;
        }
        let s = cross_half + o.corner_radius;
        if s > o.node_setback { s } else { o.node_setback }
    }

    /// Рукав `v` поперечен рукаву `u` (направления от узла): не его продолжение «прямо».
    fn crosses(u: Vec2, v: Vec2) -> bool {
        (u.x * v.x) + (u.z * v.z) > -STRAIGHT_COS
    }

    fn dir(from: Vec2, to: Vec2) -> Vec2 {
        let dx = to.x - from.x;
        let dz = to.z - from.z;
        let len = ((dx * dx) + (dz * dz)).sqrt();
        if len > 0.0 {
            Vec2::new(dx / len, dz / len)
        } else {
            Vec2::new(0.0, 0.0)
        }
    }

    /// Отступы концов прямых сегментов `a[e] -> b[e]` (узлы уже в хранилище) по полному списку рукавов (явная сеть): `(setback_a, setback_b)` на сегмент.
    /// Сегменты с неверными узлами получают `node_setback` и в рукава не идут.
    pub fn plan_setbacks(&self, s: &NetworkStore, a: &[i32], b: &[i32], lane_total: &[i32]) -> Vec<(f32, f32)> {
        let m = a.len();
        let nodes = s.node_count;
        let valid = |na: i32, nb: i32| na >= 0 && nb >= 0 && na < nodes && nb < nodes && na != nb;
        let mut head = vec![-1_i32; nodes.max(0) as usize];
        let mut next = vec![-1_i32; 2 * m];
        let mut degree = vec![0_i32; nodes.max(0) as usize];
        let mut dir = vec![Vec2::new(0.0, 0.0); 2 * m];
        let mut half = vec![0.0_f32; m];
        for e in 0..m {
            let (na, nb) = (a[e], b[e]);
            half[e] = self.half_width(lane_total[e]);
            if !valid(na, nb) {
                continue;
            }
            let pa = s.nodes[na as usize].position;
            let pb = s.nodes[nb as usize].position;
            dir[2 * e] = Self::dir(pa, pb);
            dir[(2 * e) + 1] = Self::dir(pb, pa);
            next[2 * e] = head[na as usize];
            head[na as usize] = (2 * e) as i32;
            next[(2 * e) + 1] = head[nb as usize];
            head[nb as usize] = ((2 * e) + 1) as i32;
            degree[na as usize] += 1;
            degree[nb as usize] += 1;
        }

        let end_setback = |code: usize, node: usize| -> f32 {
            let mut cross = 0.0_f32;
            let mut c = head[node];
            while c >= 0 {
                let cu = c as usize;
                if cu != code && Self::crosses(dir[code], dir[cu]) && half[cu >> 1] > cross {
                    cross = half[cu >> 1];
                }
                c = next[cu];
            }
            Self::arm_setback(&self.o, degree[node], cross)
        };

        let mut out = vec![(self.o.node_setback, self.o.node_setback); m];
        for e in 0..m {
            let (na, nb) = (a[e], b[e]);
            if valid(na, nb) {
                out[e] = (end_setback(2 * e, na as usize), end_setback((2 * e) + 1, nb as usize));
            }
        }
        out
    }

    // Надбавки сверх node_setback сжимаются пропорционально, если ось не вмещает оба отступа и min_lane_length.
    fn clamp_setbacks(&self, total: f32, setback_a: f32, setback_b: f32) -> (f32, f32) {
        let b = self.o.node_setback;
        let mut sa = if setback_a < b { b } else { setback_a };
        let mut sb = if setback_b < b { b } else { setback_b };
        let avail = total - self.o.min_lane_length;
        if sa + sb > avail {
            let ea = sa - b;
            let eb = sb - b;
            let k = (avail - (2.0 * b)) / (ea + eb);
            sa = b + (ea * k);
            sb = b + (eb * k);
        }
        (sa, sb)
    }

    fn point_at(&self, d: f32, n: usize) -> Vec2 {
        let mut i = 1;
        while i < n - 1 && self.raw_s[i] < d {
            i += 1;
        }
        let t = (d - self.raw_s[i - 1]) / (self.raw_s[i] - self.raw_s[i - 1]);
        self.raw[i - 1] + ((self.raw[i] - self.raw[i - 1]) * t)
    }
}
