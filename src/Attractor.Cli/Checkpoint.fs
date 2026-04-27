module Checkpoint

open System
open System.IO
open Attractor

let private usage () =
    eprintfn "Usage:"
    eprintfn "  attractor checkpoint inspect <run-dir>"
    eprintfn "  attractor checkpoint mark-done <run-dir> <node-id> [--outcome=success|fail] [--note=...] [--no-backup]"
    eprintfn "  attractor checkpoint set-outcome <run-dir> <node-id> <outcome> [--tool-stdout=...] [--no-backup]"
    eprintfn "  attractor checkpoint diff <run-dir>"
    eprintfn "  attractor checkpoint backup <run-dir>"

let private isRestartDirName (path: string) =
    let name =
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

    name.StartsWith("restart-", StringComparison.OrdinalIgnoreCase)

let private resolveRunDir (inputDir: string) =
    let full = Path.GetFullPath(inputDir)

    if not (Directory.Exists(full)) then
        Error $"Run directory does not exist: {full}"
    elif isRestartDirName full then
        Ok full
    else
        let restarts = Directory.GetDirectories(full, "restart-*")

        if restarts.Length = 0 then
            Ok full
        else
            restarts
            |> Array.sortByDescending (fun dir -> Directory.GetLastWriteTimeUtc(dir))
            |> Array.tryHead
            |> Option.map Ok
            |> Option.defaultValue (Ok full)

let private checkpointPath (runDir: string) = Path.Combine(runDir, "checkpoint.json")

let private backupPath (runDir: string) =
    Path.Combine(runDir, "checkpoint.json.bak")

let private ensureCheckpointExists (runDir: string) =
    let path = checkpointPath runDir

    if File.Exists(path) then
        Ok path
    else
        Error $"checkpoint.json not found in {runDir}"

let private loadCheckpoint (runDir: string) =
    match Engine.loadCheckpoint runDir with
    | Some cp -> Ok cp
    | None -> Error $"Failed to load checkpoint from {runDir}"

let private createBackup (runDir: string) =
    match ensureCheckpointExists runDir with
    | Error msg -> Error msg
    | Ok source ->
        let destination = backupPath runDir
        File.Copy(source, destination, true)
        Ok destination

let private saveCheckpoint (runDir: string) (checkpoint: Checkpoint) =
    Engine.saveCheckpoint runDir checkpoint
    Ok()

let private parseOutcome (raw: string) =
    match StageStatus.Parse(raw.Trim()) with
    | Some status -> Ok status
    | None -> Error $"Invalid outcome '{raw}'"

let private statusToOutcome
    (status: StageStatus)
    (note: string)
    (failureReason: string)
    (updates: Map<string, string>)
    =
    { Status = status
      RawOutcome = None
      PreferredLabel = ""
      SuggestedNextIds = []
      ContextUpdates = updates
      Notes = note
      FailureReason = failureReason }

let private markDone (runDir: string) (nodeId: string) (status: StageStatus) (note: string) (createBak: bool) =
    match loadCheckpoint runDir with
    | Error msg -> Error msg
    | Ok checkpoint ->
        if createBak then
            match createBackup runDir with
            | Error msg -> Error msg
            | Ok _ ->
                let updates =
                    Map.ofList
                        [ "last_response", $"Marked done via checkpoint CLI: {note}"
                          "last_stage", nodeId ]

                let failureReason =
                    match status with
                    | StageStatus.Fail -> "Marked fail via checkpoint CLI"
                    | _ -> ""

                let outcome = statusToOutcome status note failureReason updates

                let completedNodes =
                    if checkpoint.CompletedNodes |> List.contains nodeId then
                        checkpoint.CompletedNodes
                    else
                        checkpoint.CompletedNodes @ [ nodeId ]

                let contextValues =
                    checkpoint.ContextValues
                    |> Map.add "outcome" outcome.OutcomeString
                    |> Map.add "last_stage" nodeId
                    |> Map.add "last_response" $"Marked done via checkpoint CLI: {note}"

                let updated =
                    { checkpoint with
                        CurrentNode = nodeId
                        CompletedNodes = completedNodes
                        NodeOutcomes = checkpoint.NodeOutcomes |> Map.add nodeId outcome
                        ContextValues = contextValues }

                saveCheckpoint runDir updated
        else
            let updates =
                Map.ofList
                    [ "last_response", $"Marked done via checkpoint CLI: {note}"
                      "last_stage", nodeId ]

            let failureReason =
                match status with
                | StageStatus.Fail -> "Marked fail via checkpoint CLI"
                | _ -> ""

            let outcome = statusToOutcome status note failureReason updates

            let completedNodes =
                if checkpoint.CompletedNodes |> List.contains nodeId then
                    checkpoint.CompletedNodes
                else
                    checkpoint.CompletedNodes @ [ nodeId ]

            let contextValues =
                checkpoint.ContextValues
                |> Map.add "outcome" outcome.OutcomeString
                |> Map.add "last_stage" nodeId
                |> Map.add "last_response" $"Marked done via checkpoint CLI: {note}"

            let updated =
                { checkpoint with
                    CurrentNode = nodeId
                    CompletedNodes = completedNodes
                    NodeOutcomes = checkpoint.NodeOutcomes |> Map.add nodeId outcome
                    ContextValues = contextValues }

            saveCheckpoint runDir updated

