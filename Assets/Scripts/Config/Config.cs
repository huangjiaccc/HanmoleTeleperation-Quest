using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
#nullable enable

[Serializable]

public class HeartbeatMessage 
{
    public string user_email;
    public string device_id;
    public string target_device_id;
    public string token;
}
[Serializable]

public class HelmeData
{
    public string device_id;      //唯一ID
    public long ts;      //时间戳
    public int video_last_received_id;      //回传视频最新的id
    public float[] pos_head;    //头盔位置
    public float[] quat_head;    //头盔转向
    public float[] pos_left_hand;   //左手弯曲程度  拇指最底，到拇指中，然后食指中指无名指尾指
    public float[] pos_left_arm;    //左手位置
    public float[] quat_left_arm;    //左手转向
    public float[] pos_right_hand;   //右手弯曲程度  拇指最底，到拇指中，然后食指中指无名指尾指
    public float[] pos_right_arm;   //右手位置
    public float[] quat_right_arm;   //右手转向
    public float[] joystick_left;   //左轮盘值
    public float[] joystick_right;  //右轮盘值


    public PartialCommand commands;     //相关机器状态
    public int tracking;             //当前是否追踪
    public Triggers trigger;        //左右手控制器力度
    public Triggers grip;           //左右手控制器力度
    public Buttons button;          //按钮输入


    //public float video_rtt_mean_ms;       //视频延迟
    //public float hand_left;       //左手开合角度
    //public float hand_right;      //右手开合角度
    //public string target_device_id;     //机器人唯一id
    //public float velocity;          //机器人速度
    //public int hand_mode;           //"controller" | "handtracking";      0|1
    //public float height;            //机器人高度
}
[System.Serializable]
public class PartialCommand
{
    public int? SET_ROBOT_MODE;
    public int? SET_VIDEO_MODE;
    public int? SET_AUDIO_MODE;
    public int? TASK;
    public int? STEP;
    public float? SET_AUDIO_VOLUME;
    public float? MOVE_IDX_LEFT_X;
    public float? MOVE_IDX_LEFT_Y;
    public float? MOVE_IDX_RIGHT_X;
    public float? MOVE_IDX_RIGHT_Y;
    public float? SET_LEFT_ROTATE_ANGLE;
    public float? SET_RIGHT_ROTATE_ANGLE;
    public ACTION? ACTION;
    public TTS? TTS;
}

[System.Serializable]
public class Triggers 
{
    public float r;
    public float l;
}

public class Buttons 
{
    public float x;
    public float y;
    public float a;
    public float b;
    public float leftjoy;
    public float rightjoy;
}

[System.Serializable]
public class ACTION 
{
    public string? cmd;
    public string? name;
}

[System.Serializable]
public class TTS
{
    public string? cmd;
    public string? text;
}


[Serializable]

public class RobotState 
{
    public int SET_ROBOT_MODE = (int)RobotMode.HOME;
    public int SET_VIDEO_MODE = (int)VideoMode.PERSPECTIVE;
    public int SET_AUDIO_MODE = (int)AudioMode.SLEEP;
    public int TASK = (int) TaskType.pending;                     //发送视频状态下任务状态
    public int STEP = (int) TaskType.pending;                     //发送视频状态下任务状态
    public float SET_AUDIO_VOLUME;
    public float MOVE_IDX_LEFT_X;           //"MOVE_IDX_LEFT_X": lambda v: self._handle_move_idx("left", "x", int (v)),
    public float MOVE_IDX_LEFT_Y;           //"MOVE_IDX_LEFT_Y": lambda v: self._handle_move_idx("left", "y", int (v)),
    public float MOVE_IDX_RIGHT_X;          //"MOVE_IDX_RIGHT_X": lambda v: self._handle_move_idx("right", "x", int (v)),
    public float MOVE_IDX_RIGHT_Y;          //"MOVE_IDX_RIGHT_Y": lambda v: self._handle_move_idx("right", "y", int (v)),
    public float SET_LEFT_ROTATE_ANGLE;     //"SET_LEFT_ROTATE_ANGLE": lambda v: self._safe_float(//    self._handle_set_rotate_angle, v, side="left"//),
    public float SET_RIGHT_ROTATE_ANGLE;    //"SET_RIGHT_ROTATE_ANGLE": lambda v: self._safe_float(  //    self._handle_set_rotate_angle, v, side="right"//),

