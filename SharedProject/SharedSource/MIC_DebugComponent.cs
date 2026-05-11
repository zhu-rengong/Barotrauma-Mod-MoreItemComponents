using Barotrauma.Items.Components;

namespace MoreItemComponents;

partial class MIC_DebugComponent : ItemComponent, IDrawableComponent
{
    private PhysicsBody? hitbox;

    public MIC_DebugComponent(Item item, ContentXElement element) : base(item, element) { }

    public override void OnItemLoaded()
    {
        base.OnItemLoaded();

        hitbox = item.GetComponent<MIC_ThrustingMeleeWeapon>()?.Hitbox;
    }
}
