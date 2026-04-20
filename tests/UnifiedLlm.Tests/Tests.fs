module UnifiedLlm.Tests

open System
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open UnifiedLlm

// ============================================================
// 8.1 Core Infrastructure Tests
// ============================================================

module CoreInfrastructure =

    [<Fact>]
    let ``Client can be constructed programmatically with explicit adapters`` () =
        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.RegisterAdapter(MockAnthropicAdapter())

        let request =
            { Request.Create("gpt-5.2", [ Message.user ("test") ]) with
                Provider = Some "openai" }

        let response = client.Complete(request)
        Assert.Equal("openai", response.Provider)

    [<Fact>]
    let ``Client.FromEnv creates client from environment`` () =
        let client = Client.FromEnv()
        Assert.NotNull(client)

    [<Fact>]
    let ``Provider routing dispatches to correct adapter based on provider field`` () =
        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.RegisterAdapter(MockAnthropicAdapter())

        let req1 =
            { Request.Create("m", [ Message.user ("hi") ]) with
                Provider = Some "openai" }

        let req2 =
            { Request.Create("m", [ Message.user ("hi") ]) with
                Provider = Some "anthropic" }

        Assert.Equal("openai", client.Complete(req1).Provider)
        Assert.Equal("anthropic", client.Complete(req2).Provider)

    [<Fact>]
    let ``Default provider is used when provider is omitted`` () =
        let client = Client()
        client.RegisterAdapter(MockAnthropicAdapter())
        let request = Request.Create("m", [ Message.user ("test") ])
        let response = client.Complete(request)
        Assert.Equal("anthropic", response.Provider)

    [<Fact>]
    let ``First registered provider becomes default`` () =
        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.RegisterAdapter(MockAnthropicAdapter())
        Assert.Equal(Some "openai", client.DefaultProvider)

    [<Fact>]
    let ``ConfigurationError raised when no provider configured and no default`` () =
        let client = Client()
        let request = Request.Create("m", [ Message.user ("test") ])
        Assert.Throws<ConfigurationError>(fun () -> client.Complete(request) |> ignore)

    [<Fact>]
    let ``Middleware chain executes in correct order`` () =
        let mutable order = []

        let mw1 =
            { new IMiddleware with
                member _.Process(req, next) =
                    order <- order @ [ "mw1-request" ]
                    let resp = next req
                    order <- order @ [ "mw1-response" ]
                    resp

                member _.ProcessStream(req, next) = next req }

        let mw2 =
            { new IMiddleware with
                member _.Process(req, next) =
                    order <- order @ [ "mw2-request" ]
                    let resp = next req
                    order <- order @ [ "mw2-response" ]
                    resp

                member _.ProcessStream(req, next) = next req }

        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.AddMiddleware(mw1)
        client.AddMiddleware(mw2)
        let request = Request.Create("m", [ Message.user ("test") ])
        client.Complete(request) |> ignore
        Assert.Equal<string list>([ "mw1-request"; "mw2-request"; "mw2-response"; "mw1-response" ], order)

    [<Fact>]
    let ``Module-level default client works`` () =
        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        DefaultClient.setDefaultClient client
        let retrieved = DefaultClient.getDefaultClient ()
        Assert.NotNull(retrieved)

    [<Fact>]
    let ``Model catalog is populated with current models`` () =
        let models = ModelCatalog.listModels ()
        Assert.True(models.Length >= 7)

    [<Fact>]
    let ``get_model_info returns correct data for known models`` () =
        let info = ModelCatalog.getModelInfo "claude-opus-4-6"
        Assert.True(info.IsSome)
        Assert.Equal("anthropic", info.Value.Provider)
        Assert.Equal("Claude Opus 4.6", info.Value.DisplayName)
        Assert.Equal(200000, info.Value.ContextWindow)

    [<Fact>]
    let ``get_model_info returns None for unknown models`` () =
        let info = ModelCatalog.getModelInfo "nonexistent-model"
        Assert.True(info.IsNone)

    [<Fact>]
    let ``list_models_by_provider returns correct subset`` () =
        let anthropicModels = ModelCatalog.listModelsByProvider "anthropic"
        Assert.Equal(8, anthropicModels.Length)
        Assert.True(anthropicModels |> List.forall (fun m -> m.Provider = "anthropic"))
        let openaiModels = ModelCatalog.listModelsByProvider "openai"
        Assert.Equal(9, openaiModels.Length)

// ============================================================
// 8.2 Provider Adapters Tests
// ============================================================

