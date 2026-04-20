namespace McpClient

open System
open System.Text.Json
open JsonRpc

module Server =

    let private tryGetProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private getRequiredProperty (name: string) (element: JsonElement) =
        match tryGetProperty name element with
        | Some value -> Ok value
        | None -> Error(McpError.InvalidResponse $"Missing required property '{name}'")

    let private cloneOrDefault (value: JsonElement option) =
        match value with
        | Some element -> element.Clone()
        | None -> JsonSerializer.SerializeToElement({| |}).Clone()

    let private serializeToElement<'T> (value: 'T) =
        JsonSerializer.SerializeToElement(value).Clone()

    let private mapRpcError (error: JsonRpcError) =
        if error.Code = Correlator.TransportClosedCode then
            McpError.TransportClosed error.Message
        else
            McpError.RpcError(error.Code, error.Message)

    let private buildTransport (config: McpServerConfig) =
        match McpServerConfig.validate config with
        | Error error -> Error error
        | Ok _ ->
            match config.Transport with
            | McpTransportKind.Stdio ->
                match config.Command with
                | Some command -> Ok(Transport.createStdioTransport command config.Args config.Env)
                | None -> Error(McpError.InvalidConfiguration $"Server '{config.Name}' is missing command")
            | McpTransportKind.HttpSse ->
                match config.Url with
                | Some url -> Ok(Transport.createHttpSseTransport url config.RequestUrl config.Headers)
                | None -> Error(McpError.InvalidConfiguration $"Server '{config.Name}' is missing url")
            | McpTransportKind.StreamableHttp ->
                match config.Url |> Option.orElse config.RequestUrl with
                | Some url -> Ok(Transport.createHttpSseTransport url config.RequestUrl config.Headers)
                | None -> Error(McpError.InvalidConfiguration $"Server '{config.Name}' is missing url or requestUrl")

    let createServerWithTransport (config: McpServerConfig) (transport: McpTransport) (policy: McpConnectionPolicy) =
        match McpServerConfig.validate config with
        | Error error -> Error error
        | Ok _ ->
            let mutable correlator: JsonRpcCorrelator option = None
            let mutable toolCache: McpToolDefinition list option = None

            let stopCorrelator () =
                async {
                    match correlator with
                    | Some current ->
                        correlator <- None
                        do! Correlator.stop current
                    | None -> ()
                }

            let disconnectTransport () =
                async {
                    do! stopCorrelator ()
                    do! transport.Disconnect()
                }

            let sendRpc methodName parameters =
                async {
                    match correlator with
                    | None -> return Error McpError.NotConnected
                    | Some current ->
                        let! result = Correlator.sendRequest methodName parameters current

                        return
                            match result with
                            | Ok payload -> Ok payload
                            | Error error -> Error(mapRpcError error)
                }

            let initialize () =
                async {
                    let! connectResult = transport.Connect()

                    match connectResult with
                    | Error error -> return Error error
                    | Ok() ->
                        let newCorrelator =
                            let receiveStream =
                                { new System.Collections.Generic.IAsyncEnumerable<byte array> with
                                    member _.GetAsyncEnumerator(cancellationToken) =
                                        (transport.Receive cancellationToken).GetAsyncEnumerator(cancellationToken) }

                            Correlator.start
                                (fun payload ->
                                    async {
                                        let! sendResult = transport.Send payload
                                        return sendResult |> Result.mapError McpError.describe
                                    })
                                receiveStream
                                (fun _ _ -> ())

                        correlator <- Some newCorrelator

                        let parameters =
                            serializeToElement
                                {| protocolVersion = "2025-03-26"
                                   clientInfo = {| name = "fkyeah-attractor" |} |}

                        let! initResult = sendRpc "initialize" (Some parameters)

                        match initResult with
                        | Ok _ -> return Ok()
                        | Error error ->
                            do! disconnectTransport ()
                            return Error error
                }

            let ensureInitialized () =
                async {
                    match correlator with
                    | Some _ -> return Ok()
                    | None -> return! initialize ()
                }

            let parseToolDefinition (toolElement: JsonElement) : Result<McpToolDefinition, McpError> =
                match getRequiredProperty "name" toolElement with
                | Error error -> Error error
                | Ok nameElement when nameElement.ValueKind <> JsonValueKind.String ->
                    Error(McpError.InvalidResponse "Tool name must be a string")
                | Ok nameElement ->
                    let description =
                        tryGetProperty "description" toolElement
                        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                        |> Option.map _.GetString()
                        |> Option.defaultValue ""

                    let inputSchema =
                        tryGetProperty "inputSchema" toolElement
                        |> Option.orElseWith (fun () -> tryGetProperty "input_schema" toolElement)
                        |> cloneOrDefault

                    Ok
                        { Name = nameElement.GetString()
                          Description = description
                          InputSchema = inputSchema }

            let rec listToolsInternal () =
                async {
                    match toolCache with
                    | Some cached -> return Ok cached
                    | None ->
                        let! ready = ensureInitialized ()

                        match ready with
                        | Error error -> return Error error
                        | Ok() ->
                            let! response = sendRpc "tools/list" None

                            match response with
                            | Error error -> return Error error
                            | Ok payload ->
                                match tryGetProperty "tools" payload with
                                | Some toolsElement when toolsElement.ValueKind = JsonValueKind.Array ->
                                    let parsed =
                                        toolsElement.EnumerateArray()
                                        |> Seq.map parseToolDefinition
                                        |> Seq.fold
                                            (fun state next ->
                                                match state, next with
                                                | Ok acc, Ok item -> Ok(item :: acc)
                                                | Error error, _
                                                | _, Error error -> Error error)
                                            (Ok [])

                                    match parsed with
                                    | Ok tools ->
                                        let ordered = tools |> List.rev
                                        toolCache <- Some ordered
                                        return Ok ordered
                                    | Error error -> return Error error
                                | Some _ -> return Error(McpError.InvalidResponse "'tools' must be an array")
                                | None ->
                                    toolCache <- Some []
                                    return Ok []
                }

            let callToolInternal name arguments =
                async {
                    let! ready = ensureInitialized ()

                    match ready with
                    | Error error -> return Error error
                    | Ok() ->
                        let parameters = serializeToElement {| name = name; arguments = arguments |}
                        let! response = sendRpc "tools/call" (Some parameters)

                        match response with
                        | Error error -> return Error error
                        | Ok payload ->
                            match getRequiredProperty "content" payload with
                            | Error error -> return Error error
                            | Ok content ->
                                let isError =
                                    tryGetProperty "isError" payload
                                    |> Option.orElseWith (fun () -> tryGetProperty "is_error" payload)
                                    |> Option.bind (fun value ->
                                        match value.ValueKind with
                                        | JsonValueKind.True -> Some true
                                        | JsonValueKind.False -> Some false
                                        | _ -> None)
                                    |> Option.defaultValue false

                                return
                                    Ok
                                        { Content = content.Clone()
                                          IsError = isError }
                }

            let isRecoverable error =
                match error with
                | McpError.TransportClosed _
                | McpError.ProcessExited _
                | McpError.Timeout _
                | McpError.NotConnected -> true
                | _ -> false

            let withReconnect refreshTools operation =
                let rec attempt remaining =
                    async {
                        let! result = operation ()

                        match result with
                        | Ok _ -> return result
                        | Error error when policy.AutoReconnect && remaining > 0 && isRecoverable error ->
                            do! disconnectTransport ()

                            if policy.RetryDelay > TimeSpan.Zero then
                                do! Async.Sleep(int policy.RetryDelay.TotalMilliseconds)

                            let! reconnected = initialize ()

                            match reconnected with
                            | Error reconnectError -> return Error reconnectError
                            | Ok() ->
                                if refreshTools && policy.RefreshToolsOnReconnect then
                                    toolCache <- None
                                    let! refreshed = listToolsInternal ()

                                    match refreshed with
                                    | Error refreshError -> return Error refreshError
                                    | Ok _ -> return! attempt (remaining - 1)
                                else
                                    return! attempt (remaining - 1)
                        | Error _ -> return result
                    }

                attempt policy.MaxRetries

            Ok
                { Config = config
                  ListTools = fun () -> withReconnect false listToolsInternal
                  CallTool = fun name arguments -> withReconnect true (fun () -> callToolInternal name arguments)
                  Cleanup = disconnectTransport }

    let createServer (config: McpServerConfig) (policy: McpConnectionPolicy) =
        match buildTransport config with
        | Ok transport -> createServerWithTransport config transport policy
        | Error error -> Error error
