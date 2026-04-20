module UnifiedLlm.PromptCachingTests

open System
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

let private cacheControlCount (json: string) =
    let needle = "\"cache_control\""
    let mutable count = 0
    let mutable index = json.IndexOf(needle, StringComparison.Ordinal)

    while index >= 0 do
        count <- count + 1
        index <- json.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)

    count

let private mapHelpersType () =
    typeof<AnthropicAdapter>.Assembly.GetTypes()
    |> Array.find (fun t -> t.Name = "HttpAdapterHelpers")

let private invokeStatic helperName (args: obj array) =
    let t = mapHelpersType ()
    let flags = BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public
    let m = t.GetMethod(helperName, flags)
    Assert.NotNull(m)
    m.Invoke(null, args)

let private generateWithUsage provider usage =
    let mock = ConfigurableMockAdapter(provider)

    mock.SetCompleteHandler(fun _ ->
        { Id = "r1"
          Model = "m"
          Provider = provider
          Message = Message.assistant ("cached response")
          FinishReason = Stop "stop"
          Usage = usage
          ResponseId = None
          Raw = None
          Warnings = []
          RateLimit = None })

    let client = Client()
    client.RegisterAdapter(mock)
    Generation.generate client "m" (Some "hello") None None None 0 (Some provider) None None

[<Fact>]
let ``Anthropic buildBody caches system and last user by default`` () =
    let request =
        Request.Create("claude-opus-4-6", [ Message.system ("System guidance"); Message.user ("Hello") ])

    let json = buildAnthropicBody request |> JsonSerializer.Serialize
    Assert.Equal(2, cacheControlCount json)

[<Fact>]
let ``Anthropic buildBody caches system and final user across mixed roles`` () =
    let request =
        Request.Create(
            "claude-opus-4-6",
            [ Message.system ("System guidance")
              Message.user ("First user")
              Message.assistant ("Intermediate reply")
              Message.user ("Last user") ]
        )

    let json = buildAnthropicBody request |> JsonSerializer.Serialize
    Assert.Equal(2, cacheControlCount json)

[<Fact>]
let ``Anthropic buildBody caches system and final tool result`` () =
    let toolCall =
        { Id = "call_1"
          Name = "lookup"
          Arguments = "{}"
          Metadata = Map.empty }

    let assistantToolCall =
        { Role = Assistant
          Content = [ ToolCall toolCall ]
          Name = None
          ToolCallId = None }

    let request =
        Request.Create(
            "claude-opus-4-6",
            [ Message.system ("System guidance")
              Message.user ("Call the tool")
              assistantToolCall
              Message.toolResult ("call_1", "result content", false) ]
        )

    let json = buildAnthropicBody request |> JsonSerializer.Serialize
    Assert.Equal(2, cacheControlCount json)
    Assert.Contains("\"tool_result\"", json)

[<Fact>]
let ``Anthropic buildBody skips cache_control when auto_cache is false`` () =
    let providerOptions: Map<string, obj> =
        Map.ofList [ "anthropic", box (Map.ofList [ "auto_cache", box false ]) ]

    let request =
        { Request.Create("claude-opus-4-6", [ Message.system ("System guidance"); Message.user ("Hello") ]) with
            ProviderOptions = Some providerOptions }

    let json = buildAnthropicBody request |> JsonSerializer.Serialize
    Assert.Equal(0, cacheControlCount json)

[<Fact>]
let ``Anthropic cache usage fields survive Generation.generate`` () =
    let usage =
        { InputTokens = 100
          OutputTokens = 50
          ReasoningTokens = None
          CacheReadTokens = Some 30
          CacheWriteTokens = Some 120 }

    let result = generateWithUsage "anthropic" usage
    Assert.Equal(Some 30, result.TotalUsage.CacheReadTokens)
    Assert.Equal(Some 120, result.TotalUsage.CacheWriteTokens)

[<Fact>]
let ``OpenAI cache usage fields survive Generation.generate`` () =
    let usage =
        { InputTokens = 100
          OutputTokens = 50
          ReasoningTokens = None
          CacheReadTokens = Some 45
          CacheWriteTokens = None }

    let result = generateWithUsage "openai" usage
    Assert.Equal(Some 45, result.TotalUsage.CacheReadTokens)
    Assert.Equal(None, result.TotalUsage.CacheWriteTokens)

[<Fact>]
let ``Gemini cache usage fields survive Generation.generate`` () =
    let usage =
        { InputTokens = 100
          OutputTokens = 50
          ReasoningTokens = None
          CacheReadTokens = Some 20
          CacheWriteTokens = None }

    let result = generateWithUsage "gemini" usage
    Assert.Equal(Some 20, result.TotalUsage.CacheReadTokens)
    Assert.Equal(None, result.TotalUsage.CacheWriteTokens)

[<Fact>]
let ``B9 OpenAI finish reason mapping table covers all branches`` () =
    let mapOpenAIFinishReason rawStatus rawReason hasToolCalls =
        invokeStatic "mapOpenAIFinishReason" [| box rawStatus; box rawReason; box hasToolCalls |] :?> FinishReason

    let cases =
        [ ToolCalls "tool_calls", mapOpenAIFinishReason "completed" (Some "tool_calls") true
          ToolCalls "tool_calls", mapOpenAIFinishReason "failed" (None: string option) true
          Stop "completed", mapOpenAIFinishReason "completed" (None: string option) false
          Stop "completed", mapOpenAIFinishReason "completed" (Some "stop") false
          Error "server_error", mapOpenAIFinishReason "failed" (Some "server_error") false
          Error "failed", mapOpenAIFinishReason "failed" (None: string option) false
          Length "max_output_tokens", mapOpenAIFinishReason "incomplete" (Some "max_output_tokens") false
          ContentFilter "content_filter", mapOpenAIFinishReason "incomplete" (Some "content_filter") false
          Other "turn_limit", mapOpenAIFinishReason "incomplete" (Some "turn_limit") false
          Length "incomplete", mapOpenAIFinishReason "incomplete" (None: string option) false
          Other "unknown:detail", mapOpenAIFinishReason "unknown" (Some "detail") false
          Other "unknown", mapOpenAIFinishReason "unknown" (None: string option) false ]

    for expected, actual in cases do
        Assert.Equal(expected, actual)
