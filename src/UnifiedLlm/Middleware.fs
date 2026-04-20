namespace UnifiedLlm

open System
open System.Diagnostics

/// Backward-compatible middleware interface for processing requests and responses
type IMiddleware =
    /// Process a request before it is sent to the provider.
    /// Call next to continue the chain.
    abstract member Process: request: Request * next: (Request -> Response) -> Response

    /// Process a streaming request before it is sent to the provider.
    /// Call next to continue the chain.
    abstract member ProcessStream: request: Request * next: (Request -> StreamEvent seq) -> StreamEvent seq

/// Functional middleware for complete and streaming requests
type MiddlewareFn = Request -> (Request -> Response) -> Response
type StreamMiddlewareFn = Request -> (Request -> StreamEvent seq) -> StreamEvent seq

type Middleware =
    { Complete: MiddlewareFn
      Stream: StreamMiddlewareFn }

module Middleware =

    let private correlationId (request: Request) =
        request.Metadata
        |> Option.bind (Map.tryFind "correlation_id")
        |> Option.defaultValue (Guid.NewGuid().ToString("N"))

    let private providerForRequest (request: Request) =
        request.Provider
        |> Option.orElseWith (fun () ->
            ModelCatalog.tryResolveModel request.Model
            |> Option.map (fun model -> model.Provider))
        |> Option.defaultValue "default"

    let private withCacheReadUsage (response: Response) =
        { response with
            Usage =
                { response.Usage with
                    CacheReadTokens =
                        Some(
                            match response.Usage.CacheReadTokens with
                            | Some tokens when tokens > 0 -> tokens
                            | _ -> response.Usage.InputTokens
                        ) } }

    let private validationErrorMessage (issues: ValidationIssue list) =
        issues |> List.map ValidationIssue.describe |> String.concat "; "

    /// Create middleware from a request transform.
    let fromRequestTransform (transform: Request -> Request) : Middleware =
        { Complete = fun req next -> next (transform req)
          Stream = fun req next -> next (transform req) }

    /// Create middleware that only wraps complete requests.
    let fromComplete (fn: MiddlewareFn) : Middleware =
        { Complete = fn
          Stream = fun req next -> next req }

    /// Wrap a legacy interface middleware as functional middleware.
    let ofInterface (mw: IMiddleware) : Middleware =
        { Complete = fun req next -> mw.Process(req, next)
          Stream = fun req next -> mw.ProcessStream(req, next) }

    let validation (validator: RequestValidator) (sink: ObservabilitySink) : Middleware =
        let validate request =
            let cid = correlationId request

            match validator.Validate request with
            | Result.Ok validated -> validated
            | Result.Error issues ->
                sink.Emit(ObservabilityEvent.ValidationFailed(cid, issues |> List.map ValidationIssue.describe))
                raise (ValidationError(validationErrorMessage issues))

        { Complete = fun req next -> next (validate req)
          Stream = fun req next -> next (validate req) }

    let cache (store: CacheStore) (sink: ObservabilitySink) : Middleware =
        { Complete =
            fun request next ->
                let cid = correlationId request
                let key = CacheKey.fromRequest request
                let keyValue = CacheKey.value key

                match Async.RunSynchronously(store.TryGetLlm key) with
                | Some entry ->
                    sink.Emit(ObservabilityEvent.CacheHit(cid, keyValue, request.Model))
                    withCacheReadUsage entry.Response
                | None ->
                    sink.Emit(ObservabilityEvent.CacheMiss(cid, keyValue, request.Model))
                    let response = next request

                    Async.RunSynchronously(
                        store.PutLlm
                            key
                            { Response = response
                              StoredAt = DateTimeOffset.UtcNow
                              Metadata = Map.ofList [ "model", request.Model ] }
                    )

                    sink.Emit(ObservabilityEvent.CacheStored(cid, keyValue, request.Model))
                    response
          Stream =
            fun request next ->
                let cid = correlationId request
                let key = CacheKey.fromRequest request
                let keyValue = CacheKey.value key

                match Async.RunSynchronously(store.TryGetLlm key) with
                | Some entry ->
                    sink.Emit(ObservabilityEvent.CacheHit(cid, keyValue, request.Model))
                    let response = withCacheReadUsage entry.Response
                    Caching.replayStreamFromCachedResponse response
                | None ->
                    sink.Emit(ObservabilityEvent.CacheMiss(cid, keyValue, request.Model))

                    seq {
                        let mutable finalResponse: Response option = None
                        let events = next request |> Seq.cache

                        for event in events do
                            match event with
                            | StepFinish(_, Some response) -> finalResponse <- Some response
                            | Finish(_, _, Some response) -> finalResponse <- Some response
                            | _ -> ()

                            yield event

                        match finalResponse with
                        | Some response ->
                            Async.RunSynchronously(
                                store.PutLlm
                                    key
                                    { Response = response
                                      StoredAt = DateTimeOffset.UtcNow
                                      Metadata = Map.ofList [ "model", request.Model ] }
                            )

                            sink.Emit(ObservabilityEvent.CacheStored(cid, keyValue, request.Model))
                        | None -> ()
                    } }

    let observability (sink: ObservabilitySink) (ledger: CostLedger option) : Middleware =
        let recordCost cid model usage cacheHit =
            Costing.tryCalculateCostById model usage cacheHit
            |> Option.map (fun cost ->
                ledger |> Option.iter (fun current -> current.Record cost)
                sink.Emit(ObservabilityEvent.CostRecorded(cid, model, cost.TotalMicrodollars, cost.CacheHit))
                cost.TotalMicrodollars)

        { Complete =
            fun request next ->
                let cid = correlationId request
                let stopwatch = Stopwatch.StartNew()
                sink.Emit(ObservabilityEvent.RequestStarted(cid, request.Model, request.Messages.Length))
                let response = next request
                stopwatch.Stop()
                let cacheHit = response.Usage.CacheReadTokens |> Option.defaultValue 0 > 0
                let totalMicros = recordCost cid response.Model response.Usage cacheHit

                sink.Emit(
                    ObservabilityEvent.RequestCompleted(
                        cid,
                        response.Model,
                        stopwatch.ElapsedMilliseconds,
                        response.Usage,
                        totalMicros
                    )
                )

                response
          Stream =
            fun request next ->
                let cid = correlationId request
                let stopwatch = Stopwatch.StartNew()
                sink.Emit(ObservabilityEvent.StreamStarted(cid, request.Model))

                seq {
                    for event in next request do
                        match event with
                        | Finish(_, Some usage, responseOpt) ->
                            stopwatch.Stop()

                            let model =
                                responseOpt
                                |> Option.map (fun response -> response.Model)
                                |> Option.defaultValue request.Model

                            let cacheHit = usage.CacheReadTokens |> Option.defaultValue 0 > 0
                            let totalMicros = recordCost cid model usage cacheHit

                            sink.Emit(
                                ObservabilityEvent.StreamCompleted(
                                    cid,
                                    model,
                                    stopwatch.ElapsedMilliseconds,
                                    usage,
                                    totalMicros
                                )
                            )
                        | Finish(_, None, Some response) ->
                            stopwatch.Stop()
                            let cacheHit = response.Usage.CacheReadTokens |> Option.defaultValue 0 > 0
                            let totalMicros = recordCost cid response.Model response.Usage cacheHit

                            sink.Emit(
                                ObservabilityEvent.StreamCompleted(
                                    cid,
                                    response.Model,
                                    stopwatch.ElapsedMilliseconds,
                                    response.Usage,
                                    totalMicros
                                )
                            )
                        | _ -> ()

                        yield event
                } }

    let circuitBreaker (config: CircuitBreakerConfig) (sink: ObservabilitySink) : Middleware =
        let before request =
            let provider = providerForRequest request
            let breaker = CircuitBreakerRegistry.getOrCreate provider config
            let oldState = Async.RunSynchronously breaker.State

            match Async.RunSynchronously(breaker.Check()) with
            | Result.Ok() ->
                let newState = Async.RunSynchronously breaker.State

                if oldState <> newState then
                    sink.Emit(ObservabilityEvent.BreakerStateChanged(provider, oldState, newState))

                provider, breaker
            | Result.Error error ->
                let newState = Async.RunSynchronously breaker.State

                if oldState <> newState then
                    sink.Emit(ObservabilityEvent.BreakerStateChanged(provider, oldState, newState))

                raise error

        let after (provider: string) (breaker: CircuitBreaker) (action: unit -> 'T) : 'T =
            try
                let response = action ()
                let oldState = Async.RunSynchronously breaker.State
                breaker.RecordSuccess()
                let newState = Async.RunSynchronously breaker.State

                if oldState <> newState then
                    sink.Emit(ObservabilityEvent.BreakerStateChanged(provider, oldState, newState))

                response
            with :? ProviderError as error ->
                let oldState = Async.RunSynchronously breaker.State
                breaker.RecordFailure error.Kind
                let newState = Async.RunSynchronously breaker.State

                if oldState <> newState then
                    sink.Emit(ObservabilityEvent.BreakerStateChanged(provider, oldState, newState))

                raise error

        { Complete =
            fun request next ->
                let provider, breaker = before request
                after provider breaker (fun () -> next request)
          Stream =
            fun request next ->
                let provider, breaker = before request
                after provider breaker (fun () -> next request) }

/// Immutable middleware pipeline composed in onion order.
type MiddlewarePipeline(middlewares: Middleware list) =
    let chain = middlewares

    member _.Execute(request: Request, handler: Request -> Response) : Response =
        let composed =
            (handler, List.rev chain)
            ||> List.fold (fun next mw -> fun req -> mw.Complete req next)

        composed request

    member _.ExecuteStream(request: Request, handler: Request -> StreamEvent seq) : StreamEvent seq =
        let composed =
            (handler, List.rev chain)
            ||> List.fold (fun next mw -> fun req -> mw.Stream req next)

        composed request

    member _.Count = chain.Length

/// Computation expression builder for middleware pipelines.
type MiddlewareBuilder() =
    member _.Yield(mw: Middleware) : Middleware list = [ mw ]
    member _.Yield(()) : Middleware list = []
    member _.Combine(a: Middleware list, b: Middleware list) : Middleware list = a @ b
    member _.Delay(f: unit -> Middleware list) : Middleware list = f ()
    member _.Zero() : Middleware list = []
    member _.Run(middlewares: Middleware list) : MiddlewarePipeline = MiddlewarePipeline(middlewares)

/// Backward-compatible mutable middleware chain wrapping MiddlewarePipeline.
type MiddlewareChain() =
    let middlewares = System.Collections.Generic.List<Middleware>()

    /// Add a legacy middleware to the chain.
    member _.Add(middleware: IMiddleware) =
        middlewares.Add(Middleware.ofInterface middleware)

    /// Add a functional middleware to the chain.
    member _.AddFn(middleware: Middleware) = middlewares.Add(middleware)

    /// Get count of registered middleware.
    member _.Count = middlewares.Count

    member _.Execute(request: Request, handler: Request -> Response) : Response =
        MiddlewarePipeline(middlewares |> Seq.toList).Execute(request, handler)

    member _.ExecuteStream(request: Request, handler: Request -> StreamEvent seq) : StreamEvent seq =
        MiddlewarePipeline(middlewares |> Seq.toList).ExecuteStream(request, handler)

[<AutoOpen>]
module MiddlewareBuilderExtensions =
    let middleware = MiddlewareBuilder()
