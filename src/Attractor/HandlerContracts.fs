namespace Attractor

open System.IO
open System.Text.Json

type IHandler =
    abstract member Execute: node: Node * context: Context * graph: Graph * logsRoot: string -> Outcome

type ICodergenBackend =
    abstract member Run: node: Node * prompt: string * context: Context -> Result<string, Outcome>

module HandlerArtifacts =

    let private writeStageFile (stageDir: string) (rootDir: string) (fileName: string) (content: string) =
        if not (Directory.Exists(stageDir)) then
            Directory.CreateDirectory(stageDir) |> ignore

        File.WriteAllText(Path.Combine(stageDir, fileName), content)

        if stageDir <> rootDir then
            if not (Directory.Exists(rootDir)) then
                Directory.CreateDirectory(rootDir) |> ignore

            File.WriteAllText(Path.Combine(rootDir, fileName), content)

    let writeStatus (stageDir: string) (rootDir: string) (outcome: Outcome) =
        let status =
            {| outcome = outcome.OutcomeString
               preferred_label = outcome.PreferredLabel
               suggested_next_ids = outcome.SuggestedNextIds
               context_updates = outcome.ContextUpdates
               notes = outcome.Notes
               failure_reason = outcome.FailureReason |}

        let json =
            JsonSerializer.Serialize(status, JsonSerializerOptions(WriteIndented = true))

        writeStageFile stageDir rootDir "status.json" json
