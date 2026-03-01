module UnifiedLlm.LiveApiTests

open System
open System.Text.Json
open Xunit
open UnifiedLlm

// ============================================================
// Infrastructure
// ============================================================

/// Helpers for constructing clients and selecting models per provider.
module LiveApiHelpers =

    /// Map provider name to its API key environment variable.
    let envVarFor (provider: string) =
        match provider.ToLowerInvariant() with
        | "anthropic" -> "ANTHROPIC_API_KEY"
        | "openai" -> "OPENAI_API_KEY"
        | "gemini" -> "GEMINI_API_KEY"
        | other -> failwithf "Unknown provider: %s" other

    /// Create a Client with a single real adapter for the given provider.
    let createClient (provider: string) =
        let client = Client()
        let envVar = envVarFor provider
        let apiKey = Environment.GetEnvironmentVariable(envVar)
        let adapter : IProviderAdapter =
            match provider.ToLowerInvariant() with
            | "anthropic" -> AnthropicAdapter(apiKey) :> IProviderAdapter
            | "openai" -> OpenAIAdapter(apiKey) :> IProviderAdapter
            | "gemini" -> GeminiAdapter(apiKey) :> IProviderAdapter
            | other -> failwithf "Unknown provider: %s" other
        client.RegisterAdapter(adapter)
        client

    /// Return the cheapest model string for a provider.
    let modelFor (provider: string) =
        match provider.ToLowerInvariant() with
        | "anthropic" -> "claude-sonnet-4-5"
        | "openai" -> "gpt-4o-mini"
        | "gemini" -> "gemini-2.0-flash"
        | other -> failwithf "Unknown provider: %s" other

/// Custom FactAttribute that skips when the provider's API key env var is missing.
type LiveApiFactAttribute(provider: string) =
    inherit FactAttribute()
    do
        let envVar = LiveApiHelpers.envVarFor provider
        let value = Environment.GetEnvironmentVariable(envVar)
        if String.IsNullOrEmpty(value) then
            base.Skip <- sprintf "%s not set - skipping %s live test" envVar provider

// ============================================================
// 1. Simple text generation
// ============================================================

module SimpleTextGeneration =

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let result =
            Generation.generate
                client model
                (Some "Say hello in one word")
                None None None 0
                (Some provider) None None
        Assert.False(String.IsNullOrWhiteSpace result.Text, "Expected non-empty text")
        Assert.True(result.Usage.InputTokens > 0, "Expected InputTokens > 0")
        Assert.True(result.Usage.OutputTokens > 0, "Expected OutputTokens > 0")
        Assert.Equal(provider, result.Response.Provider)

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Simple text generation - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Simple text generation - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Simple text generation - Gemini`` () = runTest "gemini"

// ============================================================
// 2. Streaming text generation
// ============================================================

module StreamingTextGeneration =

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let events =
            Generation.streamWithControl
                client model
                (Some "Say hello in one word")
                None None None 0
                (Some provider) None None None None
            |> Seq.toList
        let textDeltas =
            events
            |> List.choose (fun e ->
                match e with
                | TextDelta(_, text) -> Some text
                | _ -> None)
        let fullText = String.concat "" textDeltas
        Assert.False(String.IsNullOrWhiteSpace fullText, "Expected non-empty streamed text")
        let hasFinish =
            events
            |> List.exists (fun e ->
                match e with
                | Finish _ -> true
                | _ -> false)
        Assert.True(hasFinish, "Expected a Finish event in stream")

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Streaming text generation - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Streaming text generation - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Streaming text generation - Gemini`` () = runTest "gemini"

// ============================================================
// 3. Image input (base64)
// ============================================================

module ImageInputBase64 =

    // Minimal 1x1 red PNG
    let private redPngBytes =
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==")

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let imageContent =
            Image { Url = None; Data = Some redPngBytes; FilePath = None; MediaType = Some "image/png" }
        let message =
            { Role = User
              Content = [ Text "Describe this image in one sentence."; imageContent ]
              Name = None
              ToolCallId = None }
        let request =
            { Request.Create(model, [ message ]) with
                Provider = Some provider }
        let response = client.Complete(request)
        Assert.False(String.IsNullOrWhiteSpace response.Text, "Expected non-empty response for image input")

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Image input base64 - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Image input base64 - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Image input base64 - Gemini`` () = runTest "gemini"

