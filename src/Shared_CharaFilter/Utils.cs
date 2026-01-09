using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ILL_CharaFilter;

internal static class Utils
{
    public static void Set(this RectOffset obj, int left, int right, int top, int bottom)
    {
        obj.left = left;
        obj.right = right;
        obj.top = top;
        obj.bottom = bottom;
    }

    public static GUIStyle CreateCopy(this GUIStyle original)
    {
        // Copy constructor is sometimes stripped out in IL2CPP
        var guiStyle = new GUIStyle();
        guiStyle.m_Ptr = GUIStyle.Internal_Copy(guiStyle, original);
        return guiStyle;
    }

    public static byte[] FindEmbeddedResource(string resourceFileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(str => str.EndsWith(resourceFileName));

        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] FindEmbeddedResource(Regex matcher)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().First(matcher.IsMatch);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
