namespace McpClient

module ToolDiscovery =

    let discoverTools (servers: McpRemoteServer list) =
        let rec loop remaining discovered =
            async {
                match remaining with
                | [] -> return Ok(List.rev discovered)
                | server :: rest ->
                    let! result = server.ListTools()

                    match result with
                    | Error error -> return Error error
                    | Ok tools ->
                        let next =
                            tools
                            |> List.map (fun definition ->
                                { ServerName = server.Config.Name
                                  Definition = definition })

                        return! loop rest (List.rev next @ discovered)
            }

        loop servers []