    public ACTION ACTION = new ACTION();           //动作action
    public TTS TTS = new TTS();                 //TTS

    public void CopyFrom(RobotState src)
    {
        if (src == null)
        {
            SET_ROBOT_MODE = (int)RobotMode.HOME;
            SET_VIDEO_MODE = (int)VideoMode.PERSPECTIVE;
            SET_AUDIO_MODE = (int)AudioMode.SLEEP;
            TASK = (int)TaskType.pending;
            STEP = (int)TaskType.pending;
            SET_AUDIO_VOLUME = 0f;
            MOVE_IDX_LEFT_X = 0f;
            MOVE_IDX_LEFT_Y = 0f;
            MOVE_IDX_RIGHT_X = 0f;
            MOVE_IDX_RIGHT_Y = 0f;
            SET_LEFT_ROTATE_ANGLE = 0f;
            SET_RIGHT_ROTATE_ANGLE = 0f;
            ACTION = null;
            TTS = null;
            return;
        }

        SET_ROBOT_MODE = src.SET_ROBOT_MODE;
        SET_VIDEO_MODE = src.SET_VIDEO_MODE;
        SET_AUDIO_MODE = src.SET_AUDIO_MODE;
        TASK = src.TASK;
        STEP = src.STEP;
        SET_AUDIO_VOLUME = src.SET_AUDIO_VOLUME;
        MOVE_IDX_LEFT_X = src.MOVE_IDX_LEFT_X;
        MOVE_IDX_LEFT_Y = src.MOVE_IDX_LEFT_Y;
        MOVE_IDX_RIGHT_X = src.MOVE_IDX_RIGHT_X;
        MOVE_IDX_RIGHT_Y = src.MOVE_IDX_RIGHT_Y;
        SET_LEFT_ROTATE_ANGLE = src.SET_LEFT_ROTATE_ANGLE;
        SET_RIGHT_ROTATE_ANGLE = src.SET_RIGHT_ROTATE_ANGLE;

        if (src.ACTION == null)
        {
            ACTION = null;
        }
        else
        {
            ACTION ??= new ACTION();
            ACTION.cmd = src.ACTION.cmd;
            ACTION.name = src.ACTION.name;
        }

        if (src.TTS == null)
        {
            TTS = null;
        }
        else
        {
            TTS ??= new TTS();
            TTS.cmd = src.TTS.cmd;
            TTS.text = src.TTS.text;
        }
    }

    public RobotState Clone(RobotState src)
    {
        var clone = new RobotState();
        clone.CopyFrom(src);
        return clone;
    }

    private static ACTION CloneAction(ACTION src)
    {
        if (src == null)
        {
            return null;
        }

        return new ACTION
        {
            cmd = src.cmd,
            name = src.name
        };
    }

    private static TTS CloneTTS(TTS src)
    {
        if (src == null)
        {
            return null;
        }

        return new TTS
        {
            cmd = src.cmd,
            text = src.text
        };
    }
}

[Serializable]
// 机器工作模式枚举
public enum RobotMode
{
    HOME,        // 睡眠模式
    ASYNC,      // 异步模式
    SYNC        // 同步模式
}
[Serializable]

public enum AudioMode
{
    SLEEP,  //  仅能听到机器人的声音
    DIRECT,  // 双向对话
    REALTIME // 机器人AI对话
}
[Serializable]
public enum VideoMode 
{
    PERSPECTIVE,     //
    FRONT,          //两个鱼目相机
    REALSENSE       //鱼目相机+深度相机
}

[Serializable]
public enum HandMode
{
    GRIPPER,        //夹爪模式
    SEMANTIC_GRASP, //三指模式
    DIRECT_TRACKING //映射模式
}

[Serializable]
public enum DecoderFlavor
{
    H264,
    HEVC,
    AV1
}
[Serializable]
public enum Step 
{
    next,
    previous
}

