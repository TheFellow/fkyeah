module UnifiedLlm.CachingTests

open System
open System.IO
open Xunit
open UnifiedLlm

[<Fact>]
let ``cache key is deterministic for identical request`` () =
    let request = Request.Create("gpt-5.4", [ Message.User("hello") ])
    Assert.Equal(CacheKey.fromRequest request, CacheKey.fromRequest request)

[<Fact>]
let ``cache store persists llm responses to filesystem`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fkyeah-cache-" + Guid.NewGuid().ToString("N"))

    try
        let store =
            CacheStore.fileSystem
                { CacheConfig.Default with
                    PersistencePath = Some root }

        let key =
            CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("hello") ]))

        let response =
            { Id = "r1"
              Model = "gpt-5.4"
              Provider = "openai"
              Message = Message.Assistant("cached")
              FinishReason = Stop "stop"
              Usage = Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }

        Async.RunSynchronously(
            store.PutLlm
                key
                { Response = response
                  StoredAt = DateTimeOffset.UtcNow
                  Metadata = Map.empty }
        )

        let store2 =
            CacheStore.fileSystem
                { CacheConfig.Default with
                    PersistencePath = Some root }

        let loaded = Async.RunSynchronously(store2.TryGetLlm key)
        Assert.True(loaded.IsSome)
        Assert.Equal("cached", loaded.Value.Response.Text)
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, true)

[<Fact>]
let ``cached response replays streaming finish event`` () =
    let response =
        { Id = "r1"
          Model = "gpt-5.4"
          Provider = "openai"
          Message = Message.Assistant("hello")
          FinishReason = Stop "stop"
          Usage = Usage.Zero
          ResponseId = None
          Raw = None
          Warnings = []
          RateLimit = None }

    let events = Caching.replayStreamFromCachedResponse response |> Seq.toList
    Assert.Contains(StreamStart, events)

    Assert.True(
        events
        |> List.exists (function
            | Finish _ -> true
            | _ -> false)
    )

// ── New Sprint-010 tests ──

let private makeTestResponse (text: string) =
    { Id = "r-test"
      Model = "gpt-5.4"
      Provider = "openai"
      Message = Message.Assistant(text)
      FinishReason = Stop "stop"
      Usage = Usage.Zero
      ResponseId = None
      Raw = None
      Warnings = []
      RateLimit = None }

let private makeTestEntry (text: string) =
    { Response = makeTestResponse text
      StoredAt = DateTimeOffset.UtcNow
      Metadata = Map.empty }

[<Fact>]
let ``cache key differs when model changes`` () =
    let r1 = Request.Create("gpt-5.4", [ Message.User("hello") ])
    let r2 = Request.Create("claude-opus-4-6", [ Message.User("hello") ])
    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache key differs when temperature changes`` () =
    let r1 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            Temperature = Some 0.5 }

    let r2 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            Temperature = Some 1.0 }

    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache key differs when tools change`` () =
    let tool =
        { Name = "my_tool"
          Description = "desc"
          Parameters = "{}" }

    let r1 = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let r2 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            Tools = Some [ tool ] }

    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache key differs when tool_choice changes`` () =
    let r1 = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let r2 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            ToolChoice = Some ToolChoice.Auto }

    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache key differs when reasoning_effort changes`` () =
    let r1 = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let r2 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            ReasoningEffort = Some "high" }

    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache miss on fresh store returns None`` () =
    let store = CacheStore.fileSystem CacheConfig.Default

    let key =
        CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("never-seen") ]))

    let result = Async.RunSynchronously(store.TryGetLlm key)
    Assert.True(result.IsNone)

[<Fact>]
let ``cache hit within TTL returns Some entry`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fkyeah-cache-hit-" + Guid.NewGuid().ToString("N"))

    try
        let store =
            CacheStore.fileSystem
                { CacheConfig.Default with
                    PersistencePath = Some root }

        let key =
            CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("hit-test") ]))

        Async.RunSynchronously(store.PutLlm key (makeTestEntry "hit"))
        let result = Async.RunSynchronously(store.TryGetLlm key)
        Assert.True(result.IsSome)
        Assert.Equal("hit", result.Value.Response.Text)
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, true)

