namespace Attractor

open System
open System.Collections.Generic

/// Question types for human interaction
[<RequireQualifiedAccess>]
type QuestionType =
    | YesNo
    | SingleSelect
    | MultiSelect
    | MultipleChoice
    | Freeform
    | Confirmation

/// An option in a multiple choice question
type QuestionOption = { Key: string; Label: string }

/// A question presented to a human
type Question =
    { Text: string
      Type: QuestionType
      Options: QuestionOption list
      Default: Answer option
      TimeoutSeconds: float option
      Stage: string
      Metadata: Map<string, string> }

/// Answer value types
and [<RequireQualifiedAccess>] AnswerValue =
    | Yes
    | No
    | Skipped
    | Timeout

/// An answer from a human
and Answer =
    { Value: string
      SelectedOption: QuestionOption option
      Text: string }

    static member Yes =
        { Value = "yes"
          SelectedOption = None
          Text = "" }

    static member No =
        { Value = "no"
          SelectedOption = None
          Text = "" }

    static member Skipped =
        { Value = "skipped"
          SelectedOption = None
          Text = "" }

    static member Timeout =
        { Value = "timeout"
          SelectedOption = None
          Text = "" }

    static member FromOption(opt: QuestionOption) =
        { Value = opt.Key
          SelectedOption = Some opt
          Text = opt.Label }

    static member FromText(text: string) =
        { Value = text
          SelectedOption = None
          Text = text }

    member this.IsTimeout = this.Value = "timeout"
    member this.IsSkipped = this.Value = "skipped"
    member this.IsYes = this.Value = "yes" || this.Value = "y"
    member this.IsNo = this.Value = "no" || this.Value = "n"

/// Interviewer interface for human interaction
type IInterviewer =
    abstract member Ask: Question -> Answer
    abstract member AskMultiple: Question list -> Answer list
    abstract member Inform: message: string * stage: string -> unit

/// Auto-approve interviewer for automation/testing
type AutoApproveInterviewer() =
    interface IInterviewer with
        member _.Ask(question) =
            match question.Type with
            | QuestionType.YesNo
            | QuestionType.Confirmation -> Answer.Yes
            | QuestionType.SingleSelect
            | QuestionType.MultipleChoice when question.Options.Length > 0 -> Answer.FromOption(question.Options[0])
            | QuestionType.MultiSelect when question.Options.Length > 0 ->
                let selectedKeys =
                    question.Options |> List.map (fun opt -> opt.Key) |> String.concat ","

                Answer.FromText(selectedKeys)
            | _ ->
                { Value = "auto-approved"
                  SelectedOption = None
                  Text = "auto-approved" }

        member this.AskMultiple(questions) =
            questions |> List.map (this :> IInterviewer).Ask

        member _.Inform(_, _) = ()

/// Callback interviewer that delegates to a function
type CallbackInterviewer(callback: Question -> Answer) =
    interface IInterviewer with
        member _.Ask(question) = callback question

        member this.AskMultiple(questions) =
            questions |> List.map (this :> IInterviewer).Ask

        member _.Inform(_, _) = ()

/// Console interviewer that reads from standard input
type ConsoleInterviewer() =
    interface IInterviewer with
        member _.Ask(question) =
            printfn ""
            printfn "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
            printfn "  HUMAN GATE: %s (stage: %s)" question.Text question.Stage
            printfn "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

            // Show context: where to look
            let logsRoot =
                question.Metadata |> Map.tryFind "logs_root" |> Option.defaultValue ""

            let lastStage =
                question.Metadata |> Map.tryFind "last_stage" |> Option.defaultValue ""

            let goal = question.Metadata |> Map.tryFind "goal" |> Option.defaultValue ""

            if goal <> "" then
                printfn "  Goal: %s" goal

            if logsRoot <> "" && lastStage <> "" then
                let stageDir = System.IO.Path.Combine(logsRoot, lastStage)
                printfn ""
                printfn "  Review the output from the previous stage:"
                printfn "    Prompt:   %s/prompt.md" stageDir
                printfn "    Response: %s/response.md" stageDir
                printfn "    Status:   %s/status.json" stageDir
            elif logsRoot <> "" then
                printfn ""
                printfn "  Logs directory: %s" logsRoot

            // Show prompt_file if present in metadata
            let promptFile = question.Metadata |> Map.tryFind "prompt_file"
            let responseFile = question.Metadata |> Map.tryFind "response_file"

            match promptFile with
            | Some pf ->
                printfn ""
                printfn "  Prompt:   %s" pf

                match responseFile with
                | Some rf -> printfn "  Response: %s" rf
                | None -> ()
            | None -> ()

            printfn ""

            match question.Type with
            | QuestionType.Freeform ->
                match responseFile with
                | Some _ ->
                    printfn "  Edit the response file, then press Enter to continue."
                    printf "  > [Enter] "
                | None -> printf "  > "

                let response = Console.ReadLine()

                if isNull response || UnifiedLlm.HttpCancellation.isCancelled () then
                    Answer.Skipped
                else
                    Answer.FromText(response.Trim())
            | QuestionType.SingleSelect
            | QuestionType.MultipleChoice ->
                printfn "  Options:"

                for opt in question.Options do
                    printfn "    [%s] %s" opt.Key opt.Label

                printfn ""
                printf "  Enter key or label > "
                let response = Console.ReadLine()

                if isNull response || UnifiedLlm.HttpCancellation.isCancelled () then
                    Answer.Skipped
                else
                    let response = response.Trim()

                    match
                        question.Options
                        |> List.tryFind (fun o ->
                            o.Key.Equals(response, StringComparison.OrdinalIgnoreCase)
                            || o.Label.Equals(response, StringComparison.OrdinalIgnoreCase))
                    with
                    | Some opt -> Answer.FromOption(opt)
                    | None ->
                        match question.Options |> List.tryHead with
                        | Some opt -> Answer.FromOption(opt)
                        | None -> Answer.FromText(response)
            | QuestionType.MultiSelect ->
                printfn "  Options (comma-separated keys):"

                for opt in question.Options do
                    printfn "    [%s] %s" opt.Key opt.Label

                printfn ""
                printf "  Enter keys > "
                let response = Console.ReadLine()

                if isNull response || UnifiedLlm.HttpCancellation.isCancelled () then
                    Answer.Skipped
                else
                    let selected =
                        response.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                        |> Array.toList
                        |> List.choose (fun key ->
                            question.Options
                            |> List.tryFind (fun opt ->
                                opt.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                || opt.Label.Equals(key, StringComparison.OrdinalIgnoreCase)))
                        |> List.map (fun opt -> opt.Key)

                    if selected.IsEmpty then
                        Answer.FromText(response.Trim())
                    else
                        Answer.FromText(String.concat "," selected)
            | QuestionType.YesNo ->
                printf "  [Y/N] > "
                let response = Console.ReadLine()

                if isNull response || UnifiedLlm.HttpCancellation.isCancelled () then
                    Answer.Skipped
                elif response.Trim().ToLower().StartsWith("y", StringComparison.Ordinal) then
                    Answer.Yes
                else
                    Answer.No
            | QuestionType.Confirmation ->
                printf "  [Y/N] > "
                let response = Console.ReadLine()

                if isNull response || UnifiedLlm.HttpCancellation.isCancelled () then
                    Answer.Skipped
                elif response.Trim().ToLower().StartsWith("y", StringComparison.Ordinal) then
                    Answer.Yes
                else
                    Answer.No

        member this.AskMultiple(questions) =
            questions |> List.map (this :> IInterviewer).Ask

        member _.Inform(message, stage) = printfn "[%s] %s" stage message

