using System.Text;
using System.Text.Json;
using SeleneFrontend.Models;

namespace SeleneFrontend.Tests;

/// <summary>landers.json ↔ wwwroot/models: every lander has a usable, optimised 3D model.</summary>
public class LanderModelTests
{
    private static readonly string WwwRoot = FindWwwRoot();
    private static readonly List<LanderPreset> Landers = JsonSerializer.Deserialize<List<LanderPreset>>(
        File.ReadAllText(Path.Combine(WwwRoot, "data", "landers.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string FindWwwRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SeleneFrontend", "wwwroot");
            if (File.Exists(Path.Combine(candidate, "data", "landers.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("SeleneFrontend/wwwroot not found above the test output directory");
    }

    public static TheoryData<string> LanderIds() => new(Landers.Select(l => l.Id));

    [Theory]
    [MemberData(nameof(LanderIds))]
    public void Every_lander_has_a_model_spec_with_credit(string id)
    {
        var lander = Landers.Single(l => l.Id == id);
        var spec = lander.Model;
        Assert.NotNull(spec);
        Assert.Equal($"models/{id}.glb", spec.Uri);
        Assert.True(spec.SizeM > 0);
        Assert.Contains("TRELLIS.2", spec.Credit);
        Assert.False(string.IsNullOrWhiteSpace(lander.ModelNote));
    }

    [Theory]
    [MemberData(nameof(LanderIds))]
    public void Model_file_is_an_optimised_glb_under_3_MB(string id)
    {
        var path = Path.Combine(WwwRoot, "models", $"{id}.glb");
        Assert.True(File.Exists(path), $"missing {path}");
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length <= 3 * 1024 * 1024, $"{id}.glb is {bytes.Length / 1e6:0.00} MB");
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0));   // "glTF"
        var json = Encoding.UTF8.GetString(bytes, 20, (int)BitConverter.ToUInt32(bytes, 12));
        Assert.Contains("EXT_meshopt_compression", json);
    }

    [Fact]
    public void Griffin_is_scaled_by_width_others_by_height()
    {
        var g = Landers.Single(l => l.Id == "griffin").Model!;
        Assert.Equal(("models/griffin.glb", 4.5, "width", 0.0), (g.Uri, g.SizeM, g.ScaleBy, g.RotationDeg));
        Assert.All(Landers, l => Assert.Equal(3, l.Model!.NozzleOffset?.Length));   // engine plume origin for every model
        Assert.All(Landers.Where(l => l.Id != "griffin"), l => Assert.Equal("height", l.Model!.ScaleBy));
        Assert.Equal(50, Landers.Single(l => l.Id == "starship_hls").Model!.SizeM);
    }
}
