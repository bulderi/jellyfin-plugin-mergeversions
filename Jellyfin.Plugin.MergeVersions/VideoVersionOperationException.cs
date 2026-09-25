using System;

namespace Jellyfin.Plugin.MergeVersions;

internal sealed class VideoVersionOperationException : InvalidOperationException
{
    public VideoVersionOperationException(string operation, int? statusCode, string detail)
        : base(
            $"Jellyfin VideosController.{operation} failed: " +
            $"{statusCode?.ToString() ?? "unknown status"}. {detail}")
    {
        Operation = operation;
        StatusCode = statusCode;
        Detail = detail;
    }

    public string Operation { get; }

    public int? StatusCode { get; }

    public string Detail { get; }
}
