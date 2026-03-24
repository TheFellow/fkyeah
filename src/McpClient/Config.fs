namespace McpClient

open System
open System.IO
open System.Text.Json

module Config =

    let private tryGetProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private requireString name element =
        match tryGetProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
        | Some _ -> Error(McpError.InvalidConfiguration $"Field '{name}' must be a string")
        | None -> Error(McpError.InvalidConfiguration $"Missing required field '{name}'")

    let private parseStringList name element =
        match tryGetProperty name element with
        | None -> Ok []
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.map (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    Ok(item.GetString())
                else
                    Error(McpError.InvalidConfiguration $"Array '{name}' must contain only strings"))
            |> Seq.fold
                (fun state next ->
                    match state, next with
                    | Ok acc, Ok item -> Ok(item :: acc)
                    | Error error, _
                    | _, Error error -> Error error)
                (Ok [])
            |> Result.map List.rev
        | Some _ -> Error(McpError.InvalidConfiguration $"Field '{name}' must be an array")

    let private parseStringMap name element =
        match tryGetProperty name element with
        | None -> Ok Map.empty
        | Some value when value.ValueKind = JsonValueKind.Object ->
            value.EnumerateObject()
            |> Seq.map (fun property ->
                if property.Value.ValueKind = JsonValueKind.String then
                    Ok(property.Name, property.Value.GetString())
                else
                    Error(McpError.InvalidConfiguration $"Object '{name}' must contain only string values"))
            |> Seq.fold
                (fun state next ->
                    match state, next with
                    | Ok acc, Ok (key, item) -> Ok(Map.add key item acc)
                    | Error error, _
                    | _, Error error -> Error error)
                (Ok Map.empty)
        | Some _ -> Error(McpError.InvalidConfiguration $"Field '{name}' must be an object")

    let private parseServer (element: JsonElement) =
        match requireString "name" element, requireString "transport" element with
        | Error error, _
        | _, Error error -> Error error
        | Ok name, Ok transportRaw ->
            match McpTransportKind.Parse transportRaw, parseStringList "args" element, parseStringMap "env" element, parseStringMap "headers" element with
            | None, _, _, _ -> Error(McpError.InvalidConfiguration $"Unknown transport '{transportRaw}'")
            | _, Error error, _, _
            | _, _, Error error, _
            | _, _, _, Error error -> Error error
            | Some transport, Ok args, Ok env, Ok headers ->
                let command =
                    tryGetProperty "command" element
                    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                    |> Option.map _.GetString()

                let url =
                    tryGetProperty "url" element
                    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                    |> Option.map _.GetString()

                let requestUrl =
                    tryGetProperty "requestUrl" element
                    |> Option.orElseWith (fun () -> tryGetProperty "request_url" element)
                    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                    |> Option.map _.GetString()

                let config: McpServerConfig =
                    { Name = name
                      Transport = transport
                      Command = command
                      Args = args
                      Env = env
                      Url = url
                      RequestUrl = requestUrl
                      Headers = headers }

                McpServerConfig.validate config

    let parseConfigText (text: string) =
        try
            use document = JsonDocument.Parse(text)
            let root = document.RootElement

            let serversElement =
                match root.ValueKind with
                | JsonValueKind.Array -> Ok root
                | JsonValueKind.Object ->
                    match tryGetProperty "servers" root with
                    | Some value when value.ValueKind = JsonValueKind.Array -> Ok value
                    | Some _ -> Error(McpError.InvalidConfiguration "Field 'servers' must be an array")
                    | None -> Error(McpError.InvalidConfiguration "Missing required field 'servers'")
                | _ -> Error(McpError.InvalidConfiguration "Configuration root must be an object or array")

            match serversElement with
            | Error error -> Error error
            | Ok servers ->
                servers.EnumerateArray()
                |> Seq.map parseServer
                |> Seq.fold
                    (fun state next ->
                        match state, next with
                        | Ok acc, Ok item -> Ok(item :: acc)
                        | Error error, _
                        | _, Error error -> Error error)
                    (Ok [])
                |> Result.map List.rev
        with
        | :? JsonException as ex -> Error(McpError.InvalidConfiguration $"Invalid JSON: {ex.Message}")
        | ex -> Error(McpError.InvalidConfiguration ex.Message)

    let parseConfigFile path =
        try
            parseConfigText (File.ReadAllText(path))
        with
        | :? IOException as ex -> Error(McpError.InvalidConfiguration ex.Message)