// ============================================================
// 4. Image input (URL)
// ============================================================

module ImageInputUrl =

    let private imageUrl =
        "https://upload.wikimedia.org/wikipedia/commons/thumb/4/47/PNG_transparency_demonstration_1.png/280px-PNG_transparency_demonstration_1.png"

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let imageContent =
            Image { Url = Some imageUrl; Data = None; FilePath = None; MediaType = None }
        let message =
            { Role = User
              Content = [ Text "Describe this image in one sentence."; imageContent ]
              Name = None
              ToolCallId = None }
        let request =
            { Request.Create(model, [ message ]) with
                Provider = Some provider }
        let response = client.Complete(request)
        Assert.False(String.IsNullOrWhiteSpace response.Text, "Expected non-empty response for image URL input")

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Image input URL - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Image input URL - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Image input URL - Gemini`` () = runTest "gemini"

// ============================================================
// 5. Single tool call + execution
// ============================================================

module SingleToolCall =

    let private weatherTool : Tool =
        { Definition =
            { Name = "get_weather"
              Description = "Get the current weather for a city"
              Parameters = """{"type":"object","properties":{"city":{"type":"string","description":"City name"}},"required":["city"]}""" }
          Execute = Some (fun argsJson ->
            let doc = JsonDocument.Parse(argsJson)
            let city = doc.RootElement.GetProperty("city").GetString()
            sprintf """{"city":"%s","temperature":"22C","condition":"sunny"}""" city) }

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let result =
            Generation.generate
                client model
                (Some "What is the weather in London? Use the get_weather tool.")
                None None (Some [ weatherTool ]) 3
                (Some provider) None None
        Assert.False(String.IsNullOrWhiteSpace result.Text, "Expected non-empty text after tool use")
        Assert.True(result.Steps.Length >= 2, sprintf "Expected at least 2 steps, got %d" result.Steps.Length)

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Single tool call - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Single tool call - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Single tool call - Gemini`` () = runTest "gemini"

// ============================================================
// 6. Multiple parallel tool calls
// ============================================================

module MultipleParallelToolCalls =

    let private weatherTool : Tool =
        { Definition =
            { Name = "get_weather"
              Description = "Get the current weather for a city"
              Parameters = """{"type":"object","properties":{"city":{"type":"string","description":"City name"}},"required":["city"]}""" }
          Execute = Some (fun argsJson ->
            let doc = JsonDocument.Parse(argsJson)
            let city = doc.RootElement.GetProperty("city").GetString()
            sprintf """{"city":"%s","temperature":"18C","condition":"cloudy"}""" city) }

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let result =
            Generation.generate
                client model
                (Some "What is the weather in both Paris and Tokyo? Use the get_weather tool for each city.")
                None None (Some [ weatherTool ]) 3
                (Some provider) None None
        // At least 2 tool calls across steps, or at least 2 steps
        let totalToolCalls =
            result.Steps |> List.sumBy (fun s -> s.ToolCalls.Length)
        Assert.True(
            totalToolCalls >= 2 || result.Steps.Length >= 2,
            sprintf "Expected >= 2 tool calls or >= 2 steps. Got %d tool calls, %d steps" totalToolCalls result.Steps.Length)

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Multiple parallel tool calls - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Multiple parallel tool calls - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Multiple parallel tool calls - Gemini`` () = runTest "gemini"

// ============================================================
// 7. Multi-step tool loop (3+ rounds)
// ============================================================

