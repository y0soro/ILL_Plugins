using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ILL_SliderUnlocker;

[BepInProcess("DigitalCraft")]
[BepInProcess("DigitalCraftVR")]
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public partial class Plugin : BasePlugin { }
