using System.Text.Json;
using Wimux.Core.Models;

namespace Wimux.Core.Services;

public class WorkspaceTemplateService
{
    private static readonly string TemplatesDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wimux", "templates");

    public List<WorkspaceTemplate> GetTemplates()
    {
        EnsureDir();
        var templates = new List<WorkspaceTemplate>();
        foreach (var file in Directory.EnumerateFiles(TemplatesDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var t = JsonSerializer.Deserialize<WorkspaceTemplate>(json);
                if (t != null) templates.Add(t);
            }
            catch { }
        }
        return templates.OrderBy(t => t.Name).ToList();
    }

    public void Save(WorkspaceTemplate template)
    {
        EnsureDir();
        var path = Path.Combine(TemplatesDir, $"{template.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Delete(string id)
    {
        var path = Path.Combine(TemplatesDir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static void EnsureDir()
    {
        if (!Directory.Exists(TemplatesDir))
            Directory.CreateDirectory(TemplatesDir);
    }
}

public class WorkspaceTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Template";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<TemplateSurface> Surfaces { get; set; } = [];
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
}

public class TemplateSurface
{
    public string Name { get; set; } = "Terminal";
    public List<TemplatePaneLayout> Panes { get; set; } = [];
}

public class TemplatePaneLayout
{
    public string? Shell { get; set; }
    public string? WorkingDirectory { get; set; }
    public SplitDirection Direction { get; set; } = SplitDirection.Vertical;
}
