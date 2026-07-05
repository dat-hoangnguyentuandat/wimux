using System.Text.Json;

namespace Wimux.Core.Config;

/// <summary>
/// Manages reading, writing, and caching of <see cref="WimuxSettings"/>.
/// Settings are stored at <c>%LOCALAPPDATA%/wimux/settings.json</c>.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wimux");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static WimuxSettings? _current;

    /// <summary>
    /// The current in-memory settings instance (loaded on first access).
    /// </summary>
    public static WimuxSettings Current => _current ??= Load();

    /// <summary>
    /// Raised after <see cref="NotifyChanged"/> is called to signal that settings have been modified.
    /// </summary>
    public static event Action? SettingsChanged;

    /// <summary>
    /// Reads settings from disk. Returns a fresh default instance on any failure.
    /// </summary>
    public static WimuxSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new WimuxSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<WimuxSettings>(json, JsonOptions) ?? new WimuxSettings();
            MigrateClaudibleToCustomProvider(settings);
            return settings;
        }
        catch
        {
            return new WimuxSettings();
        }
    }

    private static void MigrateClaudibleToCustomProvider(WimuxSettings settings)
    {
        var agent = settings.Agent;
        if (agent == null)
            return;

        agent.CustomProviders ??= [];

        var legacy = agent.Claudible;
        if (legacy != null && !string.IsNullOrWhiteSpace(legacy.BaseUrl))
        {
            const string migratedName = "claudible";
            bool exists = agent.CustomProviders.Any(p =>
                string.Equals(p.Name, migratedName, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                agent.CustomProviders.Add(new CustomProviderSettings
                {
                    Name = migratedName,
                    Kind = "anthropic",
                    BaseUrl = legacy.BaseUrl,
                    Model = string.IsNullOrWhiteSpace(legacy.Model) ? "claude-sonnet-4-6" : legacy.Model,
                    ApiKeySecretName = string.IsNullOrWhiteSpace(legacy.ApiKeySecretName) ? "agent.claudible.apiKey" : legacy.ApiKeySecretName,
                    AuthScheme = "bearer",
                    AnthropicVersion = "2023-06-01",
                });
            }
        }

        if (string.Equals(agent.ActiveProvider, "claudible", StringComparison.OrdinalIgnoreCase))
        {
            agent.ActiveProvider = "custom";
            if (string.IsNullOrWhiteSpace(agent.ActiveCustomProviderName))
                agent.ActiveCustomProviderName = "claudible";
        }

        agent.Claudible = null;
    }

    /// <summary>
    /// Persists the given settings to disk atomically (write to .tmp, then move).
    /// </summary>
    public static void Save(WimuxSettings? settings = null)
    {
        settings ??= Current;
        MigrateClaudibleToCustomProvider(settings);

        try
        {
            Directory.CreateDirectory(SettingsDir);

            var tmpPath = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
            _current = settings;
            NotifyChanged();
        }
        catch
        {
            // Swallow write failures (permission issues, disk full, etc.)
            // to avoid crashing the application.
        }
    }

    /// <summary>
    /// Resets settings to defaults and persists the result.
    /// </summary>
    public static WimuxSettings Reset()
    {
        _current = new WimuxSettings();
        Save(_current);
        return _current;
    }

    /// <summary>
    /// Raises the <see cref="SettingsChanged"/> event.
    /// Call after modifying <see cref="Current"/> properties.
    /// </summary>
    public static void NotifyChanged() => SettingsChanged?.Invoke();
}
