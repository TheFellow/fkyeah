namespace AcpRuntime

open System

[<CLIMutable>]
type ReadTextFileRequest =
    { Path: string }

[<CLIMutable>]
type ReadTextFileResult =
    { Path: string
      Content: string }

[<CLIMutable>]
type WriteTextFileRequest =
    { Path: string
      Content: string }

[<CLIMutable>]
type WriteTextFileResult =
    { Path: string
      BytesWritten: int }

[<CLIMutable>]
type TerminalCreateRequest =
    { Command: string
      Args: string list
      WorkingDirectory: string option
      Environment: Map<string, string> }

[<CLIMutable>]
type TerminalCreateResult =
    { TerminalId: string }

[<CLIMutable>]
type TerminalOutputRequest =
    { TerminalId: string
      MaxBytes: int option }

[<CLIMutable>]
type TerminalOutputResult =
    { Output: string
      Truncated: bool
      IsRunning: bool }

[<CLIMutable>]
type TerminalWaitForExitRequest =
    { TerminalId: string
      TimeoutMs: int option }

[<CLIMutable>]
type TerminalWaitForExitResult =
    { ExitCode: int option
      Output: string
      TimedOut: bool }

[<CLIMutable>]
type TerminalKillRequest =
    { TerminalId: string }

[<CLIMutable>]
type TerminalKillResult =
    { Killed: bool }

[<CLIMutable>]
type TerminalReleaseRequest =
    { TerminalId: string }

[<CLIMutable>]
type TerminalReleaseResult =
    { Released: bool }

[<CLIMutable>]
type PermissionRequest =
    { Operation: string
      Subject: string option
      Reason: string option }

[<CLIMutable>]
type PermissionResult =
    { Allowed: bool
      Reason: string option }

type AcpDelegate =
    { ReadTextFile: ReadTextFileRequest -> Async<Result<ReadTextFileResult, AcpError>>
      WriteTextFile: WriteTextFileRequest -> Async<Result<WriteTextFileResult, AcpError>>
      TerminalCreate: TerminalCreateRequest -> Async<Result<TerminalCreateResult, AcpError>>
      TerminalOutput: TerminalOutputRequest -> Async<Result<TerminalOutputResult, AcpError>>
      TerminalWaitForExit: TerminalWaitForExitRequest -> Async<Result<TerminalWaitForExitResult, AcpError>>
      TerminalKill: TerminalKillRequest -> Async<Result<TerminalKillResult, AcpError>>
      TerminalRelease: TerminalReleaseRequest -> Async<Result<TerminalReleaseResult, AcpError>>
      RequestPermission: PermissionRequest -> Async<Result<PermissionResult, AcpError>> }

[<RequireQualifiedAccess>]
module AcpDelegate =

    let private denied operation =
        async {
            return Error(AcpError.PermissionDenied $"Operation '{operation}' is denied")
        }

    let denyAll =
        { ReadTextFile = fun _ -> denied "filesystem/read_text_file"
          WriteTextFile = fun _ -> denied "filesystem/write_text_file"
          TerminalCreate = fun _ -> denied "terminal/create"
          TerminalOutput = fun _ -> denied "terminal/output"
          TerminalWaitForExit = fun _ -> denied "terminal/wait_for_exit"
          TerminalKill = fun _ -> denied "terminal/kill"
          TerminalRelease = fun _ -> denied "terminal/release"
          RequestPermission = fun _ -> denied "permissions/request" }
