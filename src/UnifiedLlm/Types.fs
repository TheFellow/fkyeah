namespace UnifiedLlm

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading

/// Roles for conversation messages
type Role =
    | System
    | User
    | Assistant
    | Tool
    | Developer

/// Image data for multimodal content
type ImageData =
    { Url: string option
      Data: byte array option
      FilePath: string option
      MediaType: string option }

/// Audio data
type AudioData =
    { Url: string option
      Data: byte array option
      MediaType: string option }

/// Document data
type DocumentData =
    { Url: string option
      Data: byte array option
      MediaType: string option
      FileName: string option }

/// Tool call data from the model
type ToolCallData =
    {
        Id: string
        Name: string
        Arguments: string
        /// Provider-specific metadata (e.g., Gemini thoughtSignature)
        Metadata: Map<string, string>
    }

/// Tool result data
type ToolResultData =
    { ToolCallId: string
      Content: string
      IsError: bool
      ImageData: byte array option
      ImageMediaType: string option }

/// Thinking data for reasoning content
type ThinkingData =
    { Text: string
      Signature: string option
      Redacted: bool }

/// Free-form input produced for an OpenAI custom tool.
type CustomToolCallData =
    { Id: string
      Name: string
      Input: string }

/// Output returned for a previous custom tool call.
type CustomToolResultData = { ToolCallId: string; Output: string }

/// Code generated and executed by a provider-managed code execution tool.
type CodeExecutionData = { Language: string; Code: string }

/// Result emitted by a provider-managed code execution tool.
type CodeExecutionResultData = { Outcome: string; Output: string }

/// Provider-neutral metadata for response lifecycle events.
type ResponseStreamMetadata =
    { Id: string option
      Model: string option
      Provider: string
      Status: string
      Raw: string option }

/// A provider response that paused for caller action.
type ResponseActionData =
    { Response: ResponseStreamMetadata
      Action: string option }

/// A response-scoped provider failure surfaced within an otherwise valid stream.
type ResponseErrorData =
    { Response: ResponseStreamMetadata
      Code: string option
      Message: string
      Retryable: bool option }

/// Streaming audio bytes or transcript metadata.
type AudioDeltaData =
    { Data: byte array
      Transcript: string option
      Sequence: int option
      MediaType: string option
      Final: bool }

/// Content part discriminated union — multimodal message content
type ContentPart =
    | Text of string
    | Image of ImageData
    | Audio of AudioData
    | Document of DocumentData
    | ToolCall of ToolCallData
    | ToolResult of ToolResultData
    | Thinking of ThinkingData
    | CustomToolCall of CustomToolCallData
    | CustomToolResult of CustomToolResultData
    | CodeExecution of CodeExecutionData
    | CodeExecutionResult of CodeExecutionResultData

/// A conversation message
type Message =
    { Role: Role
      Content: ContentPart list
      Name: string option
      ToolCallId: string option }

    /// Convenience: concatenate all text parts
    member this.Text =
        this.Content
        |> List.choose (fun c ->
            match c with
            | Text t -> Some t
            | _ -> None)
        |> String.concat ""

    /// Convenience constructors
    static member System(text: string) =
        { Role = System
          Content = [ Text text ]
          Name = None
          ToolCallId = None }

    static member User(text: string) =
        { Role = User
          Content = [ Text text ]
          Name = None
          ToolCallId = None }

    static member Assistant(text: string) =
        { Role = Assistant
          Content = [ Text text ]
          Name = None
          ToolCallId = None }

    static member ToolResult(toolCallId: string, content: string, isError: bool) =
        { Role = Tool
          Content =
            [ ToolResult
                  { ToolCallId = toolCallId
                    Content = content
                    IsError = isError
                    ImageData = None
                    ImageMediaType = None } ]
          Name = None
          ToolCallId = Some toolCallId }

    static member CustomToolResult(toolCallId: string, output: string) =
        { Role = Tool
          Content =
            [ CustomToolResult
                  { ToolCallId = toolCallId
                    Output = output } ]
          Name = None
          ToolCallId = Some toolCallId }

