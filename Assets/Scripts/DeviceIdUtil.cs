using UnityEngine;

public static class DeviceIdUtil
{
    public static string GetAndroidId()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var unityPlayer =
               new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            var activity = unityPlayer
                .GetStatic<AndroidJavaObject>("currentActivity");

            var contentResolver =
                activity.Call<AndroidJavaObject>("getContentResolver");

            using (var settingsSecure =
                   new AndroidJavaClass("android.provider.Settings$Secure"))
            {
                return settingsSecure.CallStatic<string>(
                    "getString",
                    contentResolver,
                    "android_id");
            }
        }
#else
        return SystemInfo.deviceUniqueIdentifier;
#endif
    }
}