module ProviderAdapters =

    [<Fact>]
    let ``OpenAI role translation is correct`` () =
        Assert.Equal("system", RoleTranslation.toOpenAI System)
        Assert.Equal("user", RoleTranslation.toOpenAI User)
        Assert.Equal("assistant", RoleTranslation.toOpenAI Assistant)
        Assert.Equal("tool", RoleTranslation.toOpenAI Tool)
        Assert.Equal("developer", RoleTranslation.toOpenAI Developer)

    [<Fact>]
    let ``Anthropic role translation is correct`` () =
        Assert.Equal("system", RoleTranslation.toAnthropic System)
        Assert.Equal("user", RoleTranslation.toAnthropic User)
        Assert.Equal("assistant", RoleTranslation.toAnthropic Assistant)
        Assert.Equal("user", RoleTranslation.toAnthropic Tool)
        Assert.Equal("system", RoleTranslation.toAnthropic Developer)

    [<Fact>]
    let ``Gemini role translation is correct`` () =
        Assert.Equal("system", RoleTranslation.toGemini System)
        Assert.Equal("user", RoleTranslation.toGemini User)
        Assert.Equal("model", RoleTranslation.toGemini Assistant)
        Assert.Equal("user", RoleTranslation.toGemini Tool)
        Assert.Equal("system", RoleTranslation.toGemini Developer)

    [<Fact>]
    let ``Mock OpenAI adapter complete returns response`` () =
        let adapter = MockOpenAIAdapter() :> IProviderAdapter
        let request = Request.Create("gpt-5.2", [ Message.user ("test") ])
        let response = adapter.Complete(request)
        Assert.Equal("openai", response.Provider)
        Assert.True(response.Text.Length > 0)
        Assert.Equal(Stop "stop", response.FinishReason)

    [<Fact>]
    let ``Mock Anthropic adapter complete returns response`` () =
        let adapter = MockAnthropicAdapter() :> IProviderAdapter
        let request = Request.Create("claude-opus-4-6", [ Message.user ("test") ])
        let response = adapter.Complete(request)
        Assert.Equal("anthropic", response.Provider)

    [<Fact>]
    let ``Mock Gemini adapter complete returns response`` () =
        let adapter = MockGeminiAdapter() :> IProviderAdapter
        let request = Request.Create("gemini-3.1-pro-preview", [ Message.user ("test") ])
        let response = adapter.Complete(request)
        Assert.Equal("gemini", response.Provider)

    [<Fact>]
    let ``Configurable mock adapter allows custom response`` () =
        let mock = ConfigurableMockAdapter("test-provider")

        mock.SetCompleteHandler(fun req ->
            { Id = "custom"
              Model = req.Model
              Provider = "test-provider"
              Message = Message.assistant ("custom response")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let adapter = mock :> IProviderAdapter
        let response = adapter.Complete(Request.Create("m", []))
        Assert.Equal("custom response", response.Text)

    [<Fact>]
    let ``Provider adapters set correct provider IDs`` () =
        Assert.Equal("openai", (MockOpenAIAdapter() :> IProviderAdapter).ProviderId)
        Assert.Equal("anthropic", (MockAnthropicAdapter() :> IProviderAdapter).ProviderId)
        Assert.Equal("gemini", (MockGeminiAdapter() :> IProviderAdapter).ProviderId)

// ============================================================
// 8.3 Message & Content Model Tests
// ============================================================

module MessageContentModel =

    [<Fact>]
    let ``Text-only message works`` () =
        let msg = Message.user ("Hello world")
        Assert.Equal("Hello world", msg.Text)
        Assert.Equal(User, msg.Role)

    [<Fact>]
    let ``System message convenience constructor`` () =
        let msg = Message.system ("You are helpful")
        Assert.Equal(System, msg.Role)
        Assert.Equal("You are helpful", msg.Text)

    [<Fact>]
    let ``Assistant message convenience constructor`` () =
        let msg = Message.assistant ("The answer is 42")
        Assert.Equal(Assistant, msg.Role)
        Assert.Equal("The answer is 42", msg.Text)

    [<Fact>]
    let ``Tool result message links correctly`` () =
        let msg = Message.toolResult ("call_123", "72F and sunny", false)
        Assert.Equal(Tool, msg.Role)
        Assert.Equal(Some "call_123", msg.ToolCallId)

    [<Fact>]
    let ``Image content part creation`` () =
        let imgData =
            { Url = Some "https://example.com/photo.jpg"
              Data = None
              FilePath = None
              MediaType = Some "image/jpeg" }

        let msg =
            { Role = User
              Content = [ Text "What is this?"; Image imgData ]
              Name = None
              ToolCallId = None }

        Assert.Equal(2, msg.Content.Length)
        Assert.Equal("What is this?", msg.Text)

    [<Fact>]
    let ``Tool call content part round-trip`` () =
        let tc =
            { Id = "call_1"
              Name = "get_weather"
              Arguments = """{"city":"SF"}"""
              Metadata = Map.empty }

        let assistantMsg =
            { Role = Assistant
              Content = [ ToolCall tc ]
              Name = None
              ToolCallId = None }

        let resultMsg = Message.toolResult ("call_1", "72F", false)

        match assistantMsg.Content with
        | [ ToolCall data ] ->
            Assert.Equal("call_1", data.Id)
            Assert.Equal("get_weather", data.Name)
        | _ -> Assert.Fail("Expected ToolCall content part")

        Assert.Equal(Some "call_1", resultMsg.ToolCallId)

    [<Fact>]
    let ``Thinking content part preserves signature`` () =
        let thinking =
            { Text = "Let me work through this..."
              Signature = Some "sig_abc123"
              Redacted = false }

        let msg =
            { Role = Assistant
              Content = [ Thinking thinking; Text "The answer is 42." ]
              Name = None
              ToolCallId = None }

        match msg.Content with
        | [ Thinking td; Text _ ] ->
            Assert.Equal("sig_abc123", td.Signature.Value)
            Assert.False(td.Redacted)
        | _ -> Assert.Fail("Expected Thinking and Text parts")

    [<Fact>]
    let ``Redacted thinking blocks pass through`` () =
        let thinking =
            { Text = "opaque-data"
              Signature = None
              Redacted = true }

        let msg =
            { Role = Assistant
              Content = [ Thinking thinking ]
              Name = None
              ToolCallId = None }

        match msg.Content.[0] with
        | Thinking td -> Assert.True(td.Redacted)
        | _ -> Assert.Fail("Expected Thinking")

    [<Fact>]
    let ``Multimodal message text + image`` () =
        let imgData =
            { Url = None
              Data = Some [| 0x89uy; 0x50uy |]
              FilePath = None
              MediaType = Some "image/png" }

        let msg =
            { Role = User
              Content = [ Text "Describe this:"; Image imgData ]
              Name = None
              ToolCallId = None }

        Assert.Equal("Describe this:", msg.Text)
        Assert.Equal(2, msg.Content.Length)

// ============================================================
// 8.4 Generation Tests
// ============================================================

module GenerationTests =

    let private makeClient () =
        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client

    [<Fact>]
    let ``generate works with simple text prompt`` () =
        let client = makeClient ()

        let result =
            Generation.generate client "gpt-5.2" (Some "Hello") None None None 0 (Some "openai") None None

        Assert.True(result.Text.Length > 0)
        Assert.Equal(Stop "stop", result.FinishReason)

    [<Fact>]
    let ``generate works with full messages list`` () =
        let client = makeClient ()
        let msgs = [ Message.user ("What is 2+2?") ]

        let result =
            Generation.generate client "gpt-5.2" None (Some msgs) None None 0 (Some "openai") None None

        Assert.True(result.Text.Length > 0)

    [<Fact>]
    let ``generate rejects when both prompt and messages provided`` () =
        let client = makeClient ()
        let msgs = [ Message.user ("hi") ]

        Assert.Throws<ValidationError>(fun () ->
            Generation.generate client "m" (Some "hi") (Some msgs) None None 0 (Some "openai") None None
            |> ignore)

    [<Fact>]
    let ``generate rejects when neither prompt nor messages provided`` () =
        let client = makeClient ()

        Assert.Throws<ValidationError>(fun () ->
            Generation.generate client "m" None None None None 0 (Some "openai") None None
            |> ignore)

    [<Fact>]
    let ``stream yields TextDelta events`` () =
        let client = makeClient ()

        let events =
            Generation.stream client "gpt-5.2" (Some "Hello") None None (Some "openai")
            |> Seq.toList

        let textDeltas =
            events
            |> List.choose (fun e ->
                match e with
                | TextDelta(_, t) -> Some t
                | _ -> None)

        Assert.True(textDeltas.Length > 0)

    [<Fact>]
    let ``stream yields StreamStart and Finish events`` () =
        let client = makeClient ()

        let events =
            Generation.stream client "gpt-5.2" (Some "Hello") None None (Some "openai")
            |> Seq.toList

        Assert.True(
            events
            |> List.exists (fun e ->
                match e with
                | StreamStart -> true
                | _ -> false)
        )

        Assert.True(
            events
            |> List.exists (fun e ->
                match e with
                | Finish _ -> true
                | _ -> false)
        )

    [<Fact>]
    let ``generate with system message`` () =
        let client = makeClient ()

        let result =
            Generation.generate
                client
                "gpt-5.2"
                (Some "hi")
                None
                (Some "You are helpful")
                None
                0
                (Some "openai")
                None
                None

        Assert.True(result.Text.Length > 0)

    [<Fact>]
    let ``generate_object raises NoObjectGeneratedError on invalid output`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetCompleteHandler(fun _req ->
            { Id = "x"
              Model = "m"
              Provider = "test"
              Message = Message.assistant ("not json at all")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        Assert.Throws<NoObjectGeneratedError>(fun () ->
            Generation.generateObject client "m" "Extract info" """{"type":"object"}""" (Some "test")
            |> ignore)

    [<Fact>]
    let ``generate_object works with JSON response`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetCompleteHandler(fun _req ->
            { Id = "x"
              Model = "m"
              Provider = "test"
              Message = Message.assistant ("""{"name":"Alice","age":30}""")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let result =
            Generation.generateObject client "m" "Extract" """{"type":"object"}""" (Some "test")

        Assert.Contains("Alice", result.Text)

    [<Fact>]
    let ``generateObjectWithControl extracts JSON from tool_call arguments when text body is empty`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetCompleteHandler(fun _ ->
            let toolCall =
                { Id = "call_1"
                  Name = "generated_object"
                  Arguments = """{"name":"Alice","age":30}"""
                  Metadata = Map.empty }

            { Id = "r-tool"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall toolCall ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let result =
            Generation.generateObjectWithControl
                client
                "m"
                "Extract user"
                """{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}},"required":["name","age"]}"""
                (Some "test")
                None

        Assert.Equal("""{"name":"Alice","age":30}""", result.Text)
        Assert.Equal("""{"name":"Alice","age":30}""", result.Response.Text)

    [<Fact>]
    let ``generate tracks usage`` () =
        let client = makeClient ()

        let result =
            Generation.generate client "gpt-5.2" (Some "Hello") None None None 0 (Some "openai") None None

        Assert.True(result.Usage.InputTokens >= 0)
        Assert.True(result.TotalUsage.InputTokens >= 0)

// ============================================================
// 8.7 Tool Calling Tests
// ============================================================

module ToolCallingTests =

    let private makeToolCallAdapter () =
        let mock = ConfigurableMockAdapter("test")
        let mutable callCount = 0

        mock.SetCompleteHandler(fun _req ->
            callCount <- callCount + 1

            if callCount = 1 then
                let tc =
                    { Id = "call_1"
                      Name = "get_weather"
                      Arguments = """{"city":"SF"}"""
                      Metadata = Map.empty }

                { Id = "r1"
                  Model = "m"
                  Provider = "test"
                  Message =
                    { Role = Assistant
                      Content = [ ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = ToolCalls "tool_calls"
                  Usage =
                    { InputTokens = 10
                      OutputTokens = 5
                      ReasoningTokens = None
                      CacheReadTokens = None
                      CacheWriteTokens = None }
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None }
            else
                { Id = "r2"
                  Model = "m"
                  Provider = "test"
                  Message = Message.assistant ("The weather in SF is 72F")
                  FinishReason = Stop "stop"
                  Usage =
                    { InputTokens = 20
                      OutputTokens = 10
                      ReasoningTokens = None
                      CacheReadTokens = None
                      CacheWriteTokens = None }
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None })

        mock

    [<Fact>]
    let ``Active tools trigger automatic tool execution loops`` () =
        let mock = makeToolCallAdapter ()
        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "get_weather"
                  Description = "Get weather"
                  Parameters = """{"type":"object"}""" }
              Execute = Some(fun _args -> "72F and sunny") }

        let result =
            Generation.generate client "m" (Some "Weather in SF?") None None (Some [ tool ]) 5 (Some "test") None None

        Assert.Equal("The weather in SF is 72F", result.Text)
        Assert.Equal(2, result.Steps.Length)

    [<Fact>]
    let ``Passive tools return tool calls without looping`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetCompleteHandler(fun _req ->
            let tc =
                { Id = "call_1"
                  Name = "get_weather"
                  Arguments = """{"city":"SF"}"""
                  Metadata = Map.empty }

            { Id = "r1"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "get_weather"
                  Description = "Get weather"
                  Parameters = """{"type":"object"}""" }
              Execute = None }

        let result =
            Generation.generate client "m" (Some "Weather?") None None (Some [ tool ]) 5 (Some "test") None None

        Assert.True(result.ToolCalls.Length > 0)
        Assert.Equal(1, result.Steps.Length)

    [<Fact>]
    let ``max_tool_rounds is respected`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetCompleteHandler(fun _req ->
            let tc =
                { Id = "call_1"
                  Name = "get_weather"
                  Arguments = """{}"""
                  Metadata = Map.empty }

            { Id = "r"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "get_weather"
                  Description = ""
                  Parameters = """{"type":"object"}""" }
              Execute = Some(fun _ -> "result") }

        let result =
            Generation.generate client "m" (Some "hi") None None (Some [ tool ]) 2 (Some "test") None None

        Assert.True(result.Steps.Length <= 3)

    [<Fact>]
    let ``max_tool_rounds 0 disables automatic execution`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetCompleteHandler(fun _req ->
            let tc =
                { Id = "call_1"
                  Name = "get_weather"
                  Arguments = """{}"""
                  Metadata = Map.empty }

            { Id = "r"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "get_weather"
                  Description = ""
                  Parameters = """{"type":"object"}""" }
              Execute = Some(fun _ -> "result") }

        let result =
            Generation.generate client "m" (Some "hi") None None (Some [ tool ]) 0 (Some "test") None None

        Assert.Equal(1, result.Steps.Length)

    [<Fact>]
    let ``Tool execution errors are sent as error results not exceptions`` () =
        let mock = makeToolCallAdapter ()
        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "get_weather"
                  Description = "Get weather"
                  Parameters = """{"type":"object"}""" }
              Execute = Some(fun _args -> failwith "API is down") }

        let result =
            Generation.generate client "m" (Some "Weather?") None None (Some [ tool ]) 5 (Some "test") None None

        Assert.Equal(2, result.Steps.Length)
        let firstStep = result.Steps.[0]
        Assert.True(firstStep.ToolResults |> List.exists (fun r -> r.IsError))

    [<Fact>]
    let ``Unknown tool calls send error result not exception`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable callCount = 0

        mock.SetCompleteHandler(fun _req ->
            callCount <- callCount + 1

            if callCount = 1 then
                let tc =
                    { Id = "call_1"
                      Name = "nonexistent_tool"
                      Arguments = """{}"""
                      Metadata = Map.empty }

                { Id = "r1"
                  Model = "m"
                  Provider = "test"
                  Message =
                    { Role = Assistant
                      Content = [ ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = ToolCalls "tool_calls"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None }
            else
                { Id = "r2"
                  Model = "m"
                  Provider = "test"
                  Message = Message.assistant ("OK")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "real_tool"
                  Description = ""
                  Parameters = """{"type":"object"}""" }
              Execute = Some(fun _ -> "ok") }

        let result =
            Generation.generate client "m" (Some "hi") None None (Some [ tool ]) 5 (Some "test") None None

        let firstStep = result.Steps.[0]

        Assert.True(
            firstStep.ToolResults
            |> List.exists (fun r -> r.IsError && r.Content.Contains("Unknown tool"))
        )

    [<Fact>]
    let ``Parallel tool calls all executed`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable callCount = 0

        mock.SetCompleteHandler(fun _req ->
            callCount <- callCount + 1

            if callCount = 1 then
                let tc1 =
                    { Id = "call_1"
                      Name = "get_weather"
                      Arguments = """{"city":"SF"}"""
                      Metadata = Map.empty }

                let tc2 =
                    { Id = "call_2"
                      Name = "get_weather"
                      Arguments = """{"city":"NY"}"""
                      Metadata = Map.empty }

                { Id = "r1"
                  Model = "m"
                  Provider = "test"
                  Message =
                    { Role = Assistant
                      Content = [ ToolCall tc1; ToolCall tc2 ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = ToolCalls "tool_calls"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None }
            else
                { Id = "r2"
                  Model = "m"
                  Provider = "test"
                  Message = Message.assistant ("Both cities checked")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "get_weather"
                  Description = ""
                  Parameters = """{"type":"object"}""" }
              Execute = Some(fun args -> sprintf "72F in %s" args) }

        let result =
            Generation.generate client "m" (Some "Weather?") None None (Some [ tool ]) 5 (Some "test") None None

        Assert.Equal(2, result.Steps.[0].ToolResults.Length)

    [<Fact>]
    let ``StepResult tracks each step tool calls results and usage`` () =
        let mock = makeToolCallAdapter ()
        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "get_weather"
                  Description = ""
                  Parameters = """{"type":"object"}""" }
              Execute = Some(fun _ -> "sunny") }

        let result =
            Generation.generate client "m" (Some "hi") None None (Some [ tool ]) 5 (Some "test") None None

        Assert.Equal(2, result.Steps.Length)
        let step1 = result.Steps.[0]
        Assert.Equal(1, step1.ToolCalls.Length)
        Assert.Equal(1, step1.ToolResults.Length)
        Assert.True(step1.Usage.InputTokens > 0)

    [<Fact>]
    let ``ToolChoice modes are available`` () =
        let auto = ToolChoice.Auto
        let none = ToolChoice.None
        let required = ToolChoice.Required
        let named = ToolChoice.Named "get_weather"
        Assert.NotNull(box auto)
        Assert.NotNull(box none)
        Assert.NotNull(box required)
        Assert.NotNull(box named)

// ============================================================
// 8.8 Error Handling & Retry Tests
// ============================================================

module ErrorHandlingTests =

    [<Fact>]
    let ``HTTP 401 maps to AuthenticationError`` () =
        let err = ErrorMapping.fromStatusCode 401 "Unauthorized" None
        Assert.IsType<AuthenticationError>(err) |> ignore
        Assert.False(err.Retryable)

    [<Fact>]
    let ``HTTP 403 maps to AccessDeniedError`` () =
        let err = ErrorMapping.fromStatusCode 403 "Forbidden" None
        Assert.IsType<AccessDeniedError>(err) |> ignore
        Assert.False(err.Retryable)

    [<Fact>]
    let ``HTTP 404 maps to NotFoundError`` () =
        let err = ErrorMapping.fromStatusCode 404 "Not Found" None
        Assert.IsType<NotFoundError>(err) |> ignore
        Assert.False(err.Retryable)

    [<Fact>]
    let ``HTTP 429 maps to RateLimitError with retryable=true`` () =
        let err = ErrorMapping.fromStatusCode 429 "Rate limited" (Some 5.0)
        Assert.IsType<RateLimitError>(err) |> ignore
        Assert.True(err.Retryable)

    [<Fact>]
    let ``HTTP 500 maps to ServerError with retryable=true`` () =
        let err = ErrorMapping.fromStatusCode 500 "Internal error" None
        Assert.IsType<ServerError>(err) |> ignore
        Assert.True(err.Retryable)

    [<Fact>]
    let ``HTTP 502 maps to ServerError`` () =
        let err = ErrorMapping.fromStatusCode 502 "Bad gateway" None
        Assert.IsType<ServerError>(err) |> ignore
        Assert.True(err.Retryable)

    [<Fact>]
    let ``HTTP 503 maps to ServerError`` () =
        let err = ErrorMapping.fromStatusCode 503 "Unavailable" None
        Assert.IsType<ServerError>(err) |> ignore

    [<Fact>]
    let ``HTTP 408 maps to TimeoutError`` () =
        let err = ErrorMapping.fromStatusCode 408 "Timeout" None
        Assert.IsType<TimeoutError>(err) |> ignore
        Assert.True(err.Retryable)

    [<Fact>]
    let ``Non-retryable errors 400 401 403 404`` () =
        for code in [ 400; 401; 403; 404 ] do
            let err = ErrorMapping.fromStatusCode code "test" None
            Assert.False(err.Retryable, sprintf "Status %d should not be retryable" code)

    [<Fact>]
    let ``Retryable errors 429 500 502 503 504`` () =
        for code in [ 429; 500; 502; 503; 504 ] do
            let err = ErrorMapping.fromStatusCode code "test" None
            Assert.True(err.Retryable, sprintf "Status %d should be retryable" code)

    [<Fact>]
    let ``Unknown errors default to retryable`` () =
        let err = ErrorMapping.fromStatusCode 999 "Unknown" None
        Assert.True(err.Retryable)

    [<Fact>]
    let ``Message-based classification not found`` () =
        let err = ErrorMapping.classifyByMessage "Resource not found" 400
        Assert.IsType<NotFoundError>(err) |> ignore

    [<Fact>]
    let ``Message-based classification unauthorized`` () =
        let err = ErrorMapping.classifyByMessage "unauthorized access" 400
        Assert.IsType<AuthenticationError>(err) |> ignore

    [<Fact>]
    let ``Message-based classification content filter`` () =
        let err = ErrorMapping.classifyByMessage "content filter triggered" 400
        Assert.IsType<ContentFilterError>(err) |> ignore

    [<Fact>]
    let ``Exponential backoff calculates correctly`` () =
        let config =
            { RetryConfig.Default with
                Jitter = false }

        let d0 = Retry.calculateDelay config 0
        let d1 = Retry.calculateDelay config 1
        let d2 = Retry.calculateDelay config 2
        Assert.Equal(1000, d0)
        Assert.Equal(2000, d1)
        Assert.Equal(4000, d2)

    [<Fact>]
    let ``Backoff respects max delay`` () =
        let config =
            { RetryConfig.Default with
                Jitter = false
                MaxDelayMs = 3000 }

        let d5 = Retry.calculateDelay config 5
        Assert.Equal(3000, d5)

    [<Fact>]
    let ``Jitter adds variance`` () =
        let config =
            { RetryConfig.Default with
                Jitter = true }

        let delays = [ for _ in 1..20 -> Retry.calculateDelay config 0 ]
        let distinct = delays |> List.distinct
        Assert.True(distinct.Length > 1)

    [<Fact>]
    let ``Retry-After overrides calculated backoff`` () =
        let config = RetryConfig.Default
        let delay = Retry.effectiveDelay config 0 (Some 3.0)
        Assert.Equal(Some 3000, delay)

    [<Fact>]
    let ``Retry-After exceeding max_delay returns None`` () =
        let config =
            { RetryConfig.Default with
                MaxDelayMs = 2000 }

        let delay = Retry.effectiveDelay config 0 (Some 5.0)
        Assert.True(delay.IsNone)

    [<Fact>]
    let ``max_retries 0 disables retries`` () =
        let config =
            { RetryConfig.Default with
                MaxRetries = 0 }

        let mutable attempts = 0

        Assert.Throws<ServerError>(fun () ->
            Retry.execute config (fun () ->
                attempts <- attempts + 1
                raise (ServerError("fail", 500))
                "never")
            |> ignore)
        |> ignore

        Assert.Equal(1, attempts)

    [<Fact>]
    let ``Retries per step not entire operation`` () =
        let config =
            { RetryConfig.Default with
                MaxRetries = 2 }

        let mutable attempts = 0

        let result =
            Retry.execute config (fun () ->
                attempts <- attempts + 1

                if attempts < 3 then
                    raise (ServerError("transient", 500))

                "success")

        Assert.Equal("success", result)
        Assert.Equal(3, attempts)

    [<Fact>]
    let ``Non-retryable errors are not retried`` () =
        let config =
            { RetryConfig.Default with
                MaxRetries = 3 }

        let mutable attempts = 0

        Assert.Throws<AuthenticationError>(fun () ->
            Retry.execute config (fun () ->
                attempts <- attempts + 1
                raise (AuthenticationError("bad key"))
                "never")
            |> ignore)
        |> ignore

        Assert.Equal(1, attempts)

// ============================================================
// Usage Tests
// ============================================================

module UsageTests =

    [<Fact>]
    let ``Usage addition sums integer fields`` () =
        let a =
            { InputTokens = 10
              OutputTokens = 5
              ReasoningTokens = Some 2
              CacheReadTokens = None
              CacheWriteTokens = Some 1 }

        let b =
            { InputTokens = 20
              OutputTokens = 10
              ReasoningTokens = Some 3
              CacheReadTokens = Some 5
              CacheWriteTokens = None }

        let sum = a + b
        Assert.Equal(30, sum.InputTokens)
        Assert.Equal(15, sum.OutputTokens)
        Assert.Equal(Some 5, sum.ReasoningTokens)
        Assert.Equal(Some 5, sum.CacheReadTokens)
        Assert.Equal(Some 1, sum.CacheWriteTokens)

    [<Fact>]
    let ``Usage.Zero has all zeros`` () =
        let z = Usage.Zero
        Assert.Equal(0, z.InputTokens)
        Assert.Equal(0, z.OutputTokens)
        Assert.True(z.ReasoningTokens.IsNone)

    [<Fact>]
    let ``Usage TotalTokens is input + output`` () =
        let u =
            { InputTokens = 10
              OutputTokens = 5
              ReasoningTokens = None
              CacheReadTokens = None
              CacheWriteTokens = None }

        Assert.Equal(15, u.TotalTokens)

    [<Fact>]
    let ``Usage addition both None stays None`` () =
        let a =
            { Usage.Zero with
                CacheReadTokens = None }

        let b =
            { Usage.Zero with
                CacheReadTokens = None }

        Assert.True((a + b).CacheReadTokens.IsNone)

// ============================================================
// Tool Registry Tests
// ============================================================

module ToolRegistryTests =

    [<Fact>]
    let ``ToolRegistry register and resolve`` () =
        let registry = ToolRegistry()

        let tool =
            { Definition =
                { Name = "test"
                  Description = "A test"
                  Parameters = "{}" }
              Execute = None }

        registry.Register(tool)
        let resolved = registry.Resolve("test")
        Assert.True(resolved.IsSome)
        Assert.Equal("test", resolved.Value.Definition.Name)

    [<Fact>]
    let ``ToolRegistry resolve returns None for unknown`` () =
        let registry = ToolRegistry()
        Assert.True((registry.Resolve("unknown")).IsNone)

    [<Fact>]
    let ``ToolRegistry list and names`` () =
        let registry = ToolRegistry()

        registry.Register(
            { Definition =
                { Name = "a"
                  Description = ""
                  Parameters = "{}" }
              Execute = None }
        )

        registry.Register(
            { Definition =
                { Name = "b"
                  Description = ""
                  Parameters = "{}" }
              Execute = None }
        )

        Assert.Equal(2, registry.List().Length)
        Assert.Contains("a", registry.Names())
        Assert.Contains("b", registry.Names())

    [<Fact>]
    let ``ToolRegistry unregister`` () =
        let registry = ToolRegistry()

        registry.Register(
            { Definition =
                { Name = "x"
                  Description = ""
                  Parameters = "{}" }
              Execute = None }
        )

        Assert.True(registry.Unregister("x"))
        Assert.True((registry.Resolve("x")).IsNone)

// ============================================================
// Middleware Tests
// ============================================================

module MiddlewareTests =

    [<Fact>]
    let ``Middleware can modify request`` () =
        let mw =
            { new IMiddleware with
                member _.Process(req, next) =
                    let modified = { req with Model = "modified-model" }
                    next modified

                member _.ProcessStream(req, next) =
                    let modified = { req with Model = "modified-model" }
                    next modified }

        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.AddMiddleware(mw)
        let request = Request.Create("original", [ Message.user ("test") ])
        let response = client.Complete(request)
        Assert.Equal("modified-model", response.Model)

    [<Fact>]
    let ``Middleware can inspect response`` () =
        let mutable capturedProvider = ""

        let mw =
            { new IMiddleware with
                member _.Process(req, next) =
                    let resp = next req
                    capturedProvider <- resp.Provider
                    resp

                member _.ProcessStream(req, next) = next req }

        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.AddMiddleware(mw)
        client.Complete(Request.Create("m", [ Message.user ("hi") ])) |> ignore
        Assert.Equal("openai", capturedProvider)

    [<Fact>]
    let ``Empty middleware chain passes through`` () =
        let chain = MiddlewareChain()

        let handler (_req: Request) : Response =
            { Id = "test"
              Model = "m"
              Provider = "p"
              Message = Message.assistant ("ok")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }

        let result = chain.Execute(Request.Create("m", []), handler)
        Assert.Equal("ok", result.Text)

// ============================================================
// Stream Tests
// ============================================================

module StreamTests =

    [<Fact>]
    let ``Mock adapter stream yields events in correct order`` () =
        let adapter = MockOpenAIAdapter() :> IProviderAdapter
        let events = adapter.Stream(Request.Create("m", [])) |> Seq.toList

        match events.[0] with
        | StreamStart -> ()
        | _ -> Assert.Fail("Expected StreamStart")

        let last = events |> List.last

        match last with
        | Finish _ -> ()
        | _ -> Assert.Fail("Expected Finish")

    [<Fact>]
    let ``Stream text deltas can be concatenated`` () =
        let adapter = MockOpenAIAdapter() :> IProviderAdapter
        let events = adapter.Stream(Request.Create("m", [])) |> Seq.toList

        let text =
            events
            |> List.choose (fun e ->
                match e with
                | TextDelta(_, t) -> Some t
                | _ -> None)
            |> String.concat ""

        Assert.Equal("Mock OpenAI stream", text)

    [<Fact>]
    let ``Configurable mock supports custom stream`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetStreamHandler(fun _ ->
            seq {
                yield StreamStart
                yield TextDelta(Some "text-1", "custom")
                yield Finish(Stop "stop", Some Usage.Zero, None)
            })

        let events =
            (mock :> IProviderAdapter).Stream(Request.Create("m", [])) |> Seq.toList

        let text =
            events
            |> List.choose (fun e ->
                match e with
                | TextDelta(_, t) -> Some t
                | _ -> None)
            |> String.concat ""

        Assert.Equal("custom", text)

    [<Fact>]
    let ``streamWithControl emits StreamError when provider stream ends without Finish`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetStreamHandler(fun _ ->
            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", "partial")
                yield TextEnd "text-1"
            })

        let client = Client()
        client.RegisterAdapter(mock)

        let events =
            Generation.streamWithControl client "m" (Some "hello") None None None 0 (Some "test") None None None None
            |> Seq.toList

        Assert.Contains(
            events,
            fun event ->
                match event with
                | StreamError "Provider stream ended without a Finish event" -> true
                | _ -> false
        )

// ============================================================
// Response Accessor Tests
// ============================================================

module ResponseAccessorTests =

    [<Fact>]
    let ``Response.Text concatenates text parts`` () =
        let response =
            { Id = "r"
              Model = "m"
              Provider = "p"
              Message =
                { Role = Assistant
                  Content = [ Text "Hello "; Text "World" ]
                  Name = None
                  ToolCallId = None }
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }

        Assert.Equal("Hello World", response.Text)

    [<Fact>]
    let ``Response.ToolCalls extracts tool calls`` () =
        let tc =
            { Id = "c1"
              Name = "tool1"
              Arguments = "{}"
              Metadata = Map.empty }

        let response =
            { Id = "r"
              Model = "m"
              Provider = "p"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc; Text "also text" ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }

        Assert.Equal(1, response.ToolCalls.Length)
        Assert.Equal("tool1", response.ToolCalls.[0].Name)

    [<Fact>]
    let ``Response.Reasoning extracts thinking`` () =
        let thinking =
            { Text = "Let me think..."
              Signature = None
              Redacted = false }

        let response =
            { Id = "r"
              Model = "m"
              Provider = "p"
              Message =
                { Role = Assistant
                  Content = [ Thinking thinking; Text "42" ]
                  Name = None
                  ToolCallId = None }
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }

        Assert.Equal(Some "Let me think...", response.Reasoning)

    [<Fact>]
    let ``Response.Reasoning is None when no thinking`` () =
        let response =
            { Id = "r"
              Model = "m"
              Provider = "p"
              Message = Message.assistant ("just text")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }

        Assert.True(response.Reasoning.IsNone)

// ============================================================
// Sprint 001 Coverage Tests
// ============================================================

module Sprint001Coverage =

    let private mkResponse id provider finishReason message usage =
        { Id = id
          Model = "m"
          Provider = provider
          Message = message
          FinishReason = finishReason
          Usage = usage
          ResponseId = None
          Raw = None
          Warnings = []
          RateLimit = None }

    [<Fact>]
    let ``Request.Create initializes sprint fields`` () =
        let req = Request.Create("m", [ Message.user ("hi") ])
        Assert.True(req.Temperature.IsNone)
        Assert.True(req.TopP.IsNone)
        Assert.True(req.StopSequences.IsNone)
        Assert.True(req.ResponseFormat.IsNone)
        Assert.True(req.Metadata.IsNone)
        Assert.True(req.Timeout.IsNone)
        Assert.True(req.AbortSignal.IsNone)

    [<Fact>]
    let ``FinishReason preserves raw provider value`` () =
        let reason = Stop "end_turn"
        Assert.Equal("end_turn", reason.Raw)

    [<Fact>]
    let ``Parallel tool execution completes in near single-tool time`` () =
        // Five tools at 500ms each. Sequential would be ~2500ms; parallel is ~500ms
        // plus thread-pool scheduling overhead. A 2000ms ceiling is comfortably below
        // the serial floor (proves parallelism) while giving CI 4x headroom over
        // the nominal 500ms parallel time.
        let toolSleepMs = 500
        let toolCount = 5
        let ceilingMs = 2000L

        let tool =
            { Definition =
                { Name = "slow"
                  Description = ""
                  Parameters = """{"type":"object"}""" }
              Execute =
                Some(fun _ ->
                    System.Threading.Thread.Sleep(toolSleepMs)
                    "ok") }

        let calls =
            [ for i in 1..toolCount ->
                  { Id = $"call_{i}"
                    Name = "slow"
                    Arguments = "{}"
                    Metadata = Map.empty } ]

        let sw = System.Diagnostics.Stopwatch.StartNew()
        let results = Generation.executeAllTools [ tool ] calls
        sw.Stop()
        Assert.Equal(toolCount, results.Length)

        Assert.True(
            sw.ElapsedMilliseconds < ceilingMs,
            $"Expected parallel execution (< {ceilingMs}ms), got {sw.ElapsedMilliseconds}ms"
        )

    [<Fact>]
    let ``Tool argument schema validation sends error result`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable callCount = 0

        mock.SetCompleteHandler(fun _ ->
            callCount <- callCount + 1

            if callCount = 1 then
                let tc =
                    { Id = "call_1"
                      Name = "weather"
                      Arguments = "{}"
                      Metadata = Map.empty }

                mkResponse
                    "r1"
                    "test"
                    (ToolCalls "tool_calls")
                    { Role = Assistant
                      Content = [ ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                    Usage.Zero
            else
                mkResponse "r2" "test" (Stop "stop") (Message.assistant ("done")) Usage.Zero)

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "weather"
                  Description = ""
                  Parameters =
                    """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""" }
              Execute = Some(fun _ -> "ok") }

        let result =
            Generation.generate client "m" (Some "hi") None None (Some [ tool ]) 2 (Some "test") None None

        let first = result.Steps.[0]

        Assert.True(
            first.ToolResults
            |> List.exists (fun r -> r.IsError && r.Content.Contains("Missing required field"))
        )

    [<Fact>]
    let ``generateWithControl retries on transient errors`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable attempts = 0

        mock.SetCompleteHandler(fun _ ->
            attempts <- attempts + 1

            if attempts = 1 then
                raise (RateLimitError("rate limited"))

            mkResponse "r" "test" (Stop "stop") (Message.assistant ("ok")) Usage.Zero)

        let client = Client()
        client.RegisterAdapter(mock)

        let result =
            Generation.generateWithControl
                client
                "m"
                (Some "hi")
                None
                None
                None
                0
                (Some "test")
                None
                (Some 1)
                None
                None
                None
                None
                None

        Assert.Equal("ok", result.Text)
        Assert.Equal(2, attempts)

    [<Fact>]
    let ``generateWithControl maxRetries 0 does not retry`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable attempts = 0

        mock.SetCompleteHandler(fun _ ->
            attempts <- attempts + 1
            raise (RateLimitError("rate limited")))

        let client = Client()
        client.RegisterAdapter(mock)

        Assert.Throws<RateLimitError>(fun () ->
            Generation.generateWithControl
                client
                "m"
                (Some "hi")
                None
                None
                None
                0
                (Some "test")
                None
                (Some 0)
                None
                None
                None
                None
                None
            |> ignore)
        |> ignore

        Assert.Equal(1, attempts)

    [<Fact>]
    let ``generateObjectWithControl uses native response format`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable capturedFormat: ResponseFormat option = None

        mock.SetCompleteHandler(fun req ->
            capturedFormat <- req.ResponseFormat
            mkResponse "r" "test" (Stop "stop") (Message.assistant ("""{"name":"Alice","age":30}""")) Usage.Zero)

        let client = Client()
        client.RegisterAdapter(mock)

        let _ =
            Generation.generateObjectWithControl
                client
                "m"
                "Extract user"
                """{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}},"required":["name","age"]}"""
                (Some "test")
                None

        match capturedFormat with
        | Some(ResponseFormat.JsonSchema(_, _, _)) -> ()
        | _ -> Assert.Fail("Expected JsonSchema response format")

    [<Fact>]
    let ``streamWithControl emits StepFinish between tool rounds`` () =
        let tc =
            { Id = "call_1"
              Name = "tool1"
              Arguments = "{}"
              Metadata = Map.empty }

        let firstResponse =
            mkResponse
                "r1"
                "test"
                (ToolCalls "tool_calls")
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
                Usage.Zero

        let secondResponse =
            mkResponse "r2" "test" (Stop "stop") (Message.assistant ("done")) Usage.Zero

        let mock = ConfigurableMockAdapter("test")
        let mutable streamCalls = 0

        mock.SetStreamHandler(fun _ ->
            streamCalls <- streamCalls + 1

            if streamCalls = 1 then
                seq {
                    yield StreamStart
                    yield ToolCallStart tc
                    yield ToolCallEnd tc
                    yield Finish(ToolCalls "tool_calls", Some Usage.Zero, Some firstResponse)
                }
            else
                seq {
                    yield StreamStart
                    yield TextStart "text-1"
                    yield TextDelta(Some "text-1", "done")
                    yield TextEnd "text-1"
                    yield Finish(Stop "stop", Some Usage.Zero, Some secondResponse)
                })

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "tool1"
                  Description = ""
                  Parameters = "{}" }
              Execute = Some(fun _ -> """{"ok":true}""") }

        let events =
            Generation.streamWithControl
                client
                "m"
                (Some "hi")
                None
                None
                (Some [ tool ])
                2
                (Some "test")
                None
                None
                None
                None
            |> Seq.toList

        Assert.True(
            events
            |> List.exists (fun e ->
                match e with
                | StepFinish _ -> true
                | _ -> false)
        )

        Assert.True(
            events
            |> List.exists (fun e ->
                match e with
                | TextDelta(_, "done") -> true
                | _ -> false)
        )

