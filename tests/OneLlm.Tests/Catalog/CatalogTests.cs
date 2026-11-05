using OneLlm.Catalog;
using OneYaml;

namespace OneLlm.Tests.Catalog;

public class CatalogTests
{
    private const string Shipped = """
        name: acme
        displayName: Acme AI
        modelPatterns:
          - "acme-*"
        aliases:
          fast: acme-small
          smart: acme-large
        apiKeyEnvVar: ACME_API_KEY
        inferenceRoutine: /routines/acme
        capabilities:
          streaming: true
          promptCaching: true
        models:
          - name: acme-large
            displayName: Acme Large
            contextWindow: 200000
            maxOutputTokens: 8192
            supportsTools: true
            baseTokens: 11
            thinking:
              efforts: [low, high]
              defaultEffort: low
            serviceTiers:
              flex:
                pricingMultiplier: 0.5
            pricing:
              inputPerMillion: 3.00
              outputPerMillion: 15.00
              asOf: 2026-03-01
              source: https://example.com/pricing
          - name: acme-small
            contextWindow: 100000
            pricing:
              inputPerMillion: 0.25
              outputPerMillion: 1.25
        """;

    private const string User = """
        name: acme
        aliases:
          fast: acme-large
        models:
          - name: acme-large
            contextWindow: 400000
            thinking:
              efforts: [low, medium, high]
            pricing:
              outputPerMillion: 16.00
          - name: acme-tiny
            contextWindow: 8000
        """;

    private static CatalogLayer CodeLayer() => new(CatalogLayer.Code,
    [
        new ProviderSpec
        {
            Name = "acme",
            Presets = new() { ["local"] = new PresetSpec { BaseUrl = "http://localhost:9999/v1", ModelPatterns = ["local-*"], Aliases = new() { ["mine"] = "local-7b" } } },
            Properties = new() { ["region"] = "eu" },
        },
    ]);

    private static ModelCatalog Layered() => ModelCatalog.Create(
    [
        CatalogLayer.FromYaml(CatalogLayer.Shipped, Shipped),
        CatalogLayer.FromYaml(CatalogLayer.User, User),
        CodeLayer(),
    ]);

    [Fact]
    public void Yaml_loads_into_the_types()
    {
        ProviderSpec p = Assert.Single(CatalogLayer.FromYaml("shipped", Shipped).Providers);
        Assert.Equal("acme", p.Name);
        Assert.Equal(["acme-*"], p.ModelPatterns);
        Assert.True(p.Capabilities!.Streaming);
        ModelSpec large = p.Models![0];
        Assert.Equal(200000, large.ContextWindow);
        Assert.Equal(0.5m, large.ServiceTiers!["flex"].PricingMultiplier);
        Assert.Equal(3.00m, large.Pricing!.InputPerMillion);
        Assert.Equal("2026-03-01", large.Pricing.AsOf);
        Assert.Equal("https://example.com/pricing", large.Pricing.Source);
        // Unknown fields are kept, at the provider and at the model.
        Assert.Equal("/routines/acme", p.ExtensionData!["inferenceRoutine"]);
        Assert.Equal(11L, large.ExtensionData!["baseTokens"]);
    }

    [Fact]
    public void A_layer_changes_fields_without_restating_the_rest()
    {
        ModelCatalog c = Layered();
        ProviderSpec p = c.FindProvider("acme")!;
        ModelSpec large = p.Models!.Single(m => m.Name == "acme-large");
        Assert.Equal(400000, large.ContextWindow); // user layer
        Assert.Equal(8192, large.MaxOutputTokens); // shipped, untouched
        Assert.Equal(3.00m, large.Pricing!.InputPerMillion); // shipped, untouched
        Assert.Equal(16.00m, large.Pricing.OutputPerMillion); // user
        Assert.Equal("https://example.com/pricing", large.Pricing.Source);
        Assert.Equal(["low", "medium", "high"], large.Thinking!.Efforts); // lists replace
        Assert.Equal("low", large.Thinking.DefaultEffort); // sibling kept
        Assert.Equal(0.5m, large.ServiceTiers!["flex"].PricingMultiplier);
        Assert.Equal(11L, large.ExtensionData!["baseTokens"]);
        Assert.Equal("/routines/acme", p.ExtensionData!["inferenceRoutine"]);
        Assert.Equal("Acme AI", p.DisplayName);
    }

