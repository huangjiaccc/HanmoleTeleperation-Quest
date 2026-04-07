using System;
using UnityEngine;
/// <summary>
/// 用来检测当前设备支持硬解的所有硬解的解码器名称
/// </summary>
public sealed class QuestCodecProbe : MonoBehaviour
{
    private const string Av1Mime = "video/av01";


    private void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            LogDeviceInfo();
            DumpDecodersForMime(Av1Mime);
        }
        catch (Exception e)
        {
            Debug.LogError($"[QuestCodecProbe] Exception: {e}");
        }
#else
        Debug.Log("[QuestCodecProbe] Not running on Android device.");
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static void LogDeviceInfo()
    {
        using var build = new AndroidJavaClass("android.os.Build");
        using var version = new AndroidJavaClass("android.os.Build$VERSION");
        var model = build.GetStatic<string>("MODEL");
        var device = build.GetStatic<string>("DEVICE");
        var manufacturer = build.GetStatic<string>("MANUFACTURER");
        var sdkInt = version.GetStatic<int>("SDK_INT");

        Debug.Log($"[QuestCodecProbe] Device: {manufacturer} {model} ({device}), SDK_INT={sdkInt}");
    }

    private static void DumpDecodersForMime(string mime)
    {
        using var mediaCodecListClass = new AndroidJavaClass("android.media.MediaCodecList");
        int allCodecs = mediaCodecListClass.GetStatic<int>("ALL_CODECS");
        using var codecList = new AndroidJavaObject("android.media.MediaCodecList", allCodecs);

        AndroidJavaObject[] codecInfos;
        try
        {
            codecInfos = codecList.Call<AndroidJavaObject[]>("getCodecInfos");
        }
        catch (Exception e)
        {
            Debug.LogError($"[QuestCodecProbe] getCodecInfos failed: {e.Message}");
            return;
        }

        Debug.Log($"[QuestCodecProbe] Total codecInfos={codecInfos?.Length ?? 0}, probing mime={mime}");

        int hit = 0;
        foreach (var info in codecInfos)
        {
            if (info == null) continue;

            bool isEncoder;
            try { isEncoder = info.Call<bool>("isEncoder"); }
            catch { continue; }
            if (isEncoder) continue;

            string[] types;
            try { types = info.Call<string[]>("getSupportedTypes"); }
            catch { continue; }

            bool supports = false;
            if (types != null)
            {
                foreach (var t in types)
                {
                    if (string.Equals(t, mime, StringComparison.OrdinalIgnoreCase))
                    {
                        supports = true;
                        break;
                    }
                }
            }
            if (!supports) continue;

            hit++;

            var name = SafeCallString(info, "getName");
            var hw = TryCallBool(info, "isHardwareAccelerated");
            var sw = TryCallBool(info, "isSoftwareOnly");
            var vendor = TryCallBool(info, "isVendor");

            Debug.Log($"[QuestCodecProbe] {mime} decoder #{hit}: name={name} hw={hw} swOnly={sw} vendor={vendor}");

            try
            {
                using var caps = info.Call<AndroidJavaObject>("getCapabilitiesForType", mime);
                if (caps == null) continue;

                // colorFormats (int[])
                int[] colorFormats = null;
                try { colorFormats = caps.Get<int[]>("colorFormats"); } catch { }
                if (colorFormats is { Length: > 0 })
                {
                    var n = Math.Min(colorFormats.Length, 16);
                    var joined = string.Join(",", colorFormats.AsSpan(0, n).ToArray());
                    Debug.Log($"[QuestCodecProbe]   colorFormats(count={colorFormats.Length}) first={joined}");
                }

                // profileLevels (MediaCodecInfo.CodecProfileLevel[])
                AndroidJavaObject[] profileLevels = null;
                try { profileLevels = caps.Get<AndroidJavaObject[]>("profileLevels"); } catch { }
                if (profileLevels is { Length: > 0 })
                {
                    var n = Math.Min(profileLevels.Length, 8);
                    for (var i = 0; i < n; i++)
                    {
                        var pl = profileLevels[i];
                        if (pl == null) continue;
                        var profile = TryGetInt(pl, "profile");
                        var level = TryGetInt(pl, "level");
                        Debug.Log($"[QuestCodecProbe]   profileLevel[{i}]: profile={profile} level={level}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuestCodecProbe]   getCapabilitiesForType failed: {e.Message}");
            }
        }

        Debug.Log($"[QuestCodecProbe] {mime} decoders found={hit}");
    }

    private static bool TryCallBool(AndroidJavaObject obj, string method)
    {
        try { return obj.Call<bool>(method); }
        catch { return false; }
    }

    private static int TryGetInt(AndroidJavaObject obj, string field)
    {
        try { return obj.Get<int>(field); }
        catch { return -1; }
    }

    private static string SafeCallString(AndroidJavaObject obj, string method)
    {
        try { return obj.Call<string>(method) ?? ""; }
        catch { return ""; }
    }
#endif
}

