using System.Text;
using System.Text.Json;

namespace SeleneFrontend.Tests;

/// <summary>
/// Space Apps "Use of Super Intelligence": the AI-generated lander models say so in their own metadata, landers.json
/// flags them, index.html carries the ai-assistance meta tag and every pipeline data file has a provenance note.
/// </summary>
public class AiDisclosureTests
{
    private static readonly string WwwRoot = Find();

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var w = Path.Combine(d.FullName, "SeleneFrontend", "wwwroot");
            if (File.Exists(Path.Combine(w, "data", "landers.json"))) return w;
        }
        throw new DirectoryNotFoundException("wwwroot not found");
    }

    private static JsonElement GlbJson(string path)
    {
        var b = File.ReadAllBytes(path);
        var len = BitConverter.ToInt32(b, 12);
        return JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, len)).RootElement;
    }

    public static TheoryData<string> Models() => new(Directory.GetFiles(Path.Combine(Find(), "models"), "*.glb").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Models))]
    public void Every_glb_names_the_AI_tools_in_its_asset_metadata(string file)
    {
        var asset = GlbJson(Path.Combine(WwwRoot, "models", file)).GetProperty("asset");
        var generator = asset.GetProperty("generator").GetString()!;
        Assert.Contains("AI-generated", generator);
        Assert.Contains("Meshy AI", generator);
        Assert.Contains("TRELLIS.2", generator);
        Assert.True(asset.GetProperty("extras").GetProperty("aiGenerated").GetBoolean());
    }

    [Fact]
    public void Landers_json_flags_every_model_as_AI_generated()
    {
        var landers = JsonDocument.Parse(File.ReadAllText(Path.Combine(WwwRoot, "data", "landers.json"))).RootElement;
        foreach (var l in landers.EnumerateArray().Where(l => l.TryGetProperty("modelUri", out _)))
            Assert.True(l.GetProperty("aiGenerated").GetProperty("model").GetBoolean(), l.GetProperty("id").GetString());
    }

    [Fact]
    public void Index_html_has_the_ai_assistance_meta_tag()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));
        Assert.Contains("<meta name=\"ai-assistance\"", html);
        Assert.Contains("AI-generated (Meshy AI, Microsoft TRELLIS.2)", html);
    }

    [Fact]
    public void Pipeline_data_files_carry_a_provenance_note()
    {
        var files = Directory.GetFiles(Path.Combine(WwwRoot, "data", "illumination"), "*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.True(text.Contains("\"provenance\"") && text.Contains("not AI-generated"), Path.GetRelativePath(WwwRoot, f));
        }
    }
}
