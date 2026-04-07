using System;
using UnityEngine;

public static class CommandDiff
{
    private const float FLOAT_EPSILON = 0.0001f;

    // 返回 PartialCommand：只有变化的字段会被赋值，未变化的字段保持 null
    public static PartialCommand BuildPartial(RobotState oldCmd, RobotState newCmd)
    {
        return BuildPartial(oldCmd, newCmd, new PartialCommand());
    }

    public static PartialCommand BuildPartial(RobotState oldCmd, RobotState newCmd, PartialCommand partial)
    {
        partial ??= new PartialCommand();
        ResetPartial(partial);

        if (newCmd == null)
        {
            return partial;
        }

        if (oldCmd == null)
        {
            oldCmd = new RobotState();
        }
#if IS_ANDROID
        //if (oldCmd.SET_ROBOT_MODE != newCmd.SET_ROBOT_MODE)
        //{
        //    partial.SET_ROBOT_MODE = newCmd.SET_ROBOT_MODE;
        //}

        //if (oldCmd.SET_VIDEO_MODE != newCmd.SET_VIDEO_MODE)
        //{
        //    partial.SET_VIDEO_MODE = newCmd.SET_VIDEO_MODE;
        //}

        //if (oldCmd.SET_AUDIO_MODE != newCmd.SET_AUDIO_MODE)
        //{
        //    partial.SET_AUDIO_MODE = newCmd.SET_AUDIO_MODE;
        //}
#else
        //if (DataManager.Instance.RobotHaveChange || DataManager.Instance.G1HaveChange)
        //{
        //    partial.SET_ROBOT_MODE = newCmd.SET_ROBOT_MODE;
        //}

        //if (DataManager.Instance.VideoHaveChange || DataManager.Instance.G2HaveChange)
        //{
        //    partial.SET_VIDEO_MODE = newCmd.SET_VIDEO_MODE;
        //}

        //if (DataManager.Instance.AudioHaveChange || DataManager.Instance.G3HaveChange)
        //{
        //    partial.SET_AUDIO_MODE = newCmd.SET_AUDIO_MODE;
        //}
#endif
        if (oldCmd.TASK != newCmd.TASK)
        {
            partial.TASK = newCmd.TASK;
        }
        if (oldCmd.STEP != newCmd.STEP)
        {
            partial.STEP = newCmd.STEP;
        }

        if (Mathf.Abs(oldCmd.SET_AUDIO_VOLUME - newCmd.SET_AUDIO_VOLUME) > FLOAT_EPSILON)
        {
            partial.SET_AUDIO_VOLUME = newCmd.SET_AUDIO_VOLUME;
        }

        if (Mathf.Abs(oldCmd.MOVE_IDX_LEFT_X - newCmd.MOVE_IDX_LEFT_X) > FLOAT_EPSILON)
        {
            partial.MOVE_IDX_LEFT_X = newCmd.MOVE_IDX_LEFT_X;
        }

        if (Mathf.Abs(oldCmd.MOVE_IDX_LEFT_Y - newCmd.MOVE_IDX_LEFT_Y) > FLOAT_EPSILON)
        {
            partial.MOVE_IDX_LEFT_Y = newCmd.MOVE_IDX_LEFT_Y;
        }

        if (Mathf.Abs(oldCmd.MOVE_IDX_RIGHT_X - newCmd.MOVE_IDX_RIGHT_X) > FLOAT_EPSILON)
        {
            partial.MOVE_IDX_RIGHT_X = newCmd.MOVE_IDX_RIGHT_X;
        }

        if (Mathf.Abs(oldCmd.MOVE_IDX_RIGHT_Y - newCmd.MOVE_IDX_RIGHT_Y) > FLOAT_EPSILON)
        {
            partial.MOVE_IDX_RIGHT_Y = newCmd.MOVE_IDX_RIGHT_Y;
        }

        if (Mathf.Abs(oldCmd.SET_LEFT_ROTATE_ANGLE - newCmd.SET_LEFT_ROTATE_ANGLE) > FLOAT_EPSILON)
        {
            partial.SET_LEFT_ROTATE_ANGLE = newCmd.SET_LEFT_ROTATE_ANGLE;
        }

        if (Mathf.Abs(oldCmd.SET_RIGHT_ROTATE_ANGLE - newCmd.SET_RIGHT_ROTATE_ANGLE) > FLOAT_EPSILON)
        {
            partial.SET_RIGHT_ROTATE_ANGLE = newCmd.SET_RIGHT_ROTATE_ANGLE;
        }

        var oldAction = oldCmd.ACTION;
        var newAction = newCmd.ACTION;
        string normalizedOldActionName = NormalizeString(oldAction?.name);
        string normalizedNewActionName = NormalizeString(newAction?.name);
        string normalizedOldActionCmd = NormalizeString(oldAction?.cmd);
        string normalizedNewActionCmd = NormalizeString(newAction?.cmd);
        if (!string.Equals(normalizedOldActionName, normalizedNewActionName, StringComparison.Ordinal))
        {
            if (normalizedNewActionName != null)
            {
                EnsureAction().name = normalizedNewActionName;
            }
        }
        if (!string.Equals(normalizedOldActionCmd, normalizedNewActionCmd, StringComparison.Ordinal))
        {
            if (normalizedNewActionCmd != null)
            {
                EnsureAction().cmd = normalizedNewActionCmd;
            }
        }
        var oldTts = oldCmd.TTS;
        var newTts = newCmd.TTS;
        string normalizedOldTtsText = NormalizeString(oldTts?.text);
        string normalizedNewTtsText = NormalizeString(newTts?.text);
        string normalizedOldTtsCmd = NormalizeString(oldTts?.cmd);
        string normalizedNewTtsCmd = NormalizeString(newTts?.cmd);
        if (!string.Equals(normalizedOldTtsText, normalizedNewTtsText, StringComparison.Ordinal))
        {
            if (normalizedNewTtsText != null)
            {
                EnsureTts().text = normalizedNewTtsText;
            }
        }
        if (!string.Equals(normalizedOldTtsCmd, normalizedNewTtsCmd, StringComparison.Ordinal))
        {
            if (normalizedNewTtsCmd != null)
            {
                EnsureTts().cmd = normalizedNewTtsCmd;
            }
        }
        // 如果没有任何变化，返回一个“空” PartialCommand（所有字段 null）
        // 如果你希望在无变化时直接把 commands 设为 null，请改成 `return null;`
        return partial;

        ACTION EnsureAction()
        {
            if (partial.ACTION == null)
            {
                partial.ACTION = new ACTION();
            }
            return partial.ACTION;
        }

        TTS EnsureTts()
        {
            if (partial.TTS == null)
            {
                partial.TTS = new TTS();
            }
            return partial.TTS;
        }

        static string NormalizeString(string value) => string.IsNullOrEmpty(value) ? null : value;
    }

