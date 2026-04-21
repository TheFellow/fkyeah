namespace Attractor

open System
open System.Collections.Generic
open System.IO
open System.Text

/// Duration value with unit
[<Struct>]
type Duration =
    { Milliseconds: int64 }

    static member FromMs(ms: int64) = { Milliseconds = ms }
    static member FromSeconds(s: int64) = { Milliseconds = s * 1000L }
    static member FromMinutes(m: int64) = { Milliseconds = m * 60L * 1000L }
    static member FromHours(h: int64) = { Milliseconds = h * 3600L * 1000L }
    static member FromDays(d: int64) = { Milliseconds = d * 86400L * 1000L }
    static member Zero = { Milliseconds = 0L }

    static member TryParse(s: string) : Duration option =
        let s = s.Trim()

        if s.EndsWith("ms", StringComparison.Ordinal) then
            match Int64.TryParse(s.Substring(0, s.Length - 2)) with
            | true, v -> Some(Duration.FromMs v)
            | _ -> None
        elif
            s.EndsWith("s", StringComparison.Ordinal)
            && not (s.EndsWith("ms", StringComparison.Ordinal))
        then
            match Int64.TryParse(s.Substring(0, s.Length - 1)) with
            | true, v -> Some(Duration.FromSeconds v)
            | _ -> None
        elif s.EndsWith("m", StringComparison.Ordinal) then
            match Int64.TryParse(s.Substring(0, s.Length - 1)) with
            | true, v -> Some(Duration.FromMinutes v)
            | _ -> None
        elif s.EndsWith("h", StringComparison.Ordinal) then
            match Int64.TryParse(s.Substring(0, s.Length - 1)) with
            | true, v -> Some(Duration.FromHours v)
            | _ -> None
        elif s.EndsWith("d", StringComparison.Ordinal) then
            match Int64.TryParse(s.Substring(0, s.Length - 1)) with
            | true, v -> Some(Duration.FromDays v)
            | _ -> None
        else
            None

    member this.ToTimeSpan() =
        TimeSpan.FromMilliseconds(float this.Milliseconds)

/// Attribute value in the DOT DSL
[<RequireQualifiedAccess>]
type AttrValue =
    | String of string
    | Integer of int
    | Float of float
    | Boolean of bool
    | Duration of Duration

    member this.AsString() =
        match this with
        | String s -> s
        | Integer i -> (i: int).ToString(System.Globalization.CultureInfo.InvariantCulture)
        | Float f -> (f: float).ToString(System.Globalization.CultureInfo.InvariantCulture)
        | Boolean b -> if b then "true" else "false"
        | Duration d ->
            d.Milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "ms"

    member this.AsInt() =
        match this with
        | Integer i -> Some i
        | String s ->
            match Int32.TryParse(s) with
            | true, i -> Some i
            | _ -> None
        | _ -> None

    member this.AsBool() =
        match this with
        | Boolean b -> Some b
        | String "true" -> Some true
        | String "false" -> Some false
        | _ -> None

    member this.AsDuration() =
        match this with
        | Duration d -> Some d
        | String s -> Duration.TryParse(s)
        | _ -> None

/// Stage status for node execution outcomes
[<RequireQualifiedAccess>]
type StageStatus =
    | Success
    | PartialSuccess
    | Retry
    | Fail
    | Skipped

    override this.ToString() =
        match this with
        | Success -> "success"
        | PartialSuccess -> "partial_success"
        | Retry -> "retry"
        | Fail -> "fail"
        | Skipped -> "skipped"

    static member Parse(s: string) =
        match s.ToLowerInvariant() with
        | "success" -> Some Success
        | "partial_success" -> Some PartialSuccess
        | "retry" -> Some Retry
        | "fail" -> Some Fail
        | "skipped" -> Some Skipped
        | _ -> None