    [Fact]
    public void A_layer_can_add_a_model_repoint_an_alias_and_add_a_preset()
    {
        ProviderSpec p = Layered().FindProvider("acme")!;
        Assert.Equal(["acme-large", "acme-small", "acme-tiny"], p.Models!.Select(m => m.Name));
        Assert.Equal("acme-large", p.Aliases!["fast"]); // repointed by the user layer
        Assert.Equal("acme-large", p.Aliases["smart"]); // untouched
        Assert.Equal("http://localhost:9999/v1", p.Presets!["local"].BaseUrl);
        Assert.Equal("eu", p.Properties!["region"]);
    }

    [Fact]
    public void The_catalog_says_which_layer_each_value_came_from()
    {
        ModelCatalog c = Layered();
        Assert.Equal("user", c.SourceOf("acme", "models[acme-large].contextWindow"));
        Assert.Equal("shipped", c.SourceOf("acme", "models[acme-large].maxOutputTokens"));
        Assert.Equal("user", c.SourceOf("acme", "models[acme-large].pricing.outputPerMillion"));
        Assert.Equal("shipped", c.SourceOf("acme", "models[acme-large].pricing.inputPerMillion"));
        Assert.Equal("user", c.SourceOf("acme", "aliases.fast"));
        Assert.Equal("shipped", c.SourceOf("acme", "aliases.smart"));
        Assert.Equal("user", c.SourceOf("acme", "models[acme-tiny].contextWindow"));
        Assert.Equal("code", c.SourceOf("acme", "presets.local.baseUrl"));
        Assert.Equal("code", c.SourceOf("acme", "properties.region"));
        Assert.Null(c.SourceOf("acme", "models[acme-large].nothing"));
    }

    [Fact]
    public void With_layer_adds_one_on_top_and_leaves_the_original_alone()
    {
        ModelCatalog baseCatalog = ModelCatalog.Create([CatalogLayer.FromYaml("shipped", Shipped)]);
        ModelCatalog changed = baseCatalog.WithLayer(new CatalogLayer("code", [new ProviderSpec { Name = "acme", Models = [new ModelSpec { Name = "acme-small", ContextWindow = 1 }] }]));
        Assert.Equal(100000, baseCatalog.Resolve("acme-small").Model!.ContextWindow);
        Assert.Equal(1, changed.Resolve("acme-small").Model!.ContextWindow);
        Assert.Equal(2, changed.Layers.Count);
    }

    [Fact]
    public void Names_resolve_by_alias_exact_name_and_pattern()
    {
        ModelCatalog c = Layered();
        ResolvedModel alias = c.Resolve("smart");
        Assert.Equal(ModelMatch.Alias, alias.Match);
        Assert.Equal("acme-large", alias.Name);
        Assert.Equal(400000, alias.Model!.ContextWindow);

        ResolvedModel exact = c.Resolve("ACME-SMALL");
        Assert.Equal(ModelMatch.Exact, exact.Match);
        Assert.Equal("acme-small", exact.Name);

        ResolvedModel pattern = c.Resolve("acme-next-gen");
        Assert.Equal(ModelMatch.Pattern, pattern.Match);
        Assert.Null(pattern.Model);
        Assert.Equal("acme", pattern.Provider.Name);
        Assert.Equal("acme:acme-next-gen", pattern.Id);
    }

    [Fact]
    public void Provider_prefix_limits_the_search_and_a_preset_name_works_too()
    {
        ModelCatalog c = Layered();
        ResolvedModel q = c.Resolve("acme:fast");
        Assert.True(q.WasQualified);
        Assert.Equal("acme-large", q.Name);

        ResolvedModel preset = c.Resolve("local:mine");
        Assert.Equal("local", preset.PresetName);
        Assert.Equal("local-7b", preset.Name);
        Assert.Equal("http://localhost:9999/v1", preset.Preset!.BaseUrl);
        Assert.Equal("local:local-7b", preset.Id);

        // Without the prefix a preset's patterns and aliases still match.
        Assert.Equal("local", c.Resolve("local-13b").PresetName);
        Assert.Equal("local-7b", c.Resolve("mine").Name);
    }

