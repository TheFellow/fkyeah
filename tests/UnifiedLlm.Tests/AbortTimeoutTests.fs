module UnifiedLlm.AbortTimeoutTests

open System
open System.Threading
open Xunit
open UnifiedLlm

// ConfigurableMockAdapter bypasses HTTP cancellation, so these tests cover the
// generation-level abort and total-timeout checks that remain observable with mocks.

[<Fact>]
let ``B4 abort between tool rounds raises AbortError`` () =
    let mock = ConfigurableMockAdapter("test")
    use signal = new AbortSignal()
    let mutable calls = 0

    mock.SetCompleteHandler(fun _ ->
        calls <- calls + 1

        if calls = 1 then
            let tc =
                { Id = "call_1"
                  Name = "test_tool"
                  Arguments = "{}"
                  Metadata = Map.empty }

            signal.Cancel()

            { Id = "r1"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }
        else
            failwith "Should not reach second call")

    let client = Client()
    client.RegisterAdapter(mock)

    let tool: Tool =
        { Definition =
            { Name = "test_tool"
              Description = "test"
              Parameters = """{"type":"object"}""" }
          Execute = Some(fun _ -> "ok") }

    let ex =
        Assert.Throws<AbortError>(fun () ->
            Generation.generateWithControl
                client
                "m"
                (Some "go")
                None
                None
                (Some [ tool ])
                5
                (Some "test")
                None
                None
                None
                (Some signal)
                None
                None
                None
            |> ignore)

    Assert.Contains("aborted", ex.Message.ToLowerInvariant())

[<Fact>]
let ``B4 abort between stream tool rounds raises AbortError`` () =
    let mock = ConfigurableMockAdapter("test")
    use signal = new AbortSignal()
    let mutable streamCalls = 0

    mock.SetStreamHandler(fun _ ->
        streamCalls <- streamCalls + 1

        if streamCalls = 1 then
            let tc =
                { Id = "call_1"
                  Name = "test_tool"
                  Arguments = "{}"
                  Metadata = Map.empty }

            let response =
                { Id = "r1"
                  Model = "m"
                  Provider = "test"
                  Message =
                    { Role = Assistant
                      Content = [ ToolCall tc ]
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
                yield StreamEvent.ToolCallStart tc
                yield StreamEvent.ToolCallEnd tc
                yield Finish(ToolCalls "tool_calls", Some Usage.Zero, Some response)
            }
        else
            failwith "Should not reach second stream call")

    let client = Client()
    client.RegisterAdapter(mock)

    let tool: Tool =
        { Definition =
            { Name = "test_tool"
              Description = "test"
              Parameters = """{"type":"object"}""" }
          Execute =
            Some(fun _ ->
                signal.Cancel()
                "ok") }

    let ex =
        Assert.Throws<AbortError>(fun () ->
            Generation.streamWithControl
                client
                "m"
                (Some "go")
                None
                None
                (Some [ tool ])
                5
                (Some "test")
                None
                None
                None
                (Some signal)
            |> Seq.toList
            |> ignore)

    Assert.Contains("aborted", ex.Message.ToLowerInvariant())

[<Fact>]
let ``B5 total timeout raises RequestTimeoutError when generation exceeds budget`` () =
    let mock = ConfigurableMockAdapter("test")
    let mutable calls = 0

    mock.SetCompleteHandler(fun _ ->
        calls <- calls + 1
        Thread.Sleep(50)

        if calls <= 10 then
            let tc =
                { Id = $"call_{calls}"
                  Name = "test_tool"
                  Arguments = "{}"
                  Metadata = Map.empty }

            { Id = $"r{calls}"
              Model = "m"
              Provider = "test"
              Message =
                { Role = Assistant
                  Content = [ ToolCall tc ]
                  Name = None
                  ToolCallId = None }
              FinishReason = ToolCalls "tool_calls"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }
        else
            { Id = "r_final"
              Model = "m"
              Provider = "test"
              Message = Message.assistant ("done")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None })

    let client = Client()
    client.RegisterAdapter(mock)

    let tool: Tool =
        { Definition =
            { Name = "test_tool"
              Description = "test"
              Parameters = """{"type":"object"}""" }
          Execute = Some(fun _ -> "ok") }

    let ex =
        Assert.Throws<RequestTimeoutError>(fun () ->
            Generation.generateWithControl
                client
                "m"
                (Some "go")
                None
                None
                (Some [ tool ])
                20
                (Some "test")
                None
                None
                None
                None
                None
                (Some { TotalMs = Some 90; PerStepMs = None })
                None
            |> ignore)

    Assert.Contains("timeout", ex.Message.ToLowerInvariant())
