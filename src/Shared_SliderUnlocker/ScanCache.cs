using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ILL_SliderUnlocker;

internal class ScanCache(string cachePath)
{
    private readonly string cachePath = cachePath;
    private readonly Dictionary<string, string> cacheKeys = [];

    private static readonly ulong refVaBase = 0x180_000_000;

    public void AddCacheFileHash(string name, string filePath)
    {
        AddCacheKey(name, File.ReadAllBytes(filePath));
    }

    public void AddCacheKey(string name, ReadOnlySpan<byte> data)
    {
        cacheKeys[name] = Convert.ToHexString(MD5.HashData(data));
    }

    public void AddCacheKey(string name, string value)
    {
        cacheKeys[name] = value;
    }

    enum State
    {
        Version,
        VaBase,
        CacheKeys,
        Fields,
        Block,
        BlockMethod,
        BlockPatch,
    }

    public bool TryLoad(
        out Dictionary<string, string> fields,
        out List<Block> blocks,
        string forcePath = null
    )
    {
        blocks = [];
        fields = [];

        FileStream file;
        try
        {
            file = File.OpenRead(forcePath ?? cachePath);
        }
        catch (Exception)
        {
            return false;
        }

        var reader = new StreamReader(file);

        var state = State.Version;

        ulong vaBase = 0;

        Block currBlock = null;

        string line = reader.ReadLine();
        while (line != null)
        {
            line = line.Trim();

            var noReadNext = false;
            switch (state)
            {
                case State.Version:
                    if (line != "version:1")
                        return false;
                    state = State.VaBase;
                    break;
                case State.VaBase:
                    if (!RemovePrefix(line, "va_base:", out var vaBaseText))
                        return false;

                    vaBase = Convert.ToUInt64(vaBaseText, 16);
                    if (vaBase != refVaBase)
                        return false;

                    state = State.CacheKeys;
                    break;
                case State.CacheKeys:
                    if (line != string.Empty)
                    {
                        if (!RemovePrefix(line, "cache_keys:", out var cache))
                            return false;
                        try
                        {
                            var fileCacheKeys = JsonSerializer.Deserialize<
                                Dictionary<string, string>
                            >(cache, lineOptions);

                            // cache keys not equal, invalidate
                            if (!DictionariesEqual(cacheKeys, fileCacheKeys))
                                return false;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        state = State.Fields;
                    }
                    break;
                case State.Fields:
                    if (line != string.Empty)
                    {
                        if (!RemovePrefix(line, "fields:", out var field))
                            return false;
                        try
                        {
                            fields = JsonSerializer.Deserialize<Dictionary<string, string>>(
                                field,
                                lineOptions
                            );
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        state = State.Block;
                    }
                    break;
                case State.Block:
                    if (line != string.Empty)
                    {
                        if (!RemovePrefix(line, "block:", out var block))
                            return false;

                        var parts = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        if (!RemovePrefix(parts[0], "VA:", out var vaText))
                            return false;

                        var va = Convert.ToUInt64(vaText, 16);
                        if (va == 0)
                            return false;

                        if (!RemovePrefix(parts[1], "Matched:", out var matchedText))
                            return false;

                        currBlock = new Block
                        {
                            Rva = va - vaBase,
                            Matched = string.Equals(
                                matchedText,
                                "True",
                                StringComparison.OrdinalIgnoreCase
                            ),
                            Paths = [],
                            Patches = [],
                        };
                        blocks.Add(currBlock);

                        state = State.BlockMethod;
                    }
                    break;
                case State.BlockMethod:
                    if (line.StartsWith("patch:"))
                    {
                        noReadNext = true;
                        state = State.BlockPatch;
                    }
                    else
                    {
                        if (line == string.Empty)
                            return false;

                        currBlock.Paths.Add(line);
                    }
                    break;
                case State.BlockPatch:
                    if (line != string.Empty)
                    {
                        if (!RemovePrefix(line, "patch:", out var patch))
                            return false;

                        var parts = patch.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        if (!RemovePrefix(parts[0], "VA:", out var vaText))
                            return false;

                        var va = Convert.ToUInt64(vaText, 16);
                        if (va == 0)
                            return false;

                        var data = Convert.FromHexString(parts[1]);

                        currBlock.Patches.Add((va - vaBase, data));
                    }
                    else
                    {
                        state = State.Block;
                    }
                    break;
                default:
                    return false;
            }
            if (!noReadNext)
                line = reader.ReadLine();
        }

        // block truncated
        if (state != State.Block)
            return false;

        return true;
    }

    public static bool DictionariesEqual<TKey, TValue>(
        IDictionary<TKey, TValue> a,
        IDictionary<TKey, TValue> b
    )
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null)
            return false;
        if (a.Count != b.Count)
            return false;

        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var other) || !v.Equals(other))
                return false;
        }

        return true;
    }

    private bool RemovePrefix(string text, string prefix, out string remain)
    {
        if (!text.StartsWith(prefix))
        {
            remain = string.Empty;
            return false;
        }

        remain = text[prefix.Length..];
        return true;
    }

    private static readonly JsonSerializerOptions lineOptions = new() { WriteIndented = false };

    public void Save(
        Dictionary<string, string> fields,
        IEnumerable<Block> blocks,
        string forcePath = null
    )
    {
        var filePath = forcePath ?? cachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        var file = File.OpenWrite(filePath);
        file.SetLength(0);

        var writer = new StreamWriter(file);

        writer.Write("version:1\n");
        writer.Write($"va_base:{refVaBase:x}\n");
        ;
        writer.Write($"cache_keys:{JsonSerializer.Serialize(cacheKeys, lineOptions)}\n");
        writer.Write('\n');

        writer.Write($"fields:{JsonSerializer.Serialize(fields, lineOptions)}\n");
        writer.Write('\n');

        foreach (var b in blocks)
        {
            writer.Write($"block: VA:{b.Rva + refVaBase:x} Matched:{b.Matched}\n");

            foreach (var p in b.Paths)
            {
                writer.Write($"  {p}\n");
            }

            foreach (var (pRva, pData) in b.Patches)
            {
                writer.Write($"    patch: VA:{pRva + refVaBase:x} {Convert.ToHexString(pData)}\n");
            }

            writer.Write('\n');
        }

        writer.Close();
    }

    public class Block
    {
        public bool Matched { get; set; }
        public ulong Rva { get; set; }
        public List<string> Paths { get; set; }
        public List<(ulong, byte[])> Patches { get; set; }
    }
}
