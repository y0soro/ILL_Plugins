# ILL_SliderUnlocker

A general SliderUnlocker for all ILLGAMES titles(AC, SVS, HC, DC) and versions.

-   HC: HoneyCome
-   DC: DigitalCraft
-   SVS: Samabake Scramble/Summer Vacation! Scramble
-   AC: Aicomi

Sliders unlocked:

-   Base face and body shaping
-   Physics(i.e. softness)
-   Proper Slerp(Spherical linear interpolation) handling for rotations
-   Positioning and scaling of various textures(eyes, makeups, clothes, ..)
-   Character voice pitch

For power users, you can also unlock other clamped variables by including related native code block in filtering rules.(See `BepInEx/config/ILL_SliderUnlocker/`)

## Installation

| Download                                                                               | Note |
| -------------------------------------------------------------------------------------- | ---- |
| [v1.0.0](https://github.com/y0soro/ILL_Plugins/releases/tag/ILL_SliderUnlocker-v1.0.0) |      |

0. (Install [BepInEx](https://builds.bepinex.dev/projects/bepinex_be).)
1. Unpack to BepInEx enabled game root.
2. For SVS, make sure you have a matching [decrypted_global-metadata.dat](https://uu.getuploader.com/y0soro/) installed and properly configured.
3. This SliderUnlock**er** rewrite supersedes previous HC_SliderUnlock, HC_SliderUnlock_DC and SVS_SliderUnlock by @Samsung Galaxy Note 10+ and [me](https://github.com/y0soro/SVS_SliderUnlock/releases), you can optionally remove those old dlls.
4. Launch the game, wait a few seconds to let plugin build the cache. Launches after that would use cache instead unless cache invalidates due to game updates or filtering rule changes.

### Breaking changes from SliderUnlock

ILL_SliderUnlock**er** changes how rotation keyframes are interpolated for slider value beyond 0-100, you may notice subtle differences in appearance of slider unlocked characters.

You can just switch back to old SliderUnlocks if you want to preserve old appearance. I will not support the old interpolation method because the current one is just better and I don't bother to add a switch in per-character basis.

## Why and how?

SliderUnclocker requires remove clamping code of shape values to work. For Mono-based ILLUSION games, it's easy because you can just hook `Mathf.Clamp` method and it's 90% done. However for IL2CPP-based games, it's a whole different story because of aggressive optimization policy that inlining `Mathf.Clamp` to all of its users, so you need to hook all of those user functions to remove clamping code.

The question is, expect the address of `Math.Clamp` are recorded in metadata, all others addresses of inlined clamping codes are not predictable and can change by game updates.

The old SliderUnlocks handles this by either stick to a specific game version or use binary signatures to locate a few predictable clamping codes. However neither of these two methods can be easily maintained and can break at any time due to game updates. And only a limited number of clamping codes are patched due to maintenance cost.

Thus in this SliderUnlocker rewrite, we employ native machine code disassembler to traverse all recorded game methods and find clamping codes much reliably. And then we further filter found clamping codes by a set filter rules so unrelated clamping codes would not be patched. And it mostly only need some filter rule changes even if game updates breaks SliderUnlocker.