    [Fact]
    public void A_colon_that_is_not_a_provider_prefix_belongs_to_the_model_name()
    {
        ModelCatalog c = ModelCatalog.Create([new CatalogLayer("code", [new ProviderSpec { Name = "ollama", ModelPatterns = ["llama3*"] }])]);
        ResolvedModel r = c.Resolve("llama3:8b");
        Assert.Equal("llama3:8b", r.Name);
        Assert.False(r.WasQualified);
        Assert.Equal("llama3:8b", c.Resolve("ollama:llama3:8b").Name);
    }

    [Fact]
    public void The_most_specific_pattern_wins_and_a_tie_is_ambiguous()
    {
        var c = ModelCatalog.Create(
        [
            new CatalogLayer("code",
            [
                new ProviderSpec { Name = "a", ModelPatterns = ["gpt-*"] },
                new ProviderSpec { Name = "b", ModelPatterns = ["gpt-oss-*"] },
                new ProviderSpec { Name = "c", ModelPatterns = ["x-*"], Models = [new ModelSpec { Name = "shared" }] },
                new ProviderSpec { Name = "d", ModelPatterns = ["x-?"], Models = [new ModelSpec { Name = "shared" }] },
            ]),
        ]);
        Assert.Equal("b", c.Resolve("gpt-oss-20b").Provider.Name);
        Assert.Equal("a", c.Resolve("gpt-5").Provider.Name);

        ModelNotFoundException ambiguous = Assert.Throws<ModelNotFoundException>(() => c.Resolve("shared"));
        Assert.Contains("c:shared", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("d:shared", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("provider:model", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal("c", c.Resolve("c:shared").Provider.Name);
    }

    [Fact]
    public void Unknown_names_and_alias_loops_are_reported()
    {
        ModelCatalog c = ModelCatalog.Create([new CatalogLayer("code", [new ProviderSpec { Name = "p", Aliases = new() { ["a"] = "b", ["b"] = "c", ["c"] = "a" } }])]);
        Assert.False(c.TryResolve("nothing", out ResolvedModel? none));
        Assert.Null(none);
        Assert.Throws<ModelNotFoundException>(() => c.Resolve("nothing"));
        Assert.Contains("loops", Assert.Throws<ModelNotFoundException>(() => c.Resolve("a")).Message, StringComparison.Ordinal);
        Assert.Throws<ModelNotFoundException>(() => c.Resolve("p:"));
    }

    [Fact]
    public void An_alias_chain_resolves_to_the_final_model()
    {
        ModelCatalog c = ModelCatalog.Create([new CatalogLayer("code", [new ProviderSpec { Name = "p", Aliases = new() { ["latest"] = "stable", ["stable"] = "m-2" }, Models = [new ModelSpec { Name = "m-2", ContextWindow = 5 }] }])]);
        ResolvedModel r = c.Resolve("latest");
        Assert.Equal("m-2", r.Name);
        Assert.Equal(5, r.Model!.ContextWindow);
    }

    [Fact]
    public void A_catalog_file_can_list_several_providers()
    {
        CatalogLayer layer = CatalogLayer.FromYaml("user", """
            providers:
              - name: one
                modelPatterns: ["one-*"]
              - name: two
                modelPatterns: ["two-*"]
            """);
        Assert.Equal(["one", "two"], layer.Providers.Select(p => p.Name));
    }

    [Fact]
    public void A_bad_file_reports_where()
    {
        BadDefinitionException e = Assert.Throws<BadDefinitionException>(() => CatalogLayer.FromYaml("user", "name: x\nmodels:\n  - name: m\n    contextWindow: lots\n", "my-catalog.yaml"));
        Assert.Equal("my-catalog.yaml", e.File);
        Assert.Equal(4, e.Line);
        Assert.StartsWith("my-catalog.yaml:4:", e.Message, StringComparison.Ordinal);
        Assert.Throws<BadDefinitionException>(() => CatalogLayer.FromYaml("user", "displayName: no name\n"));
    }

    [Fact]
    public void Catalog_specs_round_trip_through_yaml_with_unknown_fields()
    {
        ProviderSpec spec = CatalogLayer.FromYaml("shipped", Shipped).Providers[0];
        string written = YamlSerializer.Serialize(spec, CatalogYamlContext.Default.ProviderSpec);
        ProviderSpec back = YamlSerializer.Deserialize(written, CatalogYamlContext.Default.ProviderSpec)!;
        Assert.Equal(written, YamlSerializer.Serialize(back, CatalogYamlContext.Default.ProviderSpec));
        Assert.Equal(11L, back.Models![0].ExtensionData!["baseTokens"]);
    }

    [Fact]
    public void Price_items_tiers_and_multipliers_read_with_exact_decimals()
    {
        ProviderSpec p = CatalogLayer.FromYaml("shipped", """
            name: acme
            models:
              - name: m
                pricing:
                  inputPerMillion: 2.50
                  longContextThreshold: 272000
                  longContextInputPerMillion: 5.00
                  items:
                    - counter: images
                      price: 0.04
                      per: 1
                      when: { size: 1024x1024, quality: high }
                      source: https://example.com/images
                    - counter: cacheCreation1hInputTokens
                      price: 6.25
                  tiers:
                    - above: 272000
                      multipliers: { inputTokens: 2, outputTokens: 1.5 }
                  multipliers:
                    batch: 0.5
            """).Providers[0];
        PricingSpec pricing = p.Models![0].Pricing!;
        Assert.Equal(2.50m, pricing.InputPerMillion);
        Assert.Equal(272000, pricing.LongContextThreshold);
        Assert.Equal(0.04m, pricing.Items![0].Price);
        Assert.Equal("high", pricing.Items[0].When!["quality"]);
        Assert.Null(pricing.Items[1].Per);
        Assert.Equal(1.5m, pricing.Tiers![0].Multipliers!["outputTokens"]);
        Assert.Equal(0.5m, pricing.Multipliers!["batch"]);
    }

    [Fact]
    public void Every_shipped_provider_file_of_the_runtime_loads_without_loss()
    {
        string? dir = Environment.GetEnvironmentVariable("ONELLM_PROVIDER_YAML_DIR");
        if (dir is null)
        {
            Assert.Skip("Set ONELLM_PROVIDER_YAML_DIR to a directory that holds llm-provider.yaml files to run this check.");
        }

        string[] files = Directory.GetFiles(dir, "llm-provider.yaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            ProviderSpec spec = Assert.Single(CatalogLayer.FromYaml("shipped", text, file).Providers);
            string written = YamlSerializer.Serialize(spec, CatalogYamlContext.Default.ProviderSpec);
            object? before = YamlSerializer.Deserialize<object>(text);
            object? after = YamlSerializer.Deserialize<object>(written);
            string? diff = FirstDifference(before, after, "$");
            Assert.True(diff is null, $"{Path.GetFileName(Path.GetDirectoryName(file))}: {diff}");
        }
    }

    // Compares two untyped trees; numbers compare by value, so 10.00 and 10 are the same.
    private static string? FirstDifference(object? a, object? b, string path)
    {
        switch (a)
        {
            case Dictionary<string, object?> da:
                if (b is not Dictionary<string, object?> db)
                {
                    return $"{path}: not a mapping";
                }

                foreach ((string k, object? v) in da)
                {
                    if (!db.TryGetValue(k, out object? other))
                    {
                        return $"{path}.{k}: lost";
                    }

                    if (FirstDifference(v, other, path + "." + k) is { } d)
                    {
                        return d;
                    }
                }

                return db.Keys.Except(da.Keys).FirstOrDefault() is { } extra ? $"{path}.{extra}: added" : null;
            case List<object?> la:
                if (b is not List<object?> lb || la.Count != lb.Count)
                {
                    return $"{path}: list differs";
                }

                for (int i = 0; i < la.Count; i++)
                {
                    if (FirstDifference(la[i], lb[i], $"{path}[{i}]") is { } d)
                    {
                        return d;
                    }
                }

                return null;
            case long or double:
                return b is long or double && Convert.ToDecimal(a) == Convert.ToDecimal(b) ? null : $"{path}: number differs";
            default:
                return Equals(a, b) ? null : $"{path}: value differs";
        }
    }
}
