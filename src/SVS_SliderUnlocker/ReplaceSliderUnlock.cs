using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ILL_SliderUnlocker;

[BepInProcess("SamabakeScramble")]
[BepInPlugin("SVS_SliderUnlock", "Dummy SVS_SliderUnlock deactivator plugin", "99.9.9")]
public class ReplaceSliderUnlock : BasePlugin
{
    public override void Load() { }
}