    private static void ResetPartial(PartialCommand partial)
    {
        partial.SET_ROBOT_MODE = null;
        partial.SET_VIDEO_MODE = null;
        partial.SET_AUDIO_MODE = null;
        partial.TASK = null;
        partial.STEP = null;
        partial.SET_AUDIO_VOLUME = null;
        partial.MOVE_IDX_LEFT_X = null;
        partial.MOVE_IDX_LEFT_Y = null;
        partial.MOVE_IDX_RIGHT_X = null;
        partial.MOVE_IDX_RIGHT_Y = null;
        partial.SET_LEFT_ROTATE_ANGLE = null;
        partial.SET_RIGHT_ROTATE_ANGLE = null;

        if (partial.ACTION != null)
        {
            partial.ACTION.cmd = null;
            partial.ACTION.name = null;
        }

        if (partial.TTS != null)
        {
            partial.TTS.cmd = null;
            partial.TTS.text = null;
        }
    }

    // 一个快速判断：partial 是否真的有字段被设置
    public static bool IsEmpty(PartialCommand p)
    {
        if (p == null) return true;
        return p.SET_ROBOT_MODE == null &&
               p.SET_VIDEO_MODE == null &&
               p.SET_AUDIO_MODE == null &&
               p.TASK == null &&
               p.STEP == null &&
               p.SET_AUDIO_VOLUME == null &&
               p.MOVE_IDX_LEFT_X == null &&
               p.MOVE_IDX_LEFT_Y == null &&
               p.MOVE_IDX_RIGHT_X == null &&
               p.MOVE_IDX_RIGHT_Y == null &&
               p.SET_LEFT_ROTATE_ANGLE == null &&
               p.SET_RIGHT_ROTATE_ANGLE == null &&
               IsActionEmpty(p.ACTION) &&
               IsTtsEmpty(p.TTS);
    }

    private static bool IsActionEmpty(ACTION action)
    {
        return action == null ||
               (string.IsNullOrEmpty(action.cmd) && string.IsNullOrEmpty(action.name));
    }

    private static bool IsTtsEmpty(TTS tts)
    {
        return tts == null ||
               (string.IsNullOrEmpty(tts.cmd) && string.IsNullOrEmpty(tts.text));
    }
}
