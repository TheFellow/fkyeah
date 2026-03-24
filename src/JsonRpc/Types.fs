namespace JsonRpc

open System.Text.Json

type JsonRpcId =
    | StringId of string
    | NumberId of int

type JsonRpcRequest =
    { Id: JsonRpcId
      Method: string
      Params: JsonElement option }

type JsonRpcError =
    { Code: int
      Message: string
      Data: JsonElement option }

type JsonRpcMessage =
    | Response of id: JsonRpcId * result: Result<JsonElement, JsonRpcError>
    | Notification of method: string * parameters: JsonElement option
