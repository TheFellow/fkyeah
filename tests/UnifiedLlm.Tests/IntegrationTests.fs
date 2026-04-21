module UnifiedLlm.IntegrationTests

open System
open System.IO
open Xunit
open UnifiedLlm

let private makeResponse (request: Request) (text: string) (usage: Usage) =
    { Id = Guid.NewGuid().ToString("N")
      Model = request.Model
      Provider = request.Provider |> Option.defaultValue "openai"
      Message = Message.Assistant(text)
      FinishReason = Stop "stop"
      Usage = usage
      ResponseId = None
      Raw = None
      Warnings = []
      RateLimit = None }

let private recorder () =
    let events = ResizeArray<ObservabilityEvent>()
    { Emit = events.Add }, events

let private requestFor provider model prompt =
    { Request.Create(model, [ Message.User(prompt) ]) with
        Provider = Some provider }

let private eventName event =
    match event with
    | ObservabilityEvent.RequestStarted _ -> "request_started"
    | ObservabilityEvent.RequestCompleted _ -> "request_completed"
    | ObservabilityEvent.StreamStarted _ -> "stream_started"
    | ObservabilityEvent.StreamCompleted _ -> "stream_completed"
    | ObservabilityEvent.ValidationFailed _ -> "validation_failed"
    | ObservabilityEvent.CacheHit _ -> "cache_hit"
    | ObservabilityEvent.CacheMiss _ -> "cache_miss"
    | ObservabilityEvent.CacheStored _ -> "cache_stored"
    | ObservabilityEvent.BreakerStateChanged _ -> "breaker_state_changed"
    | ObservabilityEvent.CostRecorded _ -> "cost_recorded"
    | ObservabilityEvent.CheckpointSaved _ -> "checkpoint_saved"
    | ObservabilityEvent.CheckpointLoaded _ -> "checkpoint_loaded"
    | ObservabilityEvent.PipelineNodeStarted _ -> "pipeline_node_started"
    | ObservabilityEvent.PipelineNodeCompleted _ -> "pipeline_node_completed"
    | ObservabilityEvent.PipelineTotalUpdated _ -> "pipeline_total_updated"
    | ObservabilityEvent.ToolCacheHit _ -> "tool_cache_hit"
    | ObservabilityEvent.ToolCacheMiss _ -> "tool_cache_miss"

[<Fact>]
let ``full middleware pipeline caches identical complete requests`` () =
    CircuitBreakerRegistry.reset ()
    let sink, events = recorder ()
    let ledger = CostLedger.inMemory ()

    let cacheDir =
        Path.Combine(Path.GetTempPath(), "fkyeah-integration-cache-" + Guid.NewGuid().ToString("N"))

    try
        let adapterCalls = ref 0
        let mock = ConfigurableMockAdapter("openai")

        mock.SetCompleteHandler(fun request ->
            adapterCalls.Value <- adapterCalls.Value + 1

            makeResponse
                request
                "cached answer"
                { InputTokens = 120
                  OutputTokens = 40
                  ReasoningTokens = None
                  CacheReadTokens = None
                  CacheWriteTokens = Some 120 })

        let client = Client()
        client.RegisterAdapter(mock)
        client.AddMiddlewareFn(Middleware.validation (RequestValidator.fromCatalog ()) sink)
        client.AddMiddlewareFn(Middleware.circuitBreaker CircuitBreakerConfig.Default sink)

        client.AddMiddlewareFn(
            Middleware.cache
                (CacheStore.fileSystem
                    { CacheConfig.Default with
                        PersistencePath = Some cacheDir })
                sink
        )

        client.AddMiddlewareFn(Middleware.observability sink (Some ledger))

        let request = requestFor "openai" "gpt-5.4" "hello"
        let first = client.Complete(request)
        let second = client.Complete(request)

        Assert.Equal("cached answer", first.Text)
        Assert.Equal("cached answer", second.Text)
        Assert.Equal(1, adapterCalls.Value)
        Assert.Equal(1, ledger.CallCount())

        Assert.Contains(
            events,
            fun event ->
                match event with
                | ObservabilityEvent.CacheMiss _ -> true
                | _ -> false
        )

        Assert.Contains(
            events,
            fun event ->
                match event with
                | ObservabilityEvent.CacheStored _ -> true
                | _ -> false
        )

        Assert.Contains(
            events,
            fun event ->
                match event with
                | ObservabilityEvent.CacheHit _ -> true
                | _ -> false
        )
    finally
        if Directory.Exists(cacheDir) then
            Directory.Delete(cacheDir, true)

