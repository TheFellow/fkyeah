namespace UnifiedLlm

/// Interface that every provider adapter must implement
type IProviderAdapter =
    /// Provider identifier (e.g. "openai", "anthropic", "gemini")
    abstract member ProviderId: string

    /// Optional lifecycle hook invoked when adapter is registered.
    abstract member Initialize: unit -> Async<unit>

    /// Optional lifecycle hook invoked when adapter is closed.
    abstract member Close: unit -> Async<unit>

    /// Capability: whether this adapter supports explicit tool choice controls.
    abstract member SupportsToolChoice: unit -> bool

    /// Send a request and return the complete response
    abstract member Complete: request: Request -> Response

    /// Send a request and return a sequence of stream events
    abstract member Stream: request: Request -> StreamEvent seq

/// Role translation helpers per provider
module RoleTranslation =

    /// Translate a Role to the OpenAI Responses API role string
    let toOpenAI (role: Role) : string =
        match role with
        | System -> "system"
        | User -> "user"
        | Assistant -> "assistant"
        | Tool -> "tool"
        | Developer -> "developer"

    /// Translate a Role to the Anthropic Messages API role string
    let toAnthropic (role: Role) : string =
        match role with
        | System -> "system" // extracted to system parameter
        | User -> "user"
        | Assistant -> "assistant"
        | Tool -> "user" // tool results go in user messages
        | Developer -> "system" // merged with system

    /// Translate a Role to the Gemini API role string
    let toGemini (role: Role) : string =
        match role with
        | System -> "system" // extracted to systemInstruction
        | User -> "user"
        | Assistant -> "model"
        | Tool -> "user" // functionResponse in user content
        | Developer -> "system" // merged with system

/// Mock adapter for OpenAI (for testing without real API calls)
type MockOpenAIAdapter() =
    interface IProviderAdapter with
        member _.ProviderId = "openai"
        member _.Initialize() = async.Return()
        member _.Close() = async.Return()
        member _.SupportsToolChoice() = true

        member _.Complete(request: Request) =
            { Id = "mock-openai-" + System.Guid.NewGuid().ToString("N").[..7]
              Model = request.Model
              Provider = "openai"
              Message = Message.Assistant("Mock OpenAI response")
              FinishReason = Stop "stop"
              Usage =
                { InputTokens = 10
                  OutputTokens = 5
                  ReasoningTokens = None
                  CacheReadTokens = None
                  CacheWriteTokens = None }
              ResponseId = Some "resp-mock-openai"
              Raw = None
              Warnings = []
              RateLimit = None }

        member _.Stream(_request: Request) =
            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", "Mock ")
                yield TextDelta(Some "text-1", "OpenAI ")
                yield TextDelta(Some "text-1", "stream")
                yield TextEnd "text-1"

                yield
                    Finish(
                        Stop "completed",
                        Some
                            { InputTokens = 10
                              OutputTokens = 3
                              ReasoningTokens = None
                              CacheReadTokens = None
                              CacheWriteTokens = None },
                        None
                    )
            }

/// Mock adapter for Anthropic (for testing without real API calls)
type MockAnthropicAdapter() =
    interface IProviderAdapter with
        member _.ProviderId = "anthropic"
        member _.Initialize() = async.Return()
        member _.Close() = async.Return()
        member _.SupportsToolChoice() = true

        member _.Complete(request: Request) =
            { Id = "mock-anthropic-" + System.Guid.NewGuid().ToString("N").[..7]
              Model = request.Model
              Provider = "anthropic"
              Message = Message.Assistant("Mock Anthropic response")
              FinishReason = Stop "end_turn"
              Usage =
                { InputTokens = 12
                  OutputTokens = 6
                  ReasoningTokens = None
                  CacheReadTokens = None
                  CacheWriteTokens = None }
              ResponseId = Some "resp-mock-anthropic"
              Raw = None
              Warnings = []
              RateLimit = None }

        member _.Stream(_request: Request) =
            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", "Mock ")
                yield TextDelta(Some "text-1", "Anthropic ")
                yield TextDelta(Some "text-1", "stream")
                yield TextEnd "text-1"

                yield
                    Finish(
                        Stop "end_turn",
                        Some
                            { InputTokens = 12
                              OutputTokens = 3
                              ReasoningTokens = None
                              CacheReadTokens = None
                              CacheWriteTokens = None },
                        None
                    )
            }

/// Mock adapter for Gemini (for testing without real API calls)
type MockGeminiAdapter() =
    interface IProviderAdapter with
        member _.ProviderId = "gemini"
        member _.Initialize() = async.Return()
        member _.Close() = async.Return()
        member _.SupportsToolChoice() = true

        member _.Complete(request: Request) =
            { Id = "mock-gemini-" + System.Guid.NewGuid().ToString("N").[..7]
              Model = request.Model
              Provider = "gemini"
              Message = Message.Assistant("Mock Gemini response")
              FinishReason = Stop "STOP"
              Usage =
                { InputTokens = 8
                  OutputTokens = 4
                  ReasoningTokens = None
                  CacheReadTokens = None
                  CacheWriteTokens = None }
              ResponseId = Some "resp-mock-gemini"
              Raw = None
              Warnings = []
              RateLimit = None }

        member _.Stream(_request: Request) =
            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", "Mock ")
                yield TextDelta(Some "text-1", "Gemini ")
                yield TextDelta(Some "text-1", "stream")
                yield TextEnd "text-1"

                yield
                    Finish(
                        Stop "STOP",
                        Some
                            { InputTokens = 8
                              OutputTokens = 3
                              ReasoningTokens = None
                              CacheReadTokens = None
                              CacheWriteTokens = None },
                        None
                    )
            }

/// A configurable mock adapter that allows tests to control behavior
type ConfigurableMockAdapter(providerId: string) =
    let mutable completeHandler: (Request -> Response) option = Option.None
    let mutable streamHandler: (Request -> StreamEvent seq) option = Option.None

    member _.SetCompleteHandler(handler: Request -> Response) = completeHandler <- Some handler

    member _.SetStreamHandler(handler: Request -> StreamEvent seq) = streamHandler <- Some handler

    interface IProviderAdapter with
        member _.ProviderId = providerId
        member _.Initialize() = async.Return()
        member _.Close() = async.Return()
        member _.SupportsToolChoice() = true

        member _.Complete(request: Request) =
            match completeHandler with
            | Some handler -> handler request
            | Option.None ->
                { Id = sprintf "mock-%s" providerId
                  Model = request.Model
                  Provider = providerId
                  Message = Message.Assistant("Default mock response")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None }

        member _.Stream(request: Request) =
            match streamHandler with
            | Some handler -> handler request
            | Option.None ->
                seq {
                    yield StreamStart
                    yield TextStart "text-1"
                    yield TextDelta(Some "text-1", "Default mock stream")
                    yield TextEnd "text-1"
                    yield Finish(Stop "completed", Some Usage.Zero, None)
                }
