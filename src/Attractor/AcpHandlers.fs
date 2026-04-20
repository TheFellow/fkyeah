namespace Attractor

open System
open System.IO
open System.Text
open System.Text.Json
open AcpRuntime
open JsonRpc

module AcpHandlers =

    let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

    let private resolveVisitCount (context: Context) =
        context.TryGet("node.visit_count")
        |> Option.bind (fun raw ->
            match Int32.TryParse(raw) with
            | true, value -> Some value
            | _ -> None)
        |> Option.defaultValue 1

    let private resolveStageDirs (logsRoot: string) (node: Node) (context: Context) =
        let rootDir = Path.Combine(logsRoot, node.Id)
        let visit = resolveVisitCount context
        Path.Combine(rootDir, sprintf "%03d" visit), rootDir

    let private ensureDir (path: string) =
        if not (Directory.Exists(path)) then
            Directory.CreateDirectory(path) |> ignore

    let private writeStageFile (stageDir: string) (rootDir: string) (fileName: string) (content: string) =
        ensureDir stageDir
        File.WriteAllText(Path.Combine(stageDir, fileName), content)

        if stageDir <> rootDir then
            ensureDir rootDir
            File.WriteAllText(Path.Combine(rootDir, fileName), content)

    let private truncate (maxChars: int) (text: string) =
        if String.IsNullOrEmpty(text) then
            ""
        elif text.Length > maxChars then
            text.Substring(0, maxChars) + "\n[truncated]"
        else
            text

    let private buildPrompt (node: Node) (context: Context) (graph: Graph) =
        let promptBase = if node.Prompt <> "" then node.Prompt else node.Label
        let prompt = promptBase.Replace("$goal", graph.Goal)
        let snapshot = context.Snapshot()
        let sb = StringBuilder()

        if graph.Goal <> "" then
            sb.AppendLine("## Pipeline Goal") |> ignore
            sb.AppendLine(graph.Goal) |> ignore
            sb.AppendLine() |> ignore

        let appendContext label key =
            match snapshot |> Map.tryFind key with
            | Some value when value <> "" ->
                sb.AppendLine($"## {label}") |> ignore
                sb.AppendLine(truncate 20000 value) |> ignore
                sb.AppendLine() |> ignore
            | _ -> ()

        appendContext "Previous Stage Response" "last_response"
        appendContext "Previous Tool Output" "tool.output"
        appendContext "Previous Tool Stderr" "tool.stderr"
        appendContext "Human Input" "human.gate.input"

        sb.AppendLine("## Task") |> ignore
        sb.AppendLine(prompt) |> ignore
        sb.ToString()

    let private resolveWorkingDir (node: Node) (graph: Graph) =
        let nodeCwd = node.GetAttrString("cwd", "")
        let graphCwd = graph.GetGraphAttrString("cwd", "")

        let configuredWorkingDir =
            if nodeCwd <> "" then nodeCwd
            elif graphCwd <> "" then graphCwd
            else Environment.CurrentDirectory

        if Path.IsPathRooted(configuredWorkingDir) then
            configuredWorkingDir
        else
            Path.GetFullPath(configuredWorkingDir)

    let private serialize value =
        JsonSerializer.Serialize(value, jsonOptions)

    let private parseArgsJson (raw: string) =
        if String.IsNullOrWhiteSpace(raw) then
            Ok []
        else
            try
                use document = JsonDocument.Parse(raw)

                if document.RootElement.ValueKind <> JsonValueKind.Array then
                    Error "acp_args_json must be a JSON array"
                else
                    let args =
                        document.RootElement.EnumerateArray()
                        |> Seq.map (fun item ->
                            if item.ValueKind = JsonValueKind.String then
                                Ok(item.GetString())
                            else
                                Error "acp_args_json entries must be strings")
                        |> Seq.fold
                            (fun state next ->
                                match state, next with
                                | Ok acc, Ok value -> Ok(value :: acc)
                                | Error error, _
                                | _, Error error -> Error error)
                            (Ok [])

                    args |> Result.map List.rev
            with ex ->
                Error ex.Message

    let private resolveMcpServersJson (context: Context) : string option =
        match context.TryGet("_current_node_acp_mcp_servers_json") with
        | Some json when json <> "" -> Some json
        | _ ->
            match Environment.GetEnvironmentVariable("ATTRACTOR_ACP_MCP_SERVERS") with
            | null
            | "" -> None
            | json ->
                try
                    JsonDocument.Parse(json) |> ignore
                    Some json
                with _ ->
                    None

    let private renderPromptResult (result: PromptResult) =
        result.Content
        |> List.map (function
            | ContentBlock.Text text -> text
            | ContentBlock.Image uri -> $"[image] {uri}")
        |> String.concat "\n\n"

    let private truncateForContext (text: string) =
        if text.Length > 10000 then
            text.Substring(0, 10000)
            + "\n[response truncated for context — full text in logs]"
        else
            text

    let private writeSessionArtifact stageDir rootDir artifact =
        writeStageFile stageDir rootDir "acp_session.json" (serialize artifact)

    let private buildEndpoint (node: Node) (workingDir: string) =
        let rawPreset = node.GetAttrString("acp_preset", "").Trim()

        let preset =
            if rawPreset = "" then
                Ok None
            else
                match AcpPresets.PresetKind.Parse(rawPreset) with
                | Some kind -> Ok(Some(AcpPresets.resolve kind workingDir))
                | None -> Error $"Unsupported ACP preset '{rawPreset}'"

        match preset with
        | Error error -> Error error
        | Ok presetConfig ->
            let presetEndpoint = presetConfig |> Option.map AcpPresets.toEndpoint

            let transportDefault =
                presetEndpoint
                |> Option.map _.Transport
                |> Option.defaultValue AcpTransportKind.Stdio

            let rawTransport = node.GetAttrString("acp_transport", transportDefault.ToString())

            match AcpTransportKind.Parse(rawTransport) with
            | None -> Error $"Unsupported ACP transport '{rawTransport}'"
            | Some transport ->
                let hasExplicitArgs = node.GetAttr("acp_args_json").IsSome

                match parseArgsJson (node.GetAttrString("acp_args_json", "")) with
                | Error error -> Error error
                | Ok explicitArgs ->
                    let args =
                        if not hasExplicitArgs && explicitArgs.IsEmpty then
                            presetEndpoint |> Option.map _.Args |> Option.defaultValue []
                        else
                            explicitArgs

                    let command =
                        let value = node.GetAttrString("acp_command", "").Trim()

                        if value <> "" then
                            Some value
                        else
                            presetEndpoint |> Option.bind _.Command

                    let url =
                        let value = node.GetAttrString("acp_url", "").Trim()

                        if value <> "" then
                            Some value
                        else
                            presetEndpoint |> Option.bind _.Url

                    let endpointWorkingDir =
                        presetEndpoint
                        |> Option.bind _.WorkingDirectory
                        |> Option.orElse (Some workingDir)

                    let timeoutOverride =
                        node.GetAttr("acp_timeout_ms") |> Option.bind (fun value -> value.AsInt())

                    let timeoutMs =
                        timeoutOverride
                        |> Option.orElseWith (fun () -> presetConfig |> Option.map _.TimeoutMs)
                        |> Option.defaultValue 60000

                    Ok(
                        { Transport = transport
                          Command = command
                          Args = args
                          Url = url
                          Headers = Map.empty
                          WorkingDirectory = endpointWorkingDir },
                        timeoutMs
                    )

    let private startInMemoryAgent (transport: AcpTransport) =
        Async.Start(
            async {
                let! _ = transport.Connect()

                let enumerator =
                    transport.Receive System.Threading.CancellationToken.None
                    |> fun stream -> stream.GetAsyncEnumerator()

                let rec loop cancelled =
                    async {
                        let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask

                        if hasNext then
                            match Codec.decode enumerator.Current with
                            | Ok(Request request) ->
                                match request.Method with
                                | "initialize" ->
                                    let payload =
                                        Json.serializeToElement
                                            {| protocolVersion = "2026-03-23"
                                               capabilities = {| prompt = true |}
                                               serverInfo = {| name = "in-memory-acp" |} |}

                                    let! _ = transport.Send(Codec.encodeResponse request.Id payload)
                                    return! loop cancelled
                                | "session/prompt" ->
                                    let parameters =
                                        request.Params |> Option.defaultValue (Json.serializeToElement {| |})

                                    let sessionId =
                                        Json.tryGetString "sessionId" parameters |> Option.defaultValue "memory-session"

                                    let responseText = $"[Simulated ACP] Session {sessionId} completed."

                                    let payload =
                                        Json.serializeToElement
                                            {| sessionId = sessionId
                                               content =
                                                [ {| ``type`` = "text"
                                                     text = responseText |} ]
                                               stopReason = "completed" |}

                                    let! _ = transport.Send(Codec.encodeResponse request.Id payload)
                                    return! loop cancelled
                                | "session/cancel" ->
                                    let payload = Json.serializeToElement {| cancelled = true |}
                                    let! _ = transport.Send(Codec.encodeResponse request.Id payload)
                                    return! loop true
                                | _ ->
                                    let error =
                                        { Code = -32601
                                          Message = "Method not found"
                                          Data = None }

                                    let! _ = transport.Send(Codec.encodeError request.Id error)
                                    return! loop cancelled
                            | Ok(Notification _) -> return! loop cancelled
                            | Ok(Response _) -> return! loop cancelled
                            | Error _ -> return ()
                        else
                            return ()
                    }

                do! loop false
                do! transport.Disconnect()
                do! enumerator.DisposeAsync().AsTask() |> Async.AwaitTask
            }
        )

    let private recordDelegateDenial operation (denials: ResizeArray<string>) result =
        match result with
        | Error(AcpError.PermissionDenied message) -> denials.Add($"{operation}: {message}")
        | Error(AcpError.PathOutsideRoot message) -> denials.Add($"{operation}: {message}")
        | _ -> ()

        result

    let private wrapDelegate (denials: ResizeArray<string>) (delegateImpl: AcpDelegate) =
        { ReadTextFile =
            fun request ->
                async {
                    let! result = delegateImpl.ReadTextFile request
                    return recordDelegateDenial "filesystem/read_text_file" denials result
                }
          WriteTextFile =
            fun request ->
                async {
                    let! result = delegateImpl.WriteTextFile request
                    return recordDelegateDenial "filesystem/write_text_file" denials result
                }
          TerminalCreate =
            fun request ->
                async {
                    let! result = delegateImpl.TerminalCreate request
                    return recordDelegateDenial "terminal/create" denials result
                }
          TerminalOutput =
            fun request ->
                async {
                    let! result = delegateImpl.TerminalOutput request
                    return recordDelegateDenial "terminal/output" denials result
                }
          TerminalWaitForExit =
            fun request ->
                async {
                    let! result = delegateImpl.TerminalWaitForExit request
                    return recordDelegateDenial "terminal/wait_for_exit" denials result
                }
          TerminalKill =
            fun request ->
                async {
                    let! result = delegateImpl.TerminalKill request
                    return recordDelegateDenial "terminal/kill" denials result
                }
          TerminalRelease =
            fun request ->
                async {
                    let! result = delegateImpl.TerminalRelease request
                    return recordDelegateDenial "terminal/release" denials result
                }
          RequestPermission =
            fun request ->
                async {
                    let! result = delegateImpl.RequestPermission request
                    return recordDelegateDenial "permissions/request" denials result
                } }

    type AcpAgentHandler(?permissionStrategy: PermissionStrategy) =
        let permissionStrategy = defaultArg permissionStrategy PermissionStrategy.DenyAll

        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                let stageDir, rootDir = resolveStageDirs logsRoot node context

                let fail reason artifact =
                    writeSessionArtifact stageDir rootDir artifact
                    let outcome = Outcome.Fail(reason)
                    HandlerArtifacts.writeStatus stageDir rootDir outcome
                    outcome

                let promptText = buildPrompt node context graph
                writeStageFile stageDir rootDir "prompt.md" promptText

                let workingDir = resolveWorkingDir node graph

                match buildEndpoint node workingDir with
                | Error error ->
                    let artifact =
                        {| session_id = node.Id
                           transport = node.GetAttrString("acp_transport", "stdio")
                           cancelled = false
                           delegate_denials = [||]
                           notifications = [||]
                           error = error |}

                    fail error artifact
                | Ok(endpoint, timeoutMs) ->
                    let effectiveWorkingDir =
                        endpoint.WorkingDirectory |> Option.defaultValue workingDir

                    ensureDir effectiveWorkingDir
                    let timeout = Some(TimeSpan.FromMilliseconds(float timeoutMs))

                    let promptMetadata =
                        match resolveMcpServersJson context with
                        | Some json -> Some { PromptMetadata.McpServersJson = Some json }
                        | None -> None

                    let notifications = ResizeArray<obj>()
                    let denials = ResizeArray<string>()

                    let baseDelegate =
                        DefaultDelegate.createDefaultDelegate effectiveWorkingDir permissionStrategy 65536

                    let delegateImpl = wrapDelegate denials baseDelegate

                    let client =
                        match endpoint.Transport with
                        | AcpTransportKind.InMemory ->
                            let clientTransport, serverTransport = Transport.createInMemoryPair ()
                            startInMemoryAgent serverTransport
                            Client.create (fun _ -> Ok clientTransport)
                        | _ -> Library.createClient ()

                    client.AddObserver(fun methodName parameters ->
                        notifications.Add(
                            box
                                {| method = methodName
                                   parameters = parameters |}
                        ))

                    let history = ResizeArray<obj>()

                    history.Add(
                        box
                            {| kind = "request"
                               method = "initialize" |}
                    )

                    try
                        match client.Connect(endpoint, delegateImpl, timeout) |> Async.RunSynchronously with
                        | Error error ->
                            let artifact =
                                {| session_id = node.Id
                                   transport = endpoint.Transport.ToString()
                                   cancelled = false
                                   delegate_denials = denials |> Seq.toArray
                                   notifications = notifications |> Seq.toArray
                                   error = AcpError.describe error |}

                            fail (AcpError.describe error) artifact
                        | Ok initializeResult ->
                            history.Add(
                                box
                                    {| kind = "response"
                                       method = "initialize"
                                       payload =
                                        {| protocol_version = initializeResult.ProtocolVersion
                                           capabilities = initializeResult.Capabilities
                                           server_info = initializeResult.ServerInfo |} |}
                            )

                            history.Add(
                                box
                                    {| kind = "request"
                                       method = "session/prompt"
                                       session_id = node.Id |}
                            )

                            match
                                client.Prompt(node.Id, [ ContentBlock.text promptText ], promptMetadata, timeout)
                                |> Async.RunSynchronously
                            with
                            | Error error ->
                                let cancelled =
                                    match error with
                                    | AcpError.TimedOut _ -> true
                                    | _ -> false

                                let artifact =
                                    {| session_id = node.Id
                                       transport = endpoint.Transport.ToString()
                                       cancelled = cancelled
                                       delegate_denials = denials |> Seq.toArray
                                       notifications = notifications |> Seq.toArray
                                       initialize = initializeResult
                                       error = AcpError.describe error |}

                                fail (AcpError.describe error) artifact
                            | Ok promptResult ->
                                let responseText = renderPromptResult promptResult

                                history.Add(
                                    box
                                        {| kind = "response"
                                           method = "session/prompt"
                                           payload =
                                            {| session_id = promptResult.SessionId
                                               response_text = responseText
                                               stop_reason = promptResult.StopReason |} |}
                                )

                                writeStageFile stageDir rootDir "response.md" responseText
                                writeStageFile stageDir rootDir "history.json" (serialize (history |> Seq.toList))

                                let notificationCount = notifications.Count

                                let sessionArtifact =
                                    {| session_id = promptResult.SessionId
                                       transport = endpoint.Transport.ToString()
                                       cancelled = false
                                       delegate_denials = denials |> Seq.toArray
                                       notifications = notifications |> Seq.toArray
                                       initialize = initializeResult
                                       endpoint =
                                        {| transport = endpoint.Transport.ToString()
                                           command = endpoint.Command
                                           args = endpoint.Args
                                           url = endpoint.Url
                                           working_directory = endpoint.WorkingDirectory |} |}

                                writeSessionArtifact stageDir rootDir sessionArtifact

                                let contextResponse = truncateForContext responseText

                                let contextUpdates =
                                    Map.ofList
                                        [ "last_stage", node.Id
                                          "last_response", contextResponse
                                          $"acp.session_id.{node.Id}", promptResult.SessionId
                                          $"acp.output.{node.Id}", contextResponse
                                          $"acp.notifications.{node.Id}.count", string notificationCount ]

                                let outcome =
                                    Outcome.Success(
                                        notes = $"ACP agent completed: {node.Id}",
                                        contextUpdates = contextUpdates
                                    )

                                HandlerArtifacts.writeStatus stageDir rootDir outcome
                                outcome
                    finally
                        client.Disconnect() |> Async.RunSynchronously
