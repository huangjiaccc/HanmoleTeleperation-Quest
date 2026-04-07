using UnityEngine;

/// <summary>
/// 初始使用的值
/// </summary>
[CreateAssetMenu(fileName = "ServerConfig", menuName = "Config/ServerConfig")]
public class ServerConfigSO : ScriptableObject
{
    public string defaultServerIp = "120.197.140.102";

    [Header("Default Server Ports")]
    public int defaultVideoPort = 4000;
    public int defaultDataPort = 4001;
    public int defaultAudioPort = 4002;

    [Header("DefaultClientPorts")]
    public int defaultClientVideoPort = 5000;
    public int defaultClientDataPort = 5001;
    public int defaultClientAudioPort = 5002;

    public bool islan = false;
}