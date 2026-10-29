using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Источник событий для уведомлений (UI6, <see cref="IUiEventSource"/>): события копит поток симуляции (<c>SimThreadHost.CollectEvents</c>: буфер ядра живёт один тик,
/// а пачка шагает до 5 тиков), «Показать» привязывает объект к точке мира по последнему кадру.
/// </summary>
public partial class SimHost : IUiEventSource
{
    /// <summary>Подключить источник к уведомлениям UI и включить накопление событий в хосте (после создания <c>_host</c>).</summary>
    private void AttachEventSource()
    {
        if (_ui != null && _host != null)
        {
            _host.CollectEvents = true;
            _ui.Notify.Source = this;
        }
    }

    public void DrainEvents(List<SimEvent> sink) => _host?.DrainEvents(sink);

    public bool TryLocate(in NotifySubject subject, out Vector3 position, out float extent)
    {
        position = Vector3.Zero;
        extent = 0f;
        if (_frame == null)
        {
            return false;
        }

        NetworkView net = _frame.Network.View;
        switch (subject.Kind)
        {
            case NotifySubjectKind.Vehicle:
                foreach (VehicleRenderData v in _frame.Vehicles)
                {
                    if (v.Index == subject.Index && v.Generation == (ushort)subject.Generation)
                    {
                        position = new Vector3(v.X, 0f, v.Z);
                        extent = 60f;
                        return true;
                    }
                }

                return false;
            case NotifySubjectKind.Segment:
                if (subject.Index < 0 || subject.Index >= net.Segments.Length || (net.Segments[subject.Index].Generation & 1) == 0)
                {
                    return false;
                }

                SegmentData s = net.Segments[subject.Index];
                if (s.NodeA < 0 || s.NodeB < 0 || s.NodeA >= net.Nodes.Length || s.NodeB >= net.Nodes.Length)
                {
                    return false;
                }

                position = new Vector3((net.Nodes[s.NodeA].Position.X + net.Nodes[s.NodeB].Position.X) * 0.5f, 0f, (net.Nodes[s.NodeA].Position.Z + net.Nodes[s.NodeB].Position.Z) * 0.5f);
                extent = Math.Max(80f, s.Length);
                return true;
            case NotifySubjectKind.Node:
                if (subject.Index < 0 || subject.Index >= net.Nodes.Length)
                {
                    return false;
                }

                position = new Vector3(net.Nodes[subject.Index].Position.X, 0f, net.Nodes[subject.Index].Position.Z);
                extent = 80f;
                return true;
            case NotifySubjectKind.Zone:
                if (subject.Index < 0 || subject.Index >= net.Zones.Length)
                {
                    return false;
                }

                position = new Vector3(net.Zones[subject.Index].Center.X, 0f, net.Zones[subject.Index].Center.Z);
                extent = Math.Max(80f, net.Zones[subject.Index].HalfSize * 2f);
                return true;
            case NotifySubjectKind.BusRoute:
                int seg = BusRouteSegment(net, subject.Index);
                if (seg < 0)
                {
                    return false; // линия исчезла: «Показать» уйдёт на точку события
                }

                SegmentData bs = net.Segments[seg];
                position = new Vector3((net.Nodes[bs.NodeA].Position.X + net.Nodes[bs.NodeB].Position.X) * 0.5f, 0f, (net.Nodes[bs.NodeA].Position.Z + net.Nodes[bs.NodeB].Position.Z) * 0.5f);
                extent = Math.Max(80f, bs.Length);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Сегмент первой остановки линии (для инспектора сегмента); -1 - линии/остановок/сегмента нет.</summary>
    private static int BusRouteSegment(NetworkView net, int route)
    {
        if ((uint)route >= (uint)net.BusRoutes.Length || net.BusRoutes[route].StopCount <= 0 || (net.BusRoutes[route].Generation & 1) == 0)
        {
            return -1;
        }

        int first = net.BusRoutes[route].FirstStop;
        if ((uint)first >= (uint)net.BusRouteStops.Length)
        {
            return -1;
        }

        int stop = net.BusRouteStops[first];
        if ((uint)stop >= (uint)net.BusStops.Length || (uint)net.BusStops[stop].Lane >= (uint)net.Lanes.Length)
        {
            return -1;
        }

        int seg = net.Lanes[net.BusStops[stop].Lane].Segment;
        return net.IsSegmentAlive(seg) && net.Segments[seg].NodeA >= 0 && net.Segments[seg].NodeB >= 0 ? seg : -1;
    }

    /// <summary>Ближайший живой сегмент к точке мира (расстояние до отрезка A-B); -1 - сети нет. Вызывается по клику «Показать», O(сегментов).</summary>
    private static int NearestSegment(NetworkView net, float x, float z)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < net.Segments.Length; i++)
        {
            if (!net.IsSegmentAlive(i) || net.Segments[i].NodeA < 0 || net.Segments[i].NodeB < 0)
            {
                continue;
            }

            Vec2 a = net.Nodes[net.Segments[i].NodeA].Position;
            Vec2 b = net.Nodes[net.Segments[i].NodeB].Position;
            float dx = b.X - a.X;
            float dz = b.Z - a.Z;
            float len2 = (dx * dx) + (dz * dz);
            float t = len2 > 1e-6f ? Math.Clamp((((x - a.X) * dx) + ((z - a.Z) * dz)) / len2, 0f, 1f) : 0f;
            float px = a.X + (t * dx) - x;
            float pz = a.Z + (t * dz) - z;
            float d = (px * px) + (pz * pz);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }

    public void SelectSubject(in NotifySubject subject)
    {
        if (_frame == null)
        {
            return;
        }

        if (subject.Kind == NotifySubjectKind.Vehicle)
        {
            SelectVehicle(new VehicleHandle(subject.Index, subject.Generation));
        }
        else if (subject.Kind == NotifySubjectKind.Node && subject.Index >= 0 && subject.Index < _frame.Network.View.Nodes.Length)
        {
            SelectNode(_frame.Network.View, subject.Index, false);
        }
        else if (subject.Kind == NotifySubjectKind.Segment)
        {
            SelectSegment(_frame.Network.View, subject.Index);
        }
        else if (subject.Kind == NotifySubjectKind.BusRoute)
        {
            // отдельного инспектора линии пока нет: открываем инспектор сегмента первой остановки (иначе - ближайшего к месту события)
            int seg = BusRouteSegment(_frame.Network.View, subject.Index);
            if (seg < 0 && subject.HasPoint)
            {
                seg = NearestSegment(_frame.Network.View, subject.X, subject.Z);
            }

            if (seg >= 0)
            {
                SelectSegment(_frame.Network.View, seg);
            }
        }
        else if (subject.Kind == NotifySubjectKind.Building && subject.HasPoint)
        {
            // отдельного инспектора здания пока нет: инспектор ближайшего сегмента (улицы здания)
            int seg = NearestSegment(_frame.Network.View, subject.X, subject.Z);
            if (seg >= 0)
            {
                SelectSegment(_frame.Network.View, seg);
            }
        }
    }
}
