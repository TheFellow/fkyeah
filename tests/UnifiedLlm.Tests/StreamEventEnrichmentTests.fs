module UnifiedLlm.StreamEventEnrichmentTests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open UnifiedLlm

type private StreamingHandler(status: HttpStatusCode, body: string) =
    inherit HttpMessageHandler()

    member private _.Respond() =
        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(body, Encoding.UTF8, "text/event-stream")
        response

    override this.Send(_request, _cancellationToken) = this.Respond()
    override this.SendAsync(_request, _cancellationToken) = Task.FromResult(this.Respond())

type private EventAsyncEnumerable(source: StreamEvent list) =
    interface IAsyncEnumerable<StreamEvent> with
        member _.GetAsyncEnumerator(_cancellationToken: CancellationToken) =
            let enumerator = (source :> seq<_>).GetEnumerator()

            { new IAsyncEnumerator<StreamEvent> with
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

let private indexOf predicate events = events |> List.findIndex predicate

[<Fact>]
let ``OpenAI emits lifecycle refusal audio usage and preserves unknown raw events in order`` () =
    let audio = Convert.ToBase64String([| 1uy; 2uy; 3uy |])

    let payload =
        $"""event: response.created
data: {{"response":{{"id":"resp-rich","model":"gpt-5.4","status":"in_progress"}}}}

event: response.refusal.delta
data: {{"delta":"cannot"}}

event: response.refusal.done
data: {{"refusal":"cannot"}}

event: response.audio.delta
data: {{"delta":"{audio}","sequence_number":1}}

event: response.audio.transcript.delta
data: {{"delta":"spoken","sequence_number":2}}

event: response.audio.done
data: {{"sequence_number":3}}

event: response.unknown.future
data: {{"future":true}}

event: response.completed
data: {{"response":{{"id":"resp-rich","model":"gpt-5.4","status":"completed","usage":{{"input_tokens":7,"output_tokens":2}}}}}}

"""

    let handler = new StreamingHandler(HttpStatusCode.OK, payload)
    use client = new HttpClient(handler)

    let adapter =
        OpenAIAdapter("key", httpClient = client, responsesBaseUrl = "https://mock/responses")

    let events =
        (adapter :> IProviderAdapter).Stream(Request.Create("gpt-5.4", [ Message.User("go") ]))
        |> Seq.toList

    let createdIndex =
        indexOf
            (function
            | ResponseCreated _ -> true
            | _ -> false)
            events

    let refusalIndex =
        indexOf
            (function
            | RefusalDelta("cannot", false) -> true
            | _ -> false)
            events

    let audioIndex =
        indexOf
            (function
            | AudioDelta value when value.Data = [| 1uy; 2uy; 3uy |] -> true
            | _ -> false)
            events

    let usageIndex =
        indexOf
            (function
            | UsageDelta value when value.Total |> Option.exists (fun usage -> usage.InputTokens = 7) -> true
            | _ -> false)
            events

    let finishIndex =
        indexOf
            (function
            | Finish _ -> true
            | _ -> false)
            events

    Assert.True(
        createdIndex < refusalIndex
        && refusalIndex < audioIndex
        && audioIndex < usageIndex
        && usageIndex < finishIndex
    )

    Assert.Contains(
        events,
        function
        | ProviderEvent("response.unknown.future", _) -> true
        | _ -> false
    )

    let response =
        events
        |> List.choose (function
            | Finish(_, _, Some value) -> Some value
            | _ -> None)
        |> List.exactlyOne

    Assert.Equal(ContentFilter "refusal", response.FinishReason)
    Assert.Equal(7, response.Usage.InputTokens)

    Assert.Contains(
        response.Message.Content,
        function
        | Audio value -> value.Data = Some [| 1uy; 2uy; 3uy |]
        | _ -> false
    )

    Assert.Contains(response.Warnings, fun warning -> warning.Contains("Audio transcript: spoken"))

[<Fact>]
let ``OpenAI emits requires-action and response-error metadata`` () =
    let payload =
        """event: response.created
data: {"response":{"id":"resp-action","model":"gpt-5.4","status":"in_progress"}}

event: response.requires_action
data: {"response":{"id":"resp-action","model":"gpt-5.4","status":"requires_action","required_action":{"type":"submit_tool_outputs"}}}

event: response.failed
data: {"response":{"id":"resp-action","model":"gpt-5.4","status":"failed","error":{"code":"provider_error","message":"failed upstream"}}}

"""

    let handler = new StreamingHandler(HttpStatusCode.OK, payload)
    use client = new HttpClient(handler)

    let adapter =
        OpenAIAdapter("key", httpClient = client, responsesBaseUrl = "https://mock/responses")

    let events =
        (adapter :> IProviderAdapter).Stream(Request.Create("gpt-5.4", [ Message.User("go") ]))
        |> Seq.toList

    Assert.Contains(
        events,
        function
        | ResponseRequiresAction value ->
            value.Response.Id = Some "resp-action"
            && value.Action = Some "submit_tool_outputs"
        | _ -> false
    )

    Assert.Contains(
        events,
        function
        | ResponseError value -> value.Code = Some "provider_error" && value.Message = "failed upstream"
        | _ -> false
    )

[<Fact>]
let ``Anthropic emits created and incremental usage without changing final total`` () =
    let payload =
        """event: message_start
data: {"message":{"id":"msg-1","model":"claude-opus-4-6","type":"message","usage":{"input_tokens":5,"cache_read_input_tokens":2}}}

event: message_delta
data: {"delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3,"cache_read_input_tokens":2}}

event: message_stop
data: {}

"""

    let handler = new StreamingHandler(HttpStatusCode.OK, payload)
    use client = new HttpClient(handler)

    let adapter =
        AnthropicAdapter("key", httpClient = client, apiBaseUrl = "https://mock/messages")

    let events =
        (adapter :> IProviderAdapter).Stream(Request.Create("claude-opus-4-6", [ Message.User("go") ]))
        |> Seq.toList

    Assert.Contains(
        events,
        function
        | ResponseCreated value -> value.Id = Some "msg-1"
        | _ -> false
    )

    let updates =
        events
        |> List.choose (function
            | UsageDelta value -> Some value
            | _ -> None)

    Assert.Equal(2, updates.Length)
    Assert.Equal(5, updates[0].Delta.InputTokens)
    Assert.Equal(3, updates[1].Delta.OutputTokens)

    let response =
        events
        |> List.choose (function
            | Finish(_, _, Some value) -> Some value
            | _ -> None)
        |> List.exactlyOne

    Assert.Equal(5, response.Usage.InputTokens)
    Assert.Equal(3, response.Usage.OutputTokens)

[<Fact>]
let ``Gemini emits usage delta from usage metadata`` () =
    let payload =
        """data: {"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"ok"}]}}],"usageMetadata":{"promptTokenCount":4,"candidatesTokenCount":2,"thoughtsTokenCount":1}}

"""

    let handler = new StreamingHandler(HttpStatusCode.OK, payload)
    use client = new HttpClient(handler)

    let adapter =
        GeminiAdapter("key", httpClient = client, apiBaseUrl = "https://mock/v1beta")

    let events =
        (adapter :> IProviderAdapter).Stream(Request.Create("gemini-3-flash-preview", [ Message.User("go") ]))
        |> Seq.toList

    Assert.Contains(
        events,
        function
        | UsageDelta value ->
            value.Delta.InputTokens = 4
            && value.Delta.OutputTokens = 2
            && value.Total |> Option.exists (fun usage -> usage.ReasoningTokens = Some 1)
        | _ -> false
    )

[<Fact>]
let ``OpenRouter emits refusal and trailing usage updates`` () =
    let payload =
        """data: {"id":"or-1","model":"openai/gpt-5","choices":[{"delta":{"refusal":"no"},"finish_reason":"content_filter"}]}

data: {"id":"or-1","choices":[],"usage":{"prompt_tokens":6,"completion_tokens":1}}

data: [DONE]

"""

    let handler = new StreamingHandler(HttpStatusCode.OK, payload)
    use client = new HttpClient(handler)

    let adapter =
        OpenRouterAdapter("key", httpClient = client, apiBaseUrl = "https://mock")

    let request =
        { Request.Create("openai/gpt-5", [ Message.User("go") ]) with
            Provider = Some "openrouter" }

    let events = (adapter :> IProviderAdapter).Stream(request) |> Seq.toList
    Assert.Contains(RefusalDelta("no", false), events)

    Assert.Contains(
        events,
        function
        | UsageDelta value -> value.Delta.InputTokens = 6
        | _ -> false
    )

[<Fact>]
let ``accumulator uses cumulative usage totals and preserves audio refusal metadata`` () =
    let first = { Usage.Zero with InputTokens = 4 }
    let total = { first with OutputTokens = 3 }

    let events =
        [ ResponseCreated
              { Id = Some "response-1"
                Model = Some "model-1"
                Provider = "provider-1"
                Status = "in_progress"
                Raw = None }
          UsageDelta { Delta = first; Total = Some first }
          UsageDelta
              { Delta = { Usage.Zero with OutputTokens = 3 }
                Total = Some total }
          RefusalDelta("cannot", false)
          RefusalDelta("cannot", true)
          AudioDelta
              { Data = [| 9uy |]
                Transcript = Some "spoken"
                Sequence = Some 1
                MediaType = Some "audio/pcm"
                Final = false }
          Finish(ContentFilter "refusal", None, None) ]

    let accumulator = Generation.StreamAccumulator(EventAsyncEnumerable(events))
    consumeAll accumulator.Events
    let response = accumulator.PartialResponse()
    Assert.Equal(4, response.Usage.InputTokens)
    Assert.Equal(3, response.Usage.OutputTokens)
    Assert.Equal(Some "response-1", response.ResponseId)

    Assert.Contains(
        response.Message.Content,
        function
        | Audio value -> value.Data = Some [| 9uy |]
        | _ -> false
    )

    Assert.Contains(response.Warnings, fun warning -> warning = "Model refusal: cannot")

[<Fact>]
let ``cache replay emits lifecycle audio and usage before finish`` () =
    let usage =
        { Usage.Zero with
            InputTokens = 8
            OutputTokens = 2 }

    let response =
        { Id = "cached-1"
          Model = "model"
          Provider = "provider"
          Message =
            { Role = Assistant
              Content =
                [ Audio
                      { Url = None
                        Data = Some [| 4uy |]
                        MediaType = Some "audio/pcm" } ]
              Name = None
              ToolCallId = None }
          FinishReason = Stop "stop"
          Usage = usage
          ResponseId = Some "cached-1"
          Raw = None
          Warnings = []
          RateLimit = None }

    let events = Caching.replayStreamFromCachedResponse response |> Seq.toList

    let created =
        indexOf
            (function
            | ResponseCreated value -> value.Status = "cached"
            | _ -> false)
            events

    let audio =
        indexOf
            (function
            | AudioDelta value -> value.Final && value.Data = [| 4uy |]
            | _ -> false)
            events

    let usage =
        indexOf
            (function
            | UsageDelta value -> value.Total = Some response.Usage
            | _ -> false)
            events

    let finish =
        indexOf
            (function
            | Finish _ -> true
            | _ -> false)
            events

    Assert.True(created < audio && audio < usage && usage < finish)
