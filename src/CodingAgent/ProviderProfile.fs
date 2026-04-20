namespace CodingAgent

open System
open System.Diagnostics
open System.IO
open UnifiedLlm

/// Interface for provider-specific tool profiles
type IProviderProfile =
    /// Provider identifier ("openai", "anthropic", "gemini")
    abstract member Id: string
    /// Model identifier
    abstract member Model: string
    /// Get tool definitions for the LLM
    abstract member ToolDefinitions: ToolDefinition list

    /// Build the system prompt for the given environment and project docs
    abstract member BuildSystemPrompt:
        env: IExecutionEnvironment * projectDocs: string option * userInstructions: string option -> string

    /// Whether this profile supports streaming
    abstract member SupportsStreaming: bool
    /// Whether this profile supports parallel tool calls
    abstract member SupportsParallelToolCalls: bool
    /// Context window size
    abstract member ContextWindowSize: int

/// Shared tool definitions
module SharedTools =
    let readFile =
        { Name = "read_file"
          Description = "Read a file from the filesystem. Returns line-numbered content."
          Parameters =
            """{"type":"object","properties":{"file_path":{"type":"string","description":"Path to the file, relative to the working directory or absolute"},"offset":{"type":"integer","description":"1-based line number to start reading from"},"limit":{"type":"integer","description":"Max lines to read (default: 2000)"}},"required":["file_path"]}""" }

    let writeFile =
        { Name = "write_file"
          Description = "Write content to a file. Creates the file and parent directories if needed."
          Parameters =
            """{"type":"object","properties":{"file_path":{"type":"string","description":"Path to the file, relative to the working directory or absolute"},"content":{"type":"string","description":"The full file content"}},"required":["file_path","content"]}""" }

    let editFile =
        { Name = "edit_file"
          Description = "Replace an exact string occurrence in a file."
          Parameters =
            """{"type":"object","properties":{"file_path":{"type":"string","description":"Path to the file, relative to the working directory or absolute"},"old_string":{"type":"string","description":"Exact text to find"},"new_string":{"type":"string","description":"Replacement text"},"replace_all":{"type":"boolean","description":"Replace all occurrences (default: false)"}},"required":["file_path","old_string","new_string"]}""" }

    let shell =
        { Name = "shell"
          Description = "Execute a shell command. Returns stdout, stderr, and exit code."
          Parameters =
            """{"type":"object","properties":{"command":{"type":"string","description":"The command to run"},"timeout_ms":{"type":"integer","description":"Override default timeout"},"description":{"type":"string","description":"Human-readable description of what this does"}},"required":["command"]}""" }

    let grep =
        { Name = "grep"
          Description = "Search file contents using regex patterns."
          Parameters =
            """{"type":"object","properties":{"pattern":{"type":"string","description":"Regex pattern"},"path":{"type":"string","description":"Directory or file to search"},"case_insensitive":{"type":"boolean","description":"Case insensitive search"},"max_results":{"type":"integer","description":"Max results (default: 100)"},"glob_filter":{"type":"string","description":"Glob pattern to restrict search files, e.g. '*.fs'"}},"required":["pattern"]}""" }

    let glob =
        { Name = "glob"
          Description = "Find files matching a glob pattern."
          Parameters =
            """{"type":"object","properties":{"pattern":{"type":"string","description":"Glob pattern (e.g., **/*.ts)"},"path":{"type":"string","description":"Base directory"}},"required":["pattern"]}""" }

    let readManyFiles =
        { Name = "read_many_files"
          Description = "Read multiple files in one call. Returns each file with line-numbered content."
          Parameters =
            """{"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"},"description":"List of file paths to read"},"offset":{"type":"integer","description":"1-based line number to start reading from"},"limit":{"type":"integer","description":"Max lines per file (default: 2000)"}},"required":["paths"]}""" }

    let listDir =
        { Name = "list_dir"
          Description = "List directory entries with optional recursion depth."
          Parameters =
            """{"type":"object","properties":{"path":{"type":"string","description":"Directory path to list (default: .)"},"depth":{"type":"integer","description":"Max recursion depth (default: 1)"}},"required":[]}""" }

    let applyPatch =
        { Name = "apply_patch"
          Description =
            "Apply code changes using the patch format (v4a). Supports creating, deleting, and modifying files."
          Parameters =
            """{"type":"object","properties":{"patch":{"type":"string","description":"The patch content in v4a format"}},"required":["patch"]}""" }

    let spawnAgent =
        { Name = "spawn_agent"
          Description = "Spawn a subagent to handle a scoped task autonomously."
          Parameters =
            """{"type":"object","properties":{"task":{"type":"string","description":"Natural language task description"},"working_dir":{"type":"string","description":"Subdirectory to scope the agent to"},"model":{"type":"string","description":"Model override"},"max_turns":{"type":"integer","description":"Turn limit"}},"required":["task"]}""" }

    let sendInput =
        { Name = "send_input"
          Description = "Send a message to a running subagent."
          Parameters =
            """{"type":"object","properties":{"agent_id":{"type":"string","description":"Subagent ID"},"message":{"type":"string","description":"Message to send"}},"required":["agent_id","message"]}""" }

    let wait =
        { Name = "wait"
          Description = "Wait for a subagent to complete and return its result."
          Parameters =
            """{"type":"object","properties":{"agent_id":{"type":"string","description":"Subagent ID"}},"required":["agent_id"]}""" }

    let closeAgent =
        { Name = "close_agent"
          Description = "Terminate a subagent."
          Parameters =
            """{"type":"object","properties":{"agent_id":{"type":"string","description":"Subagent ID"}},"required":["agent_id"]}""" }

