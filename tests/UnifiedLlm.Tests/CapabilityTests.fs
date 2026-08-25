module UnifiedLlm.CapabilityTests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open UnifiedLlm

type private CapturedRequest =
    { Method: HttpMethod
      Uri: Uri
      Body: string
      Headers: Map<string, string> }

type private StubHttpHandler(responses: (HttpStatusCode * string) list) =
    inherit HttpMessageHandler()

    let remaining = Queue<HttpStatusCode * string>(responses)
    let captured = ResizeArray<CapturedRequest>()

    member _.Requests = captured |> Seq.toList

    member private _.Respond(request: HttpRequestMessage) =
        let body =
            if isNull request.Content then
                ""
            else
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        let headers =
            request.Headers
            |> Seq.map (fun header -> header.Key, String.concat "," header.Value)
            |> Map.ofSeq

        captured.Add(
            { Method = request.Method
              Uri = request.RequestUri
              Body = body
              Headers = headers }
        )

        let status, responseBody = remaining.Dequeue()
        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(responseBody, Encoding.UTF8, "application/json")
        response

    override this.Send(request, _cancellationToken) = this.Respond(request)

    override this.SendAsync(request, _cancellationToken) = Task.FromResult(this.Respond(request))

let private toolResult content =
    { ToolCallId = "call_1"
      Content = content
      IsError = false
      ImageData = None
      ImageMediaType = None }

type private ContinuationMockAdapter() =
    let mutable completeCalls = 0
    let mutable continuations: ToolContinuationRequest list = []

    member _.CompleteCalls = completeCalls
    member _.Continuations = continuations

    interface IProviderAdapter with
        member _.ProviderId = "continuation-test"
        member _.Initialize() = async.Return()
        member _.Close() = async.Return()
        member _.SupportsToolChoice() = true

        member _.Complete(request: Request) =
            completeCalls <- completeCalls + 1

            let call =
                { Id = "call_1"
                  Name = "lookup"
                  Arguments = "{}"
                  Metadata = Map.empty }

            { Id = "first"
              Model = request.Model
              Provider = "continuation-test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall call ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = Some "resp_123"
              Raw = None
              Warnings = []
              RateLimit = None }

        member _.Stream(_request: Request) = Seq.empty

    interface IToolContinuationAdapter with
        member _.ContinueToolOutputs(request: ToolContinuationRequest) =
            continuations <- continuations @ [ request ]

            { Id = "second"
              Model = request.Request.Model
              Provider = "continuation-test"
              Message = Message.Assistant("continued")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = Some "resp_456"
              Raw = None
              Warnings = []
              RateLimit = None }

        member _.StreamToolOutputs(_request: ToolContinuationRequest) = Seq.empty

