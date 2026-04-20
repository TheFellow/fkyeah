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

/// Content part discriminated union — multimodal message content
type ContentPart =
    | Text of string
    | Image of ImageData
    | Audio of AudioData
    | Document of DocumentData
    | ToolCall of ToolCallData
    | ToolResult of ToolResultData
    | Thinking of ThinkingData

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
    static member system(text: string) =
        { Role = System
          Content = [ Text text ]
          Name = None
          ToolCallId = None }

    static member user(text: string) =
        { Role = User
          Content = [ Text text ]
          Name = None
          ToolCallId = None }

    static member assistant(text: string) =
        { Role = Assistant
          Content = [ Text text ]
          Name = None
          ToolCallId = None }

    static member toolResult(toolCallId: string, content: string, isError: bool) =
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
    | TextStart of textId: string
    | TextDelta of textId: string option * text: string
    | TextEnd of textId: string
    | ToolCallStart of ToolCallData
    | ToolCallDelta of id: string * argsDelta: string
    | ToolCallEnd of ToolCallData
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
