using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConverterPoC;

public class Config
{
    public string ApiUrl { get; set; } = "";
    public string CrossRefUser { get; set; } = "";
    public string CrossRefPassword { get; set; } = "";
    public string CrossRefApiUrl { get; set; } = "";
    public string DepositorName { get; set; } = "";
    public string DepositorEmail { get; set; } = "";
    public string Registrant { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    // How long to wait for Crossref to process the deposits; 0 skips waiting
    public int ResultTimeoutMinutes { get; set; } = 5;
    public DoiMapping[] DoiMappings { get; set; } = [];

    // Settings from config.local.json (next to config.json, not tracked by git) override
    // config.json, so credentials and DOI mappings stay out of the repository
    public static Config Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Configuration file not found.", filePath);

        var settings = JsonNode.Parse(File.ReadAllText(filePath))!.AsObject();

        var localPath = GetLocalPath(filePath);

        if (File.Exists(localPath))
        {
            foreach (var (name, value) in JsonNode.Parse(File.ReadAllText(localPath))!.AsObject())
                settings[name] = value?.DeepClone();
        }

        return settings.Deserialize<Config>()!;
    }

    public static string GetLocalPath(string filePath) =>
        Path.Combine(
            Path.GetDirectoryName(filePath) ?? "",
            Path.GetFileNameWithoutExtension(filePath) + ".local" + Path.GetExtension(filePath));

    public Depositor GetDepositor()
    {
        if (string.IsNullOrWhiteSpace(DepositorName) ||
            string.IsNullOrWhiteSpace(DepositorEmail) ||
            string.IsNullOrWhiteSpace(Registrant))
            throw new ArgumentException("Provide DepositorName, DepositorEmail and Registrant in config.json");

        return new Depositor(DepositorName, DepositorEmail, Registrant);
    }

    public ConversionSettings GetConversionSettings()
    {
        var depositor = GetDepositor();

        if (string.IsNullOrWhiteSpace(RepositoryName))
            throw new ArgumentException("Provide RepositoryName in config.json");

        return new ConversionSettings(depositor, RepositoryName);
    }
}

public class DoiMapping
{
    public string Doi { get; set; } = "";
    public string DepositoryRecordId { get; set; } = "";
}
