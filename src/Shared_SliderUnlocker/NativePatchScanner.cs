using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using BepInEx.Unity.Common;
using Iced.Intel;
using ILL_SliderUnlocker.NativeCode;
using LibCpp2IL;
using LibCpp2IL.Logging;
using LibCpp2IL.Metadata;

namespace ILL_SliderUnlocker;

internal class NativePatchScanner(string gameAssemblyPath, string metadataPath)
{
    private readonly string gameAssemblyPath = gameAssemblyPath;
    private readonly string metadataPath = metadataPath;
    private LogWriter origWriter = null;

    private readonly Dictionary<ulong, List<Il2CppMethodInfo>> MethodsByRva = [];

    public ulong GetInfoRva { get; private set; } = 0;

    public Dictionary<ulong, BlockPatchInfo> ScanAndPatch(
        ulong imageBase,
        ulong imageSize,
        BlockPathFilter preFilter = null
    )
    {
        Dictionary<ulong, BlockPatchInfo> res;

        try
        {
            LoadMetadata();
            res = ScanAndPatch_(imageBase, imageSize, preFilter);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
            res = [];
        }
        finally
        {
            ResetMetadata();
        }

        return res;
    }

    public enum BlockPathType
    {
        Method,
        EntryBlock,
        SubBlock,
        SubFunction,
    }

    public class BlockPath
    {
        public ulong Rva = 0;
        public BlockPathType Type = BlockPathType.Method;
        public int SubIndex = 0;
        public List<Il2CppMethodInfo> MethodList = [];

        private string FormatMethod() =>
            $"{{{string.Join(",,", MethodList.Select(i => $"{i.AssemblyName}::{i.FullName}()"))}}}";

        public override string ToString()
        {
            return Type switch
            {
                BlockPathType.Method => FormatMethod(),
                BlockPathType.EntryBlock => "^entry",
                BlockPathType.SubBlock => MethodList.Count == 0
                    ? $"^block_{SubIndex}"
                    : $"^block_{SubIndex}#{FormatMethod()}",
                BlockPathType.SubFunction => $";sub{SubIndex}",
                _ => "",
            };
        }
    }

    public class BlockPatchInfo
    {
        public readonly List<List<BlockPath>> fullPaths = [];

        public readonly List<Instruction> patches = [];
    }

    private class BlocksCache
    {
        public readonly List<(ulong, BlockPatchInfo, BlockPath)> blockPaths = [];
        public readonly List<(ulong, BlockPath)> subPaths = [];
    }

    private Dictionary<ulong, BlockPatchInfo> ScanAndPatch_(
        ulong imageBase,
        ulong imageSize,
        BlockPathFilter preFilter
    )
    {
        Dictionary<ulong, BlockPatchInfo> blockRvaToPatch = [];
        Dictionary<ulong, BlocksCache> rvaToBlocksCache = [];

        var analyzer = new NativeCodeBlockAnalyzer(imageBase, imageSize);

        var clampPatcher = new ClampPatcher(imageBase, imageSize);

        Queue<(ulong, IEnumerable<BlockPath>, int)> toWalkSub = [];

        void WalkRva(ulong rva, IEnumerable<BlockPath> parentPath, int depth)
        {
            if (rvaToBlocksCache.TryGetValue(rva, out var cache))
            {
                foreach (var (blockRva, blockPatch, blockPath) in cache.blockPaths)
                {
                    blockPatch.fullPaths.Add([.. parentPath, blockPath]);
                }

                foreach (var (subRva, subPath) in cache.subPaths)
                {
                    toWalkSub.Enqueue((subRva, [.. parentPath, subPath], depth + 1));
                }

                return;
            }
            rvaToBlocksCache[rva] = cache = new BlocksCache();

            var blocks = analyzer.Walk(rva);

            var blockCnt = 0;
            foreach (var block in blocks.Blocks)
            {
                var blockRva = block.IP - imageBase;

                if (!blockRvaToPatch.TryGetValue(blockRva, out var blockPatch))
                {
                    if (!clampPatcher.PreCheck(block))
                        continue;

                    blockPatch = new BlockPatchInfo();
                    blockPatch.patches.AddRange(clampPatcher.Patch(block));
                    if (blockPatch.patches.Count == 0)
                        continue;

                    blockRvaToPatch[blockRva] = blockPatch;
                }

                var blockPath = new BlockPath { Rva = blockRva, SubIndex = blockCnt };
                if (blockRva == rva)
                {
                    blockPath.Type = BlockPathType.EntryBlock;
                }
                else if (MethodsByRva.TryGetValue(blockRva, out var blockMethodList))
                {
                    blockPath.Type = BlockPathType.SubBlock;
                    blockPath.MethodList = blockMethodList;
                }
                else
                {
                    blockPath.Type = BlockPathType.SubBlock;
                }

                blockPatch.fullPaths.Add([.. parentPath, blockPath]);
                cache.blockPaths.Add((blockRva, blockPatch, blockPath));

                blockCnt++;
            }

            var subCnt = 0;
            foreach (var callTarget in blocks.CallTargets)
            {
                var subRva = callTarget - imageBase;

                if (MethodsByRva.TryGetValue(subRva, out var subMethodList))
                {
                    // skip defined methods
                    continue;
                }

                var subPath = new BlockPath
                {
                    Rva = subRva,
                    SubIndex = subCnt,
                    Type = BlockPathType.SubFunction,
                };

                cache.subPaths.Add((subRva, subPath));

                toWalkSub.Enqueue((subRva, [.. parentPath, subPath], depth + 1));
                subCnt++;
            }
        }

        foreach (var (rva, methodList) in MethodsByRva)
        {
            var methodPath = new BlockPath
            {
                Rva = rva,
                MethodList = methodList,
                SubIndex = 0,
                Type = BlockPathType.Method,
            };

            List<BlockPath> path = [methodPath];

            if (preFilter != null && !preFilter.IsMatch([path]))
                continue;

            WalkRva(rva, path, 0);
        }

        while (toWalkSub.TryDequeue(out var nextSub))
        {
            var (rva, path, depth) = nextSub;
            if (depth > 2)
                continue;

            WalkRva(rva, path, depth);
        }

        return blockRvaToPatch;
    }

