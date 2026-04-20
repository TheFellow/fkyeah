namespace CodingAgent

open System
open System.Diagnostics
open System.IO
open System.Text

/// Project documentation discovery
module ProjectDocs =

    /// Universal doc files loaded for all providers
    let universalFiles = [ "AGENTS.md" ]

    /// Provider-specific doc files
    let providerFiles (providerId: string) =
        match providerId with
        | "anthropic" -> [ "CLAUDE.md" ]
        | "openai" -> [ ".codex/instructions.md" ]
        | "gemini" -> [ "GEMINI.md" ]
        | _ -> []

    let private runGit (workingDir: string) (arguments: string) : string option =
        try
            let psi = ProcessStartInfo()
            psi.FileName <- "git"
            psi.Arguments <- arguments
            psi.WorkingDirectory <- workingDir
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = Process.Start(psi)

            if proc.WaitForExit(2000) && proc.ExitCode = 0 then
                Some(proc.StandardOutput.ReadToEnd().Trim())
            else
                try
                    proc.Kill(true)
                with _ ->
                    ()

                None
        with _ ->
            None

    let private gitRootOrWorkingDir (workingDir: string) =
        match runGit workingDir "rev-parse --show-toplevel" with
        | Some root when root <> "" -> Path.GetFullPath(root)
        | _ -> Path.GetFullPath(workingDir)

    let private directoryChain (rootDir: string) (workingDir: string) =
        let root = Path.GetFullPath(rootDir)
        let cwd = Path.GetFullPath(workingDir)

        if not (cwd.StartsWith(root, StringComparison.OrdinalIgnoreCase)) then
            let rec climbAll (dir: string) (acc: string list) =
                let nextAcc = dir :: acc
                let parent = Directory.GetParent(dir)

                if isNull parent then
                    nextAcc
                else
                    climbAll parent.FullName nextAcc

            climbAll cwd []
        else
            let rec loop (dir: string) (acc: string list) =
                if String.Equals(dir, root, StringComparison.OrdinalIgnoreCase) then
                    dir :: acc
                else
                    let parent = Directory.GetParent(dir)

                    if isNull parent then
                        dir :: acc
                    else
                        loop parent.FullName (dir :: acc)

            loop cwd []

    /// Discover and load project documentation files
    let discover (workingDir: string) (providerId: string) : string option =
        let root = gitRootOrWorkingDir workingDir
        let dirs = directoryChain root workingDir
        let filesToCheck = universalFiles @ (providerFiles providerId)

        let parts =
            [ for dir in dirs do
                  for fileName in filesToCheck do
                      let fullPath = Path.Combine(dir, fileName)

                      if File.Exists(fullPath) then
                          let relative =
                              let rootNorm = Path.GetFullPath(root)
                              let fullNorm = Path.GetFullPath(fullPath)

                              if fullNorm.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase) then
                                  Path.GetRelativePath(rootNorm, fullNorm)
                              else
                                  fullNorm

                          let content = File.ReadAllText(fullPath)
                          yield sprintf "--- %s ---\n%s" relative content ]

        if parts.IsEmpty then
            None
        else
            let marker = "\n[Project instructions truncated at 32KB]"
            let maxBytes = 32768
            let utf8 = Encoding.UTF8
            let joined = parts |> String.concat "\n\n"
            let bytes = utf8.GetBytes(joined)

            if bytes.Length <= maxBytes then
                Some joined
            else
                let markerBytes = utf8.GetByteCount(marker)
                let keepBytes = max 0 (maxBytes - markerBytes)
                let truncated = utf8.GetString(bytes, 0, keepBytes) + marker
                Some truncated

/// System prompt assembly
module SystemPrompt =

    /// Build the full system prompt from layers
    let build (profile: IProviderProfile) (env: IExecutionEnvironment) (userInstructions: string option) : string =
        let projectDocs = ProjectDocs.discover env.WorkingDirectory profile.Id
        profile.BuildSystemPrompt(env, projectDocs, userInstructions)
