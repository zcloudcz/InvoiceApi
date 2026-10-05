namespace Fakvio.UI.Shared.Models;

/// <summary>
/// A file downloaded from the API together with the name the server chose for it.
/// The server builds document file names from the client's file name format
/// (DocumentFileName in Fakvio.Application), so the UI must use <see cref="FileName"/>
/// instead of composing names itself. <see cref="FileName"/> is null when the response
/// carried no usable Content-Disposition header — callers then supply a simple fallback.
/// </summary>
public sealed record DownloadedFile(byte[] Bytes, string? FileName);
