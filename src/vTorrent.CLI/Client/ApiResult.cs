// src/vTorrent.CLI/Client/ApiResult.cs
using System;

namespace vTorrent.Cli.Client;

public record ApiResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public int StatusCode { get; init; }

    public static ApiResult<T> Success(T data) => new() { IsSuccess = true, Data = data, StatusCode = 200 };
    public static ApiResult<T> Fail(string error, string errorCode = "UNKNOWN", int statusCode = 0)
        => new() { IsSuccess = false, Error = error, ErrorCode = errorCode, StatusCode = statusCode };
}
