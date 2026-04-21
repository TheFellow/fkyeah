namespace Attractor

open System
open System.Text

module ContextPrompt =

    type PreparedContext =
        { SystemMessage: string
          FidelityMode: FidelityMode
          TokenBudget: int
          CharBudgetUsed: int
          IncludedKeys: string list
          TruncatedKeys: string list
          ExcludedKeys: string list
          UsedFreshSession: bool }

    type private Entry =
        { Key: string
          Label: string
          Value: string
          WrapInCodeFence: bool }

    let private formatEntry (entry: Entry) (value: string) =
        if entry.WrapInCodeFence then
            $"## {entry.Label}\n```\n{value}\n```\n\n"
        else
            $"## {entry.Label}\n{value}\n\n"

    let preparePromptContext (fidelity: FidelityMode) (context: Context) (goal: string) : PreparedContext =
        let snapshot = context.Snapshot()
        let tokenBudget = FidelityMode.tokenBudget fidelity
        let charBudget = FidelityMode.charBudget fidelity
        let included = ResizeArray<string>()
        let truncated = ResizeArray<string>()
        let excluded = ResizeArray<string>()
        let sb = StringBuilder()

        sb.AppendLine("You are a stage in an Attractor pipeline. Use the context below to inform your response.")
        |> ignore

        sb.AppendLine(
            "Do NOT hallucinate information. Only reference files, code, and data that appear in the context."
        )
        |> ignore

        sb.AppendLine("If the context doesn't contain enough information, say so explicitly.")
        |> ignore

        sb.AppendLine() |> ignore

        if goal <> "" then
            sb.AppendLine($"## Pipeline Goal\n{goal}") |> ignore
            sb.AppendLine() |> ignore

            if snapshot |> Map.containsKey "graph.goal" then
                included.Add("graph.goal")

        let structuralEntries =
            snapshot
            |> Map.toList
            |> List.choose (fun (key, value) ->
                if
                    key <> "graph.goal"
                    && (key.StartsWith("graph.", StringComparison.Ordinal)
                        || key = "current_node"
                        || key = "outcome")
                then
                    Some
                        { Key = key
                          Label = key
                          Value = value
                          WrapInCodeFence = false }
                else
                    None)

        let addEntry (usedChars: int) (entry: Entry) =
            if charBudget = Int32.MaxValue then
                let rendered = formatEntry entry entry.Value
                sb.Append(rendered) |> ignore
                included.Add(entry.Key)
                usedChars + rendered.Length
            else
                let remaining = charBudget - usedChars

                if remaining <= 0 then
                    excluded.Add(entry.Key)
                    usedChars
                else
                    let fullRendered = formatEntry entry entry.Value

                    if fullRendered.Length <= remaining then
                        sb.Append(fullRendered) |> ignore
                        included.Add(entry.Key)
                        usedChars + fullRendered.Length
                    else
                        let truncatedSuffix =
                            if entry.WrapInCodeFence then
                                "\n[truncated]"
                            else
                                " [truncated]"

                        let fixedOverhead =
                            (formatEntry entry "" |> fun rendered -> rendered.Length)
                            + truncatedSuffix.Length

                        let roomForValue = max 0 (remaining - fixedOverhead)

                        if roomForValue = 0 then
                            excluded.Add(entry.Key)
                            usedChars
                        else
                            let clipped =
                                if entry.Value.Length > roomForValue then
                                    entry.Value.Substring(0, roomForValue) + truncatedSuffix
                                else
                                    entry.Value

                            let rendered = formatEntry entry clipped
                            sb.Append(rendered) |> ignore
                            included.Add(entry.Key)
                            truncated.Add(entry.Key)
                            usedChars + rendered.Length

        let prioritizedEntries =
            let addIfPresent key label wrap acc =
                match snapshot |> Map.tryFind key with
                | Some value when value <> "" ->
                    { Key = key
                      Label = label
                      Value = value
                      WrapInCodeFence = wrap }
                    :: acc
                | _ -> acc

            let toolOutputEntries =
                []
                |> addIfPresent "tool.output" "Tool Output (from previous stage)" true
                |> addIfPresent "tool.stderr" "Tool Stderr" true
                |> addIfPresent "last_response" "Previous Stage Response" false
                |> List.rev

            let parallelEntries =
                snapshot
                |> Map.toList
                |> List.choose (fun (key, value) ->
                    if key.StartsWith("parallel.branch.", StringComparison.Ordinal) then
                        Some
                            { Key = key
                              Label = key
                              Value = value
                              WrapInCodeFence = false }
                    else
                        None)

            let humanEntries =
                snapshot
                |> Map.toList
                |> List.choose (fun (key, value) ->
                    if key.StartsWith("human.gate.", StringComparison.Ordinal) then
                        Some
                            { Key = key
                              Label = key
                              Value = value
                              WrapInCodeFence = false }
                    else
                        None)

            let alreadySelected =
                structuralEntries
                |> List.map (fun entry -> entry.Key)
                |> Set.ofList
                |> Set.union (toolOutputEntries |> List.map (fun entry -> entry.Key) |> Set.ofList)
                |> Set.union (parallelEntries |> List.map (fun entry -> entry.Key) |> Set.ofList)
                |> Set.union (humanEntries |> List.map (fun entry -> entry.Key) |> Set.ofList)
                |> Set.add "graph.goal"

            let remainingEntries =
                snapshot
                |> Map.toList
                |> List.choose (fun (key, value) ->
                    if alreadySelected |> Set.contains key then
                        None
                    else
                        Some
                            { Key = key
                              Label = key
                              Value = value
                              WrapInCodeFence = false })

            structuralEntries
            @ toolOutputEntries
            @ parallelEntries
            @ humanEntries
            @ remainingEntries

        let usedChars = (0, prioritizedEntries) ||> List.fold addEntry

        { SystemMessage = sb.ToString()
          FidelityMode = fidelity
          TokenBudget = tokenBudget
          CharBudgetUsed = usedChars
          IncludedKeys = included |> Seq.toList
          TruncatedKeys = truncated |> Seq.distinct |> Seq.toList
          ExcludedKeys = excluded |> Seq.distinct |> Seq.toList
          UsedFreshSession = FidelityMode.useFreshSession fidelity }