[<Fact>]
let ``cache eviction at MaxEntries keeps count at limit`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fkyeah-cache-evict-" + Guid.NewGuid().ToString("N"))

    try
        let maxEntries = 3

        let store =
            CacheStore.fileSystem
                { CacheConfig.Default with
                    MaxEntries = maxEntries
                    PersistencePath = Some root }

        for i in 0..maxEntries do
            let key =
                CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User($"evict-{i}") ]))

            Async.RunSynchronously(store.PutLlm key (makeTestEntry $"entry-{i}"))

        Assert.True(store.Count() <= maxEntries)
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, true)

[<Fact>]
let ``streaming replay produces StreamStart event`` () =
    let response = makeTestResponse "replay-start"
    let events = Caching.replayStreamFromCachedResponse response |> Seq.toList
    Assert.Equal(StreamStart, events.Head)

[<Fact>]
let ``streaming replay produces Finish event with usage`` () =
    let response =
        { makeTestResponse "replay-finish" with
            Usage = { Usage.Zero with InputTokens = 42 } }

    let events = Caching.replayStreamFromCachedResponse response |> Seq.toList

    let finishEvent =
        events
        |> List.tryFind (function
            | Finish(_, Some u, _) when u.InputTokens = 42 -> true
            | _ -> false)

    Assert.True(finishEvent.IsSome, "expected Finish event with usage")

[<Fact>]
let ``streaming replay preserves tool calls from response`` () =
    let tc =
        { Id = "tc1"
          Name = "my_func"
          Arguments = "{}"
          Metadata = Map.empty }

    let response =
        { Id = "r-tc"
          Model = "gpt-5.4"
          Provider = "openai"
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

    let events = Caching.replayStreamFromCachedResponse response |> Seq.toList

    Assert.True(
        events
        |> List.exists (function
            | ToolCallStart t when t.Name = "my_func" -> true
            | _ -> false),
        "expected ToolCallStart event"
    )

[<Fact>]
let ``tool cache put and get round trips`` () =
    let store = CacheStore.fileSystem CacheConfig.Default
    let key = CacheKey.fromToolCall "my_tool" """{"arg":"val"}""" "/tmp"

    let toolResult: ToolResultData =
        { ToolCallId = "tc-1"
          Content = "tool output"
          IsError = false
          ImageData = None
          ImageMediaType = None }

    Async.RunSynchronously(store.PutTool key toolResult)
    let result = Async.RunSynchronously(store.TryGetTool key)
    Assert.True(result.IsSome)
    Assert.Equal("tool output", result.Value.Content)

[<Fact>]
let ``cache Clear removes all entries`` () =
    let store = CacheStore.fileSystem CacheConfig.Default

    let key =
        CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("clear-test") ]))

    Async.RunSynchronously(store.PutLlm key (makeTestEntry "to-clear"))
    Assert.True(store.Count() > 0)
    store.Clear()
    Assert.Equal(0, store.Count())

[<Fact>]
let ``concurrent read/write does not throw exceptions`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fkyeah-cache-concurrent-" + Guid.NewGuid().ToString("N"))

    try
        let store =
            CacheStore.fileSystem
                { CacheConfig.Default with
                    PersistencePath = Some root }

        let key =
            CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("concurrent") ]))

        Async.RunSynchronously(store.PutLlm key (makeTestEntry "seed"))

        let tasks =
            [| for i in 0..49 ->
                   System.Threading.Tasks.Task.Run(fun () -> Async.RunSynchronously(store.TryGetLlm key) |> ignore)
               for i in 0..49 ->
                   System.Threading.Tasks.Task.Run(fun () ->
                       let k =
                           CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User($"concurrent-w-{i}") ]))

                       Async.RunSynchronously(store.PutLlm k (makeTestEntry $"w-{i}"))) |]

        System.Threading.Tasks.Task.WaitAll(tasks)
        // If we got here without exceptions, the test passes
        Assert.True(true)
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, true)

[<Fact>]
let ``CacheConfig Disabled has zero MaxEntries and zero TTL`` () =
    Assert.Equal(0, CacheConfig.Disabled.MaxEntries)
    Assert.Equal(TimeSpan.Zero, CacheConfig.Disabled.TimeToLive)