[<Fact>]
let ``validation middleware rejects invalid models before provider dispatch`` () =
    CircuitBreakerRegistry.reset ()
    let sink, events = recorder ()
    let adapterCalls = ref 0
    let mock = ConfigurableMockAdapter("openai")

    mock.SetCompleteHandler(fun request ->
        adapterCalls.Value <- adapterCalls.Value + 1
        makeResponse request "should not happen" Usage.Zero)

    let client = Client()
    client.RegisterAdapter(mock)
    client.AddMiddlewareFn(Middleware.validation (RequestValidator.fromCatalog ()) sink)

    let request = requestFor "openai" "definitely-not-a-real-model" "hello"

    let ex =
        Assert.Throws<ValidationError>(fun () -> client.Complete(request) |> ignore)

    Assert.Contains("Unknown model", ex.Message)
    Assert.Equal(0, adapterCalls.Value)

    Assert.Contains(
        events,
        fun event ->
            match event with
            | ObservabilityEvent.ValidationFailed _ -> true
            | _ -> false
    )

[<Fact>]
let ``circuit breaker middleware opens and later recovers after cooldown`` () =
    CircuitBreakerRegistry.reset ()
    let sink, events = recorder ()
    let provider = "breaker-provider"

    let config =
        { FailureThreshold = 2
          CooldownPeriod = TimeSpan.FromMilliseconds(100.0)
          ProbeSuccessThreshold = 2 }

    let adapterCalls = ref 0
    let mock = ConfigurableMockAdapter(provider)

    mock.SetCompleteHandler(fun _ ->
        adapterCalls.Value <- adapterCalls.Value + 1
        raise (ProviderError("transient failure", Some 500, true)))

    let client = Client()
    client.RegisterAdapter(mock)
    client.AddMiddlewareFn(Middleware.validation (RequestValidator.fromCatalog ()) sink)
    client.AddMiddlewareFn(Middleware.circuitBreaker config sink)

    let request = requestFor provider "gpt-5.4" "hello"

    Assert.Throws<ProviderError>(fun () -> client.Complete(request) |> ignore)
    |> ignore

    Assert.Throws<ProviderError>(fun () -> client.Complete(request) |> ignore)
    |> ignore

    Assert.Throws<CircuitOpenError>(fun () -> client.Complete(request) |> ignore)
    |> ignore

    Assert.Equal(2, adapterCalls.Value)

    System.Threading.Thread.Sleep(400) // cooldown (100ms) + generous CI margin

    mock.SetCompleteHandler(fun successRequest ->
        adapterCalls.Value <- adapterCalls.Value + 1
        makeResponse successRequest "recovered" Usage.Zero)

    let firstProbe = client.Complete(request)
    let secondProbe = client.Complete(request)
    let breaker = CircuitBreakerRegistry.getOrCreate provider config
    let state = Async.RunSynchronously breaker.State

    Assert.Equal("recovered", firstProbe.Text)
    Assert.Equal("recovered", secondProbe.Text)
    Assert.Equal(CircuitState.Closed 0, state)

    Assert.Contains(
        events,
        fun event ->
            match event with
            | ObservabilityEvent.BreakerStateChanged _ -> true
            | _ -> false
    )

[<Fact>]
let ``observability middleware emits cost events in order`` () =
    let sink, events = recorder ()
    let ledger = CostLedger.inMemory ()
    let mock = ConfigurableMockAdapter("openai")

    mock.SetCompleteHandler(fun request ->
        makeResponse
            request
            "done"
            { InputTokens = 1000
              OutputTokens = 250
              ReasoningTokens = Some 10
              CacheReadTokens = None
              CacheWriteTokens = None })

    let client = Client()
    client.RegisterAdapter(mock)
    client.AddMiddlewareFn(Middleware.observability sink (Some ledger))

    let response = client.Complete(requestFor "openai" "gpt-5.4" "measure cost")
    let names = events |> Seq.map eventName |> Seq.toList

    Assert.Equal("done", response.Text)
    Assert.Equal<string list>([ "request_started"; "cost_recorded"; "request_completed" ], names)
    Assert.True(ledger.TotalMicrodollars() > 0L)

