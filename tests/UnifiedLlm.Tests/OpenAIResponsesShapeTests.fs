module UnifiedLlm.OpenAIResponsesShapeTests

open System.Reflection
open System.Text.Json
open Xunit
open UnifiedLlm

let private buildOpenAIBody (request: Request) =
    let adapter = OpenAIAdapter("test-key")
    let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
    let buildBody = typeof<OpenAIAdapter>.GetMethod("buildBody", flags)
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
let ``OpenAI buildBody emits reasoning.effort=low for gpt-5.5`` () =
    let request =
        { Request.Create("gpt-5.5", [ Message.User("plan") ]) with
            ReasoningEffort = Some "low"
            MaxTokens = Some 16384 }

    use doc = buildOpenAIBody request

    let reasoning =
        tryProp "reasoning" doc
        |> Option.defaultWith (fun () ->
            Assert.Fail("reasoning missing")
            Unchecked.defaultof<_>)

    Assert.Equal("low", reasoning.GetProperty("effort").GetString())
    Assert.Equal(16384, doc.RootElement.GetProperty("max_output_tokens").GetInt32())
    Assert.Equal("gpt-5.5", doc.RootElement.GetProperty("model").GetString())

[<Fact>]
let ``OpenAI buildBody emits reasoning.effort=medium for gpt-5.5`` () =
    let request =
        { Request.Create("gpt-5.5", [ Message.User("plan") ]) with
            ReasoningEffort = Some "medium"
            MaxTokens = Some 16384 }

    use doc = buildOpenAIBody request

    Assert.Equal("medium", (tryProp "reasoning" doc).Value.GetProperty("effort").GetString())
    Assert.Equal(16384, doc.RootElement.GetProperty("max_output_tokens").GetInt32())

[<Fact>]
let ``OpenAI buildBody emits reasoning.effort=high for gpt-5.5`` () =
    // The Reasoning helper bumps MaxTokens above the 32768 high budget upstream;
    // this test asserts the adapter faithfully forwards whatever it receives.
    let request =
        { Request.Create("gpt-5.5", [ Message.User("plan") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some(32768 + 4096) }

    use doc = buildOpenAIBody request

    Assert.Equal("high", (tryProp "reasoning" doc).Value.GetProperty("effort").GetString())
    Assert.Equal(32768 + 4096, doc.RootElement.GetProperty("max_output_tokens").GetInt32())

[<Fact>]
let ``OpenAI buildBody omits reasoning when reasoning_effort is not set`` () =
    let request =
        { Request.Create("gpt-5.5", [ Message.User("hi") ]) with
            MaxTokens = Some 16384 }

    use doc = buildOpenAIBody request

    Assert.False(doc.RootElement.TryGetProperty("reasoning", ref Unchecked.defaultof<JsonElement>))
    // max_output_tokens still propagates so output isn't silently truncated.
    Assert.Equal(16384, doc.RootElement.GetProperty("max_output_tokens").GetInt32())

[<Fact>]
let ``OpenAI buildBody omits max_output_tokens when MaxTokens is None`` () =
    let request =
        { Request.Create("gpt-5.5", [ Message.User("hi") ]) with
            ReasoningEffort = Some "low" }

    use doc = buildOpenAIBody request

    Assert.False(doc.RootElement.TryGetProperty("max_output_tokens", ref Unchecked.defaultof<JsonElement>))
    Assert.Equal("low", (tryProp "reasoning" doc).Value.GetProperty("effort").GetString())

[<Fact>]
let ``OpenAI buildBody preserves explicit max_output_tokens above budget`` () =
    let request =
        { Request.Create("gpt-5.5", [ Message.User("plan") ]) with
            ReasoningEffort = Some "high"
            MaxTokens = Some 100_000 }

    use doc = buildOpenAIBody request

    Assert.Equal(100_000, doc.RootElement.GetProperty("max_output_tokens").GetInt32())

[<Fact>]
let ``OpenAI buildBody includes model id and store flag for gpt-5.5`` () =
    let request =
        { Request.Create("gpt-5.5", [ Message.User("hi") ]) with
            ReasoningEffort = Some "medium"
            MaxTokens = Some 16384 }

    use doc = buildOpenAIBody request

    Assert.Equal("gpt-5.5", doc.RootElement.GetProperty("model").GetString())
    Assert.True(doc.RootElement.GetProperty("store").GetBoolean())