/// Build environment context block for system prompts
module EnvironmentContext =
    let private runProcess (workingDir: string) (fileName: string) (arguments: string) : string option =
        try
            let psi = ProcessStartInfo()
            psi.FileName <- fileName
            psi.Arguments <- arguments
            psi.WorkingDirectory <- workingDir
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = Process.Start(psi)

            if proc.WaitForExit(2000) then
                if proc.ExitCode = 0 then
                    Some(proc.StandardOutput.ReadToEnd().Trim())
                else
                    None
            else
                try
                    proc.Kill(true)
                with _ ->
                    ()

                None
        with _ ->
            None

    let private tryGit (workingDir: string) (args: string) = runProcess workingDir "git" args

    let private gitContext (workingDir: string) =
        let isGitRepository =
            if Directory.Exists(Path.Combine(workingDir, ".git")) then
                true
            else
                match tryGit workingDir "rev-parse --is-inside-work-tree" with
                | Some "true" -> true
                | _ -> false

        if not isGitRepository then
            false, None, None, []
        else
            let branch = tryGit workingDir "rev-parse --abbrev-ref HEAD"

            let modifiedCount =
                match tryGit workingDir "status --porcelain" with
                | Some s when s <> "" -> s.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries).Length |> Some
                | Some _ -> Some 0
                | None -> None

            let commits =
                match tryGit workingDir "log -n 5 --pretty=format:%h %s" with
                | Some c when c <> "" -> c.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                | _ -> []

            true, branch, modifiedCount, commits

    let build (env: IExecutionEnvironment) (model: string) =
        let isGitRepository, branch, modifiedCount, recentCommits =
            gitContext env.WorkingDirectory

        let commitBlock =
            if recentCommits.IsEmpty then
                "Recent commits: none available"
            else
                let lines =
                    recentCommits |> List.map (fun c -> sprintf "- %s" c) |> String.concat "\n"

                "Recent commits:\n" + lines

        sprintf
            """<environment>
Working directory: %s
Is git repository: %s
Git branch: %s
Modified file count: %s
Platform: %s
OS version: %s
Today's date: %s
Model: %s
Knowledge cutoff: unknown
%s
</environment>"""
            env.WorkingDirectory
            (if isGitRepository then "true" else "false")
            (branch |> Option.defaultValue "n/a")
            (modifiedCount |> Option.map string |> Option.defaultValue "n/a")
            env.Platform
            env.OsVersion
            (DateTime.UtcNow.ToString("yyyy-MM-dd"))
            model
            commitBlock

