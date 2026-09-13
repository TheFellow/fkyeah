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
    let gpt55 = ModelCatalog.resolveModel "gpt-5.5"
    let gpt56 = ModelCatalog.resolveModel "gpt-5.6"
    let gpt56Terra = ModelCatalog.resolveModel "gpt-5.6-terra"
    let gpt56Luna = ModelCatalog.resolveModel "gpt-5.6-luna"
    let latestGpt = ModelCatalog.resolveModel "gpt-latest"
    let opus1m = ModelCatalog.resolveModel "opus-1m"
    let opus48Explicit1m = ModelCatalog.resolveModel "claude-opus-4-8[1m]"
    let haiku = ModelCatalog.resolveModel "claude-haiku-4-5"
    let geminiFlash = ModelCatalog.resolveModel "gemini-2.5-flash"

    Assert.True(gptPro.IsSome)
    Assert.Equal(70.0, gptPro.Value.InputCostPerMillion)
    Assert.Equal(280.0, gptPro.Value.OutputCostPerMillion)

    Assert.True(gpt55.IsSome)
    Assert.Equal(1000000, gpt55.Value.ContextWindow)
    Assert.Equal(128000, gpt55.Value.MaxOutput)
    Assert.Equal(5.0, gpt55.Value.InputCostPerMillion)
    Assert.Equal(30.0, gpt55.Value.OutputCostPerMillion)

    Assert.True(gpt56.IsSome)
    Assert.Equal("gpt-5.6-sol", gpt56.Value.Id)
    Assert.Equal("GPT-5.6 Sol", gpt56.Value.DisplayName)
    Assert.Equal(1050000, gpt56.Value.ContextWindow)
    Assert.Equal(128000, gpt56.Value.MaxOutput)
    Assert.Equal(5.0, gpt56.Value.InputCostPerMillion)
    Assert.Equal(30.0, gpt56.Value.OutputCostPerMillion)
    Assert.True(gpt56.Value.SupportsStreaming)
    Assert.True(gpt56.Value.SupportsTools)
    Assert.True(gpt56.Value.SupportsReasoning)
    Assert.True(gpt56.Value.SupportsVision)
    Assert.Equal(Some "gpt-6-astra", latestGpt |> Option.map _.Id)
    Assert.Equal("gpt-6-astra", ModelCatalog.getLatestModel("openai").Value.Id)

    Assert.True(gpt56Terra.IsSome)
    Assert.Equal(2.5, gpt56Terra.Value.InputCostPerMillion)
    Assert.Equal(15.0, gpt56Terra.Value.OutputCostPerMillion)
    Assert.True(gpt56Luna.IsSome)
    Assert.Equal(1.0, gpt56Luna.Value.InputCostPerMillion)
    Assert.Equal(6.0, gpt56Luna.Value.OutputCostPerMillion)

    Assert.True(opus1m.IsSome)
    Assert.Equal("claude-opus-4-8", opus1m.Value.Id)
    Assert.Equal(1000000, opus1m.Value.ContextWindow)
    Assert.Equal(Some "claude-opus-4-8", opus48Explicit1m |> Option.map _.Id)

    Assert.True(haiku.IsSome)
    Assert.True(geminiFlash.IsSome)

[<Fact>]
let ``resolveModel returns GPT-6 Astra catalog metadata and aliases`` () =
    let astra = ModelCatalog.resolveModel "gpt-6-astra"

    Assert.True(astra.IsSome)
    Assert.Equal("gpt-6-astra", astra.Value.Id)
    Assert.Equal("openai", astra.Value.Provider)
    Assert.Equal("GPT-6 Astra", astra.Value.DisplayName)
    Assert.Equal(1050000, astra.Value.ContextWindow)
    Assert.Equal(128000, astra.Value.MaxOutput)
    Assert.Equal(10.0, astra.Value.InputCostPerMillion)
    Assert.Equal(50.0, astra.Value.OutputCostPerMillion)
    Assert.True(astra.Value.SupportsStreaming)
    Assert.True(astra.Value.SupportsTools)
    Assert.True(astra.Value.SupportsReasoning)
    Assert.True(astra.Value.SupportsVision)
    Assert.Equal(astra, ModelCatalog.resolveModel "gpt-6")
    Assert.Equal(astra, ModelCatalog.resolveModel "gpt-latest")
    Assert.Equal(astra, ModelCatalog.getLatestModel "openai")

    let required =
        { RequiresStreaming = true
          RequiresTools = true
          RequiresReasoning = true
          RequiresVision = true }

    Assert.Equal(astra, ModelCatalog.findModel "openai" required)

