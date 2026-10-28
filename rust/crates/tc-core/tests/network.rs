//! Unit-тесты R4: построение явной сети, коды `CreateStatus`, геометрия полос. Сверка с C# - `tc-scenarios/tests/golden.rs`.

use tc_core::api::desc::{NetworkDesc, NodeControl, NodeDesc, RoadKind, ScenarioDesc, SegmentDesc, SimCapacities};
use tc_core::api::status::CreateStatus;
use tc_core::network::lane_geometry::evaluate;
use tc_core::network::types::LaneKind;
use tc_core::sim::Sim;

fn caps() -> SimCapacities {
    SimCapacities {
        max_vehicles: 10,
        max_nodes: 16,
        max_segments: 32,
        max_lanes: 256,
        max_lane_points: 4096,
        max_pending_trips: 4,
        command_queue_capacity: 8,
        event_capacity: 8,
        max_connectors: 256,
        max_conflicts: 1024,
        ..SimCapacities::default()
    }
}

fn node(x: f32, z: f32) -> NodeDesc {
    NodeDesc {
        x,
        z,
        control: NodeControl::None,
    }
}

fn seg(a: i32, b: i32, ab: i32, ba: i32) -> SegmentDesc {
    SegmentDesc {
        a,
        b,
        lanes_ab: ab,
        lanes_ba: ba,
        speed_limit: 13.0,
        kind: RoadKind::Street,
        profile: 0,
        mirrored: false,
    }
}

fn desc(nodes: Vec<NodeDesc>, segments: Vec<SegmentDesc>) -> ScenarioDesc {
    ScenarioDesc {
        seed: 1,
        capacities: caps(),
        network: NetworkDesc {
            nodes,
            segments,
            corner_radius: 0.0,
        },
        road_profiles: Default::default(),
    }
}

fn cross() -> ScenarioDesc {
    desc(
        vec![
            node(0.0, 0.0),
            node(100.0, 0.0),
            node(-100.0, 0.0),
            node(0.0, 100.0),
            node(0.0, -100.0),
        ],
        vec![seg(0, 1, 1, 1), seg(0, 2, 1, 1), seg(0, 3, 1, 1), seg(0, 4, 1, 1)],
    )
}

#[test]
fn cross_builds_and_hash_is_stable() {
    let a = Sim::create(&cross()).unwrap();
    let b = Sim::create(&cross()).unwrap();
    assert_eq!(a.network_hash(), b.network_hash());
    let c = a.counts();
    assert_eq!((c.nodes, c.segments, c.lanes_per_segment_check()), (5, 4, true));
    // Центр: 4 рукава по 1+1 полосе -> по одной входной полосе на рукав, 3 манёвра каждая (прямо, право, лево) = 12 connector'ов.
    assert_eq!(a.network().nodes[0].connector_count, 12);
    assert_eq!(a.network().nodes[0].arm_count, 4);
    // Тупики (узлы 1..4): один рукав, разворот.
    for n in 1..5 {
        assert_eq!(a.network().nodes[n].connector_count, 1, "тупик {n}");
    }
    // Явная сеть: signal/zone = -1, elevation = 0.
    for n in &a.network().nodes[..5] {
        assert_eq!((n.signal, n.zone, n.elevation), (-1, -1, 0.0));
    }
    // Узел с 4 рукавами и control = None нормализуется в RightHandRule.
    assert_eq!(a.network().nodes[0].control, NodeControl::RightHandRule);
}

trait CountsExt {
    fn lanes_per_segment_check(&self) -> bool;
}
impl CountsExt for tc_core::sim::NetworkCounts {
    fn lanes_per_segment_check(&self) -> bool {
        // 8 Normal-полос + connector'ы (12 + 4 разворота)
        self.lanes == 8 + 12 + 4
    }
}

#[test]
fn connector_lanes_link_to_normal_lanes() {
    let sim = Sim::create(&cross()).unwrap();
    let net = sim.network();
    for l in &net.lanes[..net.lane_count as usize] {
        if l.kind == LaneKind::Connector {
            assert_eq!(net.lanes[l.from_lane as usize].kind, LaneKind::Normal);
            assert_eq!(net.lanes[l.to_lane as usize].kind, LaneKind::Normal);
            assert!(l.conflict_group >= 0);
        }
    }
    assert_eq!(sim.connectors().count, 16);
}

#[test]
fn status_codes() {
    // Прилипание: узлы ближе snap_radius (5 м) -> InvalidNetwork.
    let d = desc(vec![node(0.0, 0.0), node(3.0, 0.0)], vec![]);
    assert_eq!(Sim::create(&d).err(), Some(CreateStatus::InvalidNetwork));
    // Сегмент короче 2 * setback + min_lane_length.
    let d = desc(vec![node(0.0, 0.0), node(10.0, 0.0)], vec![seg(0, 1, 1, 1)]);
    assert_eq!(Sim::create(&d).err(), Some(CreateStatus::InvalidNetwork));
    // Несуществующий узел.
    let d = desc(vec![node(0.0, 0.0)], vec![seg(0, 5, 1, 1)]);
    assert_eq!(Sim::create(&d).err(), Some(CreateStatus::InvalidNetwork));
    // Ёмкость точек мала.
    let mut d = cross();
    d.capacities.max_lane_points = 8;
    assert_eq!(Sim::create(&d).err(), Some(CreateStatus::NetworkTooLarge));
    // Ёмкость узлов мала.
    let mut d = cross();
    d.capacities.max_nodes = 2;
    assert_eq!(Sim::create(&d).err(), Some(CreateStatus::NetworkTooLarge));
    // Ёмкость connector'ов мала.
    let mut d = cross();
    d.capacities.max_connectors = 3;
    assert_eq!(Sim::create(&d).err(), Some(CreateStatus::NetworkTooLarge));
    // Недопустимая ёмкость.
    let mut d = cross();
    d.capacities.max_vehicles = 0;
    assert_eq!(Sim::create(&d).err(), Some(CreateStatus::InvalidCapacity));
}

#[test]
fn lane_geometry_evaluate_clamps_and_interpolates() {
    let sim = Sim::create(&desc(vec![node(0.0, 0.0), node(100.0, 0.0)], vec![seg(0, 1, 1, 0)])).unwrap();
    let net = sim.network();
    let mut hint = 0;
    let len = net.lanes[0].length;
    assert_eq!(len, 100.0 - 14.0);
    let (p, d) = evaluate(&net.lanes, &net.lane_points, &net.lane_point_s, 0, -5.0, &mut hint);
    assert_eq!((p.x, d.x, d.z), (7.0, 1.0, 0.0));
    let (p, _) = evaluate(&net.lanes, &net.lane_points, &net.lane_point_s, 0, 1000.0, &mut hint);
    assert_eq!(p.x, 93.0);
    let (p, _) = evaluate(&net.lanes, &net.lane_points, &net.lane_point_s, 0, 43.0, &mut hint);
    assert_eq!(p.x, 50.0);
}

#[test]
fn step_advances_tick() {
    let mut sim = Sim::create(&cross()).unwrap();
    sim.step(100);
    assert_eq!(sim.tick(), 100);
    let h = sim.network_hash();
    sim.step(5);
    assert_eq!(sim.network_hash(), h);
}
