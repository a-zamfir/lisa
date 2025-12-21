// File: Host.Win/Models/HostSettings.cs
namespace Host.Win.Models
{
    public sealed class HostSettings
    {
        public string McpHost { get; set; } = "127.0.0.1";
        public int McpPort { get; set; } = 8123;
        public string AgentHost { get; set; } = "127.0.0.1";
        public int AgentPort { get; set; } = 5050;
        public string ProviderMode { get; set; } = "Local"; // Local | Hosted
        public string ProviderHost { get; set; } = "127.0.0.1";
        public int ProviderPort { get; set; } = 11434;
        public string ProviderApiKey { get; set; } = string.Empty;
        public string ProviderModel { get; set; } = "llama3.2";
        public double ProviderTemperature { get; set; } = 0.7;
        public bool ProviderThink { get; set; } = true;
        public string VoiceType { get; set; } = "default";
        public double VoiceRate { get; set; } = 1.0;
        public double VoiceVolume { get; set; } = 1.0;
        public string PiperExePath { get; set; } = string.Empty;
        public string PiperMode { get; set; } = "exe"; // exe | python
        public string PiperPythonPath { get; set; } = "python";
        public string PiperVoiceModelPath { get; set; } = string.Empty;
        public string PiperVoiceConfigPath { get; set; } = string.Empty;
        public int? PiperSpeakerId { get; set; }
    }
}
