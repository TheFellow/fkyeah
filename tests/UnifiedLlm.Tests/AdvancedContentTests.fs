module UnifiedLlm.AdvancedContentTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open UnifiedLlm

type private StaticHttpHandler(status: HttpStatusCode, responseBody: string) =
    inherit HttpMessageHandler()

    let requests = ResizeArray<HttpRequestMessage * string>()
    member _.Requests = requests |> Seq.toList

    member private _.Respond(request: HttpRequestMessage) =
        let body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        requests.Add(request, body)
        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(responseBody, Encoding.UTF8, "application/json")
        response

    override this.Send(request, _cancellationToken) = this.Respond(request)
    override this.SendAsync(request, _cancellationToken) = Task.FromResult(this.Respond(request))

type private TestAsyncEnumerable<'T>(source: 'T list) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_cancellationToken: CancellationToken) =
            let enumerator = (source :> seq<'T>).GetEnumerator()

            { new IAsyncEnumerator<'T> with
                member _.Current = enumerator.Current
                member _.MoveNextAsync() = ValueTask<bool>(enumerator.MoveNext())

                member _.DisposeAsync() =
                    enumerator.Dispose()
                    ValueTask() }

let private consumeAll (source: IAsyncEnumerable<'T>) =
    let enumerator = source.GetAsyncEnumerator(CancellationToken.None)
    let mutable reading = true

    while reading do
        reading <- enumerator.MoveNextAsync().AsTask().Result

    enumerator.DisposeAsync().AsTask().Wait()

let private invokeBody (adapter: obj) (request: Request) =
    let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
    let methodInfo = adapter.GetType().GetMethod("buildBody", flags)
    Assert.NotNull(methodInfo)

    let parameters =
        if methodInfo.GetParameters().Length = 2 then
            [| box request; box false |]
        else
            [| box request |]

    methodInfo.Invoke(adapter, parameters)
    |> JsonSerializer.Serialize
    |> JsonDocument.Parse

let private assistant content finishReason provider model =
    { Id = "response"
      Model = model
      Provider = provider
      Message =
        { Role = Assistant
          Content = content
          Name = None
          ToolCallId = None }
      FinishReason = finishReason
      Usage = Usage.Zero
      ResponseId = None
      Raw = None
      Warnings = []
      RateLimit = None }

[<Fact>]
let ``request helpers add custom tools and Gemini code execution without changing function tools`` () =
    let functionTool =
        { Name = "function_tool"
          Description = "function"
          Parameters = "{}" }

    let request =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            Tools = Some [ functionTool ] }
        |> fun value ->
            value.WithCustomTools(
                [ CustomToolDefinition.FreeText("shell", "commands")
                  CustomToolDefinition.Grammar("sql", "query", "lark", "start: SELECT") ]
            )
        |> fun value -> value.WithCodeExecution()

    Assert.Equal(Some [ functionTool ], request.Tools)
    Assert.Equal(2, request.CustomTools.Length)
    Assert.True(request.CodeExecutionEnabled)

[<Fact>]
let ``OpenAI request emits free-text and grammar custom tool definitions`` () =
    let request =
        { Request.Create("gpt-5.4", [ Message.User("run") ]) with
            ToolChoice = Some(ToolChoice.Named "sql") }
        |> fun value ->
            value.WithCustomTools(
                [ CustomToolDefinition.FreeText("shell", "commands")
                  CustomToolDefinition.Grammar("sql", "query", "lark", "start: SELECT") ]
            )

    use doc = invokeBody (OpenAIAdapter("key")) request
    let tools = doc.RootElement.GetProperty("tools")

    let shell =
        tools.EnumerateArray()
        |> Seq.find (fun tool -> tool.GetProperty("name").GetString() = "shell")

    let sql =
        tools.EnumerateArray()
        |> Seq.find (fun tool -> tool.GetProperty("name").GetString() = "sql")

    Assert.Equal("custom", shell.GetProperty("type").GetString())
    Assert.Equal("text", shell.GetProperty("format").GetProperty("type").GetString())
    Assert.Equal("grammar", sql.GetProperty("format").GetProperty("type").GetString())
    Assert.Equal("lark", sql.GetProperty("format").GetProperty("syntax").GetString())
    Assert.Equal("start: SELECT", sql.GetProperty("format").GetProperty("definition").GetString())
    Assert.Equal("custom", doc.RootElement.GetProperty("tool_choice").GetProperty("type").GetString())

[<Fact>]
let ``OpenAI custom call and result history use custom payload types`` () =
    let call =
        { Id = "call_custom"
          Name = "shell"
          Input = "echo hello" }

    let request =
        Request.Create(
            "gpt-5.4",
            [ { Role = Assistant
                Content = [ CustomToolCall call ]
                Name = None
                ToolCallId = None }
              Message.CustomToolResult("call_custom", "hello") ]
        )

    use doc = invokeBody (OpenAIAdapter("key")) request
    let input = doc.RootElement.GetProperty("input")

    Assert.Equal("custom_tool_call", input[0].GetProperty("type").GetString())
    Assert.Equal("echo hello", input[0].GetProperty("input").GetString())
    Assert.Equal("custom_tool_call_output", input[1].GetProperty("type").GetString())
    Assert.Equal("hello", input[1].GetProperty("output").GetString())

[<Fact>]
let ``OpenAI complete response parses custom calls and results`` () =
    let payload =
        """{"id":"resp","model":"gpt-5.4","status":"completed","output":[{"type":"custom_tool_call","call_id":"call_1","name":"shell","input":"echo hi"},{"type":"custom_tool_call_output","call_id":"call_0","output":"old"}]}"""

    let handler = new StaticHttpHandler(HttpStatusCode.OK, payload)
    use httpClient = new HttpClient(handler)

    let adapter =
        OpenAIAdapter("key", httpClient = httpClient, responsesBaseUrl = "https://mock/responses")

    let response =
        (adapter :> IProviderAdapter).Complete(Request.Create("gpt-5.4", [ Message.User("run") ]))

    Assert.Equal(ToolCalls "tool_calls", response.FinishReason)
    Assert.Equal("echo hi", response.CustomToolCalls |> List.exactlyOne |> _.Input)

    Assert.Contains(
        response.Message.Content,
        function
        | CustomToolResult result -> result.Output = "old"
        | _ -> false
    )

[<Fact>]
let ``OpenAI stream emits custom input deltas and reassembles custom call`` () =
    let payload =
        """event: response.created
data: {"response":{"id":"resp","model":"gpt-5.4"}}

event: response.output_item.added
data: {"item":{"id":"item_1","type":"custom_tool_call","call_id":"call_1","name":"shell","input":""}}

event: response.custom_tool_call_input.delta
data: {"item_id":"item_1","delta":"echo hi"}

event: response.custom_tool_call_input.done
data: {"item_id":"item_1","input":"echo hi"}

event: response.output_item.done
data: {"item":{"type":"custom_tool_call","call_id":"call_1","name":"shell","input":"echo hi"}}

event: response.completed
data: {"response":{"id":"resp","model":"gpt-5.4","status":"completed"}}

"""

    let handler = new StaticHttpHandler(HttpStatusCode.OK, payload)
    use httpClient = new HttpClient(handler)

    let adapter =
        OpenAIAdapter("key", httpClient = httpClient, responsesBaseUrl = "https://mock/responses")

    let events =
        (adapter :> IProviderAdapter).Stream(Request.Create("gpt-5.4", [ Message.User("run") ]))
        |> Seq.toList

    Assert.Contains(CustomToolCallDelta("call_1", "echo hi"), events)

    let response =
        events
        |> List.choose (function
            | Finish(_, _, Some value) -> Some value
            | _ -> None)
        |> List.exactlyOne

    Assert.Equal("echo hi", response.CustomToolCalls |> List.exactlyOne |> _.Input)

[<Fact>]
let ``Gemini request declares code execution and preserves code history`` () =
    let message =
        { Role = Assistant
          Content =
            [ CodeExecution
                  { Language = "PYTHON"
                    Code = "print(4)" }
              CodeExecutionResult { Outcome = "OUTCOME_OK"; Output = "4" } ]
          Name = None
          ToolCallId = None }

    let request =
        Request.Create("gemini-3-flash-preview", [ message ]).WithCodeExecution()

    use doc = invokeBody (GeminiAdapter("key")) request
    let tools = doc.RootElement.GetProperty("tools")
    let parts = (doc.RootElement.GetProperty("contents")[0]).GetProperty("parts")
    let mutable codeExecution = Unchecked.defaultof<JsonElement>

    Assert.True(tools[0].TryGetProperty("codeExecution", &codeExecution))
    Assert.Equal("PYTHON", parts[0].GetProperty("executableCode").GetProperty("language").GetString())
    Assert.Equal("4", parts[1].GetProperty("codeExecutionResult").GetProperty("output").GetString())

[<Fact>]
let ``Gemini complete response parses code execution parts`` () =
    let payload =
        """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"calculated"},{"executableCode":{"language":"PYTHON","code":"print(4)"}},{"codeExecutionResult":{"outcome":"OUTCOME_OK","output":"4"}}]}}]}"""

    let handler = new StaticHttpHandler(HttpStatusCode.OK, payload)
    use httpClient = new HttpClient(handler)

    let adapter =
        GeminiAdapter("key", httpClient = httpClient, apiBaseUrl = "https://mock/v1beta")

    let response =
        (adapter :> IProviderAdapter).Complete(Request.Create("gemini-3-flash-preview", [ Message.User("2+2") ]))

    Assert.Contains(
        response.Message.Content,
        function
        | CodeExecution value -> value.Code = "print(4)"
        | _ -> false
    )

    Assert.Contains(
        response.Message.Content,
        function
        | CodeExecutionResult value -> value.Outcome = "OUTCOME_OK" && value.Output = "4"
        | _ -> false
    )

[<Fact>]
let ``Gemini stream emits code execution events and final content`` () =
    let payload =
        """data: {"candidates":[{"finishReason":"STOP","content":{"parts":[{"executableCode":{"language":"PYTHON","code":"print(4)"}},{"codeExecutionResult":{"outcome":"OUTCOME_OK","output":"4"}}]}}]}

"""

    let handler = new StaticHttpHandler(HttpStatusCode.OK, payload)
    use httpClient = new HttpClient(handler)

    let adapter =
        GeminiAdapter("key", httpClient = httpClient, apiBaseUrl = "https://mock/v1beta")

    let events =
        (adapter :> IProviderAdapter).Stream(Request.Create("gemini-3-flash-preview", [ Message.User("2+2") ]))
        |> Seq.toList

    Assert.Contains(
        CodeExecutionEvent
            { Language = "PYTHON"
              Code = "print(4)" },
        events
    )

    Assert.Contains(CodeExecutionResultEvent { Outcome = "OUTCOME_OK"; Output = "4" }, events)

    let response =
        events
        |> List.choose (function
            | Finish(_, _, Some value) -> Some value
            | _ -> None)
        |> List.exactlyOne

    Assert.Contains(
        response.Message.Content,
        function
        | CodeExecution _ -> true
        | _ -> false
    )

    Assert.Contains(
        response.Message.Content,
        function
        | CodeExecutionResult _ -> true
        | _ -> false
    )

[<Fact>]
let ``stream accumulator preserves custom calls and code execution events`` () =
    let custom =
        { Id = "call"
          Name = "shell"
          Input = "" }

    let events =
        [ CustomToolCallStart custom
          CustomToolCallDelta("call", "echo hi")
          CustomToolCallEnd custom
          CodeExecutionEvent
              { Language = "PYTHON"
                Code = "print(4)" }
          CodeExecutionResultEvent { Outcome = "OUTCOME_OK"; Output = "4" }
          Finish(Stop "stop", Some Usage.Zero, None) ]

    let accumulator =
        Generation.StreamAccumulator(
            TestAsyncEnumerable(events) :> IAsyncEnumerable<_>,
            model = "model",
            provider = "test"
        )

    consumeAll accumulator.Events
    let response = accumulator.PartialResponse()

    Assert.Equal("echo hi", response.CustomToolCalls |> List.exactlyOne |> _.Input)

    Assert.Contains(
        response.Message.Content,
        function
        | CodeExecution _ -> true
        | _ -> false
    )

    Assert.Contains(
        response.Message.Content,
        function
        | CodeExecutionResult _ -> true
        | _ -> false
    )

[<Fact>]
let ``cache keys include custom tools code execution and custom payloads`` () =
    let baseline = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let custom =
        baseline.WithCustomTools([ CustomToolDefinition.FreeText("shell", "commands") ])

    let grammar =
        baseline.WithCustomTools([ CustomToolDefinition.Grammar("shell", "commands", "lark", "start: WORD") ])

    let code = baseline.WithCodeExecution()

    let customMessage =
        Request.Create(
            "gpt-5.4",
            [ { Role = Assistant
                Content =
                  [ CustomToolCall
                        { Id = "call"
                          Name = "shell"
                          Input = "echo" } ]
                Name = None
                ToolCallId = None } ]
        )

    Assert.NotEqual(CacheKey.fromRequest baseline, CacheKey.fromRequest custom)
    Assert.NotEqual(CacheKey.fromRequest custom, CacheKey.fromRequest grammar)
    Assert.NotEqual(CacheKey.fromRequest baseline, CacheKey.fromRequest code)
    Assert.NotEqual(CacheKey.fromRequest baseline, CacheKey.fromRequest customMessage)

[<Fact>]
let ``filesystem cache round trips advanced content and replay events`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fkyeah-advanced-cache-" + Guid.NewGuid().ToString("N"))

    try
        let config =
            { CacheConfig.Default with
                PersistencePath = Some root }

        let store = CacheStore.fileSystem config

        let key =
            CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("cache") ]))

        let response =
            assistant
                [ CustomToolCall
                      { Id = "call"
                        Name = "shell"
                        Input = "echo" }
                  CodeExecution
                      { Language = "PYTHON"
                        Code = "print(4)" }
                  CodeExecutionResult { Outcome = "OUTCOME_OK"; Output = "4" } ]
                (ToolCalls "tool_calls")
                "openai"
                "gpt-5.4"

        Async.RunSynchronously(
            store.PutLlm
                key
                { Response = response
                  StoredAt = DateTimeOffset.UtcNow
                  Metadata = Map.empty }
        )

        let reloaded = CacheStore.fileSystem config
        let loaded = Async.RunSynchronously(reloaded.TryGetLlm key) |> Option.get
        Assert.Equal("echo", loaded.Response.CustomToolCalls |> List.exactlyOne |> _.Input)

        Assert.Contains(
            loaded.Response.Message.Content,
            function
            | CodeExecution _ -> true
            | _ -> false
        )

        let replay = Caching.replayStreamFromCachedResponse loaded.Response |> Seq.toList

        Assert.Contains(
            replay,
            function
            | CustomToolCallEnd value -> value.Input = "echo"
            | _ -> false
        )

        Assert.Contains(
            replay,
            function
            | CodeExecutionEvent _ -> true
            | _ -> false
        )

        Assert.Contains(
            replay,
            function
            | CodeExecutionResultEvent _ -> true
            | _ -> false
        )
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, true)

[<Fact>]
let ``validation enforces provider support grammar fields and duplicate names`` () =
    let validator = RequestValidator.fromCatalog ()

    let invalidGrammar =
        Request
            .Create("gpt-5.4", [ Message.User("hello") ])
            .WithCustomTools([ CustomToolDefinition.Grammar("sql", "query", "", "") ])

    let unsupportedCustom =
        Request
            .Create("gemini-3-flash-preview", [ Message.User("hello") ])
            .WithCustomTools([ CustomToolDefinition.FreeText("shell", "commands") ])

    let unsupportedCode =
        Request.Create("gpt-5.4", [ Message.User("hello") ]).WithCodeExecution()

    let duplicate =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            Tools =
                Some
                    [ { Name = "same"
                        Description = "function"
                        Parameters = "{}" } ] }
            .WithCustomTools([ CustomToolDefinition.FreeText("same", "custom") ])

    let validCustom =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            ToolChoice = Some(ToolChoice.Named "shell") }
            .WithCustomTools([ CustomToolDefinition.FreeText("shell", "commands") ])

    let validCode =
        Request.Create("gemini-3-flash-preview", [ Message.User("hello") ]).WithCodeExecution()

    let errors request =
        match validator.Validate request with
        | Result.Error issues -> issues
        | Result.Ok _ -> []

    Assert.Contains(
        errors invalidGrammar,
        function
        | ValidationIssue.InvalidCustomTool _ -> true
        | _ -> false
    )

    Assert.Contains(
        ValidationIssue.UnsupportedCapability("gemini-3-flash-preview", "custom tools"),
        errors unsupportedCustom
    )

    Assert.Contains(ValidationIssue.UnsupportedCapability("gpt-5.4", "code execution"), errors unsupportedCode)
    Assert.Contains(ValidationIssue.InvalidCustomTool("duplicate tool name 'same'"), errors duplicate)
    Assert.Empty(errors validCustom)
    Assert.Empty(errors validCode)
