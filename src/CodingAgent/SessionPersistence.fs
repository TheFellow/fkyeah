namespace CodingAgent

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open UnifiedLlm

module CheckpointDto =

    type ToolCall =
        { Id: string
          Name: string
          Arguments: string
          Metadata: Map<string, string> }

    type ToolResult =
        { ToolCallId: string
          Content: string
          IsError: bool
          ImageData: byte array option
          ImageMediaType: string option }

    type Turn =
        { Case: string
          Content: string
          ToolCalls: ToolCall list
          ToolResults: ToolResult list
          Reasoning: string option
          Usage: Usage option
          Timestamp: DateTime }

    type Event =
        { Kind: string
          Timestamp: DateTime
          SessionId: string
          Data: Map<string, string>
          FullOutput: string option }

    type Subagent = { AgentId: string; Status: string }

type SessionCheckpointV1 =
    { Version: int
      SessionId: string
      ProviderId: string
      Model: string
      WorkingDirectory: string
      State: string
      UserInstructions: string option
      AwaitingInputRequested: bool
      CurrentDepth: int
      History: CheckpointDto.Turn list
      Events: CheckpointDto.Event list
      SteeringQueue: string list
      FollowupQueue: string list
      SubagentMetadata: CheckpointDto.Subagent list
      SavedAt: DateTimeOffset }

    static member CurrentVersion = 1

type SessionPersistence =
    { Save: string -> SessionCheckpointV1 -> Async<Result<unit, string>>
      Load: string -> Async<Result<SessionCheckpointV1, string>> }

