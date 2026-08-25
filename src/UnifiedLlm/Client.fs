namespace UnifiedLlm

open System.Collections.Generic

type ClientConfig =
    { Timeout: AdapterTimeout option }

    static member Default = { Timeout = None }

type private RegisteredAdapter =
    { Adapter: IProviderAdapter
      Timeout: AdapterTimeout option }

/// The core client that routes requests to provider adapters
type Client(?config: ClientConfig) =
    let adapters = Dictionary<string, RegisteredAdapter>()
    let middlewareChain = MiddlewareChain()
    let mutable defaultProvider: string option = Option.None
    let config = config |> Option.defaultValue ClientConfig.Default

    let applyAdapterTimeout (request: Request) (adapterTimeout: AdapterTimeout option) =
        if request.AdapterTimeout.IsSome then
            request
        else
            let effectiveTimeout = adapterTimeout |> Option.orElse config.Timeout

            { request with
                AdapterTimeout = effectiveTimeout }

    let tryRegisteredAdapter providerId =
        match adapters.TryGetValue(providerId) with
        | true, registered -> Some registered.Adapter
        | false, _ -> None

    /// Register a provider adapter
    member _.RegisterAdapter(adapter: IProviderAdapter, ?timeout: AdapterTimeout) =
        Async.RunSynchronously(adapter.Initialize())
        adapters.[adapter.ProviderId] <- { Adapter = adapter; Timeout = timeout }
        // First registered provider becomes default if none set
        if defaultProvider.IsNone then
            defaultProvider <- Some adapter.ProviderId

    /// Set the default provider explicitly
    member _.SetDefault(providerId: string) =
        if adapters.ContainsKey(providerId) then
            defaultProvider <- Some providerId
        else
            raise (ConfigurationError(sprintf "Provider '%s' is not registered" providerId))

    /// Add middleware
    member _.AddMiddleware(middleware: IMiddleware) = middlewareChain.Add(middleware)

    /// Add functional middleware
    member _.AddMiddlewareFn(middleware: Middleware) = middlewareChain.AddFn(middleware)

    /// Get the default provider id
    member _.DefaultProvider = defaultProvider

    /// Test whether a provider adapter is currently registered.
    member _.IsProviderRegistered(providerId: string) = adapters.ContainsKey(providerId)

    /// Resolve the adapter for a request
    member private _.ResolveAdapter(request: Request) : IProviderAdapter * AdapterTimeout option =
        let providerId =
            match request.Provider with
            | Some p -> p
            | Option.None ->
                match defaultProvider with
                | Some p -> p
                | Option.None -> raise (ConfigurationError("No provider specified and no default provider is set"))

        match adapters.TryGetValue(providerId) with
        | true, registered -> registered.Adapter, registered.Timeout
        | false, _ -> raise (ConfigurationError(sprintf "Provider '%s' is not registered" providerId))

    member private _.ResolveEmbeddingAdapter(request: EmbeddingRequest) : IEmbeddingAdapter * AdapterTimeout option =
        let providerId =
            request.Provider
            |> Option.orElseWith (fun () -> ModelCatalog.resolveModel request.Model |> Option.map _.Provider)
            |> Option.orElse defaultProvider
            |> Option.defaultWith (fun () ->
                raise (ConfigurationError("No embedding provider specified and no default provider is set")))

        match adapters.TryGetValue(providerId) with
        | false, _ -> raise (ConfigurationError(sprintf "Provider '%s' is not registered" providerId))
        | true, registered ->
            match registered.Adapter with
            | :? IEmbeddingAdapter as adapter -> adapter, registered.Timeout
            | _ -> raise (ConfigurationError(sprintf "Provider '%s' does not support embeddings" providerId))

    /// Test whether a registered provider exposes normalized embeddings.
    member _.SupportsEmbeddings(providerId: string) =
        tryRegisteredAdapter providerId
        |> Option.exists (fun adapter -> adapter :? IEmbeddingAdapter)

    /// Test whether a registered provider exposes response-ID tool continuation.
    member _.SupportsToolContinuation(providerId: string) =
        tryRegisteredAdapter providerId
        |> Option.exists (fun adapter -> adapter :? IToolContinuationAdapter)

    /// Create embeddings through a provider that implements the optional capability.
    member this.Embed(request: EmbeddingRequest) : EmbeddingResponse =
        if
            request.Inputs.IsEmpty
            || request.Inputs |> List.exists System.String.IsNullOrWhiteSpace
        then
            raise (ConfigurationError("Embedding request inputs must contain non-empty text"))

        match request.Dimensions with
        | Some dimensions when dimensions <= 0 ->
            raise (ConfigurationError("Embedding dimensions must be greater than zero"))
        | _ -> ()

        let adapter, adapterTimeout = this.ResolveEmbeddingAdapter(request)

        let effectiveRequest =
            if request.Timeout.IsSome then
                request
            else
                let timeout = adapterTimeout |> Option.orElse config.Timeout

                { request with
                    Timeout =
                        timeout
                        |> Option.bind _.RequestMs
                        |> Option.filter (fun milliseconds -> milliseconds > 0)
                        |> Option.map (fun milliseconds -> System.TimeSpan.FromMilliseconds(float milliseconds)) }

        adapter.Embed(effectiveRequest)

    /// Continue a stored provider response using tool outputs only.
    member this.ContinueToolOutputs(request: ToolContinuationRequest) : Response =
        if System.String.IsNullOrWhiteSpace(request.PreviousResponseId) then
            raise (ConfigurationError("A previous response ID is required for tool continuation"))

        if request.ToolResults.IsEmpty then
            raise (ConfigurationError("At least one tool result is required for tool continuation"))

        let handler (ordinaryRequest: Request) =
            let adapter, timeout = this.ResolveAdapter(ordinaryRequest)

            let effectiveRequest =
                { request with
                    Request = applyAdapterTimeout ordinaryRequest timeout }

            match adapter with
            | :? IToolContinuationAdapter as continuation -> continuation.ContinueToolOutputs(effectiveRequest)
            | _ ->
                raise (
                    ConfigurationError(
                        sprintf "Provider '%s' does not support response-ID tool continuation" adapter.ProviderId
                    )
                )

        middlewareChain.Execute(ToolContinuation.toRequest request, handler)

    /// Stream a stored provider response continuation using tool outputs only.
    member this.StreamToolOutputs(request: ToolContinuationRequest) : StreamEvent seq =
        if System.String.IsNullOrWhiteSpace(request.PreviousResponseId) then
            raise (ConfigurationError("A previous response ID is required for tool continuation"))

        if request.ToolResults.IsEmpty then
            raise (ConfigurationError("At least one tool result is required for tool continuation"))

        let handler (ordinaryRequest: Request) =
            let adapter, timeout = this.ResolveAdapter(ordinaryRequest)

            let effectiveRequest =
                { request with
                    Request = applyAdapterTimeout ordinaryRequest timeout }

            match adapter with
            | :? IToolContinuationAdapter as continuation -> continuation.StreamToolOutputs(effectiveRequest)
            | _ ->
                raise (
                    ConfigurationError(
                        sprintf "Provider '%s' does not support response-ID tool continuation" adapter.ProviderId
                    )
                )

        middlewareChain.ExecuteStream(ToolContinuation.toRequest request, handler)

    /// Send a blocking request through middleware chain to the provider
    member this.Complete(request: Request) : Response =
        let handler (req: Request) =
            let adapter, timeout = this.ResolveAdapter(req)
            let reqWithTimeout = applyAdapterTimeout req timeout
            adapter.Complete(reqWithTimeout)

        middlewareChain.Execute(request, handler)

    /// Send a streaming request to the provider
    member this.Stream(request: Request) : StreamEvent seq =
        let handler (req: Request) =
            let adapter, timeout = this.ResolveAdapter(req)
            let reqWithTimeout = applyAdapterTimeout req timeout
            adapter.Stream(reqWithTimeout)

        middlewareChain.ExecuteStream(request, handler)

    /// Close all adapters.
    member _.Close() =
        for registered in adapters.Values do
            Async.RunSynchronously(registered.Adapter.Close())

    /// Create a client from environment variables with real HTTP adapters
    static member FromEnv() =
        let client = Client()

        let anthropicKey = System.Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")

        if not (System.String.IsNullOrEmpty(anthropicKey)) then
            client.RegisterAdapter(AnthropicAdapter(anthropicKey))

        let openaiKey = System.Environment.GetEnvironmentVariable("OPENAI_API_KEY")

        if not (System.String.IsNullOrEmpty(openaiKey)) then
            client.RegisterAdapter(OpenAIAdapter(openaiKey))

        let geminiKey = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY")

        if not (System.String.IsNullOrEmpty(geminiKey)) then
            client.RegisterAdapter(GeminiAdapter(geminiKey))

        let openRouterKey = System.Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")

        if not (System.String.IsNullOrEmpty(openRouterKey)) then
            client.RegisterAdapter(OpenRouterAdapter(openRouterKey))

        client

/// Module-level default client management
module DefaultClient =
    let mutable private defaultClient: Client option = Option.None

    /// Set the module-level default client
    let setDefaultClient (client: Client) = defaultClient <- Some client

    /// Get the module-level default client. Creates from env if not set.
    let getDefaultClient () : Client =
        match defaultClient with
        | Some client -> client
        | Option.None ->
            let client = Client.FromEnv()
            defaultClient <- Some client
            client