[<Fact>]
let ``cache key for tool call is deterministic`` () =
    let key1 = CacheKey.fromToolCall "my_tool" """{"x":1}""" "/tmp"
    let key2 = CacheKey.fromToolCall "my_tool" """{"x":1}""" "/tmp"
    Assert.Equal(key1, key2)

[<Fact>]
let ``cache key differs when response_format changes`` () =
    let r1 = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let r2 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            ResponseFormat = Some ResponseFormat.JsonObject }

    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache key differs when previous_response_id changes`` () =
    let r1 = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let r2 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            PreviousResponseId = Some "resp-123" }

    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache key differs when stop_sequences change`` () =
    let r1 = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let r2 =
        { Request.Create("gpt-5.4", [ Message.User("hello") ]) with
            StopSequences = Some [ "STOP" ] }

    Assert.NotEqual(CacheKey.fromRequest r1, CacheKey.fromRequest r2)

[<Fact>]
let ``cache key includes metadata and provider options`` () =
    let baseline = Request.Create("gpt-5.4", [ Message.User("hello") ])

    let withMetadata =
        { baseline with
            Metadata = Some(Map.ofList [ "tenant", "one" ]) }

    let withOptions =
        { baseline with
            ProviderOptions =
                Some(
                    Map.ofList
                        [ "openrouter", box (Map.ofList [ "provider", box (Map.ofList [ "sort", box "throughput" ]) ]) ]
                ) }

    Assert.NotEqual(CacheKey.fromRequest baseline, CacheKey.fromRequest withMetadata)
    Assert.NotEqual(CacheKey.fromRequest baseline, CacheKey.fromRequest withOptions)

[<Fact>]
let ``cache key includes binary content bytes`` () =
    let request data =
        Request.Create(
            "gpt-5.4",
            [ { Role = User
                Content =
                  [ Image
                        { Url = None
                          Data = Some data
                          FilePath = None
                          MediaType = Some "image/png" } ]
                Name = None
                ToolCallId = None } ]
        )

    Assert.NotEqual(CacheKey.fromRequest (request [| 1uy |]), CacheKey.fromRequest (request [| 2uy |]))

[<Fact>]
let ``filesystem cache preserves binary response content`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fkyeah-cache-binary-" + Guid.NewGuid().ToString("N"))

    try
        let config =
            { CacheConfig.Default with
                PersistencePath = Some root }

        let store = CacheStore.fileSystem config

        let key =
            CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("binary") ]))

        let response =
            { makeTestResponse "" with
                Message =
                    { Role = Assistant
                      Content =
                        [ Audio
                              { Data = Some [| 1uy; 2uy |]
                                Url = None
                                MediaType = Some "audio/wav" }
                          ToolResult
                              { ToolCallId = "call"
                                Content = "image"
                                IsError = false
                                ImageData = Some [| 3uy; 4uy |]
                                ImageMediaType = Some "image/png" } ]
                      Name = None
                      ToolCallId = None } }

        Async.RunSynchronously(store.PutLlm key (makeTestEntry "" |> fun entry -> { entry with Response = response }))

        let loaded =
            CacheStore.fileSystem config
            |> fun reloaded -> Async.RunSynchronously(reloaded.TryGetLlm key)
            |> Option.get

        Assert.Contains(
            loaded.Response.Message.Content,
            function
            | Audio audio -> audio.Data = Some [| 1uy; 2uy |]
            | _ -> false
        )

        Assert.Contains(
            loaded.Response.Message.Content,
            function
            | ToolResult result -> result.ImageData = Some [| 3uy; 4uy |]
            | _ -> false
        )
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, true)

[<Fact>]
let ``cache store drops corrupted persisted entry and returns miss`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fkyeah-cache-corrupt-" + Guid.NewGuid().ToString("N"))

    try
        let store =
            CacheStore.fileSystem
                { CacheConfig.Default with
                    PersistencePath = Some root }

        let key =
            CacheKey.fromRequest (Request.Create("gpt-5.4", [ Message.User("corrupt-me") ]))

        let path = Path.Combine(root, "llm", CacheKey.value key + ".json")
        Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
        File.WriteAllText(path, """{"broken":true""")

        let result = Async.RunSynchronously(store.TryGetLlm key)

        Assert.True(result.IsNone)
        Assert.False(File.Exists(path))
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, true)
