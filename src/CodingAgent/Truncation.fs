namespace CodingAgent

open System

/// Default character limits per tool
module TruncationDefaults =
    let defaultCharLimits = Map.ofList [
        "read_file", 50000
        "shell", 30000
        "grep", 20000
        "glob", 20000
        "edit_file", 10000
        "apply_patch", 10000
        "write_file", 1000
        "spawn_agent", 20000
    ]

    let defaultLineLimits = Map.ofList [
        "shell", 256
        "grep", 200
        "glob", 500
    ]

    /// Truncation modes per tool
    let truncationMode (toolName: string) =
        match toolName with
        | "shell" -> "tail"
        | "grep" | "glob" | "edit_file" | "apply_patch" | "write_file" -> "tail"
        | _ -> "head_tail"

/// Tool output truncation
module Truncation =

    /// Character-based truncation with head/tail split or tail-only
    let truncateChars (output: string) (maxChars: int) (mode: string) : string =
        if output.Length <= maxChars then
            output
        else
            match mode with
            | "head_tail" ->
                let half = maxChars / 2
                let removed = output.Length - maxChars
                let head = output.Substring(0, half)
                let tail = output.Substring(output.Length - half)
                sprintf "%s\n\n[WARNING: Tool output was truncated. %d characters removed from the middle.]\n\n%s"
                    head removed tail
            | "tail" ->
                let removed = output.Length - maxChars
                let tail = output.Substring(output.Length - maxChars)
                sprintf "[WARNING: Tool output was truncated. First %d characters were removed.]\n\n%s"
                    removed tail
            | _ ->
                let half = maxChars / 2
                let removed = output.Length - maxChars
                let head = output.Substring(0, half)
                let tail = output.Substring(output.Length - half)
                sprintf "%s\n\n[WARNING: Tool output was truncated. %d characters removed from the middle.]\n\n%s"
                    head removed tail

    /// Line-based truncation with head/tail split
    let truncateLines (output: string) (maxLines: int) : string =
        let lines = output.Split([| '\n' |])
        if lines.Length <= maxLines then
            output
        else
            let headCount = maxLines / 2
            let tailCount = maxLines - headCount
            let omitted = lines.Length - headCount - tailCount
            let head = lines |> Array.take headCount |> String.concat "\n"
            let tail = lines |> Array.skip (lines.Length - tailCount) |> String.concat "\n"
            sprintf "%s\n[... %d lines omitted ...]\n%s" head omitted tail

    /// Full truncation pipeline: character-based first, then line-based
    let truncateToolOutput (output: string) (toolName: string) (config: SessionConfig) : string =
        // Step 1: Character-based truncation (primary safeguard)
        let maxChars =
            config.ToolOutputLimits
            |> Map.tryFind toolName
            |> Option.defaultWith (fun () ->
                TruncationDefaults.defaultCharLimits
                |> Map.tryFind toolName
                |> Option.defaultValue 30000)
        let mode = TruncationDefaults.truncationMode toolName
        let afterChars = truncateChars output maxChars mode

        // Step 2: Line-based truncation (secondary)
        let maxLines =
            config.ToolLineLimits
            |> Map.tryFind toolName
            |> Option.orElseWith (fun () ->
                TruncationDefaults.defaultLineLimits |> Map.tryFind toolName)
        match maxLines with
        | Some ml -> truncateLines afterChars ml
        | None -> afterChars
