module UnifiedLlmModelCatalogSprint007Tests

open Xunit
open UnifiedLlm

module ModelCatalogSprint007 =

    [<Fact>]
    let ``CapabilityRequirement satisfiedBy evaluates each capability independently`` () =
        let baseline =
            { Id = "test"
              Provider = "custom"
              DisplayName = "Test"
              ContextWindow = 1000
              MaxOutput = 100
              InputCostPerMillion = 0.0
              OutputCostPerMillion = 0.0
              Aliases = []
              SupportsStreaming = true
              SupportsTools = false
              SupportsReasoning = true
              SupportsVision = false }

        Assert.False(
            CapabilityRequirement.satisfiedBy
                baseline
                { CapabilityRequirement.none with
                    RequiresTools = true }
        )

        Assert.True(
            CapabilityRequirement.satisfiedBy
                baseline
                { CapabilityRequirement.none with
                    RequiresVision = false }
        )

        Assert.True(CapabilityRequirement.satisfiedBy baseline CapabilityRequirement.none)

    [<Fact>]
    let ``findModel prefers latest provider model when capabilities match`` () =
        let model =
            ModelCatalog.findModel
                "anthropic"
                { CapabilityRequirement.none with
                    RequiresStreaming = true
                    RequiresTools = true
                    RequiresReasoning = true }

        Assert.True(model.IsSome)
        Assert.Equal("claude-opus-4-8", model.Value.Id)

    [<Fact>]
    let ``findModels and resolveModel return expected catalog entries`` () =
        let allModels = ModelCatalog.findModels CapabilityRequirement.none
        Assert.Equal(30, allModels.Length)

        let alias = ModelCatalog.resolveModel "claude-opus"
        let sonnetAlias = ModelCatalog.resolveModel "claude-sonnet"
        let sonnetLatest = ModelCatalog.resolveModel "sonnet-latest"
        let latestOpenAI = ModelCatalog.resolveModel "gpt-latest"
        let exact = ModelCatalog.resolveModel "claude-opus-4-6"
        let missing = ModelCatalog.resolveModel "unknown-model-xyz"

        Assert.Equal(Some "claude-opus-4-8", alias |> Option.map (fun model -> model.Id))
        Assert.Equal(Some "claude-sonnet-5", sonnetAlias |> Option.map (fun model -> model.Id))
        Assert.Equal(Some "claude-sonnet-5", sonnetLatest |> Option.map (fun model -> model.Id))
        Assert.Equal(Some "gpt-5.6-sol", latestOpenAI |> Option.map (fun model -> model.Id))
        Assert.Equal(Some "claude-opus-4-6", exact |> Option.map (fun model -> model.Id))
        Assert.Equal(None, missing)

    [<Fact>]
    let ``findModel returns None for unknown providers`` () =
        Assert.Equal(None, ModelCatalog.findModel "nonexistent" CapabilityRequirement.none)

    [<Fact>]
    let ``every catalog model has standard text pricing compatible with ModelInfo`` () =
        for model in ModelCatalog.listModels () do
            let pricing =
                ModelCatalog.getPricing model.Id
                |> Option.defaultWith (fun () -> failwithf "missing pricing for %s" model.Id)

            let standard = pricing.Tiers[PricingTier.Standard]
            let text = standard.Modalities[PricingModality.Text]

            Assert.Equal(Some(decimal model.InputCostPerMillion), text.InputPerMillion)
            Assert.Equal(Some(decimal model.OutputCostPerMillion), text.OutputPerMillion)

    [<Fact>]
    let ``resolvePricing accepts aliases without duplicating catalog entries`` () =
        let exact = ModelCatalog.resolvePricing "gpt-5.6-sol"
        let alias = ModelCatalog.resolvePricing "gpt-latest"

        Assert.Equal(exact, alias)

    [<Fact>]
    let ``catalog exposes batch and cache pricing without changing capability metadata`` () =
        let pricing = ModelCatalog.getPricing "claude-opus-4-6" |> Option.get
        let standard = pricing.Tiers[PricingTier.Standard].Modalities[PricingModality.Text]
        let batch = pricing.Tiers[PricingTier.Batch].Modalities[PricingModality.Text]

        Assert.Equal(InputTokenAccounting.ExcludesCacheReads, pricing.InputTokenAccounting)
        Assert.Equal(Some 0.5m, standard.CacheReadPerMillion)
        Assert.Equal(Some 6.25m, standard.CacheWriteFiveMinutesPerMillion)
        Assert.Equal(Some 10m, standard.CacheWriteOneHourPerMillion)
        Assert.Equal(Some 2.5m, batch.InputPerMillion)
        Assert.Equal(Some 12.5m, batch.OutputPerMillion)