/// OpenAI profile aligned with codex-rs tools
type OpenAIProfile(model: string) =
    let mutable customTools: ToolDefinition list = []

    member _.AddCustomTool(tool: ToolDefinition) = customTools <- tool :: customTools

    interface IProviderProfile with
        member _.Id = "openai"
        member _.Model = model

        member _.ToolDefinitions =
            let baseTools =
                [ SharedTools.readFile
                  SharedTools.writeFile
                  SharedTools.applyPatch
                  SharedTools.shell
                  SharedTools.grep
                  SharedTools.glob
                  SharedTools.spawnAgent
                  SharedTools.sendInput
                  SharedTools.wait
                  SharedTools.closeAgent ]
            // Custom tools override base tools with same name
            let customNames = customTools |> List.map (fun t -> t.Name) |> set

            let filtered =
                baseTools |> List.filter (fun t -> not (customNames.Contains(t.Name)))

            filtered @ customTools

        member _.BuildSystemPrompt(env, projectDocs, userInstructions) =
            let envCtx = EnvironmentContext.build env model

            let basePart =
                "You are an AI coding assistant powered by OpenAI. Use tools deliberately, prefer minimal safe changes, and verify results with targeted checks. For code modifications, prefer apply_patch v4a format and keep patches focused. Explain constraints, report errors clearly, and avoid destructive commands unless explicitly requested."

            let parts =
                [ basePart
                  envCtx
                  match projectDocs with
                  | Some d -> d
                  | None -> ""
                  match userInstructions with
                  | Some u -> u
                  | None -> "" ]

            parts |> List.filter (fun s -> s <> "") |> String.concat "\n\n"

        member _.SupportsStreaming = true
        member _.SupportsParallelToolCalls = true
        member _.ContextWindowSize = 1047576

/// Anthropic profile aligned with Claude Code tools
type AnthropicProfile(model: string) =
    let mutable customTools: ToolDefinition list = []

    member _.AddCustomTool(tool: ToolDefinition) = customTools <- tool :: customTools

    interface IProviderProfile with
        member _.Id = "anthropic"
        member _.Model = model

        member _.ToolDefinitions =
            let baseTools =
                [ SharedTools.readFile
                  SharedTools.writeFile
                  SharedTools.editFile
                  SharedTools.shell
                  SharedTools.grep
                  SharedTools.glob
                  SharedTools.spawnAgent
                  SharedTools.sendInput
                  SharedTools.wait
                  SharedTools.closeAgent ]

            let customNames = customTools |> List.map (fun t -> t.Name) |> set

            let filtered =
                baseTools |> List.filter (fun t -> not (customNames.Contains(t.Name)))

            filtered @ customTools

        member _.BuildSystemPrompt(env, projectDocs, userInstructions) =
            let envCtx = EnvironmentContext.build env model

            let basePart =
                "You are Claude, an AI coding assistant by Anthropic. Prefer reading files before editing, and use edit_file with exact old_string/new_string replacements where old_string is unique. Favor editing existing files over creating new ones, keep changes minimal, and validate behavior with targeted commands when feasible."

            let parts =
                [ basePart
                  envCtx
                  match projectDocs with
                  | Some d -> d
                  | None -> ""
                  match userInstructions with
                  | Some u -> u
                  | None -> "" ]

            parts |> List.filter (fun s -> s <> "") |> String.concat "\n\n"

        member _.SupportsStreaming = true
        member _.SupportsParallelToolCalls = true
        member _.ContextWindowSize = 200000

/// Gemini profile aligned with gemini-cli tools
type GeminiProfile(model: string) =
    let mutable customTools: ToolDefinition list = []

    member _.AddCustomTool(tool: ToolDefinition) = customTools <- tool :: customTools

    interface IProviderProfile with
        member _.Id = "gemini"
        member _.Model = model

        member _.ToolDefinitions =
            let baseTools =
                [ SharedTools.readFile
                  SharedTools.readManyFiles
                  SharedTools.writeFile
                  SharedTools.editFile
                  SharedTools.shell
                  SharedTools.grep
                  SharedTools.glob
                  SharedTools.listDir
                  SharedTools.spawnAgent
                  SharedTools.sendInput
                  SharedTools.wait
                  SharedTools.closeAgent ]

            let customNames = customTools |> List.map (fun t -> t.Name) |> set

            let filtered =
                baseTools |> List.filter (fun t -> not (customNames.Contains(t.Name)))

            filtered @ customTools

        member _.BuildSystemPrompt(env, projectDocs, userInstructions) =
            let envCtx = EnvironmentContext.build env model

            let basePart =
                "You are a Gemini-powered AI coding assistant. Use tools to inspect, edit, and validate code with concise iterative steps. Prefer edit_file for targeted edits, use read_many_files for batch inspection when helpful, and use list_dir to map project structure quickly. Follow project instructions from GEMINI.md when present."

            let parts =
                [ basePart
                  envCtx
                  match projectDocs with
                  | Some d -> d
                  | None -> ""
                  match userInstructions with
                  | Some u -> u
                  | None -> "" ]

            parts |> List.filter (fun s -> s <> "") |> String.concat "\n\n"

        member _.SupportsStreaming = true
        member _.SupportsParallelToolCalls = true
        member _.ContextWindowSize = 1048576
