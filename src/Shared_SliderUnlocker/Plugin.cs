using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ILL_SliderUnlocker;

public partial class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;

#if !DC
    private static ConfigEntry<int> Minimum;
    private static ConfigEntry<int> Maximum;
#endif

    private enum ModCheck
    {
        None,
        Slider,
        All,
    }

    private static ConfigEntry<ModCheck> RemoveModCheck;

    private static string GameAssemblyPath
    {
        get =>
            Environment.GetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH")
            ?? Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
    }

    private static string DefaultMetadataPath =>
        Path.Combine(
            Paths.GameRootPath,
            Paths.GameDataPath,
            "il2cpp_data/Metadata/global-metadata.dat"
        );

    private static string ConfigDirPath =>
        Path.Combine(Paths.BepInExRootPath, "config", "ILL_SliderUnlocker");
    private static string StateDirPath =>
        Path.Combine(ConfigDirPath, Process.GetCurrentProcess().ProcessName);

    private static string MetadataPath
    {
        get
        {
            try
            {
                var metadataPathCfg = (string)
                    ConfigFile
                        .CoreConfig.First(i =>
                            i.Key.Section == "IL2CPP" && i.Key.Key == "GlobalMetadataPath"
                        )
                        .Value.BoxedValue;

                if (string.IsNullOrWhiteSpace(metadataPathCfg))
                    return DefaultMetadataPath;

                return Path.Combine(
                    Paths.GameRootPath,
                    metadataPathCfg
                        .Replace("{BepInEx}", Paths.BepInExRootPath)
                        .Replace("{ProcessName}", Paths.ProcessName)
                        .Replace("{GameDataPath}", Paths.GameDataPath)
                );
            }
            catch (Exception)
            {
                return DefaultMetadataPath;
            }
        }
    }

    // change if filtering logic changes
    public static string CacheEpoch = "1";

    public override void Load()
    {
        Log = base.Log;

        var metadata = MetadataHelper.GetMetadata(this);

#if !DC
        Minimum = Config.Bind(
            "Slider Limits",
            "Minimum slider value",
            -100,
            new ConfigDescription(
                "Changes will take effect next time the editor is loaded or a character is loaded.",
                new AcceptableValueRange<int>(-500, 0)
            )
        );
        Maximum = Config.Bind(
            "Slider Limits",
            "Maximum slider value",
            200,
            new ConfigDescription(
                "Changes will take effect next time the editor is loaded or a character is loaded.",
                new AcceptableValueRange<int>(100, 600)
            )
        );
#endif
        RemoveModCheck = Config.Bind(
            "Extra",
            "Remove MOD checks",
            ModCheck.Slider,
            "Allows you upload modded characters to official sites. Changes will take effect next time the editor is loaded or a character is loaded."
        );

        Directory.CreateDirectory(ConfigDirPath);
        File.WriteAllText(
            Path.Combine(ConfigDirPath, "README_filter_rules.txt"),
            "Changes to *.default.json will not persist.\n"
                + "Create pre_filter.json and/or filter.json to override default filter rules in *.default.json.\n"
        );

        var preFilter = BlockPathFilter.LoadOrDefault(
            Path.Combine(ConfigDirPath, "pre_filter.json"),
            Path.Combine(ConfigDirPath, "pre_filter.default.json"),
            BlockPathFilter.DefaultPreFilterRules,
            out var preFilterHash
        );
        var filter = BlockPathFilter.LoadOrDefault(
            Path.Combine(ConfigDirPath, "filter.json"),
            Path.Combine(ConfigDirPath, "filter.default.json"),
            BlockPathFilter.DefaultFilterRules,
            out var filterHash
        );

        var assemblyHash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(GameAssemblyPath)));

        var cache = new ScanCache(
            Path.Combine(StateDirPath, $"scan_cache_{assemblyHash[..7].ToLowerInvariant()}.txt")
        );
        cache.AddCacheKey("GameAssembly.dll", assemblyHash);
        cache.AddCacheFileHash("global-metadata.dat", MetadataPath);
        cache.AddCacheKey("Block Filter Pre", preFilterHash);
        cache.AddCacheKey("Block Filter", filterHash);
        cache.AddCacheKey("Epoch", CacheEpoch);

        var gaModule = Process
            .GetCurrentProcess()
            .Modules.Cast<ProcessModule>()
            .First(i => i.ModuleName == "GameAssembly.dll");

        var scanWatch = Stopwatch.StartNew();
        ScanAndPatch(
            (ulong)gaModule.BaseAddress,
            (ulong)gaModule.ModuleMemorySize,
            preFilter,
            filter,
            cache
        );

        scanWatch.Stop();
        Log.LogInfo($"Native scan and patch time {scanWatch.ElapsedMilliseconds} ms");

        Harmony.CreateAndPatchAll(typeof(Hooks), MyPluginInfo.PLUGIN_GUID);

        if (RemoveModCheck.Value >= ModCheck.Slider)
        {
            Harmony.CreateAndPatchAll(typeof(SliderModCheckHooks), MyPluginInfo.PLUGIN_GUID);
        }
        if (RemoveModCheck.Value == ModCheck.All)
        {
            Harmony.CreateAndPatchAll(typeof(AllModCheckHooks), MyPluginInfo.PLUGIN_GUID);
        }
    }

    private static readonly string getInfoKey = "AnimationKeyInfo.Controller:GetInfo";

    private void ScanAndPatch(
        ulong imageBase,
        ulong imageSize,
        BlockPathFilter preFilter,
        BlockPathFilter filter,
        ScanCache cache
    )
    {
        try
        {
            if (cache.TryLoad(out var cachedFields, out var cachedBlocks))
            {
                Log.LogInfo("Cache hit, patching with cache");
#if DEBUG
                cache.Save(
                    cachedFields,
                    cachedBlocks,
                    Path.Combine(StateDirPath, "debug_scan_cache.txt")
                );
#endif
                var getInfoRva = Convert.ToUInt64(cachedFields.GetValueOrDefault(getInfoKey), 16);
                if (getInfoRva != 0)
                {
                    WritePatches(
                        imageBase,
                        imageSize,
                        cachedBlocks.SelectMany(i => i.Matched ? i.Patches : [])
                    );

                    GetInfoHook.Install(imageBase + getInfoRva);

                    return;
                }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning($"Failed to load and patch from cache, {e}");
        }

        Log.LogInfo("Start scanning clamping codes");

        var scanner = new NativePatchScanner(GameAssemblyPath, MetadataPath);
        var patchBlocks = scanner.ScanAndPatch(imageBase, imageSize, preFilter);

        using var instrBuf = new MemoryStream();
        var encoder = Iced.Intel.Encoder.Create(64, new Iced.Intel.StreamCodeWriter(instrBuf));

        var patchSetCnt = 0;

        List<ScanCache.Block> cacheBlocks = [];

        foreach (var (blockRva, blockPatch) in patchBlocks)
        {
            var isMatch = filter.IsMatch(blockPatch);
            if (isMatch)
                patchSetCnt++;

            List<(ulong, byte[])> encoded = [];

            foreach (var patch in blockPatch.patches)
            {
                instrBuf.Position = 0;
                instrBuf.SetLength(0);

                int len;
                try
                {
                    len = (int)encoder.Encode(patch, patch.IP);
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                    continue;
                }

                var patchRva = patch.IP - imageBase;
                var gap = patch.Length - len;

                if (gap < 0)
                {
                    Log.LogError(
                        $"FIXME: encoded patch length {len} greater than original instruction length:{patch.Length}, patch RVA:{patchRva}"
                    );
                    continue;
                }

                // fill gap with NOPs
                for (int i = 0; i < gap; i++)
                {
                    // 0x90 NOP
                    instrBuf.WriteByte(0x90);
                }

                encoded.Add((patchRva, instrBuf.ToArray()));
            }

            cacheBlocks.Add(
                new()
                {
                    Rva = blockRva,
                    Matched = isMatch,
                    Paths = [.. blockPatch.fullPaths.Select(i => string.Join(string.Empty, i))],
                    Patches = [.. encoded],
                }
            );
        }

        WritePatches(imageBase, imageSize, cacheBlocks.SelectMany(i => i.Matched ? i.Patches : []));

        Log.LogInfo($"Included patch set count {patchSetCnt}/{patchBlocks.Count}");

        if (scanner.GetInfoRva != 0)
        {
            GetInfoHook.Install(imageBase + scanner.GetInfoRva);
        }
        else
        {
            Log.LogError($"RVA of {getInfoKey} not found");
        }

        cache.Save(new() { [getInfoKey] = scanner.GetInfoRva.ToString("x") }, cacheBlocks);
    }

    private unsafe void WritePatches(
        ulong imageBase,
        ulong imageSize,
        IEnumerable<(ulong, byte[])> patches
    )
    {
        Log.LogInfo("Applying clamping remover patches");

        using var imageStream = new UnmanagedMemoryStream(
            (byte*)imageBase,
            (long)imageSize,
            (long)imageSize,
            FileAccess.ReadWrite
        );

        foreach (var (rva, data) in patches)
        {
            var patchVa = imageBase + rva;
            if (
                !VirtualProtect(
                    (nint)patchVa,
                    (nuint)data.Length,
                    (uint)MemoryProtection.ExecuteReadWrite,
                    out var lpflOldProtect
                )
            )
            {
                Log.LogError(
                    $"Failed to change memory protection, VA:{patchVa:x}, RVA:{rva}, error:{Marshal.GetLastWin32Error()}"
                );
                continue;
            }

            imageStream.PositionPointer = (byte*)patchVa;
            imageStream.Write(data);

            VirtualProtect((nint)patchVa, (nuint)data.Length, lpflOldProtect, out var _);
        }
    }

    [Flags]
    private enum MemoryProtection
    {
        Execute = 0x10,
        ExecuteRead = 0x20,
        ExecuteReadWrite = 0x40,
        ExecuteWriteCopy = 0x80,
        NoAccess = 1,
        ReadOnly = 2,
        ReadWrite = 4,
        WriteCopy = 8,
        GuardModifierflag = 0x100,
        NoCacheModifierflag = 0x200,
        WriteCombineModifierflag = 0x400,
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint flNewProtect,
        out uint lpflOldProtect
    );
}