/// Token usage record
type Usage =
    { InputTokens: int
      OutputTokens: int
      ReasoningTokens: int option
      CacheReadTokens: int option
      CacheWriteTokens: int option }

    member this.TotalTokens = this.InputTokens + this.OutputTokens

    static member Zero =
        { InputTokens = 0
          OutputTokens = 0
          ReasoningTokens = None
          CacheReadTokens = None
          CacheWriteTokens = None }

    static member (+)(a: Usage, b: Usage) =
        let addOpt (x: int option) (y: int option) =
            match x, y with
            | None, None -> None
            | Some v, None
            | None, Some v -> Some v
            | Some v1, Some v2 -> Some(v1 + v2)

        { InputTokens = a.InputTokens + b.InputTokens
          OutputTokens = a.OutputTokens + b.OutputTokens
          ReasoningTokens = addOpt a.ReasoningTokens b.ReasoningTokens
          CacheReadTokens = addOpt a.CacheReadTokens b.CacheReadTokens
          CacheWriteTokens = addOpt a.CacheWriteTokens b.CacheWriteTokens }

/// Incremental token accounting with an optional authoritative cumulative total.
type UsageDeltaData = { Delta: Usage; Total: Usage option }

/// Why generation stopped (provider reason is preserved in the payload)
type FinishReason =
    | Stop of Raw: string
    | ToolCalls of Raw: string
    | Length of Raw: string
    | ContentFilter of Raw: string
    | Error of Raw: string
    | Other of Raw: string

    member this.Raw =
        match this with
        | Stop raw
        | ToolCalls raw
        | Length raw
        | ContentFilter raw
        | Error raw
        | Other raw -> raw

/// Generate loop stop conditions
type StopCondition =
    | ToolCalled of toolName: string
    | TextMatches of pattern: string
    | MaxRounds of n: int

/// Generation timeout controls
type TimeoutConfig =
    { TotalMs: int option
      PerStepMs: int option }

/// Provider adapter timeout controls
type AdapterTimeout =
    { ConnectMs: int option
      RequestMs: int option
      StreamReadMs: int option }

/// Response format preference
[<RequireQualifiedAccess>]
type ResponseFormat =
    | Text
    | JsonObject
    | JsonSchema of name: string * schema: string * strict: bool

/// Parsed provider rate limit info
type RateLimitInfo =
    { Limit: int option
      Remaining: int option
      ResetAt: DateTimeOffset option }

/// Caller-driven cancel handle
type AbortSignal() =
    let cts = new CancellationTokenSource()

    member _.Cancel() = cts.Cancel()
    member _.Token = cts.Token
    member _.IsAborted = cts.IsCancellationRequested

    interface IDisposable with
        member _.Dispose() = cts.Dispose()

/// Provider-neutral embedding request.
type EmbeddingRequest =
    { Model: string
      Inputs: string list
      Dimensions: int option
      Provider: string option
      ProviderOptions: Map<string, obj> option
      Timeout: TimeSpan option
      AbortSignal: AbortSignal option }

    static member Create(model: string, inputs: string list) =
        { Model = model
          Inputs = inputs
          Dimensions = None
          Provider = None
          ProviderOptions = None
          Timeout = None
          AbortSignal = None }

/// One normalized embedding vector in request order.
type Embedding = { Index: int; Vector: float array }

/// Provider-neutral embedding response.
type EmbeddingResponse =
    { Model: string
      Provider: string
      Embeddings: Embedding list
      Usage: Usage
      Raw: JsonElement option }

[<RequireQualifiedAccess>]
type CustomToolFormat =
    | FreeText
    | Grammar of Syntax: string * Definition: string

type CustomToolDefinition =
    { Name: string
      Description: string
      Format: CustomToolFormat }

    static member FreeText(name: string, description: string) =
        { Name = name
          Description = description
          Format = CustomToolFormat.FreeText }

    static member Grammar(name: string, description: string, syntax: string, definition: string) =
        { Name = name
          Description = description
          Format = CustomToolFormat.Grammar(syntax, definition) }

/// A request to the LLM
type Request =
    { Model: string
      Messages: Message list
      Prompt: string option
      Tools: ToolDefinition list option
      ToolChoice: ToolChoice option
      MaxTokens: int option
      Temperature: float option
      TopP: float option
      StopSequences: string list option
      ResponseFormat: ResponseFormat option
      Metadata: Map<string, string> option
      ReasoningEffort: string option
      Provider: string option
      ProviderOptions: Map<string, obj> option
      Timeout: TimeSpan option
      TimeoutConfig: TimeoutConfig option
      AdapterTimeout: AdapterTimeout option
      AbortSignal: AbortSignal option
      PreviousResponseId: string option }

    static member Create(model: string, messages: Message list) =
        { Model = model
          Messages = messages
          Prompt = None
          Tools = None
          ToolChoice = None
          MaxTokens = None
          Temperature = None
          TopP = None
          StopSequences = None
          ResponseFormat = None
          Metadata = None
          ReasoningEffort = None
          Provider = None
          ProviderOptions = None
          Timeout = None
          TimeoutConfig = None
          AdapterTimeout = None
          AbortSignal = None
          PreviousResponseId = None }

