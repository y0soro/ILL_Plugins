using System;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace WebRequestMask.Core;

public class CommonPlugin
{
    private static ManualLogSource Log;

    public readonly UrlProxy urlProxy;

    private readonly ConfigEntry<string> httpProxy;
    private readonly ConfigEntry<UrlPrefixes> maskUrls;
    private readonly ConfigEntry<UrlPrefixes> allowUrls;
    private readonly ConfigEntry<UrlPatterns> maskUrlPatterns;
    private readonly ConfigEntry<UrlPatterns> allowUrlPatterns;
    private readonly ConfigEntry<bool> enableMask;

    class UrlPrefixes(string[] urls)
    {
        public readonly string[] urls = urls;

        public bool MatchAny(string url)
        {
            return urls.Any(url.StartsWith);
        }
    }

    class UrlPatterns(string[] urlPatterns)
    {
        public readonly string[] urlPatterns = urlPatterns;

        private readonly UserScriptMatch[] _urlPatterns =
        [
            .. urlPatterns.Select(t => new UserScriptMatch(t)),
        ];

        public bool MatchAny(string url)
        {
            return _urlPatterns.Any(pattern => pattern.IsMatch(url));
        }
    }

    static CommonPlugin()
    {
        TomlTypeConverter.AddConverter(
            typeof(UrlPrefixes),
            new()
            {
                ConvertToObject = (str, type) =>
                {
                    return new UrlPrefixes(JsonSerializer.Deserialize<string[]>(str));
                },
                ConvertToString = (obj, type) =>
                {
                    var urls = (UrlPrefixes)obj;
                    return JsonSerializer.Serialize(
                        urls.urls,
                        new JsonSerializerOptions { WriteIndented = false }
                    );
                },
            }
        );

        TomlTypeConverter.AddConverter(
            typeof(UrlPatterns),
            new()
            {
                ConvertToObject = (str, type) =>
                {
                    return new UrlPatterns(JsonSerializer.Deserialize<string[]>(str));
                },
                ConvertToString = (obj, type) =>
                {
                    var patterns = (UrlPatterns)obj;
                    return JsonSerializer.Serialize(
                        patterns.urlPatterns,
                        new JsonSerializerOptions { WriteIndented = false }
                    );
                },
            }
        );
    }

    public CommonPlugin(ManualLogSource Log, ConfigFile config)
    {
        CommonPlugin.Log = Log;

        enableMask = config.Bind("General", "Enable", true, "Enable masking");

        maskUrls = config.Bind(
            "Prefix",
            "Mask URL Prefixes",
            new UrlPrefixes(["https://download.illgames.jp/check/game/tos_check.php"]),
            "Always responds with HTTP 200 OK for URLs matching these prefixes, effectively blocks public Web traffic towards these URLs."
                + "\nAdd an empty prefix \"\" to mask all of the traffic."
        );

        maskUrlPatterns = config.Bind(
            "Prefix",
            "Mask URL Patterns",
            new UrlPatterns([
                // https://upcheck.illgames.jp/product/svs/game/check.php
                // https://upcheck.illgames.jp/product/digitalcraft/game/tos_check.php
                // https://upcheck.illgames.jp/product/aicomi/game/check.php
                // https://upcheck.illgames.jp/product/amaloca/game/check.php
                "*://upcheck.illgames.jp/product/*/game/*",
            ]),
            "Always responds with HTTP 200 OK for URLs matching these patterns, effectively blocks public Web traffic towards these URLs."
                + "\nThe pattern follows the syntax of userscript match patterns, see https://www.tampermonkey.net/documentation.php?locale=en&q=include#meta:match"
        );

        allowUrls = config.Bind(
            "Prefix",
            "Allow URL Prefixes",
            new UrlPrefixes([]),
            "Explicitly allowed URL prefixes, has higher priority than \"Mask URL Prefixes\" and \"Mask URL Patterns\"."
        );

        allowUrlPatterns = config.Bind(
            "Prefix",
            "Allow URL Patterns",
            new UrlPatterns([]),
            "Explicitly allowed URL patterns, has higher priority than \"Mask URL Prefixes\" and \"Mask URL Patterns\"."
                + "\nThe pattern follows the syntax of userscript match patterns, see https://www.tampermonkey.net/documentation.php?locale=en&q=include#meta:match"
        );

        var initPort = config
            .Bind(
                "General",
                "Init Port",
                -1,
                "Initial TCP port used for internal HTTP server, specify -1 to find a free port to use."
            )
            .Value;

        httpProxy = config.Bind(
            "Debug",
            "HTTP Proxy",
            "",
            "Redirect all the UnityWebRequest traffic to this HTTP proxy, can set mitmproxy here to intercept Web traffic."
        );

        urlProxy = new(Log, initPort);
        urlProxy.UseHttpProxy(httpProxy.Value);

        httpProxy.SettingChanged += (_, _) =>
        {
            urlProxy.UseHttpProxy(httpProxy.Value);
        };

        urlProxy.Start();
    }

    public bool MaskUrl(string url)
    {
        return enableMask.Value
            && !(allowUrls.Value.MatchAny(url) || allowUrlPatterns.Value.MatchAny(url))
            && (maskUrls.Value.MatchAny(url) || maskUrlPatterns.Value.MatchAny(url));
    }
}