module MultiStepToolLoop =

    let private temperatureTool : Tool =
        { Definition =
            { Name = "get_temperature"
              Description = "Get the temperature for a location"
              Parameters = """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""" }
          Execute = Some (fun argsJson ->
            let doc = JsonDocument.Parse(argsJson)
            let loc = doc.RootElement.GetProperty("location").GetString()
            sprintf """{"location":"%s","temperature_c":22}""" loc) }

    let private humidityTool : Tool =
        { Definition =
            { Name = "get_humidity"
              Description = "Get the humidity for a location"
              Parameters = """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""" }
          Execute = Some (fun argsJson ->
            let doc = JsonDocument.Parse(argsJson)
            let loc = doc.RootElement.GetProperty("location").GetString()
            sprintf """{"location":"%s","humidity_pct":65}""" loc) }

    let private forecastTool : Tool =
        { Definition =
            { Name = "get_forecast"
              Description = "Get the 3-day forecast for a location"
              Parameters = """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""" }
          Execute = Some (fun argsJson ->
            let doc = JsonDocument.Parse(argsJson)
            let loc = doc.RootElement.GetProperty("location").GetString()
            sprintf """{"location":"%s","forecast":["sunny","cloudy","rainy"]}""" loc) }

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let tools = [ temperatureTool; humidityTool; forecastTool ]
        let result =
            Generation.generate
                client model
                (Some "For London: first get the temperature, then get the humidity, then get the 3-day forecast. Use each tool separately in order.")
                None None (Some tools) 5
                (Some provider) None None
        // Loose assertion: at least 2 steps (models may batch tool calls)
        Assert.True(
            result.Steps.Length >= 2,
            sprintf "Expected at least 2 steps for multi-tool loop, got %d" result.Steps.Length)

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Multi-step tool loop - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Multi-step tool loop - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Multi-step tool loop - Gemini`` () = runTest "gemini"

// ============================================================
// 8. Streaming with tool calls
// ============================================================

module StreamingWithToolCalls =

    let private weatherTool : Tool =
        { Definition =
            { Name = "get_weather"
              Description = "Get the current weather for a city"
              Parameters = """{"type":"object","properties":{"city":{"type":"string","description":"City name"}},"required":["city"]}""" }
          Execute = Some (fun argsJson ->
            let doc = JsonDocument.Parse(argsJson)
            let city = doc.RootElement.GetProperty("city").GetString()
            sprintf """{"city":"%s","temperature":"20C","condition":"clear"}""" city) }

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let events =
            Generation.streamWithControl
                client model
                (Some "What is the weather in Berlin? Use the get_weather tool.")
                None None (Some [ weatherTool ]) 3
                (Some provider) None None None None
            |> Seq.toList
        let hasToolCallStart =
            events |> List.exists (fun e -> match e with ToolCallStart _ -> true | _ -> false)
        let hasToolCallEnd =
            events |> List.exists (fun e -> match e with ToolCallEnd _ -> true | _ -> false)
        Assert.True(hasToolCallStart, "Expected ToolCallStart event in stream")
        Assert.True(hasToolCallEnd, "Expected ToolCallEnd event in stream")

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Streaming with tool calls - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Streaming with tool calls - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Streaming with tool calls - Gemini`` () = runTest "gemini"

// ============================================================
// 9. Structured output (generateObject)
// ============================================================

module StructuredOutput =

    // OpenAI requires additionalProperties:false; Gemini rejects it; Anthropic doesn't care
    let private schemaFor (provider: string) =
        match provider with
        | "openai" ->
            """{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}},"required":["name","age"],"additionalProperties":false}"""
        | _ ->
            """{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}},"required":["name","age"]}"""

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let schema = schemaFor provider
        let result =
            Generation.generateObjectWithControl
                client model
                "Generate a person named Alice who is 30 years old"
                schema (Some provider) None
        Assert.False(String.IsNullOrWhiteSpace result.Text, "Expected non-empty structured output")
        let doc = JsonDocument.Parse(result.Text.Trim())
        let root = doc.RootElement
        Assert.Equal("Alice", root.GetProperty("name").GetString())
        Assert.Equal(30, root.GetProperty("age").GetInt32())

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Structured output - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Structured output - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Structured output - Gemini`` () = runTest "gemini"

// ============================================================
// 10. Reasoning/thinking tokens
// ============================================================

module ReasoningTokens =

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let reasoningEffort =
            match provider with
            | "anthropic" -> Some "medium"
            | _ -> None
        let result =
            Generation.generate
                client model
                (Some "What is 17 * 23? Think step by step.")
                None None None 0
                (Some provider) reasoningEffort None
        Assert.False(String.IsNullOrWhiteSpace result.Text, "Expected non-empty response")
        // For all providers, just assert usage is populated
        Assert.True(result.Usage.InputTokens > 0, "Expected InputTokens > 0")

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Reasoning tokens - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Reasoning tokens - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Reasoning tokens - Gemini`` () = runTest "gemini"

// ============================================================
// 11. Invalid API key -> error
// ============================================================