/// Outcome of executing a node handler
type Outcome =
    { Status: StageStatus
      PreferredLabel: string
      SuggestedNextIds: string list
      ContextUpdates: Map<string, string>
      Notes: string
      FailureReason: string }

    static member Success(?notes, ?contextUpdates) =
        { Status = StageStatus.Success
          PreferredLabel = ""
          SuggestedNextIds = []
          ContextUpdates = defaultArg contextUpdates Map.empty
          Notes = defaultArg notes ""
          FailureReason = "" }

    static member Fail(reason: string) =
        { Status = StageStatus.Fail
          PreferredLabel = ""
          SuggestedNextIds = []
          ContextUpdates = Map.empty
          Notes = ""
          FailureReason = reason }

    static member Retry(reason: string) =
        { Status = StageStatus.Retry
          PreferredLabel = ""
          SuggestedNextIds = []
          ContextUpdates = Map.empty
          Notes = ""
          FailureReason = reason }

    static member PartialSuccess(?notes) =
        { Status = StageStatus.PartialSuccess
          PreferredLabel = ""
          SuggestedNextIds = []
          ContextUpdates = Map.empty
          Notes = defaultArg notes ""
          FailureReason = "" }

/// Context fidelity modes
[<RequireQualifiedAccess>]
type FidelityMode =
    | Full
    | Truncate
    | Compact
    | SummaryLow
    | SummaryMedium
    | SummaryHigh

    static member Parse(s: string) =
        match s.ToLowerInvariant() with
        | "full" -> Some Full
        | "truncate" -> Some Truncate
        | "compact" -> Some Compact
        | "summary:low" -> Some SummaryLow
        | "summary:medium" -> Some SummaryMedium
        | "summary:high" -> Some SummaryHigh
        | _ -> None

    override this.ToString() =
        match this with
        | Full -> "full"
        | Truncate -> "truncate"
        | Compact -> "compact"
        | SummaryLow -> "summary:low"
        | SummaryMedium -> "summary:medium"
        | SummaryHigh -> "summary:high"

module FidelityMode =

    /// Approximate token budget for each fidelity level.
    let tokenBudget (mode: FidelityMode) : int =
        match mode with
        | FidelityMode.Full -> Int32.MaxValue
        | FidelityMode.Truncate -> 100
        | FidelityMode.Compact -> 800
        | FidelityMode.SummaryLow -> 600
        | FidelityMode.SummaryMedium -> 1500
        | FidelityMode.SummaryHigh -> 3000

    /// Approximate character budget using 1 token ~= 4 characters.
    let charBudget (mode: FidelityMode) : int =
        let tokens = tokenBudget mode

        if tokens = Int32.MaxValue then
            Int32.MaxValue
        else
            tokens * 4

    /// Reduced-fidelity requests should use a fresh session.
    let useFreshSession (mode: FidelityMode) : bool = mode <> FidelityMode.Full

/// A parsed node from the DOT graph
type Node =
    { Id: string
      Attributes: Map<string, AttrValue> }

    member this.Label =
        this.Attributes
        |> Map.tryFind "label"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue this.Id

    member this.Shape =
        this.Attributes
        |> Map.tryFind "shape"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue "box"

    member this.NodeType =
        this.Attributes
        |> Map.tryFind "type"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.Prompt =
        this.Attributes
        |> Map.tryFind "prompt"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.MaxRetriesOption =
        this.Attributes |> Map.tryFind "max_retries" |> Option.bind (fun v -> v.AsInt())

    member this.MaxRetries = defaultArg this.MaxRetriesOption 0

    member this.GoalGate =
        this.Attributes
        |> Map.tryFind "goal_gate"
        |> Option.bind (fun v -> v.AsBool())
        |> Option.defaultValue false

    member this.RetryTarget =
        this.Attributes
        |> Map.tryFind "retry_target"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.FallbackRetryTarget =
        this.Attributes
        |> Map.tryFind "fallback_retry_target"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.Fidelity =
        this.Attributes
        |> Map.tryFind "fidelity"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.ThreadId =
        this.Attributes
        |> Map.tryFind "thread_id"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.Class =
        this.Attributes
        |> Map.tryFind "class"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.Timeout =
        this.Attributes
        |> Map.tryFind "timeout"
        |> Option.bind (fun v -> v.AsDuration())

    member this.LlmModel =
        this.Attributes
        |> Map.tryFind "llm_model"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.LlmProvider =
        this.Attributes
        |> Map.tryFind "llm_provider"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.ReasoningEffort =
        this.Attributes
        |> Map.tryFind "reasoning_effort"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue "high"

    member this.AutoStatus =
        this.Attributes
        |> Map.tryFind "auto_status"
        |> Option.bind (fun v -> v.AsBool())
        |> Option.defaultValue false

    member this.AllowPartial =
        this.Attributes
        |> Map.tryFind "allow_partial"
        |> Option.bind (fun v -> v.AsBool())
        |> Option.defaultValue false

    member this.MaxVisits =
        this.Attributes
        |> Map.tryFind "max_visits"
        |> Option.bind (fun v -> v.AsInt())
        |> Option.defaultValue 50

    member this.OutcomeFailPattern =
        this.Attributes
        |> Map.tryFind "outcome_fail_pattern"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.ToolHooksPre =
        this.Attributes
        |> Map.tryFind "tool_hooks.pre"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.ToolHooksPost =
        this.Attributes
        |> Map.tryFind "tool_hooks.post"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.GetAttr(key: string) = this.Attributes |> Map.tryFind key

    member this.GetAttrString(key: string, defaultValue: string) =
        this.Attributes
        |> Map.tryFind key
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue defaultValue