let private setOutcome
    (runDir: string)
    (nodeId: string)
    (status: StageStatus)
    (toolStdout: string option)
    (createBak: bool)
    =
    match loadCheckpoint runDir with
    | Error msg -> Error msg
    | Ok checkpoint ->
        if createBak then
            match createBackup runDir with
            | Error msg -> Error msg
            | Ok _ ->
                let existing = checkpoint.NodeOutcomes |> Map.tryFind nodeId

                let note =
                    existing
                    |> Option.map _.Notes
                    |> Option.defaultValue "Updated via checkpoint CLI"

                let updates =
                    let baseUpdates =
                        existing
                        |> Option.map _.ContextUpdates
                        |> Option.defaultValue Map.empty
                        |> Map.add "last_stage" nodeId

                    match toolStdout with
                    | Some value ->
                        baseUpdates
                        |> Map.add "tool.output" value
                        |> Map.add "tool_stdout" value
                        |> Map.add "tool_output" value
                    | None -> baseUpdates

                let failureReason =
                    match status with
                    | StageStatus.Fail -> "Set to fail via checkpoint CLI"
                    | _ -> ""

                let outcome = statusToOutcome status note failureReason updates

                let contextValues =
                    let withOutcome =
                        checkpoint.ContextValues |> Map.add "outcome" outcome.OutcomeString

                    let withStage = withOutcome |> Map.add "last_stage" nodeId

                    match toolStdout with
                    | Some value ->
                        withStage
                        |> Map.add "tool.output" value
                        |> Map.add "tool_stdout" value
                        |> Map.add "tool_output" value
                    | None -> withStage

                let updated =
                    { checkpoint with
                        NodeOutcomes = checkpoint.NodeOutcomes |> Map.add nodeId outcome
                        ContextValues = contextValues }

                saveCheckpoint runDir updated
        else
            let existing = checkpoint.NodeOutcomes |> Map.tryFind nodeId

            let note =
                existing
                |> Option.map _.Notes
                |> Option.defaultValue "Updated via checkpoint CLI"

            let updates =
                let baseUpdates =
                    existing
                    |> Option.map _.ContextUpdates
                    |> Option.defaultValue Map.empty
                    |> Map.add "last_stage" nodeId

                match toolStdout with
                | Some value ->
                    baseUpdates
                    |> Map.add "tool.output" value
                    |> Map.add "tool_stdout" value
                    |> Map.add "tool_output" value
                | None -> baseUpdates

            let failureReason =
                match status with
                | StageStatus.Fail -> "Set to fail via checkpoint CLI"
                | _ -> ""

            let outcome = statusToOutcome status note failureReason updates

            let contextValues =
                let withOutcome =
                    checkpoint.ContextValues |> Map.add "outcome" outcome.OutcomeString

                let withStage = withOutcome |> Map.add "last_stage" nodeId

                match toolStdout with
                | Some value ->
                    withStage
                    |> Map.add "tool.output" value
                    |> Map.add "tool_stdout" value
                    |> Map.add "tool_output" value
                | None -> withStage

            let updated =
                { checkpoint with
                    NodeOutcomes = checkpoint.NodeOutcomes |> Map.add nodeId outcome
                    ContextValues = contextValues }

            saveCheckpoint runDir updated

let private inspect (runDir: string) =
    match loadCheckpoint runDir with
    | Error msg ->
        eprintfn "%s" msg
        1
    | Ok checkpoint ->
        printfn "checkpoint: %s" (checkpointPath runDir)
        printfn "current_node: %s" checkpoint.CurrentNode
        printfn "completed_nodes: %d" checkpoint.CompletedNodes.Length

        if checkpoint.CompletedNodes.IsEmpty then
            printfn "  (none)"
        else
            checkpoint.CompletedNodes |> List.iter (fun nodeId -> printfn "  - %s" nodeId)

        printfn "node_outcomes: %d" checkpoint.NodeOutcomes.Count

        checkpoint.NodeOutcomes
        |> Map.toList
        |> List.sortBy fst
        |> List.iter (fun (nodeId, outcome) ->
            let reason =
                if String.IsNullOrWhiteSpace(outcome.FailureReason) then
                    ""
                else
                    $" reason={outcome.FailureReason}"

            printfn "  - %s: %s%s" nodeId (outcome.Status.ToString()) reason)

        0

let private backup (runDir: string) =
    match createBackup runDir with
    | Ok bak ->
        printfn "backup written: %s" bak
        0
    | Error msg ->
        eprintfn "%s" msg
        1

