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
partial class MIC_ThrustingMeleeWeapon : MeleeWeapon, IDrawableComponent
{
    public enum State { Idle, Aiming, Charging, Thrusting, Cooldown }
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
        public required float Duration;
        public required float ChargeMultiplier;
        public required InvSlotType[] ConditionalSlots;
    }

    private struct ActiveForce
    {
        public required ForceSpec Specification;
        public required Vector2 ForceWorld;
        public float Timer;

        public ActiveForce(ForceSpec specification, Vector2 forceWorld)
        {
            Specification = specification;
            ForceWorld = forceWorld;
            Timer = specification.Duration;
        }
    }

    private PhysicsBody? hitbox = null!;
    public PhysicsBody? Hitbox => hitbox;
    private Vector2 hitboxOffset;

    [Serialize(0.5f, IsPropertySaveable.No, description: "Time to reach full charge (seconds).")]
    public float MaxChargeTime { get; set; }

    [Serialize(1.3f, IsPropertySaveable.No, description: "Affects the damage of charged attacks, scaling linearly with charge progress.")]
    public float ChargeAttackMultiplier { get; set; }

    [Serialize(-10.0f, IsPropertySaveable.No, description: "Weapon pull-back distance during charge (pixels).")]
    public float RetreatDistance { get; set; }

    [Serialize(50.0f, IsPropertySaveable.No, description: "Weapon extension distance during thrust (pixels).")]
    public float ThrustDistance { get; set; }

    [Serialize(0.2f, IsPropertySaveable.No, description: "Duration of the thrust animation (seconds).")]
    public float ThrustDuration { get; set; }

    private enum SpecialActionType
    {
        OnChargeStart, Charging,
        OnThrustStart, Thrusting,
        OnCooldownStart, Cooldown,
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
                Duration = el.GetAttributeFloat("duration", 0f),
                ChargeMultiplier = el.GetAttributeFloat("chargemultiplier", 1.5f),
                ConditionalSlots = conditionalSlots
            });
        }
    }

    public override bool SecondaryUse(float deltaTime, Character? character = null)
    {
        return characterUsable || character == null;
    }

    public override bool Use(float deltaTime, Character? character = null)
    {
        return character is { Removed: false } && reloadTimer <= 0.0f;
    }

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

            activeForce.Timer -= deltaTime;
            if (activeForce.Timer <= 0f || !CheckForceValidity(picker, activeForce.Specification))
            {
                activeForces.RemoveAt(i);
                continue;
            }

            foreach (var limb in picker.AnimController.Limbs)
            {
                if (!limb.IsSevered && !limb.Removed
                        && activeForce.Specification.LimbTypes.Contains(limb.type))
                {
                    limb.body.ApplyLinearImpulse(activeForce.ForceWorld * limb.Mass);
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
                if (aimHeld) { TransitionTo(State.Aiming); }
                break;

            case State.Aiming:
                if (!aimHeld)
                {
                    TransitionTo(State.Idle);
                }
                else if (shootDown && reloadTimer <= 0)
                {
                    TransitionTo(State.Charging);
                }
                break;

            case State.Charging:
                ChargeTimer += deltaTime;
                ApplyStatusEffects(SpecialActionType.Charging, deltaTime, character: picker, user: User);

                if (!aimHeld)
                {
                    TransitionTo(State.Idle);
                }
                else if (!shootDown)
                {
                    TransitionTo(State.Thrusting);
                }
                break;

            case State.Thrusting:
                ThrustTimer += deltaTime;
                ApplyStatusEffects(SpecialActionType.Thrusting, deltaTime, character: picker, user: User);

                if (ThrustProgress == 1.0f)
                {
                    TransitionTo(State.Cooldown);
                }
                break;

            case State.Cooldown:
                ApplyStatusEffects(SpecialActionType.Cooldown, deltaTime, character: picker);

                if (!aimHeld)
                {
                    TransitionTo(State.Idle);
                }
                else
                {
                    TransitionTo(State.Aiming);
                }
                break;
        }

        ApplyStatusEffects(ActionType.OnActive, deltaTime, picker);

        if (picker.AnimController is not null && item.body.Dir != picker.AnimController.Dir)
        {
            item.FlipX(relativeToSub: false);
        }

        AnimateHold(deltaTime, aimHeld);
    }

    private void SyncHitbox()
    {
        if (hitbox?.Removed ?? true) { return; }
        hitbox.Submarine = item.body.Submarine;
        hitbox.Dir = item.body.Dir;
        Vector2 offset = MathUtils.RotatePoint(hitboxOffset * hitbox.Dir, item.body.Rotation);
        hitbox.ResetDynamics();
        hitbox.SetTransform(item.body.SimPosition + offset, item.body.Rotation);
    }

    public override void Drop(Character? dropper, bool setTransform = true)
    {
        base.Drop(dropper, setTransform);
        DisableThrustCollision();
    }

    public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

    private void AnimateHold(float deltaTime, bool aimHeld)
    {
        if (picker.AnimController is not AnimController controller) { return; }

        scaledHandlePos[0] = handlePos[0] * item.Scale;
        scaledHandlePos[1] = handlePos[1] * item.Scale;

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
                float retreat = ConvertUnits.ToSimUnits(RetreatDistance) * ChargeProgress;
                controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos + new Vector2(-retreat, 0f),
                    aim: true, holdAngle, aimAngle, aimMelee: true);
                break;

            case State.Thrusting:
                float extend = ConvertUnits.ToSimUnits(ThrustDistance) * ThrustProgress;
                controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos + new Vector2(extend, 0f),
                    aim: true, holdAngle, aimAngle, aimMelee: true);
                break;

            case State.Cooldown:
                if (aimHeld)
                {
                    controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos,
                        aim: true, holdAngle, aimAngle, aimMelee: true);
                }
                else
                {
                    controller.HoldItem(deltaTime, item, scaledHandlePos, itemPos: holdPos, aim: false, holdAngle);
                }
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
                ChargeTimer = 0f;
                ApplyStatusEffects(SpecialActionType.OnChargeStart, 1.0f, character: picker, user: User);
                break;

            case State.Thrusting:
                SetUser(picker);
                ThrustTimer = 0f;
                ActivateNearbySleepingCharacters();
                EnableThrustCollision();
                hitting = true;
                picker.AnimController?.LockFlipping();
                ApplyThrustForces(User!);
                ApplyStatusEffects(SpecialActionType.OnThrustStart, 1.0f, character: picker, user: User);
                break;

            case State.Cooldown:
                reloadTimer = Reload;
                reloadTimer /= 1f + picker.GetStatValue(StatTypes.MeleeAttackSpeed);
                reloadTimer /= 1f + item.GetQualityModifier(Quality.StatType.StrikingSpeedMultiplier);
                ApplyStatusEffects(SpecialActionType.OnCooldownStart, 1.0f, character: picker);
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
        if (User?.Removed ?? true)
        {
            DisableThrustCollision();
            return false;
        }

        contact.GetWorldManifold(out _, out var points);

        if (Submarine.PickBody(User.AnimController.AimSourceSimPos, points[0],
                collisionCategory: Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionItemBlocking,
                allowInsideFixture: true,
                customPredicate: f => f.CollidesWith.HasFlag(Physics.CollisionItem) && f.Body != f2.Body) != null)
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
            if (AllowHitMultiple && hitTargets.Contains(target)) { return false; }
            hitTargets.Add(target);
            if (!AllowHitMultiple && hitbox is not null) { hitbox.FarseerBody.OnCollision -= OnThrustCollision; }
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

        Vector2 aimAngle = Vector2.Normalize(user.CursorWorldPosition - controller.AimSourceWorldPos);

        foreach (var spec in forceSpces)
        {
            if (!CheckForceValidity(user, spec)) { continue; }

            float scale = MathHelper.Lerp(1.0f, spec.ChargeMultiplier, ChargeProgress);
            Vector2 local = spec.ForceLocal * scale;
            Vector2 world = new(local.X * aimAngle.X - local.Y * aimAngle.Y, local.X * aimAngle.Y + local.Y * aimAngle.X);

            foreach (var limb in user.AnimController.Limbs)
            {
                if (!limb.IsSevered && !limb.Removed && spec.LimbTypes.Contains(limb.type))
                {
                    limb.body.ApplyLinearImpulse(world * limb.Mass);
                }
            }

            if (spec.Duration > 0.0f)
            {
                activeForces.Add(new ActiveForce { Specification = spec, ForceWorld = world });
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