/// A parsed edge from the DOT graph
type Edge =
    { FromNode: string
      ToNode: string
      Attributes: Map<string, AttrValue> }

    member this.Label =
        this.Attributes
        |> Map.tryFind "label"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.Condition =
        this.Attributes
        |> Map.tryFind "condition"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.Weight =
        this.Attributes
        |> Map.tryFind "weight"
        |> Option.bind (fun v -> v.AsInt())
        |> Option.defaultValue 0

    member this.Fidelity =
        this.Attributes
        |> Map.tryFind "fidelity"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.ThreadId =
        this.Attributes
        |> Map.tryFind "thread_id"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.LoopRestart =
        this.Attributes
        |> Map.tryFind "loop_restart"
        |> Option.bind (fun v -> v.AsBool())
        |> Option.defaultValue false

/// The parsed DOT graph
type Graph =
    { Name: string
      Nodes: Map<string, Node>
      Edges: Edge list
      GraphAttributes: Map<string, AttrValue> }

    member this.Goal =
        this.GraphAttributes
        |> Map.tryFind "goal"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.GraphLabel =
        this.GraphAttributes
        |> Map.tryFind "label"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.ModelStylesheet =
        this.GraphAttributes
        |> Map.tryFind "model_stylesheet"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.DefaultMaxRetriesOption =
        this.GraphAttributes
        |> Map.tryFind "default_max_retries"
        |> Option.orElseWith (fun () -> Map.tryFind "default_max_retry" this.GraphAttributes)
        |> Option.bind (fun v -> v.AsInt())

    member this.DefaultMaxRetry = defaultArg this.DefaultMaxRetriesOption 0

    member this.RetryTarget =
        this.GraphAttributes
        |> Map.tryFind "retry_target"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.FallbackRetryTarget =
        this.GraphAttributes
        |> Map.tryFind "fallback_retry_target"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.StackChildDotfile =
        this.GraphAttributes
        |> Map.tryFind "stack.child_dotfile"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.StackChildWorkdir =
        this.GraphAttributes
        |> Map.tryFind "stack.child_workdir"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.DefaultFidelity =
        this.GraphAttributes
        |> Map.tryFind "default_fidelity"
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue ""

    member this.OutgoingEdges(nodeId: string) =
        this.Edges |> List.filter (fun e -> e.FromNode = nodeId)

    member this.IncomingEdges(nodeId: string) =
        this.Edges |> List.filter (fun e -> e.ToNode = nodeId)

    member this.FindStartNode() =
        this.Nodes
        |> Map.tryPick (fun _ n -> if n.Shape = "Mdiamond" then Some n else None)
        |> Option.orElseWith (fun () ->
            this.Nodes
            |> Map.tryFind "start"
            |> Option.orElseWith (fun () -> this.Nodes |> Map.tryFind "Start"))

    member this.FindExitNode() =
        this.Nodes
        |> Map.tryPick (fun _ n -> if n.Shape = "Msquare" then Some n else None)
        |> Option.orElseWith (fun () ->
            this.Nodes
            |> Map.tryFind "exit"
            |> Option.orElseWith (fun () -> this.Nodes |> Map.tryFind "end"))

    member this.GetGraphAttrString(key: string, defaultValue: string) =
        this.GraphAttributes
        |> Map.tryFind key
        |> Option.map (fun v -> v.AsString())
        |> Option.defaultValue defaultValue