[<Fact>]
let ``resolveModel returns full GPT-5.6 Terra and Luna catalog metadata`` () =
    let terra = ModelCatalog.resolveModel "gpt-5.6-terra"
    let luna = ModelCatalog.resolveModel "gpt-5.6-luna"

    Assert.True(terra.IsSome)
    Assert.Equal("openai", terra.Value.Provider)
    Assert.Equal("GPT-5.6 Terra", terra.Value.DisplayName)
    Assert.Equal(1050000, terra.Value.ContextWindow)
    Assert.Equal(128000, terra.Value.MaxOutput)
    Assert.Equal(2.5, terra.Value.InputCostPerMillion)
    Assert.Equal(15.0, terra.Value.OutputCostPerMillion)
    Assert.True(terra.Value.SupportsStreaming)
    Assert.True(terra.Value.SupportsTools)
    Assert.True(terra.Value.SupportsReasoning)
    Assert.True(terra.Value.SupportsVision)

    Assert.True(luna.IsSome)
    Assert.Equal("openai", luna.Value.Provider)
    Assert.Equal("GPT-5.6 Luna", luna.Value.DisplayName)
    Assert.Equal(1050000, luna.Value.ContextWindow)
    Assert.Equal(128000, luna.Value.MaxOutput)
    Assert.Equal(1.0, luna.Value.InputCostPerMillion)
    Assert.Equal(6.0, luna.Value.OutputCostPerMillion)
    Assert.True(luna.Value.SupportsStreaming)
    Assert.True(luna.Value.SupportsTools)
    Assert.True(luna.Value.SupportsReasoning)
    Assert.True(luna.Value.SupportsVision)

[<Fact>]
let ``resolveModel returns Claude Sonnet 5 catalog metadata and aliases`` () =
    let sonnet = ModelCatalog.resolveModel "claude-sonnet-5"
    let sonnetFamilyAlias = ModelCatalog.resolveModel "claude-sonnet"
    let sonnet1mAlias = ModelCatalog.resolveModel "claude-sonnet-1m"
    let latestSonnetAlias = ModelCatalog.resolveModel "latest-anthropic-sonnet"

    Assert.True(sonnet.IsSome)
    Assert.Equal("anthropic", sonnet.Value.Provider)
    Assert.Equal("Claude Sonnet 5", sonnet.Value.DisplayName)
    Assert.Equal(1000000, sonnet.Value.ContextWindow)
    Assert.Equal(128000, sonnet.Value.MaxOutput)
    Assert.Equal(3.0, sonnet.Value.InputCostPerMillion)
    Assert.Equal(15.0, sonnet.Value.OutputCostPerMillion)
    Assert.True(sonnet.Value.SupportsStreaming)
    Assert.True(sonnet.Value.SupportsTools)
    Assert.True(sonnet.Value.SupportsReasoning)
    Assert.True(sonnet.Value.SupportsVision)
    Assert.Equal(Some "claude-sonnet-5", sonnetFamilyAlias |> Option.map _.Id)
    Assert.Equal(Some "claude-sonnet-5", sonnet1mAlias |> Option.map _.Id)
    Assert.Equal(Some "claude-sonnet-5", latestSonnetAlias |> Option.map _.Id)

[<Fact>]
let ``catalog remains limited to anthropic openai and gemini providers`` () =
    let providers = ModelCatalog.listModels () |> List.map _.Provider |> Set.ofList

    Assert.Equal<Set<string>>(set [ "anthropic"; "openai"; "gemini" ], providers)
