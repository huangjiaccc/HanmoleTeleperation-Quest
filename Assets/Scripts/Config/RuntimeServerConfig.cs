using System.IO;
using UnityEngine;
public static class RuntimeServerConfig
{
    public static string serverIp;
    public static int serverVideoPort;
    public static int serverDataPort;
    public static int serverAudioPort;
    public static int clientVideoPort;
    public static int clientAudioPort;
    public static int clientDataPort;
    public static bool curislan;
    public static string configPath => Path.Combine(Application.persistentDataPath, "server_config.json");
    public static string streamingconfigPath = Path.Combine(Application.streamingAssetsPath, "server_config.json");
    // Avoid rewriting the same config and retriggering network reloads when nothing changed.
    private static string lastPersistedConfigJson = string.Empty;

    // 负责 JSON 序列化的对象
    [System.Serializable]
    private class SerializableConfig
    {
        public string defaultServerIp;
        public int defaultVideoPort;
        public int defaultDataPort;
        public int defaultAudioPort;
        public int defaultClientVideoPort;
        public int defaultClientAudioPort;
        public int defaultClientDataPort;
        public bool islan;
    }
    public static void Load(ServerConfigSO defaultConfig)
    {
        SerializableConfig sc;
        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            sc = JsonUtility.FromJson<SerializableConfig>(json);
        }
        else
        {
            sc = new SerializableConfig
            {
                defaultServerIp = defaultConfig.defaultServerIp,
                defaultVideoPort = defaultConfig.defaultVideoPort,
                defaultDataPort = defaultConfig.defaultDataPort,
                defaultAudioPort = defaultConfig.defaultAudioPort,
                defaultClientVideoPort = defaultConfig.defaultClientVideoPort,
                defaultClientAudioPort = defaultConfig.defaultClientAudioPort,
                defaultClientDataPort = defaultConfig.defaultClientDataPort,
                islan = defaultConfig.islan
            };
        }

        Apply(sc);
        lastPersistedConfigJson = JsonUtility.ToJson(sc, true);
    }
    public static void Save(bool reloadUdpManagers = true)
    {
        SerializableConfig sc = new SerializableConfig()
        {
            defaultServerIp = serverIp,
            defaultVideoPort = serverVideoPort,
            defaultDataPort = serverDataPort,
            defaultAudioPort = serverAudioPort,
            defaultClientVideoPort = clientVideoPort,
            defaultClientAudioPort = clientAudioPort,
            defaultClientDataPort = clientDataPort,
            islan = curislan,
        };
        string json = JsonUtility.ToJson(sc, true);
        if (json == lastPersistedConfigJson && File.Exists(configPath))
        {
            return;
        }

        File.WriteAllText(configPath, json);
        lastPersistedConfigJson = json;
        if (reloadUdpManagers)
        {
            NetClient.instance?.ReloadUdpManagers();
        }
    }

    private static void Apply(SerializableConfig sc)
    {
        serverIp = sc.defaultServerIp;
        serverVideoPort = sc.defaultVideoPort;
        serverDataPort = sc.defaultDataPort;
        serverAudioPort = sc.defaultAudioPort;
        clientVideoPort = sc.defaultClientVideoPort;
        clientAudioPort = sc.defaultClientAudioPort;
        clientDataPort = sc.defaultClientDataPort;
        curislan = sc.islan;
    }
}