module KnownAttributes =
    let node =
        set
            [ "label"
              "shape"
              "type"
              "prompt"
              "max_retries"
              "goal_gate"
              "retry_target"
              "fallback_retry_target"
              "fidelity"
              "thread_id"
              "class"
              "timeout"
              "llm_model"
              "llm_provider"
              "reasoning_effort"
              "auto_status"
              "allow_partial"
              "max_visits"
              "outcome_fail_pattern"
              "tool_hooks.pre"
              "tool_hooks.post"
              "tool_command"
              "human.default_choice"
              "system_prompt"
              "stop_condition_key"
              "observe_key"
              "lane"
              "cwd"
              "max_turns"
              "max_tool_rounds"
              "command_timeout"
              "max_cycles"
              "manager.max_cycles"
              "wait_ms"
              "acp_command"
              "acp_url"
              "acp_transport"
              "acp_preset"
              "acp_args_json"
              "acp_timeout_ms"
              "mcp_server"
              "mcp_tool"
              "mcp_config_file" ]

    let edge =
        set [ "label"; "condition"; "weight"; "fidelity"; "thread_id"; "loop_restart" ]

    let graph =
        set
            [ "goal"
              "label"
              "model_stylesheet"
              "default_fidelity"
              "default_max_retries"
              "default_max_retry"
              "retry_target"
              "fallback_retry_target"
              "stack.child_dotfile"
              "stack.child_workdir"
              "cwd"
              "llm_model"
              "mcp_servers" ]

    let graphvizPassthrough =
        set
            [ "color"
              "fillcolor"
              "fontname"
              "fontsize"
              "style"
              "penwidth"
              "margin"
              "rankdir"
              "bgcolor"
              "fontcolor"
              "width"
              "height"
              "fixedsize" ]

/// Thread-safe context key-value store
type IArtifactStore =
    abstract member Store: key: string * data: string -> unit
    abstract member Retrieve: key: string -> string option
    abstract member Has: key: string -> bool
    abstract member List: unit -> string list
    abstract member Remove: key: string -> unit
    abstract member Clear: unit -> unit

type FileArtifactStore(rootPath: string) =
    let artifactsDir = Path.Combine(rootPath, "artifacts")

    let ensureRoot () =
        if not (Directory.Exists(artifactsDir)) then
            Directory.CreateDirectory(artifactsDir) |> ignore

    let pathForKey (key: string) =
        let safe = key.Replace("/", "_").Replace("\\", "_").Replace("..", "_")
        Path.Combine(artifactsDir, safe)

    interface IArtifactStore with
        member _.Store(key, data) =
            ensureRoot ()
            File.WriteAllText(pathForKey key, data)

        member _.Retrieve(key) =
            let path = pathForKey key

            if File.Exists(path) then
                Some(File.ReadAllText(path))
            else
                None

        member _.Has(key) = File.Exists(pathForKey key)

        member _.List() =
            if not (Directory.Exists(artifactsDir)) then
                []
            else
                Directory.EnumerateFiles(artifactsDir) |> Seq.map Path.GetFileName |> Seq.toList

        member _.Remove(key) =
            let path = pathForKey key

            if File.Exists(path) then
                File.Delete(path)

        member _.Clear() =
            if Directory.Exists(artifactsDir) then
                Directory.Delete(artifactsDir, true)

