using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using BepInEx.Unity.Common;
using HarmonyLib.Public.Patching;

namespace IL2CPP_ICall2Interop.Patcher;

[PatcherPluginInfo(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Patcher : BasePatcher
{
    internal static new ManualLogSource Log;

    internal static ICallDatabase ICallDb = null;

    internal static string DumpDir = null;

    private static bool HookAllInternalCalls = false;

    private void InitConfigs()
    {
        var DumpMethodDir = Config.Bind(
            "Debug",
            "DumpMethodDir",
            "",
            "Directory to dump dlls containing generated interop methods.\nSupports the following placeholders:\n{BepInEx} - Path to the BepInEx folder."
        );
        DumpDir = DumpMethodDir.Value.Replace("{BepInEx}", Paths.BepInExRootPath);

        HookAllInternalCalls = Config
            .Bind(
                "Debug",
                "HookAllInternalCalls",
                false,
                "Pre-hook all internal call interop methods with dummy patch, useful for testing whether this patcher is correctly implemented for all internal calls."
            )
            .Value;
    }

    public override void Finalizer()
    {
        Log = base.Log;
        InitConfigs();

        var timer = new Stopwatch();

        timer.Start();

        ICallDb = ICallDatabase.Collect(
            GetUnityLibsDir(),
            Path.Combine(
                Paths.BepInExRootPath,
                "config",
                MyPluginInfo.PLUGIN_NAME,
                $"icall_db_v1.unity{UnityInfo.Version}.json"
            )
        );

        timer.Stop();

        Log.LogInfo($"Loaded internal call db in {timer.ElapsedMilliseconds}ms");

        ResolveICallHook.Install();

        PatchManager.ResolvePatcher += HarmonyICallPatcher.TryResolve;

        if (!string.IsNullOrWhiteSpace(DumpDir))
        {
            EmptyDirectory(DumpDir);
        }

        if (HookAllInternalCalls)
        {
            DebugICall.HookAllICalls();
        }
    }

    private static string GetUnityLibsDir()
    {
        try
        {
            var interopBase = (string)
                ConfigFile
                    .CoreConfig.First(i =>
                        i.Key.Section == "IL2CPP" && i.Key.Key == "IL2CPPInteropAssembliesPath"
                    )
                    .Value.BoxedValue;

            interopBase = interopBase
                .Replace("{BepInEx}", Paths.BepInExRootPath)
                .Replace("{ProcessName}", Paths.ProcessName);

            return Path.Combine(interopBase, "unity-libs");
        }
        catch
        {
            return null;
        }
    }

    public static void EmptyDirectory(string dirPath)
    {
        var dir = new DirectoryInfo(dirPath);

        if (!dir.Exists)
            return;

        foreach (FileInfo file in dir.EnumerateFiles())
        {
            file.Delete();
        }

        foreach (DirectoryInfo subDir in dir.EnumerateDirectories())
        {
            subDir.Delete(true);
        }
    }
}
