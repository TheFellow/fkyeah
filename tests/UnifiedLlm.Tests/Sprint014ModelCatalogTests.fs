module UnifiedLlmSprint014ModelCatalogTests

open Xunit
open UnifiedLlm

[<Fact>]
let ``resolveModel returns refreshed Opus 4.6 pricing and output ceiling`` () =
    let model = ModelCatalog.resolveModel "claude-opus-4-6"
    Assert.True(model.IsSome)
    Assert.Equal(128000, model.Value.MaxOutput)
    Assert.Equal(5.0, model.Value.InputCostPerMillion)
    Assert.Equal(25.0, model.Value.OutputCostPerMillion)

[<Fact>]
let ``resolveModel finds newly added models and aliases`` () =
    let gptPro = ModelCatalog.resolveModel "gpt-5.4-pro"
    let opus1m = ModelCatalog.resolveModel "opus-1m"
    let haiku = ModelCatalog.resolveModel "claude-haiku-4-5"
    let geminiFlash = ModelCatalog.resolveModel "gemini-2.5-flash"

    Assert.True(gptPro.IsSome)
    Assert.Equal(70.0, gptPro.Value.InputCostPerMillion)
    Assert.Equal(280.0, gptPro.Value.OutputCostPerMillion)

    Assert.True(opus1m.IsSome)
    Assert.Equal("claude-opus-4-7[1m]", opus1m.Value.Id)

    Assert.True(haiku.IsSome)
    Assert.True(geminiFlash.IsSome)

[<Fact>]
let ``catalog remains limited to anthropic openai and gemini providers`` () =
    let providers = ModelCatalog.listModels () |> List.map _.Provider |> Set.ofList

    Assert.Equal<Set<string>>(set [ "anthropic"; "openai"; "gemini" ], providers)
