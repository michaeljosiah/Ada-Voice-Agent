using Ada.Core;

namespace Ada.Core.Tests;

public class OnnxTests
{
    [Fact]
    public void Catalog_has_a_default_and_a_gemma_model()
    {
        Assert.NotNull(OnnxModelCatalog.Find(OnnxModelCatalog.DefaultModelId));
        Assert.Contains(OnnxModelCatalog.Models, m => m.Family == "gemma");
    }

    [Fact]
    public void Store_reports_not_ready_for_a_model_that_is_not_downloaded()
    {
        var root = Directory.CreateTempSubdirectory("ada_models_").FullName;
        try
        {
            var store = new OnnxModelStore(root);
            Assert.False(store.IsReady("gemma-3-1b"));
            Assert.Empty(store.Downloaded());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Onnx_provider_without_a_downloaded_model_fails_with_a_clear_hint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelClientFactory.Create(new AdaModelOptions { Provider = "onnx", ModelId = "definitely-not-downloaded" }));
        Assert.Contains("ada model pull", ex.Message);
    }
}
