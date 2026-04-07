using UnityEngine;

public static class AppLog
{
    public static bool EnableLog { get; set; } = true;
    public static bool EnableWarning { get; set; } = true;
    public static bool EnableError { get; set; } = true;

    public static bool Enabled
    {
        get => EnableLog && EnableWarning && EnableError;
        set => SetAll(value);
    }

    public static void SetAll(bool enabled)
    {
        EnableLog = enabled;
        EnableWarning = enabled;
        EnableError = enabled;
    }

    public static void Log(object message)
    {
        if (!EnableLog) return;
        UnityEngine.Debug.Log(message);
    }

    public static void Log(object message, Object context)
    {
        if (!EnableLog) return;
        UnityEngine.Debug.Log(message, context);
    }

    public static void LogWarning(object message)
    {
        if (!EnableWarning) return;
        UnityEngine.Debug.LogWarning(message);
    }

    public static void LogWarning(object message, Object context)
    {
        if (!EnableWarning) return;
        UnityEngine.Debug.LogWarning(message, context);
    }

    public static void LogError(object message)
    {
        if (!EnableError) return;
        UnityEngine.Debug.LogError(message);
    }

    public static void LogError(object message, Object context)
    {
        if (!EnableError) return;
        UnityEngine.Debug.LogError(message, context);
    }
}
