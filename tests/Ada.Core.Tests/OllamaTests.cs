using Ada.Core;

namespace Ada.Core.Tests;

public class OllamaTests
{
    [Fact]
    public async Task Reachable_is_false_when_nothing_is_listening()
        => Assert.False(await OllamaRuntime.IsReachableAsync("http://127.0.0.1:1"));

    [Fact]
    public void Default_options_target_gemma4_on_loopback()
    {
        var o = new OllamaOptions();
        Assert.Equal("gemma4:e4b", o.DefaultModel);
        Assert.Contains("11434", o.Endpoint);
        Assert.Contains("ollama-windows-amd64.zip", o.DownloadUrl);
    }

    [Fact]
    public void Config_persists_the_ollama_runtime_choice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ada_cfg_{Guid.NewGuid():n}.json");
        try
        {
            new ConfigStore(path).Save(new AdaConfig { LocalRuntime = "ollama", OllamaModel = "gemma4:e4b" });
            var c = new ConfigStore(path).Load();
            Assert.Equal("ollama", c.LocalRuntime);
            Assert.Equal("gemma4:e4b", c.OllamaModel);
        }
        finally { File.Delete(path); }
    }
}
