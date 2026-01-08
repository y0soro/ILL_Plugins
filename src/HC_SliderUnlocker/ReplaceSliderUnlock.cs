using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ILL_SliderUnlocker;

[BepInProcess("HoneyCome")]
[BepInProcess("HoneyComeccp")]
[BepInPlugin("HC_SliderUnlock", "Dummy HC_SliderUnlock deactivator plugin", "99.9.9")]
public class ReplaceSliderUnlock : BasePlugin
{
    public override void Load() { }
}
