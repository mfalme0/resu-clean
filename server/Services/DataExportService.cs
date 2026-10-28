using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ResuClean.Data;
using ResuClean.Services.Providers;

namespace ResuClean.Services;

/// <summary>
/// Encrypted-at-rest export and import of the whole data directory, for backup and machine moves.
///
/// The export is a zip of everything under <see cref="Paths.DataDir"/> (the SQLite database,
/// machine.key, resumes, kits, uploads), encrypted with AES-256 using a key derived from a
/// password the user supplies. Nothing leaves the machine except the file the user chooses to
/// write, and nothing can be read back without that password.
///
/// On Windows, API keys inside the database are encrypted with DPAPI bound to the current user
/// account, so a backup restored on a different Windows account or machine will need those keys
/// re-entered. On macOS and Linux the machine.key is included in the backup, so the same machine
/// (or a copy of machine.key carried alongside) can decrypt them. Either way, the backup file
/// itself is unreadable without the export password.
/// </summary>
public sealed class DataExportService
{
    private const string BackupExtension = ".resubak";

    public sealed record ExportSummary(
        string FileName,
        long Bytes,
        int FilesIncluded);

    /// <summary>Builds an encrypted backup of the whole data directory and writes it to disk.</summary>
    public ExportSummary ExportToFile(string destinationPath, string password)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.");
        if (!destinationPath.EndsWith(BackupExtension, StringComparison.OrdinalIgnoreCase))
            destinationPath += BackupExtension;
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A password is required to encrypt the backup.");

        var dataDir = Paths.DataDir;
        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);

        // Stage 1: zip the data directory into memory.
        var zipBytes = ZipDirectory(dataDir);

        // Stage 2: derive a key from the password and encrypt the zip.
        var encrypted = Encrypt(zipBytes, password);

        File.WriteAllBytes(destinationPath, encrypted);

        var included = Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories).Length;
        return new ExportSummary(
            Path.GetFileName(destinationPath),
            encrypted.Length,
            included);
    }

    /// <summary>Decrypts and validates an encrypted backup file without writing anything.</summary>
    public sealed record Preview(
        int Files,
        long TotalBytes,
        string? DatabasePath,
        string? MachineKeyPath,
        bool LooksValid);

    public Preview PreviewBackup(string archivePath, string password)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Backup file not found: {archivePath}");

        var encrypted = File.ReadAllBytes(archivePath);
        byte[] zipBytes;
        try
        {
            zipBytes = Decrypt(encrypted, password);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The password is wrong, or this file is not a resu-clean backup.");
        }

        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var db = files.FirstOrDefault(e => e.Name.Equals("resu-clean.db", StringComparison.OrdinalIgnoreCase));
        var key = files.FirstOrDefault(e => e.Name.Equals("machine.key", StringComparison.OrdinalIgnoreCase));
        return new Preview(files.Count, zipBytes.Length, db?.FullName, key?.FullName, db is not null);
    }

    /// <summary>Decrypts a backup and restores it into the data directory, replacing existing files.</summary>
    public sealed record ImportResult(
        int FilesRestored,
        long BytesRestored,
        bool DatabaseReplaced);

    public ImportResult ImportFromFile(string archivePath, string password)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Backup file not found: {archivePath}");

        var encrypted = File.ReadAllBytes(archivePath);
        byte[] zipBytes;
        try
        {
            zipBytes = Decrypt(encrypted, password);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The password is wrong, or this file is not a resu-clean backup.");
        }

        // Validate before touching anything: the archive must contain the database.
        var tempDir = Path.Combine(Path.GetTempPath(), $"resu-clean-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            zip.ExtractToDirectory(tempDir, overwriteFiles: true);

            if (!File.Exists(Path.Combine(tempDir, "resu-clean.db")))
                throw new InvalidOperationException("This backup does not contain a resu-clean database and cannot be restored.");

            // Replace the live data directory. Clear it first so stale files from an older
            // backup do not linger, then copy everything in.
            if (Directory.Exists(Paths.DataDir))
                Directory.Delete(Paths.DataDir, recursive: true);
            Directory.CreateDirectory(Paths.DataDir);

            var restored = 0L;
            var fileCount = 0;
            foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(tempDir, file);
                var dest = Path.Combine(Paths.DataDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                fileCount++;
                restored += new FileInfo(dest).Length;
            }

            return new ImportResult(fileCount, restored, true);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- Internals -----------------------------------------------------------

    private static byte[] ZipDirectory(string dir)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(file);
                fileStream.CopyTo(entryStream);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Derives a 256-bit key from the password with PBKDF2 and encrypts with AES-256-CBC.</summary>
    private static byte[] Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var key = pbkdf2.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        var cipher = aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);

        // Format: [salt:16][iv:16][cipher:rest]
        var outBytes = new byte[16 + 16 + cipher.Length];
        Buffer.BlockCopy(salt, 0, outBytes, 0, 16);
        Buffer.BlockCopy(iv, 0, outBytes, 16, 16);
        Buffer.BlockCopy(cipher, 0, outBytes, 32, cipher.Length);
        return outBytes;
    }

    private static byte[] Decrypt(byte[] protectedBytes, string password)
    {
        if (protectedBytes.Length < 32)
            throw new CryptographicException("Backup file is too short to be valid.");

        var salt = new byte[16];
        var iv = new byte[16];
        var cipher = new byte[protectedBytes.Length - 32];
        Buffer.BlockCopy(protectedBytes, 0, salt, 0, 16);
        Buffer.BlockCopy(protectedBytes, 16, iv, 0, 16);
        Buffer.BlockCopy(protectedBytes, 32, cipher, 0, cipher.Length);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var key = pbkdf2.GetBytes(32);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        try
        {
            return aes.CreateDecryptor().TransformFinalBlock(cipher, 0, cipher.Length);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("Decryption failed: wrong password or corrupt file.");
        }
    }
}
