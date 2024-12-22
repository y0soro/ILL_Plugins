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
    private readonly ConfigEntry<bool> enableMask;

    class UrlPrefixes(string[] urls)
    {
        public readonly string[] urls = urls;

        public bool MatchAny(string url)
        {
            return urls.Any(url.StartsWith);
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
    }

    public CommonPlugin(ManualLogSource Log, ConfigFile config)
    {
        CommonPlugin.Log = Log;

        enableMask = config.Bind("General", "Enable", true, "Enable masking");

        maskUrls = config.Bind(
            "Prefix",
            "Mask URL Prefixes",
            new UrlPrefixes(
                [
                    "https://upcheck.illgames.jp/product/svs/game/check.php",
                    "https://download.illgames.jp/check/game/tos_check.php",
                    "https://upcheck.illgames.jp/product/digitalcraft/game/tos_check.php",
                ]
            ),
            "Always responds with HTTP 200 OK for URLs matching these prefixes, effectively blocks public Web traffic towards these URLs."
                + "\nAdd an empty prefix \"\" to mask all of the traffic."
        );
        allowUrls = config.Bind(
            "Prefix",
            "Allow URL Prefixes",
            new UrlPrefixes([]),
            "Explicitly allowed URL prefixes, has higher priority than \"Mask URL Prefixes\"."
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
        return enableMask.Value && !allowUrls.Value.MatchAny(url) && maskUrls.Value.MatchAny(url);
    }
}
