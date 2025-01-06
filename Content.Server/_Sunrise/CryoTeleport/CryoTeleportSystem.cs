﻿using Content.Server.Bed.Cryostorage;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Sunrise.SunriseCCVars;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Sunrise.CryoTeleport;

public sealed class CryoTeleportationSystem : EntitySystem
{
    [Dependency] private readonly CryostorageSystem _cryostorage = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IEntityManager _entity = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;
    [Dependency] private readonly IPlayerManager _playerMan = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private bool _enable;
    private TimeSpan _transferDelay;
    public TimeSpan NextTick = TimeSpan.Zero;
    public TimeSpan RefreshCooldown = TimeSpan.FromSeconds(5);

    public override void Initialize()
    {
        _cfg.OnValueChanged(SunriseCCVars.CryoTeleportEnable, OnCryoTeleportEnableChanged, true);
        _cfg.OnValueChanged(SunriseCCVars.CryoTeleportTransferDelay, OnCryoTeleportTransferDelayChanged, true);

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnCompleteSpawn);
        SubscribeLocalEvent<CryoTeleportTargetComponent, PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<CryoTeleportTargetComponent, PlayerAttachedEvent>(OnPlayerAttached);
        _playerMan.PlayerStatusChanged += OnSessionStatus;
    }

    private void OnCryoTeleportEnableChanged(bool value)
    {
        _enable = value;
    }

    private void OnCryoTeleportTransferDelayChanged(int value)
    {
        _transferDelay = TimeSpan.FromMinutes(value);
    }

    public override void Update(float delay)
    {
        if (NextTick > _timing.CurTime)
            return;

        NextTick += RefreshCooldown;

        if (!_enable)
            return;

        var query = AllEntityQuery<CryoTeleportTargetComponent, MobStateComponent>();
        while (query.MoveNext(out var uid, out var comp, out var mobStateComponent))
        {
            if (comp.Station == null
                || !TryComp<StationCryoTeleportComponent>(comp.Station, out var stationCryoTeleportComponent)
                || !TryComp<StationDataComponent>(comp.Station, out var stationData)
                || mobStateComponent.CurrentState != MobState.Alive
                || comp.ExitTime == null
                || _timing.CurTime - comp.ExitTime < _transferDelay
                || HasComp<CryostorageContainedComponent>(uid))
                continue;

            var stationGrid = _stationSystem.GetLargestGrid(stationData);

            if (stationGrid == null)
                continue;

            var cryoStorage = FindCryoStorage(Transform(stationGrid.Value));

            if (cryoStorage == null)
                continue;

            var containedComp = AddComp<CryostorageContainedComponent>(uid);

            containedComp.Cryostorage = cryoStorage.Value;
            containedComp.GracePeriodEndTime = _timing.CurTime;

            var portalCoordinates = _transformSystem.GetMapCoordinates(Transform(uid));

            var portalUid = _entity.SpawnEntity(stationCryoTeleportComponent.PortalPrototype, portalCoordinates);
            _audio.PlayPvs(stationCryoTeleportComponent.TransferSound, portalUid);

            var container = _container.EnsureContainer<ContainerSlot>(cryoStorage.Value, "storage");

            if (!_container.Insert(uid, container))
                _cryostorage.HandleEnterCryostorage((uid, containedComp), comp.UserId);
        }
    }

    private void OnCompleteSpawn(PlayerSpawnCompleteEvent ev)
    {
        if (!HasComp<CryoTeleportTargetComponent>(ev.Station)
            || ev.JobId == null
            || ev.Player.AttachedEntity == null
            || !_enable)
            return;

        var targetComponent = EnsureComp<CryoTeleportTargetComponent>(ev.Player.AttachedEntity.Value);
        targetComponent.Station = ev.Station;
        targetComponent.UserId = ev.Player.UserId;
    }

    private void OnPlayerDetached(EntityUid uid, CryoTeleportTargetComponent comp, PlayerDetachedEvent ev)
    {
        comp.ExitTime ??= _timing.CurTime;
        if (_mind.TryGetMind(uid, out var mindId, out var mind))
            comp.UserId = mind.UserId;
    }

    private void OnPlayerAttached(EntityUid uid, CryoTeleportTargetComponent comp, PlayerAttachedEvent ev)
    {
        var compExitTime = comp.ExitTime;
        if (compExitTime != null)
            comp.ExitTime = null;
        if (_mind.TryGetMind(uid, out var mindId, out var mind))
            comp.UserId = mind.UserId;
    }

    private void OnSessionStatus(object? sender, SessionStatusEventArgs args)
    {
        if (!_enable)
            return;

        if (!TryComp<CryoTeleportTargetComponent>(args.Session.AttachedEntity, out var comp))
            return;

        if (args.Session.Status == SessionStatus.Disconnected && comp.ExitTime == null)
            comp.ExitTime = _timing.CurTime;
        else if (args.Session.Status == SessionStatus.Connected && comp.ExitTime != null)
            comp.ExitTime = null;

        comp.UserId = args.Session.UserId;
    }

    private EntityUid? FindCryoStorage(TransformComponent stationGridTransform)
    {
        var query = AllEntityQuery<CryostorageComponent, TransformComponent>();
        while (query.MoveNext(out var cryoUid, out _, out var cryoTransform))
        {
            if (stationGridTransform.MapUid != cryoTransform.MapUid)
                continue;

            var container = _container.EnsureContainer<ContainerSlot>(cryoUid, "storage");

            if (container.ContainedEntities.Count > 0)
                continue;

            return cryoUid;
        }

        return null;
    }
}
