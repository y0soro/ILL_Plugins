# WebRequestMask

Mask/block unwanted Web requests initiated with `UnityWebRequest`. Pre-configured to mask AC, SVS, HC and DC connection checks on launch.

This is similar to WebRequestBlocker but without hardcoded internal HTTP server port.

For instance, this would allows you to launch both DigitalCraft and SVS without one of them yelling "Connection failed" due to port allocation collision that causes one of the internal HTTP server failed to start.

This also allows you to configure a HTTP proxy bypassing TLS certification checks, so you can intercept `UnityWebRequest` traffic with mitmproxy easily.

## Installation

| Download                                                                           | Note |
| ---------------------------------------------------------------------------------- | ---- |
| [v0.0.4](https://github.com/y0soro/ILL_Plugins/releases/tag/WebRequestMask-v0.0.4) |      |

0. (Install [BepInEx](https://builds.bepinex.dev/projects/bepinex_be).)
1. Unpack to BepInEx enabled game root.
2. For SVS, make sure you have a matching [decrypted_global-metadata.dat](https://uu.getuploader.com/y0soro/) installed and properly configured.
