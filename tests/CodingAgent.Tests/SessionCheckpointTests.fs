module CodingAgent.SessionCheckpointTests

open System
open System.IO
open Xunit
open UnifiedLlm
open CodingAgent

[<Fact>]
let ``session checkpoint save and restore round trips history`` () =
    let dir = Path.Combine(Path.GetTempPath(), "coding-agent-checkpoint-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let env = LocalExecutionEnvironment(dir) :> IExecutionEnvironment
        let client = Client()
        client.RegisterAdapter(ConfigurableMockAdapter("test"))
        let session = Session(CodingAgent.Tests.TestProfile("m"), env, client)
        session.SetUserInstructions("be careful")
        session.ProcessInput("hello")
        let path = Path.Combine(dir, SessionPersistence.checkpointFileName)
        session.SaveCheckpoint(path)
        let restored = Session.RestoreFromCheckpoint(CodingAgent.Tests.TestProfile("m"), env, client, path)
        Assert.Equal(session.History.Length, restored.History.Length)
        Assert.Equal(session.UserInstructions, restored.UserInstructions)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

// ── New Sprint-010 tests ──

let private persistence = SessionPersistence.fileBacked()

let private defaultCheckpoint () : SessionCheckpointV1 =
    { Version = SessionCheckpointV1.CurrentVersion
      SessionId = "test-session-001"
      ProviderId = "test"
      Model = "gpt-5.4"
      WorkingDirectory = "/tmp/test"
      State = "Idle"
      UserInstructions = Some "be careful"
      AwaitingInputRequested = false
      CurrentDepth = 0
      History = []
      Events = []
      SteeringQueue = []
      FollowupQueue = []
      SubagentMetadata = []
      SavedAt = DateTimeOffset.UtcNow }

[<Fact>]
let ``load with missing file returns Error with not found`` () =
    let path = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".json")
    let result = Async.RunSynchronously(persistence.Load path)
    match result with
    | Result.Error msg -> Assert.Contains("not found", msg)
    | Result.Ok _ -> Assert.Fail("expected error for missing file")

[<Fact>]
let ``load with invalid JSON returns Error`` () =
    let path = Path.Combine(Path.GetTempPath(), "invalid-json-" + Guid.NewGuid().ToString("N") + ".json")
    try
        File.WriteAllText(path, "this is not json {{{")
        let result = Async.RunSynchronously(persistence.Load path)
        Assert.True(Result.isError result)
    finally
        if File.Exists(path) then File.Delete(path)

[<Fact>]
let ``load with wrong version returns Error with version mismatch`` () =
    let path = Path.Combine(Path.GetTempPath(), "wrong-version-" + Guid.NewGuid().ToString("N") + ".json")
    try
        let checkpoint = { defaultCheckpoint() with Version = 999 }
        let bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(checkpoint, SessionPersistence.jsonOptions)
        File.WriteAllBytes(path, bytes)
        let result = Async.RunSynchronously(persistence.Load path)
        match result with
        | Result.Error msg -> Assert.Contains("version mismatch", msg)
        | Result.Ok _ -> Assert.Fail("expected version mismatch error")
    finally
        if File.Exists(path) then File.Delete(path)

[<Fact>]
let ``save creates parent directory if needed`` () =
    let root = Path.Combine(Path.GetTempPath(), "checkpoint-parent-" + Guid.NewGuid().ToString("N"))
    let path = Path.Combine(root, "subdir", "checkpoint.json")
    try
        let checkpoint = defaultCheckpoint()
        let result = Async.RunSynchronously(persistence.Save path checkpoint)
        Assert.True(Result.isOk result)
        Assert.True(File.Exists(path))
    finally
        if Directory.Exists(root) then Directory.Delete(root, true)

[<Fact>]
let ``CheckpointV1 has 15 fields`` () =
    let fields =
        typeof<SessionCheckpointV1>.GetProperties(
            System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Instance)
    // Version, SessionId, ProviderId, Model, WorkingDirectory, State,
    // UserInstructions, AwaitingInputRequested, CurrentDepth, History, Events,
    // SteeringQueue, FollowupQueue, SubagentMetadata, SavedAt
    Assert.Equal(15, fields.Length)

[<Fact>]
let ``round-trip preserves SessionId`` () =
    let dir = Path.Combine(Path.GetTempPath(), "rt-sessionid-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "checkpoint.json")
    try
        let checkpoint = { defaultCheckpoint() with SessionId = "my-unique-session-42" }
        Async.RunSynchronously(persistence.Save path checkpoint) |> ignore
        let loaded = Async.RunSynchronously(persistence.Load path) |> Result.defaultWith failwith
        Assert.Equal("my-unique-session-42", loaded.SessionId)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``round-trip preserves WorkingDirectory`` () =
    let dir = Path.Combine(Path.GetTempPath(), "rt-workdir-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "checkpoint.json")
    try
        let checkpoint = { defaultCheckpoint() with WorkingDirectory = "/some/special/path" }
        Async.RunSynchronously(persistence.Save path checkpoint) |> ignore
        let loaded = Async.RunSynchronously(persistence.Load path) |> Result.defaultWith failwith
        Assert.Equal("/some/special/path", loaded.WorkingDirectory)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``round-trip preserves Model`` () =
    let dir = Path.Combine(Path.GetTempPath(), "rt-model-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "checkpoint.json")
    try
        let checkpoint = { defaultCheckpoint() with Model = "claude-opus-4-6" }
        Async.RunSynchronously(persistence.Save path checkpoint) |> ignore
        let loaded = Async.RunSynchronously(persistence.Load path) |> Result.defaultWith failwith
        Assert.Equal("claude-opus-4-6", loaded.Model)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``round-trip preserves empty steering and followup queues`` () =
    let dir = Path.Combine(Path.GetTempPath(), "rt-empty-queues-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "checkpoint.json")
    try
        let checkpoint = { defaultCheckpoint() with SteeringQueue = []; FollowupQueue = [] }
        Async.RunSynchronously(persistence.Save path checkpoint) |> ignore
        let loaded = Async.RunSynchronously(persistence.Load path) |> Result.defaultWith failwith
        Assert.Empty(loaded.SteeringQueue)
        Assert.Empty(loaded.FollowupQueue)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``round-trip preserves non-empty steering queue`` () =
    let dir = Path.Combine(Path.GetTempPath(), "rt-steering-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "checkpoint.json")
    try
        let checkpoint = { defaultCheckpoint() with SteeringQueue = [ "steer1"; "steer2" ] }
        Async.RunSynchronously(persistence.Save path checkpoint) |> ignore
        let loaded = Async.RunSynchronously(persistence.Load path) |> Result.defaultWith failwith
        Assert.Equal<string list>([ "steer1"; "steer2" ], loaded.SteeringQueue)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``atomic write means file exists after save`` () =
    let dir = Path.Combine(Path.GetTempPath(), "rt-atomic-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, "checkpoint.json")
    try
        let checkpoint = defaultCheckpoint()
        let result = Async.RunSynchronously(persistence.Save path checkpoint)
        Assert.True(Result.isOk result)
        Assert.True(File.Exists(path))
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)
