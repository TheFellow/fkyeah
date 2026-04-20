module UnifiedLlm.SseParsingTests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipelines
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open UnifiedLlm

// A duplex pipe helper: the test writes raw SSE bytes into one end and parseSse reads from the other.
// The read-side stream blocks on empty until more is written or the writer is completed.
type private TestPipe() =
    let pipe = Pipe()
    member _.ReadStream() = pipe.Reader.AsStream()
    member _.WriteText(text: string) =
        let bytes = Encoding.UTF8.GetBytes(text: string)
        let vt = pipe.Writer.WriteAsync(ReadOnlyMemory(bytes))
        vt.AsTask().GetAwaiter().GetResult() |> ignore
    member _.Complete() = pipe.Writer.Complete()

[<Fact>]
let ``parseSse raises TimeoutError on complete silence`` () =
    let tp = TestPipe()
    use reader = new StreamReader(tp.ReadStream(), Encoding.UTF8)
    let events = SseParsing.parseWithIdleTimeout reader CancellationToken.None 300
    let sw = Stopwatch.StartNew()
    let ex = Assert.Throws<TimeoutError>(fun () -> events |> Seq.iter (fun _ -> ()))
    Assert.Contains("idle", ex.Message.ToLowerInvariant())
    Assert.InRange(sw.ElapsedMilliseconds, 200L, 3000L)

[<Fact>]
let ``parseSse ping events do not refresh idle timer`` () =
    let tp = TestPipe()
    use reader = new StreamReader(tp.ReadStream(), Encoding.UTF8)

    use stopPings = new CancellationTokenSource()
    let pinger = Task.Run(fun () ->
        while not stopPings.IsCancellationRequested do
            try tp.WriteText("event: ping\ndata: {}\n\n") with _ -> ()
            Thread.Sleep(50))

    try
        let events = SseParsing.parseWithIdleTimeout reader CancellationToken.None 300
        let sw = Stopwatch.StartNew()
        let ex = Assert.Throws<TimeoutError>(fun () -> events |> Seq.iter (fun _ -> ()))
        Assert.Contains("idle", ex.Message.ToLowerInvariant())
        // Must fire within a small multiple of the budget despite constant ping traffic.
        Assert.InRange(sw.ElapsedMilliseconds, 200L, 3000L)
    finally
        stopPings.Cancel()
        try tp.Complete() with _ -> ()
        pinger.Wait(TimeSpan.FromSeconds(2.0)) |> ignore

[<Fact>]
let ``parseSse SSE comment lines do not refresh idle timer`` () =
    let tp = TestPipe()
    use reader = new StreamReader(tp.ReadStream(), Encoding.UTF8)

    use stopPings = new CancellationTokenSource()
    let pinger = Task.Run(fun () ->
        while not stopPings.IsCancellationRequested do
            try tp.WriteText(":keepalive\n") with _ -> ()
            Thread.Sleep(50))

    try
        let events = SseParsing.parseWithIdleTimeout reader CancellationToken.None 300
        let sw = Stopwatch.StartNew()
        let ex = Assert.Throws<TimeoutError>(fun () -> events |> Seq.iter (fun _ -> ()))
        Assert.Contains("idle", ex.Message.ToLowerInvariant())
        Assert.InRange(sw.ElapsedMilliseconds, 200L, 3000L)
    finally
        stopPings.Cancel()
        try tp.Complete() with _ -> ()
        pinger.Wait(TimeSpan.FromSeconds(2.0)) |> ignore

[<Fact>]
let ``parseSse real events refresh idle timer and stream completes`` () =
    let tp = TestPipe()
    use reader = new StreamReader(tp.ReadStream(), Encoding.UTF8)

    let producer = Task.Run(fun () ->
        for i in 1..3 do
            tp.WriteText(sprintf "event: message\ndata: e%d\n\n" i)
            Thread.Sleep(100)
        tp.Complete())

    let events = SseParsing.parseWithIdleTimeout reader CancellationToken.None 5000
    let collected = events |> Seq.toList
    producer.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
    Assert.Equal(3, collected.Length)
    Assert.All(collected, (fun (name, _) -> Assert.Equal("message", name)))

[<Fact>]
let ``parseSse outer cancellation raises AbortError`` () =
    let tp = TestPipe()
    use reader = new StreamReader(tp.ReadStream(), Encoding.UTF8)

    use outer = new CancellationTokenSource()
    let _trigger = Task.Run(fun () ->
        Thread.Sleep(150)
        outer.Cancel())

    let events = SseParsing.parseWithIdleTimeout reader outer.Token 5000
    Assert.Throws<AbortError>(fun () -> events |> Seq.iter (fun _ -> ())) |> ignore

[<Fact>]
let ``isHeartbeatEvent classifies known heartbeat names`` () =
    Assert.True(SseParsing.isHeartbeatEvent "ping")
    Assert.True(SseParsing.isHeartbeatEvent "keepalive")
    Assert.True(SseParsing.isHeartbeatEvent "heartbeat")
    Assert.False(SseParsing.isHeartbeatEvent "message")
    Assert.False(SseParsing.isHeartbeatEvent "response.output_text.delta")