    private void LoadMetadata()
    {
        origWriter = LibLogger.Writer;
        LibLogger.Writer = new Cpp2IlLogWriter();

        LibCpp2IlMain.Reset();
        LibCpp2IlMain.Settings.AllowManualMetadataAndCodeRegInput = false;
        LibCpp2IlMain.Settings.DisableGlobalResolving = true;
        LibCpp2IlMain.Settings.DisableMethodPointerMapping = true;
        LibCpp2IlMain.LoadFromFile(gameAssemblyPath, metadataPath, UnityInfo.Version);

        foreach (var methodDef in LibCpp2IlMain.TheMetadata.methodDefs)
        {
            var info = new Il2CppMethodInfo(methodDef);

            var rva = info.Rva;
            if (rva == 0)
                continue;

            if (!MethodsByRva.TryGetValue(rva, out var methodList))
            {
                methodList = [];
                MethodsByRva.Add(rva, methodList);
            }

            methodList.Add(info);

            if (
                GetInfoRva == 0
                && info.TypeFullName.EndsWith("AnimationKeyInfo.Controller")
                && info.Name == "GetInfo"
                && methodDef.Parameters.Length == 6
                && methodDef.Parameters[1].ParameterName == "rate"
            )
            {
                GetInfoRva = rva;
            }
        }
    }

    private void ResetMetadata()
    {
        MethodsByRva.Clear();

        LibCpp2IlMain.Reset();
        LibCpp2IlMain.Binary = null;
        LibCpp2IlMain.TheMetadata = null;
        LibCpp2IlMain.Settings.DisableGlobalResolving = false;
        LibCpp2IlMain.Settings.DisableMethodPointerMapping = false;

        LibLogger.Writer = origWriter;
    }

    public class Il2CppMethodInfo
    {
        // XXX: evaluate and store all used info instead?
        // public readonly Il2CppMethodDefinition MethodDef;
        public readonly ulong Rva;

        public readonly string AssemblyName;
        public readonly string TypeFullName;
        public readonly string Name;
        public readonly string FullName;

        public Il2CppMethodInfo(Il2CppMethodDefinition methodDef)
        {
            // MethodDef = methodDef;
            Rva = methodDef.Rva;

            AssemblyName = methodDef.DeclaringType?.DeclaringAssembly?.Name ?? "";
            TypeFullName = methodDef.DeclaringType?.FullName ?? "";
            Name = methodDef.Name ?? "";
            FullName = $"{TypeFullName}:{Name}";
            // XXX: parameter types and return type
        }
    }

    private class Cpp2IlLogWriter() : LogWriter
    {
        private static void LogMsg(LogLevel level, string message)
        {
            if (message.EndsWith(Environment.NewLine))
            {
                message = message[..^Environment.NewLine.Length];
            }
            Plugin.Log.Log(level, $"LibCpp2IL: {message}");
        }

        public override void Error(string message)
        {
            LogMsg(LogLevel.Error, message);
        }

        public override void Info(string message)
        {
            LogMsg(LogLevel.Info, message);
        }

        public override void Verbose(string message) { }

        public override void Warn(string message)
        {
            LogMsg(LogLevel.Warning, message);
        }
    }
}