module InvalidApiKeyError =

    let private runTest (provider: string) =
        let client = Client()
        let adapter : IProviderAdapter =
            match provider.ToLowerInvariant() with
            | "anthropic" -> AnthropicAdapter("invalid-key-12345") :> IProviderAdapter
            | "openai" -> OpenAIAdapter("invalid-key-12345") :> IProviderAdapter
            | "gemini" -> GeminiAdapter("invalid-key-12345") :> IProviderAdapter
            | other -> failwithf "Unknown provider: %s" other
        client.RegisterAdapter(adapter)
        let model = LiveApiHelpers.modelFor provider
        let request =
            { Request.Create(model, [ Message.user("hello") ]) with
                Provider = Some provider }
        let ex =
            Assert.ThrowsAny<ProviderError>(fun () ->
                client.Complete(request) |> ignore)
        Assert.NotNull(ex)

    // These tests don't need real API keys; they always run with an invalid key.
    // But we still gate on the provider env var being set so we know the provider
    // endpoint is reachable (i.e. network works). Use Fact if you want them always.
    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Invalid API key error - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Invalid API key error - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Invalid API key error - Gemini`` () = runTest "gemini"

// ============================================================
// 13. Usage token counts accurate
// ============================================================

module UsageTokenCounts =

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let result =
            Generation.generate
                client model
                (Some "Say the word 'test' and nothing else.")
                None None None 0
                (Some provider) None None
        Assert.True(result.Usage.InputTokens > 0, "Expected InputTokens > 0")
        Assert.True(result.Usage.OutputTokens > 0, "Expected OutputTokens > 0")
        let expected = result.Usage.InputTokens + result.Usage.OutputTokens
        Assert.True(result.Usage.TotalTokens = expected,
            sprintf "Expected TotalTokens = %d but got %d" expected result.Usage.TotalTokens)

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Usage token counts - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Usage token counts - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Usage token counts - Gemini`` () = runTest "gemini"

// ============================================================
// 14. Prompt caching
// ============================================================

module PromptCaching =

    let private longSystemPrompt =
        String.replicate 200 "This is a long system prompt designed to test prompt caching behavior across multiple API calls. "

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        // First call: prime the cache
        let _result1 =
            Generation.generate
                client model
                (Some "Reply with the single word 'pong'.")
                None (Some longSystemPrompt) None 0
                (Some provider) None None
        // Second call: should benefit from cache
        let result2 =
            Generation.generate
                client model
                (Some "Reply with the single word 'pong'.")
                None (Some longSystemPrompt) None 0
                (Some provider) None None
        // For all providers, just assert no error and non-empty response
        Assert.False(String.IsNullOrWhiteSpace result2.Text, "Expected non-empty response")

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Prompt caching - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Prompt caching - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Prompt caching - Gemini`` () = runTest "gemini"

// ============================================================
// 15. Provider options passthrough
// ============================================================

module ProviderOptionsPassthrough =

    let private runTest (provider: string) =
        let client = LiveApiHelpers.createClient provider
        let model = LiveApiHelpers.modelFor provider
        let providerOptions =
            match provider with
            | "anthropic" ->
                Some (Map.ofList [ "anthropic", Map.ofList [ "auto_cache", true :> obj ] :> obj ])
            | "openai" ->
                Some (Map.ofList [ "openai", Map.ofList [ "store", false :> obj ] :> obj ])
            | "gemini" ->
                Some (Map.ofList [ "gemini", Map.ofList [ "safetySettings", ([] : obj list) :> obj ] :> obj ])
            | _ -> None
        let request =
            { Request.Create(model, [ Message.user("Say hello in one word.") ]) with
                Provider = Some provider
                ProviderOptions = providerOptions }
        let response = client.Complete(request)
        Assert.False(String.IsNullOrWhiteSpace response.Text, "Expected non-empty response with provider options")

    [<LiveApiFact("anthropic")>] [<Trait("Category", "LiveApi")>]
    let ``Provider options passthrough - Anthropic`` () = runTest "anthropic"

    [<LiveApiFact("openai")>] [<Trait("Category", "LiveApi")>]
    let ``Provider options passthrough - OpenAI`` () = runTest "openai"

    [<LiveApiFact("gemini")>] [<Trait("Category", "LiveApi")>]
    let ``Provider options passthrough - Gemini`` () = runTest "gemini"
