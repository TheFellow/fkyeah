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
        Assert.Equal("claude-opus-4-7", model.Value.Id)

    [<Fact>]
    let ``findModels and resolveModel return expected catalog entries`` () =
        let allModels = ModelCatalog.findModels CapabilityRequirement.none
        Assert.Equal(24, allModels.Length)

        let alias = ModelCatalog.resolveModel "claude-opus"
        let exact = ModelCatalog.resolveModel "claude-opus-4-6"
        let missing = ModelCatalog.resolveModel "unknown-model-xyz"

        Assert.Equal(Some "claude-opus-4-7", alias |> Option.map (fun model -> model.Id))
        Assert.Equal(Some "claude-opus-4-6", exact |> Option.map (fun model -> model.Id))
        Assert.Equal(None, missing)

    [<Fact>]
    let ``findModel returns None for unknown providers`` () =
        Assert.Equal(None, ModelCatalog.findModel "nonexistent" CapabilityRequirement.none)
