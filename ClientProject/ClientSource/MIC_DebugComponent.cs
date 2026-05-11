using Barotrauma.Items.Components;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MoreItemComponents;

partial class MIC_DebugComponent : ItemComponent, IDrawableComponent
{
    public Vector2 DrawSize => Vector2.Zero;

    public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null)
    {
        if (!GameMain.DebugDraw) { return; }

        if (hitbox is not null)
        {
            hitbox.UpdateDrawPosition(interpolate: false);
            hitbox.DebugDraw(spriteBatch, Color.Orange);
        }
    }
}