let private diff (runDir: string) =
    match loadCheckpoint runDir with
    | Error msg ->
        eprintfn "%s" msg
        1
    | Ok checkpoint ->
        let parent = Directory.GetParent(runDir)

        let dotFiles =
            [ yield! Directory.GetFiles(runDir, "*.dot")
              if not (isNull parent) then
                  yield! Directory.GetFiles(parent.FullName, "*.dot") ]
            |> List.distinct

        match dotFiles with
        | [] ->
            printfn "No sibling .dot file found for checkpoint diff."
            0
        | dotPath :: _ ->
            let source = File.ReadAllText(dotPath)
            let graph = DotParser.parseOrRaise source
            let graphNodeIds = graph.Nodes |> Map.toSeq |> Seq.map fst |> Set.ofSeq

            let completedMissing =
                checkpoint.CompletedNodes
                |> List.filter (fun id -> not (graphNodeIds.Contains(id)))

            let outcomeMissing =
                checkpoint.NodeOutcomes
                |> Map.keys
                |> Seq.filter (fun id -> not (graphNodeIds.Contains(id)))
                |> Seq.toList

            let unseenNodes =
                graphNodeIds
                |> Set.filter (fun id -> not (checkpoint.CompletedNodes |> List.contains id))

            printfn "dot_file: %s" dotPath
            printfn "checkpoint: %s" (checkpointPath runDir)

            if completedMissing.IsEmpty && outcomeMissing.IsEmpty then
                printfn "No stale node IDs found in checkpoint."
            else
                if not completedMissing.IsEmpty then
                    printfn "Completed nodes missing from DOT:"
                    completedMissing |> List.iter (fun id -> printfn "  - %s" id)

                if not outcomeMissing.IsEmpty then
                    printfn "Outcome entries missing from DOT:"
                    outcomeMissing |> List.iter (fun id -> printfn "  - %s" id)

            if unseenNodes.IsEmpty then
                printfn "All DOT nodes are present in checkpoint completed_nodes."
            else
                printfn "DOT nodes not yet completed in checkpoint:"
                unseenNodes |> Seq.sort |> Seq.iter (fun id -> printfn "  - %s" id)

            0

let dispatch (args: string array) =
    if args.Length = 0 then
        usage ()
        3
    else
        let command = args[0]

        let runWithResolvedDir (inputDir: string) (action: string -> int) =
            match resolveRunDir inputDir with
            | Ok resolved ->
                if resolved <> Path.GetFullPath(inputDir) then
                    printfn "resolved run dir: %s" resolved

                action resolved
            | Error msg ->
                eprintfn "%s" msg
                3

        match command with
        | "inspect" when args.Length >= 2 -> runWithResolvedDir args[1] inspect
        | "backup" when args.Length >= 2 -> runWithResolvedDir args[1] backup
        | "diff" when args.Length >= 2 -> runWithResolvedDir args[1] diff
        | "mark-done" when args.Length >= 3 ->
            let runDir = args[1]
            let nodeId = args[2]
            let mutable outcomeRaw = "success"
            let mutable note = "manual checkpoint update"
            let mutable noBackup = false
            let mutable hasUnknown = false

            for arg in args |> Array.skip 3 do
                if arg.StartsWith("--outcome=", StringComparison.Ordinal) then
                    outcomeRaw <- arg.Substring("--outcome=".Length)
                elif arg.StartsWith("--note=", StringComparison.Ordinal) then
                    note <- arg.Substring("--note=".Length)
                elif arg = "--no-backup" then
                    noBackup <- true
                else
                    eprintfn "Unknown option: %s" arg
                    hasUnknown <- true

            if hasUnknown then
                usage ()
                3
            else
                match parseOutcome outcomeRaw with
                | Error msg ->
                    eprintfn "%s" msg
                    3
                | Ok status ->
                    runWithResolvedDir runDir (fun resolved ->
                        match markDone resolved nodeId status note (not noBackup) with
                        | Ok() ->
                            printfn "Updated checkpoint: node '%s' marked %s" nodeId (status.ToString())
                            0
                        | Error msg ->
                            eprintfn "%s" msg
                            1)
        | "set-outcome" when args.Length >= 4 ->
            let runDir = args[1]
            let nodeId = args[2]
            let outcomeRaw = args[3]
            let mutable toolStdout: string option = None
            let mutable noBackup = false
            let mutable hasUnknown = false

            for arg in args |> Array.skip 4 do
                if arg.StartsWith("--tool-stdout=", StringComparison.Ordinal) then
                    toolStdout <- Some(arg.Substring("--tool-stdout=".Length))
                elif arg = "--no-backup" then
                    noBackup <- true
                else
                    eprintfn "Unknown option: %s" arg
                    hasUnknown <- true

            if hasUnknown then
                usage ()
                3
            else
                match parseOutcome outcomeRaw with
                | Error msg ->
                    eprintfn "%s" msg
                    3
                | Ok status ->
                    runWithResolvedDir runDir (fun resolved ->
                        match setOutcome resolved nodeId status toolStdout (not noBackup) with
                        | Ok() ->
                            printfn "Updated checkpoint: node '%s' outcome=%s" nodeId (status.ToString())
                            0
                        | Error msg ->
                            eprintfn "%s" msg
                            1)
        | _ ->
            usage ()
            3