// ============================================================
// Sprint 004 Coverage Tests
// ============================================================

module Sprint004Coverage =

    type private TestAsyncEnumerable<'T>(source: 'T list) =
        interface IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(_cancellationToken: CancellationToken) =
                let enumerator: IEnumerator<'T> = (source :> seq<'T>).GetEnumerator()

                { new IAsyncEnumerator<'T> with
                    member _.Current = enumerator.Current

                    member _.MoveNextAsync() =
                        ValueTask<bool>(Task.FromResult(enumerator.MoveNext()))

                    member _.DisposeAsync() =
                        enumerator.Dispose()
                        ValueTask() }

    let private toList (source: IAsyncEnumerable<'T>) =
        let results = ResizeArray<'T>()
        let enumerator = source.GetAsyncEnumerator(CancellationToken.None)
        let mutable keepReading = true

        while keepReading do
            let hasNext = enumerator.MoveNextAsync().AsTask().Result

            if hasNext then
                results.Add(enumerator.Current)
            else
                keepReading <- false

        enumerator.DisposeAsync().AsTask().Wait()
        results |> Seq.toList

    [<Fact>]
    let ``Model catalog includes sprint-004 fields and latest model lookups`` () =
        let models = ModelCatalog.listModels ()
        Assert.True(models.Length > 0)
        Assert.True(models |> List.exists (fun m -> m.Provider = "anthropic"))
        Assert.True(models |> List.exists (fun m -> m.Provider = "openai"))
        Assert.True(models |> List.exists (fun m -> m.Provider = "gemini"))

        Assert.True(
            models
            |> List.forall (fun m ->
                m.MaxOutput > 0
                && m.InputCostPerMillion >= 0.0
                && m.OutputCostPerMillion >= 0.0
                && not m.Aliases.IsEmpty
                && (m.SupportsVision = m.SupportsImages))
        )

        Assert.True(ModelCatalog.getLatestModel("anthropic").IsSome)
        Assert.True(ModelCatalog.getLatestModel("openai").IsSome)
        Assert.True(ModelCatalog.getLatestModel("gemini").IsSome)
        Assert.Equal("gemini-3.1-pro-preview", ModelCatalog.getLatestModel("gemini").Value.Id)

    [<Fact>]
    let ``ToolResultData supports image payload fields`` () =
        let imageBytes = [| 0x89uy; 0x50uy; 0x4euy; 0x47uy |]

        let result =
            { ToolCallId = "call_1"
              Content = "screenshot"
              IsError = false
              ImageData = Some imageBytes
              ImageMediaType = Some "image/png" }

        Assert.True(result.ImageData.IsSome)
        Assert.True(result.ImageData.Value = imageBytes)
        Assert.Equal("image/png", result.ImageMediaType.Value)

    [<Fact>]
    let ``StreamAccumulator exposes partial and final response snapshots`` () =
        let finalResponse =
            { Id = "resp_1"
              Model = "m"
              Provider = "test"
              Message = Message.assistant ("Hello world")
              FinishReason = Stop "stop"
              Usage = { Usage.Zero with OutputTokens = 2 }
              ResponseId = Some "resp_1"
              Raw = None
              Warnings = []
              RateLimit = None }

        let events: IAsyncEnumerable<StreamEvent> =
            TestAsyncEnumerable<StreamEvent>(
                [ StreamStart
                  TextStart "t1"
                  TextDelta(Some "t1", "Hello ")
                  TextDelta(Some "t1", "world")
                  TextEnd "t1"
                  Finish(Stop "stop", Some finalResponse.Usage, Some finalResponse) ]
            )
            :> IAsyncEnumerable<StreamEvent>

        let accumulator =
            Generation.StreamAccumulator(events, model = "m", provider = "test")

        let enumerator = accumulator.Events.GetAsyncEnumerator(CancellationToken.None)

        let moveNext () =
            enumerator.MoveNextAsync().AsTask().Result

        Assert.True(moveNext ()) // StreamStart
        Assert.True(moveNext ()) // TextStart
        Assert.True(moveNext ()) // first TextDelta
        let partial = accumulator.PartialResponse()
        Assert.Equal("Hello ", partial.Text)

        while moveNext () do
            ()

        enumerator.DisposeAsync().AsTask().Wait()

        let final = accumulator.PartialResponse()
        Assert.Equal("Hello world", final.Text)
        Assert.Equal(Stop "stop", final.FinishReason)

    [<Fact>]
    let ``streamObject emits partial parseable objects and final object`` () =
        let mock = ConfigurableMockAdapter("test")

        mock.SetStreamHandler(fun _ ->
            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", """{"Name":"Alice",""")
                yield TextDelta(Some "text-1", """"Age":30}""")
                yield TextEnd "text-1"
                yield Finish(Stop "stop", Some Usage.Zero, None)
            })

        let client = Client()
        client.RegisterAdapter(mock)

        let streamed =
            Generation.streamObject<JsonElement>
                client
                "m"
                "Extract"
                """{"type":"object","properties":{"Name":{"type":"string"},"Age":{"type":"integer"}},"required":["Name","Age"]}"""
                (Some "test")

        let partials = toList streamed.PartialObjects
        Assert.True(partials.Length > 0)
        let partial = partials |> List.last
        Assert.Equal(JsonValueKind.Object, partial.ValueKind)
        Assert.Equal("Alice", partial.GetProperty("Name").GetString())
        Assert.Equal(30, partial.GetProperty("Age").GetInt32())

        let final = streamed.FinalObject()
        Assert.True(final.IsSome)
        Assert.Equal("Alice", final.Value.GetProperty("Name").GetString())

    [<Fact>]
    let ``generate stopWhen ToolCalled halts after first matching tool round`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable completions = 0

        mock.SetCompleteHandler(fun _ ->
            completions <- completions + 1

            let tc =
                { Id = "call_1"
                  Name = "write_file"
                  Arguments = """{"path":"a.txt","content":"x"}"""
                  Metadata = Map.empty }

            { Id = "r1"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let writeTool =
            { Definition =
                { Name = "write_file"
                  Description = ""
                  Parameters =
                    """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""" }
              Execute = Some(fun _ -> "ok") }

        let result =
            Generation.generate
                client
                "m"
                (Some "write a file")
                None
                None
                (Some [ writeTool ])
                5
                (Some "test")
                None
                (Some [ ToolCalled "write_file" ])

        Assert.Equal(1, completions)
        Assert.Equal(1, result.Steps.Length)

    [<Fact>]
    let ``Error mapping includes sprint-004 hierarchy additions`` () =
        let contextErr = ErrorMapping.fromStatusCode 413 "too many tokens" None
        Assert.IsType<ContextLengthError>(contextErr) |> ignore

        let quotaErr = ErrorMapping.fromStatusCode 429 "insufficient_quota" None
        Assert.IsType<QuotaExceededError>(quotaErr) |> ignore
        Assert.False(quotaErr.Retryable)

        let invalidErr = ErrorMapping.fromStatusCode 400 "bad request" None
        Assert.IsType<InvalidRequestError>(invalidErr) |> ignore

    [<Fact>]
    let ``Client middleware observes both complete and stream calls`` () =
        let mutable completeCalls = 0
        let mutable streamCalls = 0

        let mw =
            { new IMiddleware with
                member _.Process(req, next) =
                    completeCalls <- completeCalls + 1
                    next req

                member _.ProcessStream(req, next) =
                    streamCalls <- streamCalls + 1
                    next req }

        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        client.AddMiddleware(mw)

        client.Complete(Request.Create("m", [ Message.user ("hi") ])) |> ignore

        client.Stream(Request.Create("m", [ Message.user ("hi") ]))
        |> Seq.toList
        |> ignore

        Assert.Equal(1, completeCalls)
        Assert.Equal(1, streamCalls)

    [<Fact>]
    let ``Anthropic provider option auto_cache=false disables cache_control injection`` () =
        let adapter = AnthropicAdapter("test-key")
        let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
        let buildBody = typeof<AnthropicAdapter>.GetMethod("buildBody", flags)
        Assert.NotNull(buildBody)

        let defaultRequest =
            Request.Create("claude-opus-4-6", [ Message.system ("You are a test assistant."); Message.user ("Hello") ])

        let defaultBody = buildBody.Invoke(adapter, [| box defaultRequest; box false |])
        let defaultJson = JsonSerializer.Serialize(defaultBody)
        Assert.Contains("cache_control", defaultJson)

        let providerOptions: Map<string, obj> =
            Map.ofList [ "anthropic", box (Map.ofList [ "auto_cache", box false ]) ]

        let noCacheRequest =
            { defaultRequest with
                ProviderOptions = Some providerOptions }

        let noCacheBody = buildBody.Invoke(adapter, [| box noCacheRequest; box false |])
        let noCacheJson = JsonSerializer.Serialize(noCacheBody)
        Assert.DoesNotContain("cache_control", noCacheJson)

    [<Fact>]
    let ``Gemini provider options unknown keys are forwarded to request body`` () =
        let adapter = GeminiAdapter("test-key")
        let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
        let buildBody = typeof<GeminiAdapter>.GetMethod("buildBody", flags)
        Assert.NotNull(buildBody)

        let providerOptions: Map<string, obj> =
            Map.ofList
                [ "gemini",
                  box (Map.ofList [ "custom_setting", box 123; "nested", box (Map.ofList [ "flag", box true ]) ]) ]

        let request =
            { Request.Create("gemini-3.1-pro-preview", [ Message.user ("Hi") ]) with
                ProviderOptions = Some providerOptions }

        let body = buildBody.Invoke(adapter, [| box request |])
        let json = JsonSerializer.Serialize(body)
        Assert.Contains("\"custom_setting\":123", json)
        Assert.Contains("\"nested\"", json)

// ============================================================
// Sprint 005 Coverage Tests
// ============================================================

module Sprint005Coverage =

    let private mapHelpersType () =
        let asm = typeof<Client>.Assembly
        asm.GetTypes() |> Array.find (fun t -> t.Name.Contains("HttpAdapterHelpers"))

    let private invokeStatic helperName (args: obj array) =
        let t = mapHelpersType ()
        let flags = BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public
        let m = t.GetMethod(helperName, flags)
        Assert.NotNull(m)
        m.Invoke(null, args)

    [<Fact>]
    let ``B1 Prompt caching default adds cache_control to Anthropic system messages`` () =
        let adapter = AnthropicAdapter("test-key")
        let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
        let buildBody = typeof<AnthropicAdapter>.GetMethod("buildBody", flags)
        Assert.NotNull(buildBody)

        let req =
            Request.Create("claude-opus-4-6", [ Message.system ("System guidance"); Message.user ("Hello") ])

        let body = buildBody.Invoke(adapter, [| box req; box false |])
        let json = JsonSerializer.Serialize(body)
        Assert.Contains("cache_control", json)

    [<Fact>]
    let ``B8 Usage addition handles None Some edges for cache and reasoning tokens`` () =
        let a =
            { InputTokens = 0
              OutputTokens = 0
              ReasoningTokens = Some 5
              CacheReadTokens = Some 5
              CacheWriteTokens = None }

        let b =
            { InputTokens = 0
              OutputTokens = 0
              ReasoningTokens = None
              CacheReadTokens = None
              CacheWriteTokens = Some 10 }

        let sum = a + b
        Assert.Equal(Some 5, sum.ReasoningTokens)
        Assert.Equal(Some 5, sum.CacheReadTokens)
        Assert.Equal(Some 10, sum.CacheWriteTokens)

    [<Fact>]
    let ``B9 Anthropic finish reason mapping table is correct`` () =
        let mapReason (raw: string) =
            invokeStatic "mapAnthropicFinishReason" [| box raw |] :?> FinishReason

        Assert.Equal(Stop "end_turn", mapReason "end_turn")
        Assert.Equal(Stop "stop_sequence", mapReason "stop_sequence")
        Assert.Equal(Length "max_tokens", mapReason "max_tokens")
        Assert.Equal(ToolCalls "tool_use", mapReason "tool_use")

    [<Fact>]
    let ``B9 Gemini finish reason mapping table is correct`` () =
        let mapReason (raw: string) (hasToolCalls: bool) =
            invokeStatic "mapGeminiFinishReason" [| box raw; box hasToolCalls |] :?> FinishReason

        Assert.Equal(Stop "STOP", mapReason "STOP" false)
        Assert.Equal(Length "MAX_TOKENS", mapReason "MAX_TOKENS" false)
        Assert.Equal(ContentFilter "SAFETY", mapReason "SAFETY" false)
        Assert.Equal(ContentFilter "RECITATION", mapReason "RECITATION" false)

    [<Fact>]
    let ``B6 RateLimitInfo parsing reads provider headers`` () =
        use response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)

        response.Headers.TryAddWithoutValidation("x-ratelimit-limit-requests", "100")
        |> ignore

        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "42")
        |> ignore

        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "1735689600")
        |> ignore

        let parsed =
            invokeStatic "parseOpenAIRateLimit" [| box response |] :?> RateLimitInfo option

        Assert.True(parsed.IsSome)
        Assert.Equal(Some 100, parsed.Value.Limit)
        Assert.Equal(Some 42, parsed.Value.Remaining)
        Assert.True(parsed.Value.ResetAt.IsSome)

    [<Fact>]
    let ``B7 Response warnings are surfaced`` () =
        let warningResponse =
            { Id = "r_warn"
              Model = "m"
              Provider = "test"
              Message = Message.assistant ("ok")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = [ "degraded output" ]
              RateLimit = None }

        Assert.True(warningResponse.Warnings.Length > 0)
        Assert.Contains("degraded output", warningResponse.Warnings)

    [<Fact>]
    let ``B3 AudioData and DocumentData round-trip in message content`` () =
        let audio =
            { Url = Some "https://example.com/a.mp3"
              Data = None
              MediaType = Some "audio/mpeg" }

        let doc =
            { Url = None
              Data = Some [| 0x25uy; 0x50uy |]
              MediaType = Some "application/pdf"
              FileName = Some "x.pdf" }

        let msg =
            { Role = User
              Content = [ Audio audio; Document doc ]
              Name = None
              ToolCallId = None }

        match msg.Content.[0], msg.Content.[1] with
        | Audio a, Document d ->
            Assert.Equal("https://example.com/a.mp3", a.Url.Value)
            Assert.Equal("application/pdf", d.MediaType.Value)
            Assert.Equal("x.pdf", d.FileName.Value)
            Assert.True(d.Data.IsSome)
        | _ -> Assert.Fail("Expected audio and document content parts")

    [<Fact>]
    let ``B4 generate abort signal cancellation raises AbortError`` () =
        let client = Client()
        client.RegisterAdapter(MockOpenAIAdapter())
        use signal = new AbortSignal()
        signal.Cancel()

        Assert.Throws<AbortError>(fun () ->
            Generation.generateWithControl
                client
                "gpt-5.2"
                (Some "hello")
                None
                None
                None
                0
                (Some "openai")
                None
                None
                None
                (Some signal)
                None
                None
                None
            |> ignore)
        |> ignore

    [<Fact>]
    let ``B5 TimeoutConfig total timeout raises RequestTimeoutError`` () =
        let mock = ConfigurableMockAdapter("test")
        let mutable calls = 0

        mock.SetCompleteHandler(fun _ ->
            calls <- calls + 1

            if calls <= 2 then
                let tc =
                    { Id = $"call_{calls}"
                      Name = "slow_tool"
                      Arguments = "{}"
                      Metadata = Map.empty }

                { Id = $"r_{calls}"
                  Model = "m"
                  Provider = "test"
                  Message =
                    { Role = Assistant
                      Content = [ ToolCall tc ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = ToolCalls "tool_calls"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None }
            else
                { Id = "r_final"
                  Model = "m"
                  Provider = "test"
                  Message = Message.assistant ("done")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None })

        let client = Client()
        client.RegisterAdapter(mock)

        let tool =
            { Definition =
                { Name = "slow_tool"
                  Description = "slow"
                  Parameters = "{}" }
              Execute =
                Some(fun _ ->
                    System.Threading.Thread.Sleep(25)
                    "ok") }

        Assert.Throws<RequestTimeoutError>(fun () ->
            Generation.generateWithControl
                client
                "m"
                (Some "run")
                None
                None
                (Some [ tool ])
                5
                (Some "test")
                None
                None
                None
                None
                None
                (Some { TotalMs = Some 1; PerStepMs = None })
                None
            |> ignore)
        |> ignore
