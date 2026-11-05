using OneLlm.Catalog;

namespace OneLlm.Tests.Catalog;

public class CatalogLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onellm-catalog-" + Guid.NewGuid().ToString("N"));

    public CatalogLoadingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private const string ShippedYaml = """
        name: loadtest
        modelPatterns: ["lt-*"]
        aliases: { fast: lt-small }
        models:
          - name: lt-small
            contextWindow: 1000
            pricing: { inputPerMillion: 1.00, outputPerMillion: 2.00, asOf: 2026-03-01 }
          - name: lt-big
            contextWindow: 5000
            pricing: { inputPerMillion: 10.00, outputPerMillion: 20.00 }
        """;

    private static void RegisterShipped() => CatalogLoader.RegisterShipped("loadtest/llm-provider.yaml", () => ShippedYaml);

    private static ModelSpec Model(ModelCatalog catalog, string name) => catalog.Resolve("loadtest:" + name).Model!;

    [Fact]
    public void Shipped_layers_come_from_registrations_and_registering_twice_is_harmless()
    {
        RegisterShipped();
        RegisterShipped();
        ModelCatalog catalog = CatalogLoader.Load(getEnvironmentVariable: _ => null);
        Assert.Equal(1000, Model(catalog, "lt-small").ContextWindow);
        Assert.Equal("shipped", catalog.SourceOf("loadtest", "models[lt-small].contextWindow"));
        Assert.Equal(1, catalog.Layers.Count(l => l.Providers.Any(p => p.Name == "loadtest")));
    }

    [Fact]
    public void A_user_file_overrides_the_shipped_value_and_leaves_the_rest()
    {
        RegisterShipped();
        string file = Path.Combine(_dir, "mine.yaml");
        File.WriteAllText(file, """
            name: loadtest
            models:
              - name: lt-small
                pricing: { inputPerMillion: 1.50 }
            """);
        ModelCatalog catalog = CatalogLoader.Load(file, getEnvironmentVariable: _ => throw new InvalidOperationException("the path argument wins over the environment"));
        ModelSpec model = Model(catalog, "lt-small");
        Assert.Equal(1.50m, model.Pricing!.InputPerMillion);
        Assert.Equal(2.00m, model.Pricing.OutputPerMillion);
        Assert.Equal("user", catalog.SourceOf("loadtest", "models[lt-small].pricing.inputPerMillion"));
        Assert.Equal("shipped", catalog.SourceOf("loadtest", "models[lt-small].pricing.outputPerMillion"));
    }

    [Fact]
    public void The_environment_variable_names_a_directory_and_code_overrides_sit_above_the_user_files()
    {
        RegisterShipped();
        File.WriteAllText(Path.Combine(_dir, "a.yaml"), "name: loadtest\nmodels:\n  - name: lt-big\n    contextWindow: 7000\n    maxOutputTokens: 300\n");
        File.WriteAllText(Path.Combine(_dir, "b.yml"), "name: loadtest\nmodels:\n  - name: lt-big\n    contextWindow: 8000\n");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a catalog file");

        var code = new CatalogLayer(CatalogLayer.Code, [new ProviderSpec { Name = "loadtest", Models = [new ModelSpec { Name = "lt-big", MaxOutputTokens = 99 }] }]);
        ModelCatalog catalog = CatalogLoader.Load(overrides: [code], getEnvironmentVariable: name => name == "ONELLM_CATALOG" ? _dir : null);

        ModelSpec big = Model(catalog, "lt-big");
        Assert.Equal(8000, big.ContextWindow);          // b.yml after a.yaml, both above shipped's 5000
        Assert.Equal(99, big.MaxOutputTokens);          // code above user's 300
        Assert.Equal("user", catalog.SourceOf("loadtest", "models[lt-big].contextWindow"));
        Assert.Equal("code", catalog.SourceOf("loadtest", "models[lt-big].maxOutputTokens"));
        Assert.Equal("shipped", catalog.SourceOf("loadtest", "models[lt-big].pricing.inputPerMillion"));
    }

    [Fact]
    public void Provenance_lists_every_value_with_the_layer_that_set_it()
    {
        RegisterShipped();
        File.WriteAllText(Path.Combine(_dir, "mine.yaml"), "name: loadtest\naliases: { fast: lt-big }\n");
        ModelCatalog catalog = CatalogLoader.Load(_dir, getEnvironmentVariable: _ => null);
        IReadOnlyDictionary<string, string> sources = catalog.SourcesOf("loadtest");
        Assert.Equal("user", sources["aliases.fast"]);
        Assert.Equal("shipped", sources["models[lt-small].contextWindow"]);
        Assert.Equal("lt-big", catalog.Resolve("loadtest:fast").Name);
        Assert.Empty(catalog.SourcesOf("nope"));
    }

    [Fact]
    public void A_missing_user_path_is_an_error_and_a_bad_file_names_its_position()
    {
        Assert.Throws<FileNotFoundException>(() => CatalogLoader.Load(Path.Combine(_dir, "missing.yaml")));
        string bad = Path.Combine(_dir, "bad.yaml");
        File.WriteAllText(bad, "name: x\nmodels:\n  - name: [unclosed\n");
        BadDefinitionException e = Assert.Throws<BadDefinitionException>(() => CatalogLoader.Load(bad, getEnvironmentVariable: _ => null));
        Assert.Equal(bad, e.File);
        Assert.True(e.Line > 0);
    }
}
