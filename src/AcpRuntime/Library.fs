namespace AcpRuntime

module Library =

    let createTransport (endpoint: AcpEndpoint) =
        match endpoint.Transport with
        | AcpTransportKind.Stdio ->
            match endpoint.Command with
            | Some command when not (System.String.IsNullOrWhiteSpace(command)) ->
                Ok(Transport.createStdioTransport command endpoint.Args endpoint.WorkingDirectory)
            | _ -> Error(AcpError.InvalidPayload "Stdio endpoint requires command")
        | AcpTransportKind.WebSocket ->
            match endpoint.Url with
            | Some url when not (System.String.IsNullOrWhiteSpace(url)) ->
                Ok(Transport.createWebSocketTransport url endpoint.Headers)
            | _ -> Error(AcpError.InvalidPayload "WebSocket endpoint requires url")
        | AcpTransportKind.HttpSse ->
            match endpoint.Url with
            | Some url when not (System.String.IsNullOrWhiteSpace(url)) ->
                Ok(Transport.createHttpSseTransport url None endpoint.Headers)
            | _ -> Error(AcpError.InvalidPayload "HTTP+SSE endpoint requires url")
        | AcpTransportKind.InMemory ->
            Error(AcpError.InvalidPayload "In-memory transport requires an injected transport")

    let createClient () = Client.create createTransport

    let connect (client: AcpClient) endpoint delegateImpl timeout =
        client.Connect(endpoint, delegateImpl, timeout)

    let prompt (client: AcpClient) sessionId content metadata timeout =
        client.Prompt(sessionId, content, metadata, timeout)

    let cancel (client: AcpClient) sessionId timeout = client.Cancel(sessionId, timeout)
