using ResuClean.Services;

namespace ResuClean.Endpoints;

public static class DataEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/data").WithTags("Data");

        /// <summary>
        /// Exports the whole data directory as an encrypted .resubak file. Returns the file as a
        /// download. The file is encrypted with a password the user supplies; without it nothing
        /// inside can be read.
        /// </summary>
        group.MapPost("/export", (ExportRequest body, DataExportService export) =>
        {
            if (string.IsNullOrWhiteSpace(body.Destination))
                throw new ArgumentException("A destination path is required.");
            if (string.IsNullOrWhiteSpace(body.Password))
                throw new ArgumentException("A password is required to encrypt the backup.");

            var summary = export.ExportToFile(body.Destination, body.Password);
            return Api.Ok(new ExportResult(summary.FileName, summary.Bytes, summary.FilesIncluded));
        });

        /// <summary>
        /// Preview an encrypted backup: how many files, whether it contains a database, etc.
        /// Does not write anything.
        /// </summary>
        group.MapPost("/preview", (PreviewRequest body, DataExportService export) =>
        {
            if (string.IsNullOrWhiteSpace(body.ArchivePath))
                throw new ArgumentException("A backup file path is required.");
            if (string.IsNullOrWhiteSpace(body.Password))
                throw new ArgumentException("A password is required to open the backup.");

            var preview = export.PreviewBackup(body.ArchivePath, body.Password);
            return Api.Ok(new PreviewResult(preview.Files, preview.TotalBytes, preview.DatabasePath, preview.MachineKeyPath, preview.LooksValid));
        });

        /// <summary>
        /// Import an encrypted backup into the data directory, replacing existing files. The
        /// database file is swapped in place; the next request reads from the restored data.
        /// </summary>
        group.MapPost("/import", (ImportRequest body, DataExportService export) =>
        {
            if (string.IsNullOrWhiteSpace(body.ArchivePath))
                throw new ArgumentException("A backup file path is required.");
            if (string.IsNullOrWhiteSpace(body.Password))
                throw new ArgumentException("A password is required to open the backup.");

            var result = export.ImportFromFile(body.ArchivePath, body.Password);
            return Api.Ok(new ImportResult(result.FilesRestored, result.BytesRestored, result.DatabaseReplaced));
        });
    }

    public sealed record ExportRequest(string? Destination, string? Password);
    public sealed record ExportResult(string FileName, long Bytes, int FilesIncluded);

    public sealed record PreviewRequest(string? ArchivePath, string? Password);
    public sealed record PreviewResult(int Files, long TotalBytes, string? DatabasePath, string? MachineKeyPath, bool LooksValid);

    public sealed record ImportRequest(string? ArchivePath, string? Password);
    public sealed record ImportResult(int FilesRestored, long BytesRestored, bool DatabaseReplaced);
}
