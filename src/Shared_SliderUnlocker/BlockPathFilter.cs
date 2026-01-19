using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ILL_SliderUnlocker;

internal class BlockPathFilter(BlockPathFilter.Rule[] rules)
{
    public readonly Rule[] Rules = rules;

    public bool IsMatch(NativePatchScanner.BlockPatchInfo blockPatch)
    {
        return IsMatch(blockPatch.fullPaths);
    }

    public bool IsMatch(IEnumerable<List<NativePatchScanner.BlockPath>> fullPaths)
    {
        var hasInclude = false;
        var excludeCnt = 0;
        var pathCnt = 0;

        foreach (var path in fullPaths)
        {
            foreach (var rule in Rules)
            {
                // skip by default
                if (!rule.IsMatch(path))
                    continue;

                // skip this path
                if (rule.Action == RuleAction.Exclude)
                {
                    excludeCnt++;
                    break;
                }

                if (rule.Action == RuleAction.Include)
                {
                    hasInclude = true;
                    break;
                }

                if (rule.Action == RuleAction.Reject)
                    return false;

                if (rule.Action == RuleAction.Accept)
                    return true;
            }

            pathCnt++;
        }

        // Include has higher priority
        return hasInclude;
    }

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static BlockPathFilter LoadOrDefault(
        string rulesPath,
        string defaultRulesPath,
        Func<Rule[]> getDefaultRules,
        out string hash
    )
    {
        var defaultRules = getDefaultRules();
        var defaultRulesText = JsonSerializer.SerializeToUtf8Bytes(defaultRules, jsonOptions);

        Directory.CreateDirectory(Path.GetDirectoryName(defaultRulesPath));
        File.WriteAllBytes(defaultRulesPath, defaultRulesText);

        try
        {
            var rulesText = File.ReadAllBytes(rulesPath);
            hash = Convert.ToHexString(MD5.HashData(rulesText));

            var rules = JsonSerializer.Deserialize<Rule[]>(rulesText, jsonOptions);
            return new(rules);
        }
        catch (Exception)
        {
            hash = Convert.ToHexString(MD5.HashData(defaultRulesText));
            return new(defaultRules);
        }
    }

    public static Rule[] DefaultPreFilterRules() =>
        [
            new() { Path = [new() { Assembly = "/^(Assembly-CSharp|ILGLib|IL)\\.dll$/" }] },
            new() { Path = [], Action = RuleAction.Exclude },
        ];

    public static Rule[] DefaultFilterRules() =>
        [
            new() { Path = [new() { TypeName = "UnityEngine." }], Action = RuleAction.Reject },
            new()
            {
                Path =
                [
                    new() { TypeName = "TypefaceAnimator", Method = "Modify" },
                    new() { PathKind = "SubFunction", SubIndex = "" },
                    new() { PathKind = "EntryBlock", BlockIndex = "" },
                ],
                Action = RuleAction.Reject,
            },
            new()
            {
                Path =
                [
                    new() { TypeName = "Character.HumanFace", Method = "UpdateBlendShapeVoice" },
                    new() { PathKind = "SubFunction" },
                    new() { PathKind = "EntryBlock" },
                ],
                Action = RuleAction.Reject,
            },
            new()
            {
                Path =
                [
                    new() { Method = "get_voicePitch" },
                    new() { PathKind = "/(EntryBlock|SubBlock)/", BlockIndex = "" },
                ],
                Action = RuleAction.Accept,
            },
            new()
            {
                Path = [new() { TypeName = "Character.HumanFace" }],
                Action = RuleAction.Exclude,
            },
            new()
            {
                Path =
                [
                    new()
                    {
                        TypeName =
                            "/(Character.Human|HumanCustom|EyeLookMaterialControll|VoiceCtrl)/",
                    },
                ],
            },
            new()
            {
                Path = [new() { TypeName = "SV.H.HScene/AnimeSpeeder" }],
                Action = RuleAction.Exclude,
            },
            new()
            {
                Path =
                [
                    new() { TypeName = "/(^|\\.)H\\./", Method = "/SetAnimationParam(e|a)ter/" },
                ],
                Action = RuleAction.Exclude,
            },
            new()
            {
                Path =
                [
                    new()
                    {
                        TypeName =
                            "/(^(AC|SV|HC|DigitalCraft)\\.|(^|\\.)(H|ADV)\\.|\\.FBS|ColorPicker|EyeLookCalc|NeckLookCalc|NeckLookController|Color|Fade|Camera|Blink|Mouth|AnimationControllerBase|InertialAnimator|BaseCameraControl|BoneSwayCtr|PopupMsg|InteractableAlphaChanger|MatAnm|TexAnm|Morph|Rigging|MotionIK|OverrideCursor|SlicedFilledImage|CaptureFrame|CustomWindowDragMove|StateMiniSelection|StateSetting|CustomImage|MoveWindow|ImageCustom|Manager\\.|ScreenshotHandlerURP)/",
                    },
                ],
                Action = RuleAction.Exclude,
            },
            new()
            {
                Path =
                [
                    new()
                    {
                        TypeName =
                            "/^(DynamicBone|SuperScrollView|KriptoFX|Funly|ARYKEI|SensorToolkit|RuntimeMeshSimplifier|AmplifyColor|CFX_|SmoothCameraOrbit|ImplicitSurface|IncrementalModeling|MetaballBuilder|LakePolygon|MeshColoringRam|RamSpline|BFX_|EMTransition|MatAnmFrame|TexAnmUV)/",
                    },
                ],
                Action = RuleAction.Exclude,
            },
            .. DefaultPreFilterRules(),
        ];

