module UnifiedLlm.OpenRouterTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open UnifiedLlm

type private CapturingHandler(status: HttpStatusCode, responseBody: string, ?responseHeaders: (string * string) list) =
    inherit HttpMessageHandler()

    let requests = ResizeArray<string * Uri * Map<string, string>>()

    member _.Requests = requests |> Seq.toList

    member private _.Respond(request: HttpRequestMessage) =
        let body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        let headers =
            Seq.append request.Headers request.Content.Headers
            |> Seq.map (fun item -> item.Key, String.concat "," item.Value)
            |> Map.ofSeq

        requests.Add(body, request.RequestUri, headers)
        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(responseBody, Encoding.UTF8, "application/json")

        for name, value in defaultArg responseHeaders [] do
            response.Headers.TryAddWithoutValidation(name, value) |> ignore

        response

    override this.Send(request, _cancellationToken) = this.Respond(request)
    override this.SendAsync(request, _cancellationToken) = Task.FromResult(this.Respond(request))

let private successfulResponse =
    """{"id":"or-response","model":"anthropic/claude-sonnet-4","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""

let private requestWithProvider model messages =
    { Request.Create(model, messages) with
        Provider = Some "openrouter" }

[<Fact>]
let ``OpenRouter request maps multimodal tools structured output options and headers`` () =
    let handler = new CapturingHandler(HttpStatusCode.OK, successfulResponse)
    use client = new HttpClient(handler)

    let adapter =
        OpenRouterAdapter(
            "key",
            httpClient = client,
            apiBaseUrl = "https://router.test/api/v1/",
            defaultHeaders = Map.ofList [ "X-OpenRouter-Title", "fkyeah" ]
        )

    let tool =
        { Name = "weather"
          Description = "Get weather"
          Parameters = """{"type":"object","properties":{"city":{"type":"string"}}}""" }

    let request =
        { requestWithProvider
              "anthropic/claude-sonnet-4"
              [ Message.System("be useful")
                { Role = User
                  Content =
                    [ Text "look"
                      Image
                          { Url = None
                            Data = Some [| 1uy; 2uy |]
                            FilePath = None
                            MediaType = Some "image/png" } ]
                  Name = None
                  ToolCallId = None } ] with
            Tools = Some [ tool ]
            ToolChoice = Some(ToolChoice.Named "weather")
            ResponseFormat = Some(ResponseFormat.JsonSchema("answer", """{"type":"object"}""", true))
            ReasoningEffort = Some "high"
            ProviderOptions =
                Some(
                    Map.ofList
                        [ "openrouter",
                          box (
                              Map.ofList<string, obj>
                                  [ "provider", box (Map.ofList<string, obj> [ "sort", box "price" ])
                                    "headers", box (Map.ofList<string, obj> [ "HTTP-Referer", box "https://test" ]) ]
                          ) ]
                ) }

    let response = (adapter :> IProviderAdapter).Complete(request)
    Assert.Equal("openrouter", response.Provider)
    let body, uri, headers = handler.Requests |> List.exactlyOne
    Assert.Equal("https://router.test/api/v1/chat/completions", uri.ToString())
    Assert.Equal("Bearer key", headers["Authorization"])
    Assert.Equal("fkyeah", headers["X-OpenRouter-Title"])
    Assert.Equal("https://test", headers["HTTP-Referer"])

    use doc = JsonDocument.Parse(body)
    let root = doc.RootElement
    let userContent = (root.GetProperty("messages")[1]).GetProperty("content")
    Assert.Equal("image_url", userContent[1].GetProperty("type").GetString())
    Assert.StartsWith("data:image/png;base64,", userContent[1].GetProperty("image_url").GetProperty("url").GetString())
    Assert.Equal("weather", (root.GetProperty("tools")[0]).GetProperty("function").GetProperty("name").GetString())
    Assert.Equal("weather", root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString())
    Assert.Equal("json_schema", root.GetProperty("response_format").GetProperty("type").GetString())
    Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString())
    Assert.Equal("price", root.GetProperty("provider").GetProperty("sort").GetString())

[<Fact>]
let ``OpenRouter request preserves assistant function calls and tool results`` () =
    let handler = new CapturingHandler(HttpStatusCode.OK, successfulResponse)
    use client = new HttpClient(handler)

    let adapter =
        OpenRouterAdapter("key", httpClient = client, apiBaseUrl = "https://router.test")

    let assistant =
        { Role = Assistant
          Content =
            [ Text "checking"
              ToolCall
                  { Id = "call-1"
                    Name = "weather"
                    Arguments = """{"city":"Paris"}"""
                    Metadata = Map.empty } ]
          Name = None
          ToolCallId = None }

    let request =
        requestWithProvider "openai/gpt-5" [ assistant; Message.ToolResult("call-1", "sunny", false) ]

    (adapter :> IProviderAdapter).Complete(request) |> ignore
    let body, _, _ = handler.Requests |> List.exactlyOne
    use doc = JsonDocument.Parse(body)
    let messages = doc.RootElement.GetProperty("messages")
    Assert.Equal("call-1", (messages[0].GetProperty("tool_calls")[0]).GetProperty("id").GetString())
    Assert.Equal("tool", messages[1].GetProperty("role").GetString())
    Assert.Equal("call-1", messages[1].GetProperty("tool_call_id").GetString())
    Assert.Equal("sunny", messages[1].GetProperty("content").GetString())

[<Fact>]
let ``OpenRouter complete normalizes reasoning tools usage finish and rate limits`` () =
    let payload =
        """{"id":"or-1","model":"openai/gpt-5","choices":[{"message":{"role":"assistant","content":"answer","reasoning_details":[{"type":"reasoning.text","text":"thought","signature":"sig"}],"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"id\":1}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":20,"completion_tokens":8,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":3}}}"""

    let handler =
        new CapturingHandler(
            HttpStatusCode.OK,
            payload,
            responseHeaders = [ "x-ratelimit-limit", "100"; "x-ratelimit-remaining", "99" ]
        )

    use client = new HttpClient(handler)

    let adapter =
        OpenRouterAdapter("key", httpClient = client, apiBaseUrl = "https://router.test")

    let response =
        (adapter :> IProviderAdapter).Complete(requestWithProvider "openai/gpt-5" [ Message.User("go") ])

    Assert.Equal(ToolCalls "tool_calls", response.FinishReason)
    Assert.Equal("answer", response.Text)
    Assert.Equal("thought", response.Reasoning |> Option.get)
    Assert.Equal("lookup", response.ToolCalls |> List.exactlyOne |> _.Name)
    Assert.Equal(20, response.Usage.InputTokens)
    Assert.Equal(Some 4, response.Usage.CacheReadTokens)
    Assert.Equal(Some 3, response.Usage.ReasoningTokens)
    Assert.Equal(Some 100, response.RateLimit |> Option.bind _.Limit)

[<Fact>]
let ``OpenRouter stream accumulates text reasoning indexed tool deltas and trailing usage`` () =
    let payload =
        """data: {"id":"or-stream","model":"openai/gpt-5","choices":[{"index":0,"delta":{"content":"hel","reasoning":"think "},"finish_reason":null}]}

data: {"id":"or-stream","choices":[{"index":0,"delta":{"content":"lo","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"id\":"}}]},"finish_reason":null}]}

data: {"id":"or-stream","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":"tool_calls"}]}

data: {"id":"or-stream","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":6,"completion_tokens_details":{"reasoning_tokens":2}}}

data: [DONE]

"""

    let handler = new CapturingHandler(HttpStatusCode.OK, payload)
    use client = new HttpClient(handler)

    let adapter =
        OpenRouterAdapter("key", httpClient = client, apiBaseUrl = "https://router.test")

    let events =
        (adapter :> IProviderAdapter).Stream(requestWithProvider "openai/gpt-5" [ Message.User("go") ])
        |> Seq.toList

    Assert.Contains(TextDelta(Some "text-0", "hel"), events)
    Assert.Contains(ThinkingEvent "think ", events)
    Assert.Contains(ToolCallDelta("call-1", "1}"), events)

    let response =
        events
        |> List.choose (function
            | Finish(_, _, Some value) -> Some value
            | _ -> None)
        |> List.exactlyOne

    Assert.Equal("hello", response.Text)
    Assert.Equal("think ", response.Reasoning |> Option.get)
    Assert.Equal("""{"id":1}""", response.ToolCalls |> List.exactlyOne |> _.Arguments)
    Assert.Equal(Some 2, response.Usage.ReasoningTokens)
    Assert.Equal(ToolCalls "tool_calls", response.FinishReason)

[<Fact>]
let ``OpenRouter errors use normalized provider error classification`` () =
    let handler =
        new CapturingHandler(
            HttpStatusCode.TooManyRequests,
            """{"error":{"message":"slow down"}}""",
            responseHeaders = [ "Retry-After", "2" ]
        )

    use client = new HttpClient(handler)

    let adapter =
        OpenRouterAdapter("key", httpClient = client, apiBaseUrl = "https://router.test")

    let error =
        Assert.Throws<RateLimitError>(fun () ->
            (adapter :> IProviderAdapter).Complete(requestWithProvider "openai/gpt-5" [ Message.User("go") ])
            |> ignore)

    Assert.Equal(Some 2.0, error.RetryAfter)

[<Fact>]
let ``catalog validation accepts explicit uncatalogued OpenRouter slash models only`` () =
    let validator = RequestValidator.fromCatalog ()
    let explicitRequest = requestWithProvider "vendor/new-model" [ Message.User("go") ]
    let implicitRequest = Request.Create("vendor/new-model", [ Message.User("go") ])
    Assert.True(validator.Validate(explicitRequest).IsOk)

    match validator.Validate implicitRequest with
    | Result.Error issues -> Assert.Contains(ValidationIssue.UnknownModel "vendor/new-model", issues)
    | Result.Ok _ -> Assert.Fail("expected unknown model failure without explicit OpenRouter provider")

    let unsupportedCustom =
        requestWithProvider "gpt-5.4" [ Message.User("go") ]
        |> fun request -> request.WithCustomTools([ CustomToolDefinition.FreeText("shell", "commands") ])

    match validator.Validate unsupportedCustom with
    | Result.Error issues -> Assert.Contains(ValidationIssue.UnsupportedCapability("gpt-5.4", "custom tools"), issues)
    | Result.Ok _ -> Assert.Fail("expected OpenAI-only custom tools to be rejected for OpenRouter")

[<Fact>]
let ``Client FromEnv registers OpenRouter adapter`` () =
    let previous = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")

    try
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "test-openrouter-key")
        let client = Client.FromEnv()
        Assert.True(client.IsProviderRegistered("openrouter"))
    finally
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", previous)