type Context(?artifactStore: IArtifactStore, ?offloadThresholdBytes: int) =
    let values = Dictionary<string, string>()
    let logs = ResizeArray<string>()
    let lockObj = obj ()
    let mutable artifactStore = artifactStore
    let thresholdBytes = defaultArg offloadThresholdBytes (100 * 1024)

    let tryResolveArtifactRef (value: string) =
        if value.StartsWith("artifact:", StringComparison.Ordinal) then
            match artifactStore with
            | Some store ->
                let key = value.Substring("artifact:".Length)
                store.Retrieve(key)
            | None -> None
        else
            Some value

    let withOffload (key: string) (value: string) =
        match artifactStore with
        | Some store when
            not (value.StartsWith("artifact:", StringComparison.Ordinal))
            && Encoding.UTF8.GetByteCount(value) > thresholdBytes
            ->
            let artifactKey = sprintf "%s-%s.txt" key (Guid.NewGuid().ToString("N"))
            store.Store(artifactKey, value)
            $"artifact:{artifactKey}"
        | _ -> value

    member _.ConfigureArtifactStore(store: IArtifactStore) =
        lock lockObj (fun () -> artifactStore <- Some store)

    member _.Set(key: string, value: string) =
        lock lockObj (fun () -> values[key] <- withOffload key value)

    member _.Get(key: string, ?defaultValue: string) =
        lock lockObj (fun () ->
            match values.TryGetValue(key) with
            | true, v ->
                match tryResolveArtifactRef v with
                | Some resolved -> resolved
                | None -> defaultArg defaultValue ""
            | false, _ -> defaultArg defaultValue "")

    member _.TryGet(key: string) =
        lock lockObj (fun () ->
            match values.TryGetValue(key) with
            | true, v ->
                match tryResolveArtifactRef v with
                | Some resolved -> Some resolved
                | None -> None
            | false, _ -> None)

    member _.AppendLog(entry: string) =
        lock lockObj (fun () -> logs.Add(entry))

    member _.Snapshot() =
        lock lockObj (fun () -> values |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)

    member _.Logs = lock lockObj (fun () -> logs |> Seq.toList)

    member _.Clone() =
        // F# strings are immutable; copy is by value. Artifact store handle is intentionally shared.
        let ctx =
            match artifactStore with
            | Some store -> Context(artifactStore = store, offloadThresholdBytes = thresholdBytes)
            | None -> Context(offloadThresholdBytes = thresholdBytes)

        lock lockObj (fun () ->
            for kv in values do
                ctx.Set(kv.Key, kv.Value)

            for log in logs do
                ctx.AppendLog(log))

        ctx

    member _.ApplyUpdates(updates: Map<string, string>) =
        lock lockObj (fun () ->
            for kv in updates do
                values[kv.Key] <- withOffload kv.Key kv.Value)

    member _.Keys = lock lockObj (fun () -> values.Keys |> Seq.toList)

    member _.Count = lock lockObj (fun () -> values.Count)

    /// Project context through a fidelity mode, returning a filtered clone
    member this.Project(fidelity: FidelityMode, ?truncateLimit: int) : Context =
        let createProjectedContext () =
            match artifactStore with
            | Some store -> Context(artifactStore = store, offloadThresholdBytes = thresholdBytes)
            | None -> Context(offloadThresholdBytes = thresholdBytes)

        let copyLogs (ctx: Context) =
            for log in logs do
                ctx.AppendLog(log)

        let charLimit = defaultArg truncateLimit (FidelityMode.charBudget fidelity)

        let setWithinBudget (ctx: Context) (key: string) (value: string) (charCount: int) =
            if charCount >= charLimit then
                charCount
            else
                let remaining = charLimit - charCount

                let stored =
                    if value.Length > remaining then
                        value.Substring(0, remaining)
                    else
                        value

                ctx.Set(key, stored)
                charCount + key.Length + stored.Length

        match fidelity with
        | FidelityMode.Full -> this.Clone()
        | FidelityMode.Truncate ->
            let ctx = createProjectedContext ()

            lock lockObj (fun () ->
                for kv in values do
                    let resolved = tryResolveArtifactRef kv.Value |> Option.defaultValue kv.Value

                    let v =
                        if resolved.Length > charLimit then
                            resolved.Substring(0, charLimit)
                        else
                            resolved

                    ctx.Set(kv.Key, v)

                copyLogs ctx)

            ctx
        | FidelityMode.Compact ->
            let ctx = createProjectedContext ()

            lock lockObj (fun () ->
                let mutable charCount = 0

                for kv in values do
                    if
                        kv.Key.StartsWith("graph.", StringComparison.Ordinal)
                        || kv.Key = "current_node"
                        || kv.Key = "outcome"
                    then
                        let resolved = tryResolveArtifactRef kv.Value |> Option.defaultValue kv.Value
                        charCount <- setWithinBudget ctx kv.Key resolved charCount

                for kv in values do
                    if
                        not (
                            kv.Key.StartsWith("graph.", StringComparison.Ordinal)
                            || kv.Key = "current_node"
                            || kv.Key = "outcome"
                        )
                    then
                        let resolved = tryResolveArtifactRef kv.Value |> Option.defaultValue kv.Value
                        charCount <- setWithinBudget ctx kv.Key resolved charCount

                copyLogs ctx)

            ctx
        | FidelityMode.SummaryLow
        | FidelityMode.SummaryMedium ->
            let ctx = createProjectedContext ()

            lock lockObj (fun () ->
                let mutable charCount = 0

                for kv in values do
                    let resolved = tryResolveArtifactRef kv.Value |> Option.defaultValue kv.Value
                    charCount <- setWithinBudget ctx kv.Key resolved charCount

                copyLogs ctx)

            ctx
        | FidelityMode.SummaryHigh ->
            let ctx = createProjectedContext ()

            lock lockObj (fun () ->
                let summary =
                    values
                    |> Seq.map (fun kv ->
                        let resolved = tryResolveArtifactRef kv.Value |> Option.defaultValue kv.Value

                        let v =
                            if resolved.Length > 50 then
                                resolved.Substring(0, 50) + "..."
                            else
                                resolved

                        $"{kv.Key}={v}")
                    |> String.concat "; "

                let truncated =
                    if summary.Length > charLimit then
                        summary.Substring(0, charLimit)
                    else
                        summary

                ctx.Set("context_summary", truncated)
                copyLogs ctx)

            ctx