[Serializable]
public enum TaskType 
{
    pending,    
    active,
    paused,
    success,
    failure,
    aborted
}

[Serializable]

public class ServerRobotData
{
    public string id;
    public string name;
    public string device_id;
    public string store;
    public string ip_address;
    public string device_type;
    public string last_seen;
    public string status;
    public string user_email;
    public string created_at;
    public string updated_at;
}
[Serializable]

public class ServerRobotList 
{
    public ServerRobotData[]  list;
}

[System.Serializable]
public class RobotStateMessage
{
    public string type;
    public RobotStateData data;
}

[System.Serializable]
public class RobotStateData
{
    public int audio_mode;
    public int video_mode;
    public int robot_mode;
    public int hand_mode;           //  - 0 = GRIPPER (夹爪模式) - 1 = SEMANTIC_GRASP(三指模式) - 2 = DIRECT_TRACKING(映射模式)

    public float? robot_height; // 使用可空类型处理null
    public long? vr_ts;

    public int? wifi;
    public int? battery_percent;
    public string? battery_status;
    public float? video_rtt_ms;
    public RobotNotifications? notifications;
}

[JsonConverter(typeof(RobotNotificationsConverter))]
[System.Serializable]
public sealed class RobotNotifications
{
    public string? text;
    public List<RobotLogNotification>? robot_log;

    public string? GetLatestMessage()
    {
        if (robot_log != null)
        {
            for (int i = robot_log.Count - 1; i >= 0; i--)
            {
                string? message = robot_log[i]?.GetDisplayMessage();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

[System.Serializable]
public sealed class RobotLogNotification
{
    public string? message;
    public string? category;
    public string? level;
    public double? timestamp;
    public string? mode;
    public JToken? new_value;
    public JToken? old_value;

    public string? GetDisplayMessage()
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (!string.IsNullOrWhiteSpace(mode))
        {
            string newValue = new_value != null ? new_value.ToString(Formatting.None) : "?";
            string oldValue = old_value != null ? old_value.ToString(Formatting.None) : "?";
            return $"{mode}: {oldValue} -> {newValue}";
        }

        return null;
    }
}

public sealed class RobotNotificationsConverter : JsonConverter<RobotNotifications>
{
    public override RobotNotifications? ReadJson(JsonReader reader, Type objectType, RobotNotifications? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonToken.String)
        {
            return new RobotNotifications { text = reader.Value as string };
        }

        JToken token = JToken.Load(reader);
        switch (token.Type)
        {
            case JTokenType.Object:
            {
                JObject obj = (JObject)token;
                var result = new RobotNotifications();
                if (obj.TryGetValue("text", out JToken? textToken) && textToken.Type == JTokenType.String)
                {
                    result.text = textToken.Value<string>();
                }
                if (obj.TryGetValue("robot_log", out JToken? robotLogToken))
                {
                    result.robot_log = robotLogToken.ToObject<List<RobotLogNotification>>(serializer);
                }
                if (string.IsNullOrWhiteSpace(result.text) && (result.robot_log == null || result.robot_log.Count == 0))
                {
                    result.text = obj.ToString(Formatting.None);
                }
                return result;
            }
            case JTokenType.Array:
                return new RobotNotifications
                {
                    robot_log = token.ToObject<List<RobotLogNotification>>(serializer)
                };
            default:
                return new RobotNotifications
                {
                    text = token.ToString(Formatting.None)
                };
        }
    }

    public override void WriteJson(JsonWriter writer, RobotNotifications? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        if (!string.IsNullOrWhiteSpace(value.text) && (value.robot_log == null || value.robot_log.Count == 0))
        {
            writer.WriteValue(value.text);
            return;
        }

        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(value.text))
        {
            writer.WritePropertyName("text");
            writer.WriteValue(value.text);
        }

        if (value.robot_log != null)
        {
            writer.WritePropertyName("robot_log");
            serializer.Serialize(writer, value.robot_log);
        }

        writer.WriteEndObject();
    }
}

[System.Serializable]
public class RobotJointStateMessage
{
    public string type;
    public RobotJointStateData data;
}

[System.Serializable]
public class RobotJointStateData
{
    public float[] joints_pos;
    public float[] left_arm_pos;
    public float[] leg_pos;
    public float[] neck_pos;
    public float[] right_arm_pos;
    public string[] name;
    public float[] position;
    public float[] joint_radians;
    public float[] joint_angles;
    public float[] radians;
    public float[] angles;
    public float[] velocity;
    public float[] effort;
    public long ts;

