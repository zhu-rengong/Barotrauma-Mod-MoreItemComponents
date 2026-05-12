using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using System;
using System.ComponentModel;

namespace MoreItemComponents;

/// <summary>
/// <see cref="MeleeWeapon"/> is a melee weapon component solely responsible for handling swing attack logic,
/// while <see cref="MIC_ThrustingMeleeWeapon"/> is derived from <see cref="MeleeWeapon"/> to support thrusting attack logic
/// while maintaining as much compatibility with the original <see cref="AIObjectiveCombat"/> as possible.
/// <para/>
/// Additionally, a separate <b>Hitbox</b> definition has been introduced.
/// The original <see cref="MeleeWeapon"/> uses the item's entire collider as the attack detection volume,
/// which causes areas that should not deal damage to still be used for hit registration.
/// In <see cref="MIC_ThrustingMeleeWeapon"/>, when the weapon initiates an attack,
/// a temporary hidden collider (the <b>Hitbox</b>) is briefly activated to perform attack detection.
/// The <b>Hitbox</b> can be defined by the user to specify where the weapon's blade is located.
/// </summary>
partial class MIC_ThrustingMeleeWeapon : MeleeWeapon
{
    public enum State { Idle, Aiming, Charging, Thrusting }
    private State currentState;
    public State CurrentState => currentState;

    private float thrustTimer;
    public float ThrustTimer
    {
        get => thrustTimer;
        set => thrustTimer = Math.Clamp(value, 0.0f, ThrustDuration);
    }

    public float ThrustProgress => ThrustDuration > 0.0f ? thrustTimer / ThrustDuration : 1.0f;

    private float chargeTimer;
    public float ChargeTimer
    {
        get => chargeTimer;
        set => chargeTimer = Math.Clamp(value, 0.0f, MaxChargeTime);
    }

    public float ChargeProgress => MaxChargeTime > 0.0f ? chargeTimer / MaxChargeTime : 1.0f;

    private readonly List<ForceSpec> forceSpces = [];
    private readonly List<ActiveForce> activeForces = [];

    private class ForceSpec
    {
        public required LimbType[] LimbTypes;
        public required Vector2 ForceLocal;
        public required float Delay;
        public required float Duration;
        public required float ChargeMultiplier;
        public required InvSlotType[] ConditionalSlots;
    }

    private struct ActiveForce
    {
        public ForceSpec Specification;
        public Vector2 ForceWorld;
        public float Timer;

        public ActiveForce(ForceSpec specification, Vector2 forceWorld)
        {
            Specification = specification;
            ForceWorld = forceWorld;
            Timer = specification.Delay + specification.Duration;
        }
    }

    private PhysicsBody? hitbox = null!;
    public PhysicsBody? Hitbox => hitbox;
    private Vector2 hitboxOffset;

    [Editable, Serialize(0.5f, IsPropertySaveable.Yes, description: "Time to reach full charge (seconds).")]
    public float MaxChargeTime { get; set; }

    [Editable, Serialize(1.3f, IsPropertySaveable.Yes, description: "Affects the damage of charged attacks, scaling linearly with charge progress.")]
    public float ChargeAttackMultiplier { get; set; }

    [Editable, Serialize("-10.0, 0.0", IsPropertySaveable.Yes, description: "Weapon pull-back offset during charge (pixels).")]
    public Vector2 ChargeHoldPosOffset { get; set; }