/// Tool definition for the LLM
and ToolDefinition =
    { Name: string
      Description: string
      Parameters: string }

/// Tool choice mode
and [<RequireQualifiedAccess>] ToolChoice =
    | Auto
    | None
    | Required
    | Named of string

[<AutoOpen>]
module RequestFeatureExtensions =
    let private customToolsKey = "unified_llm.custom_tools"
    let private codeExecutionKey = "unified_llm.gemini_code_execution"

    type Request with
        member this.CustomTools =
            this.ProviderOptions
            |> Option.bind (Map.tryFind customToolsKey)
            |> Option.bind (function
                | :? (CustomToolDefinition list) as tools -> Some tools
                | _ -> None)
            |> Option.defaultValue []

        member this.CodeExecutionEnabled =
            this.ProviderOptions
            |> Option.bind (Map.tryFind codeExecutionKey)
            |> Option.bind (function
                | :? bool as enabled -> Some enabled
                | _ -> None)
            |> Option.defaultValue false

        member this.WithCustomTools(tools: CustomToolDefinition list) =
            let options = this.ProviderOptions |> Option.defaultValue Map.empty

            { this with
                ProviderOptions = Some(options |> Map.add customToolsKey (box tools)) }

        member this.WithCodeExecution(?enabled: bool) =
            let options = this.ProviderOptions |> Option.defaultValue Map.empty
            let enabled = defaultArg enabled true

            { this with
                ProviderOptions = Some(options |> Map.add codeExecutionKey (box enabled)) }

/// A response-ID continuation containing only outputs for previously requested tool calls.
type ToolContinuationRequest =
    { PreviousResponseId: string
      ToolResults: ToolResultData list
      Request: Request }

    static member Create(request: Request, previousResponseId: string, toolResults: ToolResultData list) =
        { PreviousResponseId = previousResponseId
          ToolResults = toolResults
          Request = request }

/// LLM response
type Response =
    { Id: string
      Model: string
      Provider: string
      Message: Message
      FinishReason: FinishReason
      Usage: Usage
      ResponseId: string option
      Raw: JsonElement option
      Warnings: string list
      RateLimit: RateLimitInfo option }

    member this.Text = this.Message.Text

    member this.ToolCalls =
        this.Message.Content
        |> List.choose (fun c ->
            match c with
            | ToolCall tc -> Some tc
            | _ -> Option.None)

    member this.CustomToolCalls =
        this.Message.Content
        |> List.choose (fun content ->
            match content with
            | CustomToolCall call -> Some call
            | _ -> None)

    member this.Reasoning =
        this.Message.Content
        |> List.choose (fun c ->
            match c with
            | Thinking td -> Some td.Text
            | _ -> Option.None)
        |> function
            | [] -> Option.None
            | parts -> Some(String.concat "" parts)

/// Stream event types
type StreamEvent =
    | StreamStart
    | ResponseCreated of ResponseStreamMetadata
    | ResponseRequiresAction of ResponseActionData
    | ResponseError of ResponseErrorData
    | UsageDelta of UsageDeltaData
    | RefusalDelta of text: string * final: bool
    | AudioDelta of AudioDeltaData
    | TextStart of textId: string
    | TextDelta of textId: string option * text: string
    | TextEnd of textId: string
    | ToolCallStart of ToolCallData
    | ToolCallDelta of id: string * argsDelta: string
    | ToolCallEnd of ToolCallData
    | CustomToolCallStart of CustomToolCallData
    | CustomToolCallDelta of id: string * inputDelta: string
    | CustomToolCallEnd of CustomToolCallData
    | CodeExecutionEvent of CodeExecutionData
    | CodeExecutionResultEvent of CodeExecutionResultData
    | ReasoningStart of id: string option
    | ThinkingEvent of string
    | ReasoningEnd of id: string option
    | StepFinish of step: int * response: Response option
    | StreamError of message: string
    | ProviderEvent of eventType: string * payload: string
    | Finish of finishReason: FinishReason * usage: Usage option * response: Response option

/// Result of a single step in a multi-step generation
type StepResult =
    { ToolCalls: ToolCallData list
      ToolResults: ToolResultData list
      Usage: Usage
      Response: Response }
