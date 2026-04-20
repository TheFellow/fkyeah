namespace JsonRpc

open System
open System.IO
open System.Text
open System.Text.Json

module Codec =

    let private writeId (writer: Utf8JsonWriter) (id: JsonRpcId) =
        writer.WritePropertyName("id")

        match id with
        | StringId value -> writer.WriteStringValue(value)
        | NumberId value -> writer.WriteNumberValue(value)

    let private parseId (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String -> Ok(StringId(element.GetString()))
        | JsonValueKind.Number ->
            match element.TryGetInt32() with
            | true, value -> Ok(NumberId value)
            | false, _ -> Error "JSON-RPC id number must fit Int32"
        | kind -> Error $"JSON-RPC id must be string or number, got {kind}"

    let private tryGetProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private cloneOption (elementOpt: JsonElement option) = elementOpt |> Option.map _.Clone()

    let encode (request: JsonRpcRequest) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("jsonrpc", "2.0")
        writeId writer request.Id
        writer.WriteString("method", request.Method)

        match request.Params with
        | Some parameters ->
            writer.WritePropertyName("params")
            parameters.WriteTo(writer)
        | None -> ()

        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let encodeNotification (methodName: string) (parameters: JsonElement option) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("jsonrpc", "2.0")
        writer.WriteString("method", methodName)

        match parameters with
        | Some value ->
            writer.WritePropertyName("params")
            value.WriteTo(writer)
        | None -> ()

        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let encodeResponse (id: JsonRpcId) (result: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("jsonrpc", "2.0")
        writeId writer id
        writer.WritePropertyName("result")
        result.WriteTo(writer)
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let encodeError (id: JsonRpcId) (error: JsonRpcError) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("jsonrpc", "2.0")
        writeId writer id
        writer.WritePropertyName("error")
        writer.WriteStartObject()
        writer.WriteNumber("code", error.Code)
        writer.WriteString("message", error.Message)

        match error.Data with
        | Some value ->
            writer.WritePropertyName("data")
            value.WriteTo(writer)
        | None -> ()

        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let decode (payload: byte array) =
        try
            use document = JsonDocument.Parse(payload)
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "JSON-RPC payload must be an object"
            else
                match tryGetProperty "id" root with
                | Some idElement ->
                    match parseId idElement with
                    | Error error -> Error error
                    | Ok id ->
                        match tryGetProperty "error" root with
                        | Some errorElement ->
                            let codeElement =
                                match tryGetProperty "code" errorElement with
                                | Some value -> value
                                | None -> raise (InvalidOperationException("JSON-RPC error is missing code"))

                            let messageElement =
                                match tryGetProperty "message" errorElement with
                                | Some value -> value
                                | None -> raise (InvalidOperationException("JSON-RPC error is missing message"))

                            let code =
                                match codeElement.TryGetInt32() with
                                | true, value -> value
                                | false, _ -> raise (InvalidOperationException("JSON-RPC error code must be an Int32"))

                            if messageElement.ValueKind <> JsonValueKind.String then
                                Error "JSON-RPC error message must be a string"
                            else
                                Ok(
                                    Response(
                                        id,
                                        Error
                                            { Code = code
                                              Message = messageElement.GetString()
                                              Data = tryGetProperty "data" errorElement |> cloneOption }
                                    )
                                )
                        | None ->
                            match tryGetProperty "result" root with
                            | Some resultElement -> Ok(Response(id, Ok(resultElement.Clone())))
                            | None ->
                                match tryGetProperty "method" root with
                                | Some methodElement when methodElement.ValueKind = JsonValueKind.String ->
                                    Ok(
                                        Request
                                            { Id = id
                                              Method = methodElement.GetString()
                                              Params = tryGetProperty "params" root |> cloneOption }
                                    )
                                | Some _ -> Error "JSON-RPC request method must be a string"
                                | None -> Error "JSON-RPC response must contain either result or error"
                | None ->
                    match tryGetProperty "method" root with
                    | Some methodElement when methodElement.ValueKind = JsonValueKind.String ->
                        Ok(Notification(methodElement.GetString(), tryGetProperty "params" root |> cloneOption))
                    | Some _ -> Error "JSON-RPC notification method must be a string"
                    | None -> Error "JSON-RPC message is missing method or id"
        with
        | :? JsonException as ex -> Error $"Malformed JSON: {ex.Message}"
        | :? InvalidOperationException as ex -> Error ex.Message
        | ex -> Error $"Failed to decode JSON-RPC payload: {ex.Message}"