    private static readonly string[] ControllerJointOrder =
    {
        "joint_waist_yaw",
        "joint_waist_pitch",
        "joint_knee",
        "joint_ankle",
        "joint_left_shoulder_inner",
        "joint_left_shoulder_outer",
        "joint_left_upper_arm",
        "joint_left_elbow",
        "joint_left_forearm",
        "joint_left_wrist_upper",
        "joint_left_wrist_lower",
        "joint_right_shoulder_inner",
        "joint_right_shoulder_outer",
        "joint_right_upper_arm",
        "joint_right_elbow",
        "joint_right_forearm",
        "joint_right_wrist_upper",
        "joint_right_wrist_lower",
        "joint_neck_yaw",
        "joint_neck_pitch"
    };

    private const string JointNamePrefix = "joint_";

    public float[] GetControllerJointRadians()
    {
        if (joints_pos != null && joints_pos.Length >= ControllerJointOrder.Length)
        {
            return joints_pos;
        }

        float[] sourceRadians = GetNamedJointRadiansSource();
        if (sourceRadians == null || sourceRadians.Length == 0 || name == null || name.Length != sourceRadians.Length)
        {
            return null;
        }

        float[] mapped = new float[ControllerJointOrder.Length];
        for (int i = 0; i < ControllerJointOrder.Length; i++)
        {
            int sourceIndex = Array.IndexOf(name, ControllerJointOrder[i]);
            if (sourceIndex < 0 || sourceIndex >= sourceRadians.Length)
            {
                return null;
            }

            mapped[i] = sourceRadians[sourceIndex];
        }

        joints_pos = mapped;
        return joints_pos;
    }

    public int GetRawJointRadiansCount()
    {
        if (joints_pos != null)
        {
            return joints_pos.Length;
        }

        float[] sourceRadians = GetNamedJointRadiansSource();
        return sourceRadians?.Length ?? 0;
    }

    private float[] GetNamedJointRadiansSource()
    {
        if (joint_radians != null && joint_radians.Length > 0)
        {
            return joint_radians;
        }

        if (joint_angles != null && joint_angles.Length > 0)
        {
            return joint_angles;
        }

        if (radians != null && radians.Length > 0)
        {
            return radians;
        }

        if (angles != null && angles.Length > 0)
        {
            return angles;
        }

        // ROS-style JointState messages commonly expose revolute joint angles in the `position` array.
        if (position != null && position.Length > 0)
        {
            return position;
        }

        return null;
    }

    public float[] GetControllerJointPositions()
    {
        return GetControllerJointRadians();
    }

    public bool TryFillUrdfJointPositions(Dictionary<string, float> output)
    {
        if (output == null)
        {
            return false;
        }

        output.Clear();

        float[] sourcePositions = GetNamedJointPositionSource();
        if (sourcePositions == null || sourcePositions.Length == 0 || name == null || name.Length != sourcePositions.Length)
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            string normalizedName = NormalizeJointName(name[i]);
            if (string.IsNullOrEmpty(normalizedName))
            {
                continue;
            }

            output[normalizedName] = sourcePositions[i];
        }

        return output.Count > 0;
    }

    private float[] GetNamedJointPositionSource()
    {
        if (position != null && position.Length > 0)
        {
            return position;
        }

        return GetNamedJointRadiansSource();
    }

    private static string NormalizeJointName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        string normalized = rawName.Trim();
        if (normalized.StartsWith(JointNamePrefix, StringComparison.Ordinal))
        {
            normalized = normalized.Substring(JointNamePrefix.Length);
        }

        return normalized;
    }
}

[System.Serializable]
public class TaskStateMessage 
{
    public string type;
    public TaskStateData data;
}
[System.Serializable]
public class TaskStateData
{
    public string task;
    public string status;
}
