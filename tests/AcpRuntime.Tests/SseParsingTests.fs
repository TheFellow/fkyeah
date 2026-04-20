module AcpRuntime.SseParsingTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Pipelines
open System.Text
open System.Threading
open System.Threading.Tasks
open AcpRuntime
open Xunit

type private TestPipe() =
    let pipe = Pipe()
    member _.ReadStream() = pipe.Reader.AsStream()
    member _.WriteText(text: string) =
        let bytes = Encoding.UTF8.GetBytes(text: string)
        let vt = pipe.Writer.WriteAsync(ReadOnlyMemory(bytes))
        vt.AsTask().GetAwaiter().GetResult() |> ignore
    member _.Complete() = pipe.Writer.Complete()

let private drainAsync (source: IAsyncEnumerable<ParsedSseEvent>) =
    task {
        let collected = ResizeArray<ParsedSseEvent>()
        let enumerator = source.GetAsyncEnumerator(CancellationToken.None)
        try
            let mutable moveNext = true
            while moveNext do
                let! hasNext = enumerator.MoveNextAsync().AsTask()
                if hasNext then collected.Add(enumerator.Current)
                else moveNext <- false
            return collected :> IReadOnlyList<_>
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

[<Fact>]
let ``parseSseStream raises TimeoutException on complete silence`` () =
    task {
        let tp = TestPipe()
        let events = Transport.parseSseStreamWithIdleTimeout (tp.ReadStream()) CancellationToken.None 300
        let sw = Stopwatch.StartNew()
        let! ex =
            Assert.ThrowsAsync<TimeoutException>(fun () ->
                (drainAsync events) :> Task)
        Assert.Contains("idle", ex.Message.ToLowerInvariant())
        Assert.InRange(sw.ElapsedMilliseconds, 200L, 3000L)
    } :> Task

[<Fact>]
let ``parseSseStream ping events do not refresh idle timer`` () =
    task {
        let tp = TestPipe()
        use stopPings = new CancellationTokenSource()
        let pinger = Task.Run(fun () ->
            while not stopPings.IsCancellationRequested do
                try tp.WriteText("event: ping\ndata: {}\n\n") with _ -> ()
                Thread.Sleep(50))

        try
            let events = Transport.parseSseStreamWithIdleTimeout (tp.ReadStream()) CancellationToken.None 300
            let sw = Stopwatch.StartNew()
            let! ex =
                Assert.ThrowsAsync<TimeoutException>(fun () ->
                    (drainAsync events) :> Task)
            Assert.Contains("idle", ex.Message.ToLowerInvariant())
            Assert.InRange(sw.ElapsedMilliseconds, 200L, 3000L)
        finally
            stopPings.Cancel()
            try tp.Complete() with _ -> ()
            pinger.Wait(TimeSpan.FromSeconds(2.0)) |> ignore
    } :> Task

[<Fact>]
let ``parseSseStream SSE comment lines do not refresh idle timer`` () =
    task {
        let tp = TestPipe()
        use stopPings = new CancellationTokenSource()
        let pinger = Task.Run(fun () ->
            while not stopPings.IsCancellationRequested do
                try tp.WriteText(":keepalive\n") with _ -> ()
                Thread.Sleep(50))

        try
            let events = Transport.parseSseStreamWithIdleTimeout (tp.ReadStream()) CancellationToken.None 300
            let sw = Stopwatch.StartNew()
            let! ex =
                Assert.ThrowsAsync<TimeoutException>(fun () ->
                    (drainAsync events) :> Task)
            Assert.Contains("idle", ex.Message.ToLowerInvariant())
            Assert.InRange(sw.ElapsedMilliseconds, 200L, 3000L)
        finally
            stopPings.Cancel()
            try tp.Complete() with _ -> ()
            pinger.Wait(TimeSpan.FromSeconds(2.0)) |> ignore
    } :> Task

[<Fact>]
let ``parseSseStream real events refresh idle timer and stream completes`` () =
    task {
        let tp = TestPipe()
        let producer = Task.Run(fun () ->
            for i in 1..3 do
                tp.WriteText(sprintf "event: message\ndata: e%d\n\n" i)
                Thread.Sleep(100)
            tp.Complete())

        let events = Transport.parseSseStreamWithIdleTimeout (tp.ReadStream()) CancellationToken.None 5000
        let! collected = drainAsync events
        producer.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        Assert.Equal(3, collected.Count)
        Assert.All(collected, (fun e -> Assert.Equal("message", e.EventType)))
    } :> Task

[<Fact>]
let ``parseSseStream outer cancellation exits cleanly without exception`` () =
    task {
        let tp = TestPipe()
        use outer = new CancellationTokenSource()
        let _trigger = Task.Run(fun () ->
            Thread.Sleep(150)
            outer.Cancel())

        let events = Transport.parseSseStreamWithIdleTimeout (tp.ReadStream()) outer.Token 5000
        let! collected = drainAsync events
        Assert.Empty(collected)
    } :> Task