/// Checkpoint for crash recovery
type Checkpoint =
    { Timestamp: DateTimeOffset
      CurrentNode: string
      CompletedNodes: string list
      NodeRetries: Map<string, int>
      NodeOutcomes: Map<string, Outcome>
      ContextValues: Map<string, string>
      Logs: string list }

    static member Create
        (
            context: Context,
            currentNode: string,
            completedNodes: string list,
            nodeRetries: Map<string, int>,
            nodeOutcomes: Map<string, Outcome>
        ) =
        { Timestamp = DateTimeOffset.UtcNow
          CurrentNode = currentNode
          CompletedNodes = completedNodes
          NodeRetries = nodeRetries
          NodeOutcomes = nodeOutcomes
          ContextValues = context.Snapshot()
          Logs = context.Logs }

/// Shape-to-handler-type mapping
module ShapeMapping =
    let shapeToHandlerType =
        Map.ofList
            [ "Mdiamond", "start"
              "Msquare", "exit"
              "box", "codergen"
              "tab", "coding_agent"
              "hexagon", "wait.human"
              "diamond", "conditional"
              "component", "parallel"
              "tripleoctagon", "parallel.fan_in"
              "parallelogram", "tool"
              "house", "stack.manager_loop" ]

    let resolveHandlerType (node: Node) =
        if node.NodeType <> "" then
            node.NodeType
        else
            shapeToHandlerType |> Map.tryFind node.Shape |> Option.defaultValue "codergen"

    let isTerminal (node: Node) =
        node.Shape = "Msquare" || resolveHandlerType node = "exit"

    let isStart (node: Node) =
        node.Shape = "Mdiamond" || resolveHandlerType node = "start"
