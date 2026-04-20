module UnifiedLlm.AnthropicThinkingShapeTests

open System.Reflection
open System.Text.Json
open Xunit
open UnifiedLlm

let private buildAnthropicBody (request: Request) =
    let adapter = AnthropicAdapter("test-key")
    let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
    let buildBody = typeof<AnthropicAdapter>.GetMethod("buildBody", flags)
    Assert.NotNull(buildBody)

    buildBody.Invoke(adapter, [| box request; box false |])
    |> JsonSerializer.Serialize
    |> JsonDocument.Parse

let private tryProp (name: string) (doc: JsonDocument) =
    let mutable v = Unchecked.defaultof<JsonElement>

    if doc.RootElement.TryGetProperty(name, &v) then
        Some v
    else
        None

[<Fact>]
let ``Anthropic buildBody emits adaptive thinking for opus-4-7`` () =
    let request =
        { Request.Create("claude-opus-4-7", [ Message.user ("plan") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some 4096 }

    use doc = buildAnthropicBody request

    let thinking =
        tryProp "thinking" doc
        |> Option.defaultWith (fun () ->
            Assert.Fail("thinking missing")
            Unchecked.defaultof<_>)

    Assert.Equal("adaptive", thinking.GetProperty("type").GetString())
    Assert.False(thinking.TryGetProperty("budget_tokens", ref Unchecked.defaultof<JsonElement>))

    let outputConfig =
        tryProp "output_config" doc
        |> Option.defaultWith (fun () ->
            Assert.Fail("output_config missing")
            Unchecked.defaultof<_>)

    Assert.Equal("high", outputConfig.GetProperty("effort").GetString())

    // Thinking tokens consume max_tokens on the adaptive path too, so the
    // adapter must bump small max_tokens to budget + 4096 for parity with legacy.
    Assert.Equal(32768 + 4096, doc.RootElement.GetProperty("max_tokens").GetInt32())

[<Fact>]
let ``Anthropic buildBody maps xhigh to high on adaptive models`` () =
    let request =
        { Request.Create("claude-opus-4-7", [ Message.user ("plan") ]) with
            ReasoningEffort = Some "xhigh" }

    use doc = buildAnthropicBody request

    Assert.Equal("adaptive", (tryProp "thinking" doc).Value.GetProperty("type").GetString())
    Assert.Equal("high", (tryProp "output_config" doc).Value.GetProperty("effort").GetString())

[<Fact>]
let ``Anthropic buildBody keeps legacy enabled thinking for 4.6 models`` () =
    let request =
        { Request.Create("claude-opus-4-6", [ Message.user ("plan") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some 4096 }

    use doc = buildAnthropicBody request

    let thinking = (tryProp "thinking" doc).Value
    Assert.Equal("enabled", thinking.GetProperty("type").GetString())
    Assert.Equal(32768, thinking.GetProperty("budget_tokens").GetInt32())
    Assert.False(doc.RootElement.TryGetProperty("output_config", ref Unchecked.defaultof<JsonElement>))
    // max_tokens <= budget must be bumped to budget + 4096.
    Assert.Equal(32768 + 4096, doc.RootElement.GetProperty("max_tokens").GetInt32())

[<Fact>]
let ``Anthropic buildBody omits thinking when reasoning_effort is not set`` () =
    let request = Request.Create("claude-opus-4-7", [ Message.user ("hi") ])

    use doc = buildAnthropicBody request

    Assert.False(doc.RootElement.TryGetProperty("thinking", ref Unchecked.defaultof<JsonElement>))
    Assert.False(doc.RootElement.TryGetProperty("output_config", ref Unchecked.defaultof<JsonElement>))

[<Fact>]
let ``Anthropic buildBody adaptive path preserves explicit max_tokens above budget`` () =
    let request =
        { Request.Create("claude-opus-4-7", [ Message.user ("plan") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some 100000 }

    use doc = buildAnthropicBody request

    // Caller-specified max_tokens (> budget) must not be overridden.
    Assert.Equal(100000, doc.RootElement.GetProperty("max_tokens").GetInt32())

[<Fact>]
let ``Anthropic buildBody adaptive path applies to sonnet-4-7 and haiku-4-7`` () =
    for model in [ "claude-sonnet-4-7"; "claude-haiku-4-7" ] do
        let request =
            { Request.Create(model, [ Message.user ("plan") ]) with
                ReasoningEffort = Some "medium" }

        use doc = buildAnthropicBody request

        Assert.Equal("adaptive", (tryProp "thinking" doc).Value.GetProperty("type").GetString())
        Assert.Equal("medium", (tryProp "output_config" doc).Value.GetProperty("effort").GetString())
