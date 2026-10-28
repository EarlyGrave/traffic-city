using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
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
            default:
                return false;
        }
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
    }
}
