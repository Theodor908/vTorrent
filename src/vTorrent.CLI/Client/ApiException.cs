// src/vTorrent.CLI/Client/ApiException.cs
using System;

namespace vTorrent.Cli.Client;

public class ApiException : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }

    public ApiException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