/// Queue interviewer that reads from pre-filled answers
type QueueInterviewer(answers: Answer list) =
    let queue = Queue<Answer>(answers)

    interface IInterviewer with
        member _.Ask(_) =
            if queue.Count > 0 then queue.Dequeue() else Answer.Skipped

        member this.AskMultiple(questions) =
            questions |> List.map (this :> IInterviewer).Ask

        member _.Inform(_, _) = ()

/// Recording interviewer that wraps another and records Q&A pairs
type RecordingInterviewer(inner: IInterviewer) =
    let recordings = ResizeArray<Question * Answer>()

    member _.Recordings = recordings |> Seq.toList

    interface IInterviewer with
        member _.Ask(question) =
            let answer = inner.Ask(question)
            recordings.Add(question, answer)
            answer

        member this.AskMultiple(questions) =
            questions |> List.map (this :> IInterviewer).Ask

        member _.Inform(message, stage) = inner.Inform(message, stage)

/// Helper to parse accelerator keys from edge labels
module AcceleratorKey =
    open System.Text.RegularExpressions

    let parse (label: string) : string =
        if String.IsNullOrWhiteSpace(label) then
            ""
        else
            // Pattern: [K] Label
            let m1 = Regex.Match(label, @"^\[(\w)\]\s+")

            if m1.Success then
                m1.Groups[1].Value
            else
                // Pattern: K) Label
                let m2 = Regex.Match(label, @"^(\w)\)\s+")

                if m2.Success then
                    m2.Groups[1].Value
                else
                    // Pattern: K - Label
                    let m3 = Regex.Match(label, @"^(\w)\s*-\s+")

                    if m3.Success then
                        m3.Groups[1].Value
                    else
                        // First character
                        label[0].ToString()

    /// Strip accelerator prefix from a label for display (preserves case)
    let displayLabel (label: string) : string =
        if String.IsNullOrWhiteSpace(label) then
            ""
        else
            let l = label.Trim()
            let m1 = Regex.Match(l, @"^\[\w\]\s+(.+)")

            if m1.Success then
                m1.Groups[1].Value
            else
                let m2 = Regex.Match(l, @"^\w\)\s+(.+)")

                if m2.Success then
                    m2.Groups[1].Value
                else
                    let m3 = Regex.Match(l, @"^\w\s*-\s+(.+)")
                    if m3.Success then m3.Groups[1].Value else l

    /// Normalize a label for matching (lowercase, trim, strip accelerator prefix)
    let normalizeLabel (label: string) : string =
        if String.IsNullOrWhiteSpace(label) then
            ""
        else
            let stripped =
                let l = label.Trim()
                // Strip [K] prefix
                let m1 = Regex.Match(l, @"^\[\w\]\s+(.+)")

                if m1.Success then
                    m1.Groups[1].Value
                else
                    // Strip K) prefix
                    let m2 = Regex.Match(l, @"^\w\)\s+(.+)")

                    if m2.Success then
                        m2.Groups[1].Value
                    else
                        // Strip K - prefix
                        let m3 = Regex.Match(l, @"^\w\s*-\s+(.+)")
                        if m3.Success then m3.Groups[1].Value else l

            stripped.Trim().ToLowerInvariant()