[<Fact>]
let ``streaming pipeline replays cache hits without calling provider twice`` () =
    CircuitBreakerRegistry.reset ()
    let sink, _ = recorder ()

    let cacheDir =
        Path.Combine(Path.GetTempPath(), "fkyeah-stream-cache-" + Guid.NewGuid().ToString("N"))

    try
        let adapterCalls = ref 0

        let usage =
            { InputTokens = 80
              OutputTokens = 20
              ReasoningTokens = None
              CacheReadTokens = None
              CacheWriteTokens = None }

        let mock = ConfigurableMockAdapter("openai")

        mock.SetStreamHandler(fun request ->
            adapterCalls.Value <- adapterCalls.Value + 1
            let response = makeResponse request "streamed answer" usage

            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", response.Text)
                yield TextEnd "text-1"
                yield StepFinish(0, Some response)
                yield Finish(response.FinishReason, Some response.Usage, Some response)
            })

        let client = Client()
        client.RegisterAdapter(mock)
        client.AddMiddlewareFn(Middleware.validation (RequestValidator.fromCatalog ()) sink)

        client.AddMiddlewareFn(
            Middleware.cache
                (CacheStore.fileSystem
                    { CacheConfig.Default with
                        PersistencePath = Some cacheDir })
                sink
        )

        let request = requestFor "openai" "gpt-5.4" "stream me"
        let first = client.Stream(request) |> Seq.toList
        let second = client.Stream(request) |> Seq.toList

        Assert.Equal(1, adapterCalls.Value)

        Assert.Contains(
            first,
            fun event ->
                match event with
                | Finish _ -> true
                | _ -> false
        )

        Assert.Contains(
            second,
            fun event ->
                match event with
                | StepFinish(_, Some response) when response.Text = "streamed answer" -> true
                | _ -> false
        )

        Assert.Contains(
            second,
            fun event ->
                match event with
                | Finish(_, Some finishUsage, Some response) when
                    finishUsage.CacheReadTokens.IsSome && response.Text = "streamed answer"
                    ->
                    true
                | _ -> false
        )
    finally
        if Directory.Exists(cacheDir) then
            Directory.Delete(cacheDir, true)

// ── New Sprint-010 tests ──

[<Fact>]
let ``cost tracker accumulates totals from multiple distinct calls`` () =
    CircuitBreakerRegistry.reset ()
    let sink, _ = recorder ()
    let ledger = CostLedger.inMemory ()
    let mock = ConfigurableMockAdapter("openai")

    mock.SetCompleteHandler(fun request ->
        makeResponse
            request
            "costed"
            { InputTokens = 100
              OutputTokens = 50
              ReasoningTokens = None
              CacheReadTokens = None
              CacheWriteTokens = None })

    let client = Client()
    client.RegisterAdapter(mock)
    client.AddMiddlewareFn(Middleware.observability sink (Some ledger))

    // Make 3 distinct calls (different prompts so no caching concerns)
    for i in 1..3 do
        client.Complete(requestFor "openai" "gpt-5.4" $"call-{i}") |> ignore

    Assert.Equal(3, ledger.CallCount())
    // All 3 calls have the same cost, so total should be 3x the individual
    let singleCost =
        Costing.tryCalculateCostById
            "gpt-5.4"
            { InputTokens = 100
              OutputTokens = 50
              ReasoningTokens = None
              CacheReadTokens = None
              CacheWriteTokens = None }
            false
        |> Option.get

    Assert.Equal(singleCost.TotalMicrodollars * 3L, ledger.TotalMicrodollars())

[<Fact>]
let ``full pipeline event ordering follows expected sequence`` () =
    CircuitBreakerRegistry.reset ()
    let sink, events = recorder ()
    let ledger = CostLedger.inMemory ()

    let cacheDir =
        Path.Combine(Path.GetTempPath(), "fkyeah-order-test-" + Guid.NewGuid().ToString("N"))

    try
        let mock = ConfigurableMockAdapter("openai")

        mock.SetCompleteHandler(fun request ->
            makeResponse
                request
                "ordering"
                { InputTokens = 10
                  OutputTokens = 5
                  ReasoningTokens = None
                  CacheReadTokens = None
                  CacheWriteTokens = None })

        let client = Client()
        client.RegisterAdapter(mock)
        client.AddMiddlewareFn(Middleware.validation (RequestValidator.fromCatalog ()) sink)
        client.AddMiddlewareFn(Middleware.circuitBreaker CircuitBreakerConfig.Default sink)

        client.AddMiddlewareFn(
            Middleware.cache
                (CacheStore.fileSystem
                    { CacheConfig.Default with
                        PersistencePath = Some cacheDir })
                sink
        )

        client.AddMiddlewareFn(Middleware.observability sink (Some ledger))

        client.Complete(requestFor "openai" "gpt-5.4" "event-order") |> ignore

        let names = events |> Seq.map eventName |> Seq.toList

        // request_started should come before request_completed
        let startIdx = names |> List.tryFindIndex ((=) "request_started")
        let completeIdx = names |> List.tryFindIndex ((=) "request_completed")
        Assert.True(startIdx.IsSome, "expected request_started event")
        Assert.True(completeIdx.IsSome, "expected request_completed event")
        Assert.True(startIdx.Value < completeIdx.Value, "request_started should precede request_completed")

        // cache_miss should appear (first call on fresh store)
        Assert.Contains("cache_miss", names)
    finally
        if Directory.Exists(cacheDir) then
            Directory.Delete(cacheDir, true)
