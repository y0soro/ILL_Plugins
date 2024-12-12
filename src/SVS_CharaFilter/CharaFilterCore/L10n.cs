using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;

namespace CharaFilterCore;

internal class Translation
{
    public Dictionary<string, string> groupName { get; set; } = [];
    public Dictionary<string, string> tagName { get; set; } = [];
    public Dictionary<string, string> ui { get; set; } = [];
}

public static class L10n
{
    private static bool initialized = false;
    private static Translation translation = new();

    public static void Init(string lang = null)
    {
        if (initialized)
            return;
        if (string.IsNullOrWhiteSpace(lang))
        {
            lang = TryGetAutoTranslatorLanguage();
            if (string.IsNullOrEmpty(lang))
            {
                lang = Thread.CurrentThread.CurrentCulture.Name;
            }
        }

        var index = lang.Length;

        byte[] bytes = null;
        do
        {
            lang = lang[..index];

            try
            {
                bytes = Utils.FindEmbeddedResource($"{lang}.json");
                break;
            }
            catch (Exception)
            {
                if (lang == "en")
                    throw null;
            }

            try
            {
                var regex = new Regex($".*{lang}(-[a-zA-Z]+)+\\.json");
                bytes = Utils.FindEmbeddedResource(regex);
                break;
            }
            catch (Exception) { }

            index = lang.LastIndexOf("-");

            if (index < 0 && lang != "en")
            {
                lang = "en";
                index = lang.Length;
            }
        } while (index > 0);

        if (bytes == null)
            throw null;

        try
        {
            translation = JsonSerializer.Deserialize<Translation>(bytes);
        }
        catch (Exception)
        {
            translation = new Translation
            {
                groupName = [],
                tagName = [],
                ui = [],
            };
        }

        initialized = true;
    }

    private static string GetTranslation(Dictionary<string, string> dict, string from)
    {
        if (dict.TryGetValue(from, out string to))
        {
            if (string.IsNullOrEmpty(to))
                return from;
            return to;
        }
        return from;
    }

    public static string Group(string from)
    {
        return GetTranslation(translation.groupName, from);
    }

    public static string Tag(string from)
    {
        return GetTranslation(translation.tagName, from);
    }

    public static string UI(string from)
    {
        return GetTranslation(translation.ui, from);
    }

    public static string TryGetAutoTranslatorLanguage()
    {
        try
        {
            var path = Path.Combine(Paths.ConfigPath, "AutoTranslatorConfig.ini");
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                if (line.StartsWith("Language="))
                    return line["Language=".Length..].Trim();
            }
        }
        catch (Exception) { }
        return null;
    }
}
