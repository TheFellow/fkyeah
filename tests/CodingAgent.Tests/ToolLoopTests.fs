module CodingAgent.ToolLoopTests

open System
open System.IO
open System.Text.Json
open Xunit
open UnifiedLlm
open CodingAgent

let private toolCallResponse callId toolName arguments =
    fun (_: Request) ->
        let tc =
            { Id = callId
              Name = toolName
              Arguments = arguments
              Metadata = Map.empty }

        { Id = "r_" + callId
          Model = "m"
          Provider = "test"
          Message = { Role = Assistant; Content = [ ToolCall tc ]; Name = None; ToolCallId = None }
          FinishReason = ToolCalls "tool_calls"
          Usage = Usage.Zero
          ResponseId = None
          Raw = None
          Warnings = []
          RateLimit = None }

[<Fact>]
let ``C3 read edit verify tool loop updates file and records tool turns`` () =
    let dir = CodingAgent.Tests.createTempDir ()

    try
        File.WriteAllText(Path.Combine(dir, "target.txt"), "original content")

        let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
        let readArgs = JsonSerializer.Serialize({| file_path = "target.txt" |})

        let editArgs =
            JsonSerializer.Serialize(
                {| file_path = "target.txt"
                   old_string = "original content"
                   new_string = "updated content" |})

        let mock =
            CodingAgent.Tests.makeMockAdapter
                [ toolCallResponse "call_1" "read_file" readArgs
                  toolCallResponse "call_2" "edit_file" editArgs
                  toolCallResponse "call_3" "read_file" readArgs
                  fun (_: Request) ->
                      { Id = "r4"
                        Model = "m"
                        Provider = "test"
                        Message = Message.assistant("Edit verified.")
                        FinishReason = Stop "stop"
                        Usage = Usage.Zero
                        ResponseId = None
                        Raw = None
                        Warnings = []
                        RateLimit = None } ]

        let client = Client()
        client.RegisterAdapter(mock)

        let session = Session(CodingAgent.Tests.TestProfile("m"), env, client)
        session.ProcessInput("Read target.txt, edit it, then verify the change.")

        let updated = File.ReadAllText(Path.Combine(dir, "target.txt"))
        Assert.Contains("updated content", updated)

        let toolTurns =
            session.History
            |> List.choose (function
                | ToolResultsTurn(results, _) -> Some results
                | _ -> None)

        Assert.Equal(3, toolTurns.Length)

        match session.History |> List.last with
        | AssistantTurn(content, _, _, _, _) -> Assert.Contains("verified", content)
        | _ -> Assert.Fail("Expected final assistant turn")
    finally
        CodingAgent.Tests.cleanupDir dir
