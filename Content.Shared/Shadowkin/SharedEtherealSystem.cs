using Content.Shared.Physics;
using Robust.Shared.Physics;
using System.Linq;
using Robust.Shared.Physics.Systems;
using Content.Shared.Interaction.Events;
using Robust.Shared.Timing;
using Content.Shared.Popups;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Psionics;
using Content.Shared.Mobs;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Tag;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;

namespace Content.Shared.Shadowkin;

public abstract class SharedEtherealSystem : EntitySystem
{
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly StaminaSystem _stamina = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EtherealComponent, MapInitEvent>(OnStartup);
        SubscribeLocalEvent<EtherealComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<EtherealComponent, InteractionAttemptEvent>(OnInteractionAttempt);
        SubscribeLocalEvent<EtherealComponent, BeforeThrowEvent>(OnBeforeThrow);
        SubscribeLocalEvent<EtherealComponent, OnAttemptPowerUseEvent>(OnAttemptPowerUse);
        SubscribeLocalEvent<EtherealComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<EtherealComponent, ShotAttemptedEvent>(OnShootAttempt);
        SubscribeLocalEvent<EtherealComponent, OnMindbreakEvent>(OnMindbreak);
        SubscribeLocalEvent<EtherealComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<EtherealComponent, OnManaUpdateEvent>(OnManaUpdate);
    }

    public virtual void OnStartup(EntityUid uid, EtherealComponent component, MapInitEvent args)
    {
        if (TryComp<PsionicComponent>(uid, out var magic)
            && component.DrainMana)
        {
            component.OldManaGain = magic.ManaGain;
            magic.ManaGain = -1;
        }

        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return;

        if (TryComp<StandingStateComponent>(uid, out var standingstate))
        {
            _standingStateSystem.Stand(uid, standingstate);
        }

        var fixture = fixtures.Fixtures.First();

        component.OldMobMask = fixture.Value.CollisionMask;
        component.OldMobLayer = fixture.Value.CollisionLayer;

        if (_cfg.GetCVar(CCVars.EtherealPassThrough))
        {
            _physics.SetCollisionMask(uid, fixture.Key, fixture.Value, (int) CollisionGroup.GhostImpassable, fixtures);
            _physics.SetCollisionLayer(uid, fixture.Key, fixture.Value, 0, fixtures);

            if (_tag.RemoveTag(uid, "DoorBumpOpener"))
                component.HasDoorBumpTag = true;

            return;
        }

        _physics.SetCollisionMask(uid, fixture.Key, fixture.Value, (int) CollisionGroup.FlyingMobMask, fixtures);
        _physics.SetCollisionLayer(uid, fixture.Key, fixture.Value, (int) CollisionGroup.FlyingMobLayer, fixtures);
    }

    public virtual void OnShutdown(EntityUid uid, EtherealComponent component, ComponentShutdown args)
    {
        if (TryComp<PsionicComponent>(uid, out var magic)
            && component.DrainMana)
            magic.ManaGain = component.OldManaGain;

        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return;

        var fixture = fixtures.Fixtures.First();

        _physics.SetCollisionMask(uid, fixture.Key, fixture.Value, component.OldMobMask, fixtures);
        _physics.SetCollisionLayer(uid, fixture.Key, fixture.Value, component.OldMobLayer, fixtures);

        if (_cfg.GetCVar(CCVars.EtherealPassThrough))
            if (component.HasDoorBumpTag)
                _tag.AddTag(uid, "DoorBumpOpener");
    }

    private void OnManaUpdate(EntityUid uid, EtherealComponent component, ref OnManaUpdateEvent args)
    {
        if (!TryComp<PsionicComponent>(uid, out var magic))
            return;

        if (magic.Mana <= 0)
        {
            if (TryComp<StaminaComponent>(uid, out var stamina))
                _stamina.TakeStaminaDamage(uid, stamina.CritThreshold, stamina, uid);

            SpawnAtPosition("ShadowkinShadow", Transform(uid).Coordinates);
            SpawnAtPosition("EffectFlashShadowkinDarkSwapOff", Transform(uid).Coordinates);
            RemComp(uid, component);
        }
    }

    private void OnMindbreak(EntityUid uid, EtherealComponent component, ref OnMindbreakEvent args)
    {
        SpawnAtPosition("ShadowkinShadow", Transform(uid).Coordinates);
        SpawnAtPosition("EffectFlashShadowkinDarkSwapOff", Transform(uid).Coordinates);
        RemComp(uid, component);
    }

    private void OnMobStateChanged(EntityUid uid, EtherealComponent component, MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Critical
            || args.NewMobState == MobState.Dead)
        {
            SpawnAtPosition("ShadowkinShadow", Transform(uid).Coordinates);
            SpawnAtPosition("EffectFlashShadowkinDarkSwapOff", Transform(uid).Coordinates);
            RemComp(uid, component);
        }
    }

    private void OnShootAttempt(Entity<EtherealComponent> ent, ref ShotAttemptedEvent args)
    {
        args.Cancel();
    }

    private void OnAttackAttempt(EntityUid uid, EtherealComponent component, AttackAttemptEvent args)
    {
        if (HasComp<EtherealComponent>(args.Target))
            return;

        args.Cancel();
    }

    private void OnBeforeThrow(Entity<EtherealComponent> ent, ref BeforeThrowEvent args)
    {
        var thrownItem = args.ItemUid;

        // Raise an AttemptPacifiedThrow event and rely on other systems to check
        // whether the candidate item is OK to throw:
        var ev = new AttemptPacifiedThrowEvent(thrownItem, ent);
        RaiseLocalEvent(thrownItem, ref ev);
        if (!ev.Cancelled)
            return;

        args.Cancelled = true;
    }

    private void OnInteractionAttempt(EntityUid uid, EtherealComponent component, InteractionAttemptEvent args)
    {
        if (!HasComp<TransformComponent>(args.Target)
            || HasComp<EtherealComponent>(args.Target))
            return;

        args.Cancel();
        if (_gameTiming.InPrediction)
            return;

        _popup.PopupEntity(Loc.GetString("ethereal-pickup-fail"), args.Target.Value, uid);
    }

    private void OnAttemptPowerUse(EntityUid uid, EtherealComponent component, OnAttemptPowerUseEvent args)
    {
        if (args.Power == "DarkSwap")
            return;

        args.Cancel();
    }

    private void DownAttemptEvent(EntityUid uid, EtherealComponent component, DownAttemptEvent args)
    {
        args.Cancel();
    }
}
