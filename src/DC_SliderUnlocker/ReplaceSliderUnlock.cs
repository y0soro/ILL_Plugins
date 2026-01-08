using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ILL_SliderUnlocker;

[BepInProcess("DigitalCraft")]
[BepInPlugin("HC_SliderUnlock_DC", "Dummy HC_SliderUnlock_DC deactivator plugin", "99.9.9")]
public class ReplaceSliderUnlock : BasePlugin
{
    public override void Load() { }
}