type private StreamingContinuationMockAdapter() =
    let mutable streamCalls = 0
    let mutable continuationCalls = 0

    member _.StreamCalls = streamCalls
    member _.ContinuationCalls = continuationCalls

    interface IProviderAdapter with
        member _.ProviderId = "stream-continuation-test"
        member _.Initialize() = async.Return()
        member _.Close() = async.Return()
        member _.SupportsToolChoice() = true
        member _.Complete(_request: Request) = failwith "not used"

        member _.Stream(request: Request) =
            streamCalls <- streamCalls + 1

            let call =
                { Id = "call_1"
                  Name = "lookup"
                  Arguments = "{}"
                  Metadata = Map.empty }

            let response =
                { Id = "first"
                  Model = request.Model
                  Provider = "stream-continuation-test"
                  Message =
                    { Role = Assistant
                      Content = [ ToolCall call ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = ToolCalls "tool_calls"
                  Usage = Usage.Zero
                  ResponseId = Some "resp_stream_1"
                  Raw = None
                  Warnings = []
                  RateLimit = None }

            seq {
                yield StreamStart
                yield ToolCallStart call
                yield ToolCallEnd call
                yield Finish(response.FinishReason, Some response.Usage, Some response)
            }

    interface IToolContinuationAdapter with
        member _.ContinueToolOutputs(_request: ToolContinuationRequest) = failwith "not used"

        member _.StreamToolOutputs(request: ToolContinuationRequest) =
            continuationCalls <- continuationCalls + 1

            let response =
                { Id = "second"
                  Model = request.Request.Model
                  Provider = "stream-continuation-test"
                  Message = Message.Assistant("stream continued")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = Some "resp_stream_2"
                  Raw = None
                  Warnings = []
                  RateLimit = None }

            seq {
                yield StreamStart
                yield TextStart "text"
                yield TextDelta(Some "text", "stream continued")
                yield TextEnd "text"
                yield Finish(response.FinishReason, Some response.Usage, Some response)
            }

[<Fact>]
let ``capability checks distinguish optional interfaces`` () =
    let client = Client()
    client.RegisterAdapter(OpenAIAdapter("key"))
    client.RegisterAdapter(GeminiAdapter("key"))
    client.RegisterAdapter(MockAnthropicAdapter())

    Assert.True(client.SupportsEmbeddings("openai"))
    Assert.True(client.SupportsEmbeddings("gemini"))
    Assert.False(client.SupportsEmbeddings("anthropic"))
    Assert.True(client.SupportsToolContinuation("openai"))
    Assert.False(client.SupportsToolContinuation("gemini"))
    Assert.False(client.SupportsToolContinuation("anthropic"))

[<Fact>]
let ``unsupported embedding provider fails clearly before dispatch`` () =
    let client = Client()
    client.RegisterAdapter(MockAnthropicAdapter())

    let request =
        { EmbeddingRequest.Create("embedding-model", [ "hello" ]) with
            Provider = Some "anthropic" }

    let error =
        Assert.Throws<ConfigurationError>(fun () -> client.Embed(request) |> ignore)

    Assert.Contains("does not support embeddings", error.Message)

[<Fact>]
let ``unsupported continuation provider fails clearly`` () =
    let client = Client()
    client.RegisterAdapter(MockGeminiAdapter())

    let request =
        { Request.Create("gemini-3-flash-preview", []) with
            Provider = Some "gemini" }

    let continuation =
        ToolContinuationRequest.Create(request, "resp_1", [ toolResult "result" ])

    let error =
        Assert.Throws<ConfigurationError>(fun () -> client.ContinueToolOutputs(continuation) |> ignore)

    Assert.Contains("does not support response-ID tool continuation", error.Message)

[<Fact>]
let ``embedding validation rejects empty inputs and dimensions`` () =
    let client = Client()
    client.RegisterAdapter(OpenAIAdapter("key"))

    let noInputs =
        { EmbeddingRequest.Create("text-embedding-3-small", []) with
            Provider = Some "openai" }

    let badDimensions =
        { EmbeddingRequest.Create("text-embedding-3-small", [ "hello" ]) with
            Provider = Some "openai"
            Dimensions = Some 0 }

    Assert.Throws<ConfigurationError>(fun () -> client.Embed(noInputs) |> ignore)
    |> ignore

    Assert.Throws<ConfigurationError>(fun () -> client.Embed(badDimensions) |> ignore)
    |> ignore

[<Fact>]
let ``OpenAI embeddings use normalized request and response over HTTP`` () =
    let response =
        """{"data":[{"index":1,"embedding":[0.3,0.4]},{"index":0,"embedding":[0.1,0.2]}],"model":"text-embedding-3-small","usage":{"prompt_tokens":7,"total_tokens":7}}"""

    let handler = new StubHttpHandler([ HttpStatusCode.OK, response ])
    use httpClient = new HttpClient(handler)

    let adapter =
        OpenAIAdapter(
            "secret",
            httpClient = httpClient,
            responsesBaseUrl = "https://mock.test/v1/responses",
            embeddingsBaseUrl = "https://mock.test/v1/embeddings"
        )

    let client = Client()
    client.RegisterAdapter(adapter)

    let request =
        { EmbeddingRequest.Create("text-embedding-3-small", [ "first"; "second" ]) with
            Provider = Some "openai"
            Dimensions = Some 2 }

    let result = client.Embed(request)
    let captured = handler.Requests |> List.exactlyOne
    use body = JsonDocument.Parse(captured.Body)

    Assert.Equal("https://mock.test/v1/embeddings", captured.Uri.AbsoluteUri)
    Assert.Equal("Bearer secret", captured.Headers["Authorization"])
    Assert.Equal(2, body.RootElement.GetProperty("input").GetArrayLength())
    Assert.Equal(2, body.RootElement.GetProperty("dimensions").GetInt32())
    Assert.Equal(2, result.Embeddings.Length)
    Assert.Equal<float>([| 0.1; 0.2 |], result.Embeddings[0].Vector)
    Assert.Equal(7, result.Usage.InputTokens)

[<Fact>]
let ``Gemini embeddings use batch endpoint and preserve input order`` () =
    let response = """{"embeddings":[{"values":[1.0,2.0]},{"values":[3.0,4.0]}]}"""
    let handler = new StubHttpHandler([ HttpStatusCode.OK, response ])
    use httpClient = new HttpClient(handler)

    let adapter =
        GeminiAdapter("secret", httpClient = httpClient, apiBaseUrl = "https://mock.test/v1beta")

    let client = Client()
    client.RegisterAdapter(adapter)

    let request =
        { EmbeddingRequest.Create("gemini-embedding-001", [ "first"; "second" ]) with
            Provider = Some "gemini"
            Dimensions = Some 2
            ProviderOptions = Some(Map.ofList [ "gemini", box (Map.ofList [ "task_type", box "RETRIEVAL_DOCUMENT" ]) ]) }

    let result = client.Embed(request)
    let captured = handler.Requests |> List.exactlyOne
    use body = JsonDocument.Parse(captured.Body)
    let requests = body.RootElement.GetProperty("requests")

    Assert.Equal(
        "https://mock.test/v1beta/models/gemini-embedding-001:batchEmbedContents?key=secret",
        captured.Uri.AbsoluteUri
    )

    Assert.Equal(2, requests.GetArrayLength())
    Assert.Equal("models/gemini-embedding-001", requests[0].GetProperty("model").GetString())
    Assert.Equal("RETRIEVAL_DOCUMENT", requests[0].GetProperty("taskType").GetString())
    Assert.Equal(2, requests[0].GetProperty("outputDimensionality").GetInt32())
    Assert.Equal<float>([| 3.0; 4.0 |], result.Embeddings[1].Vector)

[<Fact>]
let ``OpenAI continuation sends only tool outputs with previous response ID`` () =
    let response =
        """{"id":"resp_456","model":"gpt-5.4","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"done"}]}],"usage":{"input_tokens":4,"output_tokens":1}}"""

    let handler = new StubHttpHandler([ HttpStatusCode.OK, response ])
    use httpClient = new HttpClient(handler)

    let adapter =
        OpenAIAdapter("secret", httpClient = httpClient, responsesBaseUrl = "https://mock.test/v1/responses")

    let client = Client()
    client.RegisterAdapter(adapter)

    let baseRequest =
        { Request.Create("gpt-5.4", [ Message.User("original history") ]) with
            Provider = Some "openai" }

    let result =
        client.ContinueToolOutputs(
            ToolContinuationRequest.Create(baseRequest, "resp_123", [ toolResult "tool output" ])
        )

    let captured = handler.Requests |> List.exactlyOne
    use body = JsonDocument.Parse(captured.Body)
    let input = body.RootElement.GetProperty("input")

    Assert.Equal("done", result.Text)
    Assert.Equal("resp_123", body.RootElement.GetProperty("previous_response_id").GetString())
    Assert.Equal(1, input.GetArrayLength())
    Assert.Equal("function_call_output", input[0].GetProperty("type").GetString())
    Assert.Equal("call_1", input[0].GetProperty("call_id").GetString())
    Assert.Equal("tool output", input[0].GetProperty("output").GetString())

[<Fact>]
let ``OpenAI streaming response preserves nested response ID for continuation`` () =
    let response =
        """event: response.created
data: {"type":"response.created","response":{"id":"resp_stream","model":"gpt-5.4"}}

event: response.completed
data: {"type":"response.completed","response":{"id":"resp_stream","model":"gpt-5.4","status":"completed","usage":{"input_tokens":2,"output_tokens":1}}}

"""

    let handler = new StubHttpHandler([ HttpStatusCode.OK, response ])
    use httpClient = new HttpClient(handler)

    let adapter =
        OpenAIAdapter("secret", httpClient = httpClient, responsesBaseUrl = "https://mock.test/v1/responses")

    let request =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            Provider = Some "openai" }

    let events = (adapter :> IProviderAdapter).Stream(request) |> Seq.toList

    let completed =
        events
        |> List.choose (function
            | Finish(_, _, Some response) -> Some response
            | _ -> None)
        |> List.exactlyOne

    Assert.Equal(Some "resp_stream", completed.ResponseId)
    Assert.Equal("gpt-5.4", completed.Model)

[<Fact>]
let ``automatic tool loop prefers response continuation when available`` () =
    let adapter = ContinuationMockAdapter()
    let client = Client()
    let observedRequests = ResizeArray<Request>()
    client.RegisterAdapter(adapter)

    client.AddMiddlewareFn(
        { Complete =
            fun request next ->
                observedRequests.Add(request)
                next request
          Stream = fun request next -> next request }
    )

    let tool =
        { Definition =
            { Name = "lookup"
              Description = "lookup"
              Parameters = """{"type":"object"}""" }
          Execute = Some(fun _ -> "result") }

    let result =
        Generation.generate
            client
            "model"
            (Some "question")
            None
            None
            (Some [ tool ])
            3
            (Some "continuation-test")
            None
            None

    Assert.Equal("continued", result.Text)
    Assert.Equal(1, adapter.CompleteCalls)
    let continuation = adapter.Continuations |> List.exactlyOne
    Assert.Equal("resp_123", continuation.PreviousResponseId)
    Assert.Equal("result", continuation.ToolResults.Head.Content)
    Assert.Equal(2, observedRequests.Count)
    Assert.Equal(Some "resp_123", observedRequests[1].PreviousResponseId)
    Assert.All(observedRequests[1].Messages, fun message -> Assert.Equal(Role.Tool, message.Role))

[<Fact>]
let ``automatic streaming tool loop prefers response continuation when available`` () =
    let adapter = StreamingContinuationMockAdapter()
    let client = Client()
    client.RegisterAdapter(adapter)

    let tool =
        { Definition =
            { Name = "lookup"
              Description = "lookup"
              Parameters = """{"type":"object"}""" }
          Execute = Some(fun _ -> "result") }

    let events =
        Generation.streamWithControl
            client
            "model"
            (Some "question")
            None
            None
            (Some [ tool ])
            3
            (Some "stream-continuation-test")
            None
            None
            None
            None
        |> Seq.toList

    Assert.Equal(1, adapter.StreamCalls)
    Assert.Equal(1, adapter.ContinuationCalls)

    Assert.Contains(
        events,
        fun event ->
            match event with
            | TextDelta(_, "stream continued") -> true
            | _ -> false
    )
