module CodingAgent.StreamingToolTests

open System
open System.IO
open Xunit
open UnifiedLlm
open CodingAgent

[<Fact>]
let ``C4 streaming response with multiple tool calls dispatches all tools`` () =
    let mock = ConfigurableMockAdapter("test")
    let mutable streamCalls = 0

    mock.SetStreamHandler(fun _ ->
        streamCalls <- streamCalls + 1

        if streamCalls = 1 then
            let tc1 =
                { Id = "call_1"
                  Name = "tool_a"
                  Arguments = """{"x":"1"}"""
                  Metadata = Map.empty }

            let tc2 =
                { Id = "call_2"
                  Name = "tool_b"
                  Arguments = """{"y":"2"}"""
                  Metadata = Map.empty }

            let response =
                { Id = "r1"
                  Model = "m"
                  Provider = "test"
                  Message =
                    { Role = Assistant
                      Content = [ Text "thinking..."; ToolCall tc1; ToolCall tc2 ]
                      Name = None
                      ToolCallId = None }
                  FinishReason = ToolCalls "tool_calls"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None }

            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", "thinking...")
                yield TextEnd "text-1"
                yield StreamEvent.ToolCallStart tc1
                yield StreamEvent.ToolCallEnd tc1
                yield StreamEvent.ToolCallStart tc2
                yield StreamEvent.ToolCallEnd tc2
                yield Finish(ToolCalls "tool_calls", Some Usage.Zero, Some response)
            }
        else
            let response =
                { Id = "r2"
                  Model = "m"
                  Provider = "test"
                  Message = Message.Assistant("done")
                  FinishReason = Stop "stop"
                  Usage = Usage.Zero
                  ResponseId = None
                  Raw = None
                  Warnings = []
                  RateLimit = None }

            seq {
                yield StreamStart
                yield TextStart "text-2"
                yield TextDelta(Some "text-2", "done")
                yield TextEnd "text-2"
                yield Finish(Stop "stop", Some Usage.Zero, Some response)
            })

    let client = Client()
    client.RegisterAdapter(mock)

    let seen = System.Collections.Concurrent.ConcurrentBag<string>()

    let toolA: Tool =
        { Definition =
            { Name = "tool_a"
              Description = "tool a"
              Parameters = """{"type":"object","properties":{"x":{"type":"string"}}}""" }
          Execute =
            Some(fun _ ->
                seen.Add("a")
                "result_a") }

    let toolB: Tool =
        { Definition =
            { Name = "tool_b"
              Description = "tool b"
              Parameters = """{"type":"object","properties":{"y":{"type":"string"}}}""" }
          Execute =
            Some(fun _ ->
                seen.Add("b")
                "result_b") }

    let events =
        Generation.streamWithControl
            client
            "m"
            (Some "go")
            None
            None
            (Some [ toolA; toolB ])
            5
            (Some "test")
            None
            None
            None
            None
        |> Seq.toList

    let executed = seen |> Seq.toList
    Assert.Equal(2, executed.Length)
    Assert.Contains("a", executed)
    Assert.Contains("b", executed)
    Assert.Equal(2, streamCalls)

    let stepFinishIndex =
        events
        |> List.findIndex (function
            | StepFinish _ -> true
            | _ -> false)

    let finishIndex =
        events
        |> List.findIndex (function
            | Finish _ -> true
            | _ -> false)

    Assert.True(stepFinishIndex < finishIndex)

    Assert.Contains(
        events,
        fun event ->
            match event with
            | Finish(Stop "stop", _, Some response) when response.Text = "done" -> true
            | _ -> false
    )

[<Fact>]
let ``streaming session synthesizes final response when Finish omits response payload`` () =
    let dir = CodingAgent.Tests.createTempDir ()

    try
        let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
        let mock = ConfigurableMockAdapter("test")

        mock.SetStreamHandler(fun _ ->
            seq {
                yield StreamStart
                yield TextStart "text-1"
                yield TextDelta(Some "text-1", "hello ")
                yield TextDelta(Some "text-1", "world")
                yield TextEnd "text-1"
                yield Finish(Stop "stop", Some Usage.Zero, None)
            })

        let client = Client()
        client.RegisterAdapter(mock)

        let session =
            Session(
                CodingAgent.Tests.TestProfile("m"),
                env,
                client,
                { SessionConfig.Default with
                    EnableStreaming = true }
            )

        session.ProcessInput("stream this")

        Assert.Equal(Idle, session.State)

        match session.History |> List.last with
        | AssistantTurn(content, toolCalls, _, _, _) ->
            Assert.Equal("hello world", content)
            Assert.True(toolCalls.IsEmpty)
        | _ -> Assert.Fail("Expected final assistant turn")
    finally
        CodingAgent.Tests.cleanupDir dir
