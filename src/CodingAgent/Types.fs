namespace CodingAgent

open System

/// Session lifecycle state
type SessionState =
    | Idle
    | Processing
    | AwaitingInput
    | Closed

/// Tool-call hook phase
type ToolCallHookPhase =
    | Pre
    | Post

/// A turn in the conversation history
type Turn =
    | UserTurn of content: string * timestamp: DateTime
    | AssistantTurn of
        content: string *
        toolCalls: UnifiedLlm.ToolCallData list *
        reasoning: string option *
        usage: UnifiedLlm.Usage *
        timestamp: DateTime
    | ToolResultsTurn of results: UnifiedLlm.ToolResultData list * timestamp: DateTime
    | SteeringTurn of content: string * timestamp: DateTime
    | SystemTurn of content: string * timestamp: DateTime

/// Session event kinds
type EventKind =
    | SessionStart
    | SessionEnd
    | TurnStart
    | TurnEnd
    | ToolCallStart
    | ToolCallEnd
    | LlmCallStart
    | LlmCallEnd
    | Warning
    | Error
    | UserInput
    | AssistantTextStart
    | AssistantTextDelta
    | AssistantTextEnd
    | ToolCallOutputDelta
    | SteeringInjected
    | TurnLimit
    | LoopDetection

/// A session event delivered to the host application
type SessionEvent =
    { Kind: EventKind
      Timestamp: DateTime
      SessionId: string
      Data: Map<string, string>
      FullOutput: string option }

/// Session configuration
type SessionConfig =
    { MaxTurns: int
      MaxToolRoundsPerInput: int
      DefaultCommandTimeoutMs: int
      MaxCommandTimeoutMs: int
      EnableStreaming: bool
      ReasoningEffort: string option
      MaxTokens: int option
      ProviderOptions: Map<string, obj> option
      ToolOutputLimits: Map<string, int>
      ToolLineLimits: Map<string, int>
      EnableLoopDetection: bool
      LoopDetectionWindow: int
      MaxSubagentDepth: int
      ToolCallHook:
          (ToolCallHookPhase -> UnifiedLlm.ToolCallData -> UnifiedLlm.ToolResultData option -> Result<unit, string>) option
      OnEvent: (SessionEvent -> unit) option }

    static member Default =
        { MaxTurns = 0
          MaxToolRoundsPerInput = 0
          DefaultCommandTimeoutMs = 10000
          MaxCommandTimeoutMs = 600000
          EnableStreaming = false
          ReasoningEffort = None
          MaxTokens = Some 16384
          ProviderOptions = None
          ToolOutputLimits = Map.empty
          ToolLineLimits = Map.empty
          EnableLoopDetection = true
          LoopDetectionWindow = 10
          MaxSubagentDepth = 1
          ToolCallHook = None
          OnEvent = None }

/// Result from command execution
type ExecResult =
    { Stdout: string
      Stderr: string
      ExitCode: int
      TimedOut: bool
      DurationMs: int }

/// Directory entry
type DirEntry =
    { Name: string
      IsDir: bool
      Size: int64 option }
