using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ILL_SliderUnlocker;

[BepInProcess("AmanatsuLocation")]
[BepInProcess("AmanatsuLocation_Trial")]
[BepInProcess("AmanatsuLocation_CharacterViewer")]
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public partial class Plugin : BasePlugin { }