module SessionPersistence =

    let checkpointFileName = "session-checkpoint.json"

    let jsonOptions =
        let options = JsonSerializerOptions(WriteIndented = true)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options

    let toolCallToDto (toolCall: ToolCallData) : CheckpointDto.ToolCall =
        { Id = toolCall.Id
          Name = toolCall.Name
          Arguments = toolCall.Arguments
          Metadata = toolCall.Metadata }

    let toolCallOfDto (toolCall: CheckpointDto.ToolCall) : ToolCallData =
        { Id = toolCall.Id
          Name = toolCall.Name
          Arguments = toolCall.Arguments
          Metadata = toolCall.Metadata }

    let toolResultToDto (toolResult: ToolResultData) : CheckpointDto.ToolResult =
        { ToolCallId = toolResult.ToolCallId
          Content = toolResult.Content
          IsError = toolResult.IsError
          ImageData = toolResult.ImageData
          ImageMediaType = toolResult.ImageMediaType }

    let toolResultOfDto (toolResult: CheckpointDto.ToolResult) : ToolResultData =
        { ToolCallId = toolResult.ToolCallId
          Content = toolResult.Content
          IsError = toolResult.IsError
          ImageData = toolResult.ImageData
          ImageMediaType = toolResult.ImageMediaType }

    let turnToDto (turn: Turn) : CheckpointDto.Turn =
        match turn with
        | UserTurn(content, timestamp) ->
            { Case = "user"
              Content = content
              ToolCalls = []
              ToolResults = []
              Reasoning = None
              Usage = None
              Timestamp = timestamp }
        | AssistantTurn(content, toolCalls, reasoning, usage, timestamp) ->
            { Case = "assistant"
              Content = content
              ToolCalls = toolCalls |> List.map toolCallToDto
              ToolResults = []
              Reasoning = reasoning
              Usage = Some usage
              Timestamp = timestamp }
        | ToolResultsTurn(results, timestamp) ->
            { Case = "tool_results"
              Content = ""
              ToolCalls = []
              ToolResults = results |> List.map toolResultToDto
              Reasoning = None
              Usage = None
              Timestamp = timestamp }
        | SteeringTurn(content, timestamp) ->
            { Case = "steering"
              Content = content
              ToolCalls = []
              ToolResults = []
              Reasoning = None
              Usage = None
              Timestamp = timestamp }
        | SystemTurn(content, timestamp) ->
            { Case = "system"
              Content = content
              ToolCalls = []
              ToolResults = []
              Reasoning = None
              Usage = None
              Timestamp = timestamp }

    let turnOfDto (turn: CheckpointDto.Turn) : Turn =
        match turn.Case with
        | "user" -> UserTurn(turn.Content, turn.Timestamp)
        | "assistant" ->
            AssistantTurn(
                turn.Content,
                turn.ToolCalls |> List.map toolCallOfDto,
                turn.Reasoning,
                turn.Usage |> Option.defaultValue Usage.Zero,
                turn.Timestamp
            )
        | "tool_results" -> ToolResultsTurn(turn.ToolResults |> List.map toolResultOfDto, turn.Timestamp)
        | "steering" -> SteeringTurn(turn.Content, turn.Timestamp)
        | "system" -> SystemTurn(turn.Content, turn.Timestamp)
        | other -> failwith $"Unknown turn case '{other}'"

    let eventToDto (event: SessionEvent) : CheckpointDto.Event =
        { Kind = ((event.Kind: EventKind).ToString())
          Timestamp = event.Timestamp
          SessionId = event.SessionId
          Data = event.Data
          FullOutput = event.FullOutput }

    let subagentToDto (agentId: string, status: string) : CheckpointDto.Subagent =
        { AgentId = agentId; Status = status }

    let eventKindOfString (value: string) =
        match value with
        | "SessionStart" -> EventKind.SessionStart
        | "SessionEnd" -> EventKind.SessionEnd
        | "TurnStart" -> EventKind.TurnStart
        | "TurnEnd" -> EventKind.TurnEnd
        | "ToolCallStart" -> EventKind.ToolCallStart
        | "ToolCallEnd" -> EventKind.ToolCallEnd
        | "LlmCallStart" -> EventKind.LlmCallStart
        | "LlmCallEnd" -> EventKind.LlmCallEnd
        | "Warning" -> EventKind.Warning
        | "Error" -> EventKind.Error
        | "UserInput" -> EventKind.UserInput
        | "AssistantTextStart" -> EventKind.AssistantTextStart
        | "AssistantTextDelta" -> EventKind.AssistantTextDelta
        | "AssistantTextEnd" -> EventKind.AssistantTextEnd
        | "ToolCallOutputDelta" -> EventKind.ToolCallOutputDelta
        | "SteeringInjected" -> EventKind.SteeringInjected
        | "TurnLimit" -> EventKind.TurnLimit
        | "LoopDetection" -> EventKind.LoopDetection
        | other -> failwith $"Unknown event kind '{other}'"

    let eventOfDto (event: CheckpointDto.Event) : SessionEvent =
        { Kind = eventKindOfString event.Kind
          Timestamp = event.Timestamp
          SessionId = event.SessionId
          Data = event.Data
          FullOutput = event.FullOutput }

    let private writeAtomically (path: string) (content: byte array) =
        let dir = Path.GetDirectoryName(path)

        if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
            Directory.CreateDirectory(dir) |> ignore

        let tempPath = path + ".tmp." + Guid.NewGuid().ToString("N")
        File.WriteAllBytes(tempPath, content)
        File.Move(tempPath, path, true)

    let fileBacked () : SessionPersistence =
        { Save =
            fun path checkpoint ->
                async {
                    try
                        let bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, jsonOptions)
                        writeAtomically path bytes
                        return Result.Ok()
                    with ex ->
                        return Result.Error($"Failed to save checkpoint: {ex.Message}")
                }
          Load =
            fun path ->
                async {
                    try
                        if not (File.Exists(path)) then
                            return Result.Error($"Checkpoint file not found: {path}")
                        else
                            let json = File.ReadAllBytes(path)
                            let checkpoint = JsonSerializer.Deserialize<SessionCheckpointV1>(json, jsonOptions)

                            if checkpoint.Version <> SessionCheckpointV1.CurrentVersion then
                                return
                                    Result.Error(
                                        $"Checkpoint version mismatch: expected {SessionCheckpointV1.CurrentVersion}, got {checkpoint.Version}"
                                    )
                            else
                                return Result.Ok checkpoint
                    with ex ->
                        return Result.Error($"Failed to load checkpoint: {ex.Message}")
                } }

module AutoCheckpointRegistry =

    let private callbacks = ConcurrentDictionary<string, unit -> unit>()

    let register (key: string) (callback: unit -> unit) = callbacks[key] <- callback

    let unregister (key: string) = callbacks.TryRemove(key) |> ignore

    let saveAll () =
        for callback in callbacks.Values do
            try
                callback ()
            with _ ->
                ()
