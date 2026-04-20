module UnifiedLlmStreamAccumulatorSprint007Tests

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open UnifiedLlm

module StreamAccumulatorSprint007 =

    type private TestAsyncEnumerable<'T>(source: 'T list) =
        interface IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(_cancellationToken: CancellationToken) =
                let enumerator: IEnumerator<'T> = (source :> seq<'T>).GetEnumerator()

                { new IAsyncEnumerator<'T> with
                    member _.Current = enumerator.Current

                    member _.MoveNextAsync() =
                        ValueTask<bool>(Task.FromResult(enumerator.MoveNext()))

                    member _.DisposeAsync() =
                        enumerator.Dispose()
                        ValueTask() }

    let private consumeAll (source: IAsyncEnumerable<'T>) =
        let enumerator = source.GetAsyncEnumerator(CancellationToken.None)
        let mutable keepReading = true

        while keepReading do
            let hasNext = enumerator.MoveNextAsync().AsTask().Result

            if not hasNext then
                keepReading <- false

        enumerator.DisposeAsync().AsTask().Wait()

    [<Fact>]
    let ``stream accumulator accumulates reasoning independently from text`` () =
        let events =
            [ StreamStart
              ReasoningStart None
              ThinkingEvent "step 1; "
              TextStart "t1"
              TextDelta(Some "t1", "answer ")
              ThinkingEvent "step 2"
              TextDelta(Some "t1", "ready")
              ReasoningEnd None
              Finish(Stop "stop", Some Usage.Zero, None) ]

        let accumulator =
            Generation.StreamAccumulator(
                TestAsyncEnumerable(events) :> IAsyncEnumerable<_>,
                model = "m",
                provider = "test"
            )

        consumeAll accumulator.Events

        let partial = accumulator.PartialResponse()
        Assert.Equal(Some "step 1; step 2", accumulator.ReasoningText)
        Assert.Equal(Some "step 1; step 2", partial.Reasoning)
        Assert.Equal("answer ready", partial.Text)

    [<Fact>]
    let ``stream accumulator marks balanced tool call json as complete`` () =
        let events =
            [ StreamStart
              ToolCallStart
                  { Id = "tool-1"
                    Name = "write_file"
                    Arguments = ""
                    Metadata = Map.empty }
              ToolCallDelta("tool-1", """{"name":""")
              ToolCallDelta("tool-1", """"test","payload":{"ok":true}}""")
              Finish(Stop "tool_calls", Some Usage.Zero, None) ]

        let accumulator =
            Generation.StreamAccumulator(
                TestAsyncEnumerable(events) :> IAsyncEnumerable<_>,
                model = "m",
                provider = "test"
            )

        consumeAll accumulator.Events

        let toolCall = accumulator.PartialResponse().ToolCalls |> List.head
        Assert.Equal("""{"name":"test","payload":{"ok":true}}""", toolCall.Arguments)
        Assert.Equal(Some "true", toolCall.Metadata |> Map.tryFind "json_complete")

    [<Fact>]
    let ``stream accumulator handles orphan tool call deltas and preserves metadata on end`` () =
        let completed =
            { Id = "tool-2"
              Name = "run"
              Arguments = """{"code":"if (x) {"}"""
              Metadata = Map.ofList [ "provider", "test" ] }

        let events =
            [ StreamStart
              ToolCallDelta("tool-2", "{\"code\":\"if (x) {\"}")
              ToolCallEnd completed
              Finish(Stop "tool_calls", Some Usage.Zero, None) ]

        let accumulator =
            Generation.StreamAccumulator(
                TestAsyncEnumerable(events) :> IAsyncEnumerable<_>,
                model = "m",
                provider = "test"
            )

        consumeAll accumulator.Events

        let toolCall = accumulator.PartialResponse().ToolCalls |> List.head
        Assert.Equal("run", toolCall.Name)
        Assert.Equal("""{"code":"if (x) {"}""", toolCall.Arguments)
        Assert.Equal(Some "true", toolCall.Metadata |> Map.tryFind "orphan")
        Assert.Equal(Some "test", toolCall.Metadata |> Map.tryFind "provider")

    [<Fact>]
    let ``stream accumulator returns no reasoning when stream omits thinking events`` () =
        let events =
            [ StreamStart
              TextStart "t1"
              TextDelta(Some "t1", "hello")
              Finish(Stop "stop", Some Usage.Zero, None) ]

        let accumulator =
            Generation.StreamAccumulator(
                TestAsyncEnumerable(events) :> IAsyncEnumerable<_>,
                model = "m",
                provider = "test"
            )

        consumeAll accumulator.Events

        Assert.Equal(None, accumulator.ReasoningText)
        Assert.Equal(None, accumulator.PartialResponse().Reasoning)
