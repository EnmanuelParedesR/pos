namespace Toki.Client.UI.Api;

/// <summary>A file the API answered (a report as CSV or Excel), ready to hand to the browser's download.</summary>
public sealed record FileDownload(byte[] Content, string FileName, string ContentType);
