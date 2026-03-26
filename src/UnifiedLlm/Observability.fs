namespace UnifiedLlm

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
type ObservabilityEvent =
    | RequestStarted of correlationId: string * model: string * messageCount: int
    | RequestCompleted of correlationId: string * model: string * durationMs: int64 * usage: Usage * totalMicrodollars: int64 option
    | StreamStarted of correlationId: string * model: string
    | StreamCompleted of correlationId: string * model: string * durationMs: int64 * usage: Usage * totalMicrodollars: int64 option
    | ValidationFailed of correlationId: string * issues: string list
    | CacheHit of correlationId: string * cacheKey: string * model: string
    | CacheMiss of correlationId: string * cacheKey: string * model: string
    | CacheStored of correlationId: string * cacheKey: string * model: string
    | BreakerStateChanged of provider: string * oldState: CircuitState * newState: CircuitState
    | CostRecorded of correlationId: string * model: string * totalMicrodollars: int64 * cacheHit: bool
    | CheckpointSaved of sessionId: string * path: string * turnCount: int
    | CheckpointLoaded of sessionId: string * path: string * turnCount: int
    | PipelineNodeStarted of pipelineRunId: string * nodeId: string * shape: string
    | PipelineNodeCompleted of pipelineRunId: string * nodeId: string * durationMs: int64 * status: string
    | PipelineTotalUpdated of pipelineRunId: string * totalMicrodollars: int64
    | ToolCacheHit of correlationId: string * toolName: string
    | ToolCacheMiss of correlationId: string * toolName: string

type ObservabilitySink =
    { Emit: ObservabilityEvent -> unit }

module ObservabilityEvent =

    let private usageJson (usage: Usage) =
        {| inputTokens = usage.InputTokens
           outputTokens = usage.OutputTokens
           reasoningTokens = usage.ReasoningTokens
           cacheReadTokens = usage.CacheReadTokens
           cacheWriteTokens = usage.CacheWriteTokens |}

    let private stateName state =
        match state with
        | CircuitState.Closed failures -> "closed", box {| consecutiveFailures = failures |}
        | CircuitState.Open(openedAt, retryAt) -> "open", box {| openedAt = openedAt; retryAt = retryAt |}
        | CircuitState.HalfOpen(probeStartedAt, successCount) ->
            "half_open", box {| probeStartedAt = probeStartedAt; successCount = successCount |}

    let toPayload (timestamp: DateTimeOffset) (event: ObservabilityEvent) : obj =
        match event with
        | ObservabilityEvent.RequestStarted(correlationId, model, messageCount) ->
            box
                {| timestamp = timestamp
                   event = "request_started"
                   correlationId = correlationId
                   model = model
                   messageCount = messageCount |}
        | ObservabilityEvent.RequestCompleted(correlationId, model, durationMs, usage, totalMicrodollars) ->
            box
                {| timestamp = timestamp
                   event = "request_completed"
                   correlationId = correlationId
                   model = model
                   durationMs = durationMs
                   usage = usageJson usage
                   totalMicrodollars = totalMicrodollars |}
        | ObservabilityEvent.StreamStarted(correlationId, model) ->
            box {| timestamp = timestamp; event = "stream_started"; correlationId = correlationId; model = model |}
        | ObservabilityEvent.StreamCompleted(correlationId, model, durationMs, usage, totalMicrodollars) ->
            box
                {| timestamp = timestamp
                   event = "stream_completed"
                   correlationId = correlationId
                   model = model
                   durationMs = durationMs
                   usage = usageJson usage
                   totalMicrodollars = totalMicrodollars |}
        | ObservabilityEvent.ValidationFailed(correlationId, issues) ->
            box {| timestamp = timestamp; event = "validation_failed"; correlationId = correlationId; issues = issues |}
        | ObservabilityEvent.CacheHit(correlationId, cacheKey, model) ->
            box {| timestamp = timestamp; event = "cache_hit"; correlationId = correlationId; cacheKey = cacheKey; model = model |}
        | ObservabilityEvent.CacheMiss(correlationId, cacheKey, model) ->
            box {| timestamp = timestamp; event = "cache_miss"; correlationId = correlationId; cacheKey = cacheKey; model = model |}
        | ObservabilityEvent.CacheStored(correlationId, cacheKey, model) ->
            box {| timestamp = timestamp; event = "cache_stored"; correlationId = correlationId; cacheKey = cacheKey; model = model |}
        | ObservabilityEvent.BreakerStateChanged(provider, oldState, newState) ->
            let oldName, oldPayload = stateName oldState
            let newName, newPayload = stateName newState
            box
                {| timestamp = timestamp
                   event = "breaker_state_changed"
                   provider = provider
                   oldState = oldName
                   oldData = oldPayload
                   newState = newName
                   newData = newPayload |}
        | ObservabilityEvent.CostRecorded(correlationId, model, totalMicrodollars, cacheHit) ->
            box
                {| timestamp = timestamp
                   event = "cost_recorded"
                   correlationId = correlationId
                   model = model
                   totalMicrodollars = totalMicrodollars
                   cacheHit = cacheHit |}
        | ObservabilityEvent.CheckpointSaved(sessionId, path, turnCount) ->
            box {| timestamp = timestamp; event = "checkpoint_saved"; sessionId = sessionId; path = path; turnCount = turnCount |}
        | ObservabilityEvent.CheckpointLoaded(sessionId, path, turnCount) ->
            box {| timestamp = timestamp; event = "checkpoint_loaded"; sessionId = sessionId; path = path; turnCount = turnCount |}
        | ObservabilityEvent.PipelineNodeStarted(pipelineRunId, nodeId, shape) ->
            box {| timestamp = timestamp; event = "pipeline_node_started"; pipelineRunId = pipelineRunId; nodeId = nodeId; shape = shape |}
        | ObservabilityEvent.PipelineNodeCompleted(pipelineRunId, nodeId, durationMs, status) ->
            box
                {| timestamp = timestamp
                   event = "pipeline_node_completed"
                   pipelineRunId = pipelineRunId
                   nodeId = nodeId
                   durationMs = durationMs
                   status = status |}
        | ObservabilityEvent.PipelineTotalUpdated(pipelineRunId, totalMicrodollars) ->
            box {| timestamp = timestamp; event = "pipeline_total_updated"; pipelineRunId = pipelineRunId; totalMicrodollars = totalMicrodollars |}
        | ObservabilityEvent.ToolCacheHit(correlationId, toolName) ->
            box {| timestamp = timestamp; event = "tool_cache_hit"; correlationId = correlationId; toolName = toolName |}
        | ObservabilityEvent.ToolCacheMiss(correlationId, toolName) ->
            box {| timestamp = timestamp; event = "tool_cache_miss"; correlationId = correlationId; toolName = toolName |}

