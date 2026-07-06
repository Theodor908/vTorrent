using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using vTorrent.Server.Models;

namespace vTorrent.Server.Filters;

public class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        _logger.LogError(context.Exception, "Unhandled exception in {Action}", context.ActionDescriptor.DisplayName);

        var (statusCode, code) = context.Exception switch
        {
            KeyNotFoundException => (404, "RESOURCE_NOT_FOUND"),
            ArgumentException e when e.Message.Contains("magnet", StringComparison.OrdinalIgnoreCase)
                => (400, "INVALID_MAGNET"),
            ArgumentException e when e.Message.Contains("torrent file", StringComparison.OrdinalIgnoreCase)
                => (400, "INVALID_TORRENT_FILE"),
            ArgumentException e when e.Message.Contains("setting", StringComparison.OrdinalIgnoreCase)
                => (400, "INVALID_SETTINGS"),
            ArgumentException => (400, "VALIDATION_ERROR"),
            InvalidOperationException e when e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                => (409, "TORRENT_EXISTS"),
            InvalidOperationException e when e.Message.Contains("state", StringComparison.OrdinalIgnoreCase)
                => (409, "INVALID_STATE"),
            InvalidOperationException => (409, "INVALID_STATE"),
            IOException => (500, "DISK_ERROR"),
            _ => (500, "INTERNAL_ERROR")
        };

        context.Result = new ObjectResult(new ErrorResponse(context.Exception.Message, code))
        {
            StatusCode = statusCode
        };
        context.ExceptionHandled = true;
    }
}
