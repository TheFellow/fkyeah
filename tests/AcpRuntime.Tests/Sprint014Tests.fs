module AcpRuntimeSprint014Tests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open AcpRuntime
open AcpRuntimeTests
open Attractor
open JsonRpc
open MockAcpAgent
open Xunit

module PromptMetadataTests =

    [<Fact>]
    let ``client prompt includes mcpServersJson metadata when provided`` () =
        let clientTransport, serverTransport = Transport.createInMemoryPair()
        let seenMetadata = TaskCompletionSource<string option>()

        Helpers.startServer serverTransport (fun transport request ->
            async {
                match request.Method with
                | "initialize" ->
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| protocolVersion = "2026-03-23"
                               capabilities = {| prompt = true |}
                               serverInfo = {| name = "metadata-server" |} |}
                        |> fun value -> value.Clone()
                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | "session/prompt" ->
                    let metadata =
                        request.Params
                        |> Option.bind (fun parameters ->
                            let mutable metadataElement = Unchecked.defaultof<JsonElement>
                            if parameters.TryGetProperty("metadata", &metadataElement) then
                                let mutable value = Unchecked.defaultof<JsonElement>
                                if metadataElement.TryGetProperty("mcpServersJson", &value) then
                                    Some(value.GetString())
                                else
                                    None
                            else
                                None)
                    seenMetadata.TrySetResult(metadata) |> ignore
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| sessionId = "metadata-session"
                               content = [ {| ``type`` = "text"; text = "ok" |} ]
                               stopReason = "completed" |}
                        |> fun value -> value.Clone()
                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | _ -> ()
            })

        let client = Client.create (fun _ -> Ok clientTransport)
        let endpoint =
            { Transport = AcpTransportKind.InMemory
              Command = None
              Args = []
              Url = None
              Headers = Map.empty
              WorkingDirectory = None }

        client.Connect(endpoint, AcpDelegate.denyAll, Some(TimeSpan.FromSeconds(1.0))) |> Async.RunSynchronously |> ignore
        let metadata = { PromptMetadata.McpServersJson = Some """{"servers":{"demo":{"command":"echo"}}}""" }
        client.Prompt("metadata-session", [ ContentBlock.text "hello" ], Some metadata, Some(TimeSpan.FromSeconds(1.0)))
        |> Async.RunSynchronously
        |> ignore

        Assert.Equal(Some """{"servers":{"demo":{"command":"echo"}}}""", seenMetadata.Task.Result)
        client.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``client prompt omits metadata when not provided`` () =
        let clientTransport, serverTransport = Transport.createInMemoryPair()
        let sawMetadata = TaskCompletionSource<bool>()

        Helpers.startServer serverTransport (fun transport request ->
            async {
                match request.Method with
                | "initialize" ->
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| protocolVersion = "2026-03-23"
                               capabilities = {| prompt = true |}
                               serverInfo = {| name = "metadata-server" |} |}
                        |> fun value -> value.Clone()
                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | "session/prompt" ->
                    let hasMetadata =
                        request.Params
                        |> Option.map (fun parameters ->
                            let mutable metadataElement = Unchecked.defaultof<JsonElement>
                            parameters.TryGetProperty("metadata", &metadataElement))
                        |> Option.defaultValue false
                    sawMetadata.TrySetResult(hasMetadata) |> ignore
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| sessionId = "no-metadata-session"
                               content = [ {| ``type`` = "text"; text = "ok" |} ]
                               stopReason = "completed" |}
                        |> fun value -> value.Clone()
                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | _ -> ()
            })

        let client = Client.create (fun _ -> Ok clientTransport)
        let endpoint =
            { Transport = AcpTransportKind.InMemory
              Command = None
              Args = []
              Url = None
              Headers = Map.empty
              WorkingDirectory = None }

        client.Connect(endpoint, AcpDelegate.denyAll, Some(TimeSpan.FromSeconds(1.0))) |> Async.RunSynchronously |> ignore
        client.Prompt("no-metadata-session", [ ContentBlock.text "hello" ], None, Some(TimeSpan.FromSeconds(1.0)))
        |> Async.RunSynchronously
        |> ignore

        Assert.False(sawMetadata.Task.Result)
        client.Disconnect() |> Async.RunSynchronously

module AcpHandlerMetadataTests =

    let private withEnvValue (key: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(key)
        try
            match value with
            | Some raw -> Environment.SetEnvironmentVariable(key, raw)
            | None -> Environment.SetEnvironmentVariable(key, null)
            action()
        finally
            Environment.SetEnvironmentVariable(key, original)

    [<Fact>]
    let ``acp handler passes context mcp servers json into session metadata`` () =
        let handler = AcpHandlers.AcpAgentHandler(permissionStrategy = PermissionStrategy.DenyAll) :> IHandler
        let node =
            Helpers.makeAcpNode
                [ "shape", "tab"
                  "type", "acp.agent"
                  "acp_transport", "stdio"
                  "acp_command", "dotnet"
                  "acp_args_json", JsonSerializer.Serialize([ Helpers.fixtureDll () ])
                  "prompt", "Use available MCP servers." ]
        let context = Context()
        context.Set("_current_node_acp_mcp_servers_json", """{"servers":{"demo":{"command":"echo"}}}""")
        let outcome = handler.Execute(node, context, Helpers.makeGraph (), Helpers.createTempDir ())

        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.Contains("[mcp servers provided]", outcome.ContextUpdates["acp.output.agent"])

    [<Fact>]
    let ``invalid ATTRACTOR_ACP_MCP_SERVERS is ignored without crashing`` () =
        withEnvValue "ATTRACTOR_ACP_MCP_SERVERS" (Some "{not-json") (fun () ->
            let handler = AcpHandlers.AcpAgentHandler(permissionStrategy = PermissionStrategy.DenyAll) :> IHandler
            let node =
                Helpers.makeAcpNode
                    [ "shape", "tab"
                      "type", "acp.agent"
                      "acp_transport", "stdio"
                      "acp_command", "dotnet"
                      "acp_args_json", JsonSerializer.Serialize([ Helpers.fixtureDll () ])
                      "prompt", "Ignore invalid MCP env." ]

            let outcome = handler.Execute(node, Context(), Helpers.makeGraph (), Helpers.createTempDir ())

            Assert.Equal(StageStatus.Success, outcome.Status)
            Assert.DoesNotContain("[mcp servers provided]", outcome.ContextUpdates["acp.output.agent"]))