module ObservabilitySink =

    let none: ObservabilitySink =
        { Emit = ignore }

    let console (verbose: bool) : ObservabilitySink =
        let usd micros = decimal micros / 1_000_000m

        { Emit =
            fun event ->
                match event with
                | ObservabilityEvent.RequestCompleted(_, model, durationMs, usage, totalMicrodollars) ->
                    let cost =
                        totalMicrodollars
                        |> Option.map (fun micros -> sprintf " $%.4f" (float (usd micros)))
                        |> Option.defaultValue ""
                    eprintfn "[llm] %s %dms in=%d out=%d%s" model durationMs usage.InputTokens usage.OutputTokens cost
                | ObservabilityEvent.StreamCompleted(_, model, durationMs, usage, totalMicrodollars) ->
                    let cost =
                        totalMicrodollars
                        |> Option.map (fun micros -> sprintf " $%.4f" (float (usd micros)))
                        |> Option.defaultValue ""
                    eprintfn "[llm] %s stream %dms in=%d out=%d%s" model durationMs usage.InputTokens usage.OutputTokens cost
                | ObservabilityEvent.CacheHit(_, _, model) when verbose ->
                    eprintfn "[cache] hit %s" model
                | ObservabilityEvent.CacheMiss(_, _, model) when verbose ->
                    eprintfn "[cache] miss %s" model
                | ObservabilityEvent.ValidationFailed(_, issues) ->
                    eprintfn "[validation] %s" (String.concat "; " issues)
                | ObservabilityEvent.BreakerStateChanged(provider, oldState, newState) ->
                    eprintfn "[breaker] %s %A -> %A" provider oldState newState
                | _ when verbose ->
                    eprintfn "[obs] %A" event
                | _ ->
                    () }

    let jsonLines (path: string) : ObservabilitySink =
        let dir = Path.GetDirectoryName(path)
        if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
            Directory.CreateDirectory(dir) |> ignore

        let gate = obj ()
        let options = JsonSerializerOptions(WriteIndented = false)

        { Emit =
            fun event ->
                let payload = ObservabilityEvent.toPayload DateTimeOffset.UtcNow event
                let json = JsonSerializer.Serialize(payload, options)
                lock gate (fun () -> File.AppendAllText(path, json + Environment.NewLine)) }

    let combine (sinks: ObservabilitySink list) : ObservabilitySink =
        { Emit = fun event -> for sink in sinks do sink.Emit event }
