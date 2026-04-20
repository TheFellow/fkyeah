namespace UnifiedLlm

open System.Collections.Generic

/// A tool with its definition and optional execute handler
type Tool =
    { Definition: ToolDefinition
      Execute: (string -> string) option }

/// Registry of tools for lookup and dispatch
type ToolRegistry() =
    let tools = Dictionary<string, Tool>()

    /// Register a tool. If a tool with the same name exists, it is replaced.
    member _.Register(tool: Tool) = tools.[tool.Definition.Name] <- tool

    /// Remove a tool by name. Returns true if it was found.
    member _.Unregister(name: string) = tools.Remove(name)

    /// Look up a tool by name
    member _.Resolve(name: string) : Tool option =
        match tools.TryGetValue(name) with
        | true, tool -> Some tool
        | false, _ -> Option.None

    /// List all registered tool definitions
    member _.List() : ToolDefinition list =
        tools.Values |> Seq.map (fun t -> t.Definition) |> Seq.toList

    /// List all tool names
    member _.Names() : string list = tools.Keys |> Seq.toList

    /// Get count of registered tools
    member _.Count = tools.Count