    [Editable, Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "")]
    public Vector2 ChargeHandle1Offset { get; set; }

    [Editable, Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "")]
    public Vector2 ChargeHandle2Offset { get; set; }

    [Editable, Serialize("50.0, 0.0", IsPropertySaveable.Yes, description: "Weapon extension offset during thrust (pixels).")]
    public Vector2 ThrustHoldPosOffset { get; set; }

    [Editable, Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "")]
    public Vector2 ThrustHandle1Offset { get; set; }

    [Editable, Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "")]
    public Vector2 ThrustHandle2Offset { get; set; }

    [Editable, Serialize(0.2f, IsPropertySaveable.Yes, description: "Duration of the thrust animation (seconds).")]
    public float ThrustDuration { get; set; }

    [Editable, Serialize(1, IsPropertySaveable.Yes, description: "How many targets the weapon can hit before it stops.")]
    public int MaxTargetsToHit { get; set; }

    private enum SpecialActionType
    {
        OnChargeStart, Charging,
        OnThrustStart, Thrusting, OnThrustEnd
    }

    private Dictionary<SpecialActionType, List<StatusEffect>>? thrustingMeleeWeaponStatusEffects;

    public MIC_ThrustingMeleeWeapon(Item item, ContentXElement element) : base(item, element)
    {
        ParseForceSpecs(element);
        CreateHitbox(element);


        foreach (var el in element.Elements())
        {
            if (el.Name.ToString().Equals("thrustingmeleeweaponstatuseffects", StringComparison.OrdinalIgnoreCase))
            {
                LoadStatusEffects(el);
            }
        }

        void LoadStatusEffects(ContentXElement subElement)
        {
            foreach (var el in subElement.Elements())
            {
                if (!Enum.TryParse(el.Name.ToString(), ignoreCase: true, out SpecialActionType type))
                {
                    Plugin.DebugConsole.ThrowError($"Invalid special action type \"{el.Name}\" in StatusEffect ({nameof(MIC_ThrustingMeleeWeapon)})");
                }

                var statusEffect = StatusEffect.Load(el, $"{item.Name}, {nameof(MIC_ThrustingMeleeWeapon)}");

                thrustingMeleeWeaponStatusEffects ??= new();
                if (!thrustingMeleeWeaponStatusEffects.TryGetValue(type, out var effectList))
                {
                    thrustingMeleeWeaponStatusEffects.Add(type, effectList = new List<StatusEffect>());
                }
                effectList.Add(statusEffect);
            }
        }
    }

    private void ApplyStatusEffects(SpecialActionType type, float deltaTime, Character? character = null, Character? user = null)
    {
        if (thrustingMeleeWeaponStatusEffects is null
            || !thrustingMeleeWeaponStatusEffects.TryGetValue(type, out var statusEffectList))
        {
            return;
        }

        foreach (var effect in statusEffectList)
        {
            if (user is not null) { effect.SetUser(user); }
            item.ApplyStatusEffect(effect, effect.Type, deltaTime, character);
        }
    }

    private void CreateHitbox(ContentXElement componentElement)
    {
        var hitboxEl = componentElement.GetChildElement("hitbox");
        float width, height;

        if (hitboxEl is not null)
        {
            width = ConvertUnits.ToSimUnits(hitboxEl.GetAttributeFloat("width", 0.0f)) * item.Scale;
            height = ConvertUnits.ToSimUnits(hitboxEl.GetAttributeFloat("height", 0.0f)) * item.Scale;
            hitboxOffset = ConvertUnits.ToSimUnits(hitboxEl.GetAttributeVector2("offset", Vector2.Zero)) * item.Scale;
        }
        else if (item.body != null)
        {
            width = item.body.Width;
            height = item.body.Height;
            hitboxOffset = Vector2.Zero;
        }
        else
        {
            width = 0.0f;
            height = 0.0f;
            hitboxOffset = Vector2.Zero;
        }

        hitbox = new PhysicsBody(width, height, radius: 0f, density: Physics.NeutralDensity, BodyType.Dynamic,
            collisionCategory: Physics.CollisionProjectile,
            collidesWith: Physics.CollisionCharacter | Physics.CollisionWall | Physics.CollisionItemBlocking)
        {
            UserData = item,
            Enabled = false
        };
        hitbox.FarseerBody.FixedRotation = false;
        hitbox.FarseerBody.IgnoreGravity = true;

        DisableThrustCollision();
    }

    private void ParseForceSpecs(ContentXElement componentElement)
    {
        foreach (var el in componentElement.GetChildElements("forcespec"))
        {
            var typeNames = el.GetAttributeString("limbtypes", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (typeNames.Length == 0) { continue; }

            var types = new LimbType[typeNames.Length];
            bool failed = false;
            for (int i = 0; i < typeNames.Length; i++)
            {
                if (!Enum.TryParse(typeNames[i], ignoreCase: true, out LimbType limbType))
                {
                    failed = true;
                    break;
                }
                types[i] = limbType;
            }
            if (failed) { continue; }

            string slotString = el.GetAttributeString("conditionalslots", string.Empty);
            string[] slotCombinations = slotString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            InvSlotType[] conditionalSlots = new InvSlotType[slotCombinations.Length];
            for (int i = 0; i < slotCombinations.Length; i++)
            {
                string slotCombination = slotCombinations[i];
                string[] slots = slotCombination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                InvSlotType conditionalSlot = InvSlotType.None;
                foreach (string slot in slots)
                {
                    switch (slot.ToLowerInvariant())
                    {
                        case "bothhands":
                            conditionalSlot = InvSlotType.LeftHand | InvSlotType.RightHand;
                            break;
                        default:
                            conditionalSlot |= (InvSlotType)Enum.Parse(typeof(InvSlotType), slot);
                            break;
                    }
                }

                conditionalSlots[i] = conditionalSlot;
            }

            forceSpces.Add(new ForceSpec()
            {
                LimbTypes = types,
                ForceLocal = el.GetAttributeVector2("forcelocal", Vector2.UnitX),
                Delay = el.GetAttributeFloat("delay", 0f),
                Duration = el.GetAttributeFloat("duration", 0f),
                ChargeMultiplier = el.GetAttributeFloat("chargemultiplier", 1.5f),
                ConditionalSlots = conditionalSlots
            });
        }
    }

    public override bool SecondaryUse(float deltaTime, Character? character = null)
    {
        return character is { Removed: false };
    }

    public override bool Use(float deltaTime, Character? character = null)
    {
        return false;
    }

    public override void Drop(Character? dropper, bool setTransform = true)
    {
        base.Drop(dropper, setTransform);
        DisableThrustCollision();
    }

    public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

    public override void Update(float deltaTime, Camera cam)
    {
        if (!item.body.Enabled)
        {
            return;
        }

        if (picker is null || !picker.HeldItems.Contains(item))
        {
            TransitionTo(State.Idle);
            IsActive = false;
            return;
        }

        if (impactQueue.Any())
        {
            // didn't work as HandleImpact will overwrite the damage multiplier
            if (Attack is Attack attack)
            {
                float damageMultiplier = MathHelper.Lerp(1.0f, ChargeAttackMultiplier, ChargeProgress);
                attack.DamageMultiplier = damageMultiplier;
            }

            while (impactQueue.Count > 0)
            {
                HandleImpact(impactQueue.Dequeue());
            }
        }

        if (picker is null) { return; }

        for (int i = activeForces.Count - 1; i >= 0; i--)
        {
            var activeForce = activeForces[i];
            var spec = activeForce.Specification;

            activeForce.Timer -= deltaTime;

            if (activeForce.Timer >= spec.Duration)
            {
                continue;
            }

            if (activeForce.Timer <= 0f || !CheckForceValidity(picker, spec))
            {
                activeForces.RemoveAt(i);
                continue;
            }

            foreach (var limb in picker.AnimController.Limbs)
            {
                if (!limb.IsSevered && !limb.Removed && spec.LimbTypes.Contains(limb.type))
                {
                    limb.body.ApplyForce(activeForce.ForceWorld * limb.Mass);
                }
            }
        }

        SyncHitbox();
        reloadTimer = Math.Max(0f, reloadTimer - deltaTime);
        bool aimHeld = picker.IsKeyDown(InputType.Aim) && picker.CanAim && !UsageDisabledByRangedWeapon(picker);
        bool shootDown = picker.IsKeyDown(InputType.Shoot);

        switch (currentState)
        {
            case State.Idle:
                if (aimHeld && CanControlWeapon(picker)) { TransitionTo(State.Aiming); }
                break;

            case State.Aiming:
                if (aimHeld && CanControlWeapon(picker))
                {
                    if (shootDown && reloadTimer <= 0)
                    {
                        TransitionTo(State.Charging);
                    }
                }
                else
                {
                    TransitionTo(State.Idle);
                }
                break;

            case State.Charging:
                if (aimHeld && CanControlWeapon(User))
                {
                    if (shootDown)
                    {
                        ChargeTimer += deltaTime;
                        ApplyStatusEffects(SpecialActionType.Charging, deltaTime, character: picker, user: User);
                    }
                    else
                    {
                        TransitionTo(State.Thrusting);
                    }
                }
                else
                {
                    TransitionTo(State.Idle);
                }
                break;

            case State.Thrusting:
                if (CanControlWeapon(User))
                {
                    ThrustTimer += deltaTime;
                    if (ThrustProgress < 1.0f)
                    {
                        ApplyStatusEffects(SpecialActionType.Thrusting, deltaTime, character: picker, user: User);
                        if (item.AiTarget != null)
                        {
                            item.AiTarget.SoundRange = item.AiTarget.MaxSoundRange;
                            item.AiTarget.SightRange = item.AiTarget.MaxSightRange;
                        }
                    }
                    else
                    {
                        FinishThrusting();
                        TransitionTo(aimHeld ? State.Aiming : State.Idle);
                    }
                }
                else
                {
                    FinishThrusting();
                    TransitionTo(State.Idle);
                }
                break;
        }

        ApplyStatusEffects(ActionType.OnActive, deltaTime, picker);

        if (picker.AnimController is not null && item.body.Dir != picker.AnimController.Dir)
        {
            item.FlipX(relativeToSub: false);
        }

        AnimateHold(deltaTime, aimHeld);

        bool CanControlWeapon(Character? character)
        {
            return character is { IsKnockedDownOrRagdolled: false, LockHands: false, AllowInput: true };
        }

        void FinishThrusting()
        {
            reloadTimer = Reload;
            reloadTimer /= 1f + picker.GetStatValue(StatTypes.MeleeAttackSpeed);
            reloadTimer /= 1f + item.GetQualityModifier(Quality.StatType.StrikingSpeedMultiplier);
            ApplyStatusEffects(SpecialActionType.OnThrustEnd, 1.0f, character: picker, user: User);
        }
    }

    private void SyncHitbox()
    {
        if (hitbox?.Removed ?? true) { return; }
        hitbox.Submarine = item.body.Submarine;
        hitbox.Dir = item.body.Dir;
        Vector2 offset = hitboxOffset;
        float rotationRad = item.body.Rotation;
        if (rotationRad != 0f)
        {
            offset = MathUtils.RotatePoint(offset, item.FlippedX ^ item.FlippedY ? -rotationRad : rotationRad);
        }
        if (item.FlippedX) { offset.X = -offset.X; }
        if (item.FlippedY) { offset.Y = -offset.Y; }

        hitbox.ResetDynamics();
        hitbox.SetTransform(item.body.SimPosition + offset, item.body.Rotation);
    }

    private void AnimateHold(float deltaTime, bool aimHeld)
    {
        if (picker.AnimController is not AnimController controller) { return; }

        Span<Vector2> handleOffset = stackalloc Vector2[2];

        switch (currentState)
        {
            case State.Charging:
                handleOffset[0] = ConvertUnits.ToSimUnits(ChargeHandle1Offset) * ChargeProgress;
                handleOffset[1] = ConvertUnits.ToSimUnits(ChargeHandle2Offset) * ChargeProgress;
                break;

            case State.Thrusting:
                handleOffset[0] = ConvertUnits.ToSimUnits(ThrustHandle1Offset) * ThrustProgress;
                handleOffset[1] = ConvertUnits.ToSimUnits(ThrustHandle2Offset) * ThrustProgress;
                break;
        }

        if (item.FlippedX)
        {
            for (int i = 0; i < handleOffset.Length; i++)
            {
                handleOffset[i].X = -handleOffset[i].X;
            }
        }

        scaledHandlePos[0] = (handlePos[0] + handleOffset[0]) * item.Scale;
        scaledHandlePos[1] = (handlePos[1] + handleOffset[1]) * item.Scale;

        switch (currentState)
        {
            case State.Idle:
                controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: holdPos, aim: false, holdAngle);
                break;

            case State.Aiming:
                controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos,
                    aim: true, holdAngle, aimAngle, aimMelee: true);
                break;

            case State.Charging:
                Vector2 chargeOffset = ConvertUnits.ToSimUnits(ChargeHoldPosOffset) * ChargeProgress;
                controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos + chargeOffset,
                    aim: true, holdAngle, aimAngle, aimMelee: true);
                break;

            case State.Thrusting:
                Vector2 thrustOffset = ConvertUnits.ToSimUnits(ThrustHoldPosOffset) * ThrustProgress;
                controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos + thrustOffset,
                    aim: true, holdAngle, aimAngle, aimMelee: true);
                break;
        }
    }

    private void TransitionTo(State newState)
    {
        if (currentState == State.Thrusting && newState != State.Thrusting)
        {
            User = null;
            DisableThrustCollision();
            hitting = false;
            activeForces.Clear();
        }

        switch (newState)
        {
            case State.Idle:
                break;

            case State.Aiming:
                break;

            case State.Charging:
                SetUser(picker);
                ChargeTimer = 0f;
                ApplyStatusEffects(SpecialActionType.OnChargeStart, 1.0f, character: picker, user: User);
                break;

            case State.Thrusting:
                SetUser(picker);
                ThrustTimer = 0f;
                ActivateNearbySleepingCharacters();
                EnableThrustCollision();
                hitting = true;
                picker.AnimController?.LockFlipping(ThrustDuration);
                ApplyThrustForces(User!);
                ApplyStatusEffects(SpecialActionType.OnThrustStart, 1.0f, character: picker, user: User);
                break;
        }

        currentState = newState;
    }

    private void EnableThrustCollision()
    {
        if (hitbox?.Removed ?? true) { return; }
        hitbox.FarseerBody.OnCollision += OnThrustCollision;
        hitbox.FarseerBody.IsBullet = true;
        hitbox.PhysEnabled = true;
        hitbox.Enabled = true;
    }

    private void DisableThrustCollision()
    {
        impactQueue.Clear();
        hitTargets.Clear();
        if (hitbox?.Removed ?? true) { return; }
        hitbox.FarseerBody.OnCollision -= OnThrustCollision;
        hitbox.FarseerBody.IsBullet = false;
        hitbox.PhysEnabled = false;
        hitbox.Enabled = false;
    }

    private bool OnThrustCollision(Fixture f1, Fixture f2, Contact contact)
    {
        if (User?.AnimController is not AnimController controller || User.Removed)
        {
            DisableThrustCollision();
            return false;
        }

        contact.GetWorldManifold(out _, out var points);

        Body[] ignoredBodies = [f1.Body, f2.Body];

        if (Submarine.PickBody(User.AnimController.AimSourceSimPos, points[0],
                ignoredBodies,
                collisionCategory: Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionItemBlocking,
                allowInsideFixture: true) != null)
        {
            return false;
        }

        bool hitAccepted = false;

        if (f2.Body.UserData is Limb limb)
        {
            if (!limb.IsSevered
                && limb.character is Character targetCharacter
                && targetCharacter != User
                && !targetCharacter.IgnoreMeleeWeapons
                && !HitFriendlyTarget(targetCharacter))
            {
                hitAccepted = RegisterHitTarget(targetCharacter);
            }
        }
        else if (f2.Body.UserData is Character)
        {
            return false;
        }
        else if (!HitOnlyCharacters)
        {
            if ((f2.Body.UserData as Structure ?? f2.UserData as Structure) is Structure targetStructure)
            {
                hitAccepted = RegisterHitTarget(targetStructure);
            }
            else if ((f2.Body.UserData as Item ?? f2.UserData as Item) is Item targetItem)
            {
                hitAccepted = RegisterHitTarget(targetItem);
            }
            else if (f2.Body.UserData is Holdable { CanPush: true } holdable)
            {
                if (holdable.Item.GetRootInventoryOwner() == User) { return false; }
                hitAccepted = RegisterHitTarget(holdable.Item);
            }
        }

        if (hitAccepted)
        {
            impactQueue.Enqueue(f2);
            return true;
        }

        return false;

        bool RegisterHitTarget(Entity target)
        {
            if (hitTargets.Contains(target)) { return false; }
            if (hitTargets.Count < MaxTargetsToHit)
            {
                hitTargets.Add(target);
            }
            if ((!AllowHitMultiple || hitTargets.Count >= MaxTargetsToHit) && hitbox is not null) { hitbox.FarseerBody.OnCollision -= OnThrustCollision; }
            return true;
        }
    }

    private bool HitFriendlyTarget(Character target)
    {
        if (User is null || User.IsPlayer) { return false; }
        if (User.AIController is HumanAIController { Enabled: true } humanAI
            && humanAI.ObjectiveManager.CurrentObjective is AIObjectiveCombat combat && combat.Enemy != target)
        {
            return humanAI.IsFriendly(target, onlySameTeam: true);
        }
        return false;
    }

    private void ApplyThrustForces(Character? user)
    {
        if (user?.AnimController is not AnimController controller) { return; }

        Vector2 aimVector = Vector2.Normalize(user.CursorWorldPosition - controller.AimSourceWorldPos);
        float aimAngleRad = MathUtils.VectorToAngle(aimVector);

        foreach (var spec in forceSpces)
        {
            if (!CheckForceValidity(user, spec)) { continue; }

            float scale = MathHelper.Lerp(1.0f, spec.ChargeMultiplier, ChargeProgress);
            Vector2 local = new(spec.ForceLocal.X, spec.ForceLocal.Y * controller.Dir);
            Vector2 world = MathUtils.RotatePoint(local, aimAngleRad) * scale;

            foreach (var limb in user.AnimController.Limbs)
            {
                if (!limb.IsSevered && !limb.Removed && spec.LimbTypes.Contains(limb.type))
                {
                    limb.body.ApplyForce(world * limb.Mass);
                }
            }

            if (spec.Duration > 0.0f)
            {
                activeForces.Add(new ActiveForce(spec, world));
            }
        }
    }

    private bool CheckForceValidity(Character character, ForceSpec specification)
    {
        if (specification.ConditionalSlots.None()) { return true; }

        if (character.Inventory is CharacterInventory inventory)
        {
            InvSlotType itemSlots = InvSlotType.None;

            for (int i = 0; i < inventory.Capacity; i++)
            {
                if (inventory.GetItemsAt(i).Contains(item))
                {
                    itemSlots |= inventory.SlotTypes[i];
                }
            }

            return specification.ConditionalSlots.Any(slot => itemSlots.HasFlag(slot));
        }

        return false;
    }

    public override void RemoveComponentSpecific()
    {
        base.RemoveComponentSpecific();
        if (hitbox is not null)
        {
            hitbox.Remove();
            hitbox = null;
        }
    }
}
