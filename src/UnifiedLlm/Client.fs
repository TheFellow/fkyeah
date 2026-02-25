namespace UnifiedLlm

open System.Collections.Generic

type ClientConfig = {
    Timeout: AdapterTimeout option
} with
    static member Default = { Timeout = None }

type private RegisteredAdapter = {
    Adapter: IProviderAdapter
    Timeout: AdapterTimeout option
}

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
            let effectiveTimeout =
                adapterTimeout |> Option.orElse config.Timeout
            { request with AdapterTimeout = effectiveTimeout }

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
    member _.AddMiddleware(middleware: IMiddleware) =
        middlewareChain.Add(middleware)

    /// Get the default provider id
    member _.DefaultProvider = defaultProvider

    /// Resolve the adapter for a request
    member private _.ResolveAdapter(request: Request) : IProviderAdapter * AdapterTimeout option =
        let providerId =
            match request.Provider with
            | Some p -> p
            | Option.None ->
                match defaultProvider with
                | Some p -> p
                | Option.None ->
                    raise (ConfigurationError("No provider specified and no default provider is set"))
        match adapters.TryGetValue(providerId) with
        | true, registered -> registered.Adapter, registered.Timeout
        | false, _ ->
            raise (ConfigurationError(sprintf "Provider '%s' is not registered" providerId))

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

        client

/// Module-level default client management
module DefaultClient =
    let mutable private defaultClient: Client option = Option.None

    /// Set the module-level default client
    let setDefaultClient (client: Client) =
        defaultClient <- Some client

    /// Get the module-level default client. Creates from env if not set.
    let getDefaultClient () : Client =
        match defaultClient with
        | Some client -> client
        | Option.None ->
            let client = Client.FromEnv()
            defaultClient <- Some client
            client
