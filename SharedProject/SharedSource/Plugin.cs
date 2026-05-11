using Barotrauma.Plugins;

namespace MoreItemComponents;

public partial class Plugin : IBarotraumaPlugin
{
    public static readonly IDebugConsole DebugConsole = PluginServiceProvider.GetService<IDebugConsole>();
    public static readonly ISettingsService SettingsService = PluginServiceProvider.GetService<ISettingsService>();
    public static readonly IHarmonyProvider HarmonyProvider = PluginServiceProvider.GetService<IHarmonyProvider>();
    public static readonly IItemComponentRegistrar ItemComponentRegistrar = PluginServiceProvider.GetService<IItemComponentRegistrar>();

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void Init()
    {
        HarmonyProvider.PatchAll();

        ItemComponentRegistrar.RegisterAllItemComponents();

        InitializeProjectSpecific();
    }

    public partial void InitializeProjectSpecific();

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void OnContentLoaded()
    {

    }

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void Dispose()
    {

    }
}