    public class PathMatcher
    {
        public string PathKind { get; set; }
        public string Assembly { get; set; }
        public string TypeName { get; set; }
        public string Method { get; set; }
        public string SubIndex { get; set; }
        public string BlockIndex { get; set; }

        private bool Match(string matcher, string value, bool ignoreCase = false)
        {
            if (string.IsNullOrWhiteSpace(matcher))
            {
                return true;
            }

            if (matcher.StartsWith("/") && matcher.EndsWith("/"))
            {
                // Plugin.Log.LogError($"regex format {matcher[1..^1]}");
                try
                {
                    return Regex.IsMatch(
                        value,
                        matcher[1..^1],
                        RegexOptions.ECMAScript
                            | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
                    );
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Invalid regex format {matcher}, {e}");
                    return false;
                }
            }

            return value.Contains(
                matcher,
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            );
        }

        public bool IsMatch(NativePatchScanner.BlockPath blockPath)
        {
            if (!Match(PathKind, blockPath.Type.ToString(), true))
                return false;

            var matchIndex = blockPath.Type switch
            {
                NativePatchScanner.BlockPathType.EntryBlock
                or NativePatchScanner.BlockPathType.SubBlock => Match(
                    BlockIndex,
                    blockPath.SubIndex.ToString()
                ),
                NativePatchScanner.BlockPathType.SubFunction => Match(
                    SubIndex,
                    blockPath.SubIndex.ToString()
                ),
                _ => string.IsNullOrWhiteSpace(BlockIndex) && string.IsNullOrWhiteSpace(SubIndex),
            };
            if (!matchIndex)
                return false;

            if (
                string.IsNullOrWhiteSpace(Assembly)
                && string.IsNullOrWhiteSpace(TypeName)
                && string.IsNullOrWhiteSpace(Method)
            )
            {
                return true;
            }

            var matchMethod = false;
            foreach (var method in blockPath.MethodList)
            {
                if (
                    Match(Assembly, method.AssemblyName)
                    && Match(TypeName, method.TypeFullName)
                    && Match(Method, method.Name)
                )
                {
                    matchMethod = true;
                    break;
                }
            }

            return matchMethod;
        }
    }

    public enum RuleAction
    {
        Include = 0,
        Exclude,
        Accept,
        Reject,
    }

    public class Rule
    {
        public PathMatcher[] Path { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RuleAction Action { get; set; }

        public bool IsMatch(IEnumerable<NativePatchScanner.BlockPath> path)
        {
            if (Path == null)
                return true;

            var it = path.GetEnumerator();
            foreach (var matcher in Path)
            {
                if (!it.MoveNext() || !matcher.IsMatch(it.Current))
                    return false;
            }

            return true;
        }
    }
}
