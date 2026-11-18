using System.Diagnostics;
using System.Text.Json;
using WindowsReborn.Core.Compatibility;
using WindowsReborn.Core.Configuration;
using WindowsReborn.Core.Logging;
using WindowsReborn.Core.Paths;
using WindowsReborn.Storage;

namespace WindowsReborn.Backup;

/// <summary>
/// Motor de copias de seguridad: inventario → escritura en temporal (con SHA-256 al
/// vuelo) → cierre → verificación completa → renombrado final. Nunca devuelve
/// <see cref="BackupStatus.Success"/> si la verificación no ha pasado.
/// </summary>
public sealed class BackupEngine
{
    private const int LockRetries = 3;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(400);

    public async Task<BackupResult> RunAsync(BackupPlan plan, InventoryResult? inventory,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new BackupResult();
        IBackupWriter? writer = null;

        try
        {
            inventory ??= await Inventory.ScanAsync(plan.Sources, plan.Exclusions, plan.IncludeSensitive, progress, ct).ConfigureAwait(false);
            result.Report.Skipped.AddRange(inventory.Skipped);
            result.Report.Warnings.AddRange(inventory.Warnings);

            var validation = DestinationValidator.Validate(plan, inventory.TotalBytes);
            if (validation.Errors.Count > 0)
                throw new BackupValidationException(validation.Errors);
            result.Report.Warnings.AddRange(validation.Warnings);

            Log.Info($"Backup started: {inventory.Files.Count} files, {inventory.TotalBytes} bytes, format {plan.Format}");

            Directory.CreateDirectory(plan.DestinationFolder);
            writer = BackupWriterFactory.Create(plan.Format, plan.FinalPath, plan.Compression, plan.SevenZipPath, plan.Password);

            var manifest = CreateManifest(plan);
            var categoryIds = plan.Sources.Select(s => s.Id).ToList();
            long extrasBytes = plan.Extras.Sum(e => e.Content?.LongLength ?? SafeLength(e.SourcePath));
            long totalBytes = inventory.TotalBytes + extrasBytes;
            long doneBytes = 0;
            var filesTotal = inventory.Files.Count + plan.Extras.Count;
            var filesDone = 0;
            var lastReport = 0L;

            void Report(string? current, bool force = false)
            {
                var now = Environment.TickCount64;
                if (!force && now - lastReport < 100) return;
                lastReport = now;
                progress?.Report(new BackupProgress(BackupPhase.Writing, doneBytes, totalBytes, filesDone, filesTotal, current));
            }

            // 1. Datos del usuario.
            foreach (var item in inventory.Files)
            {
                ct.ThrowIfCancellationRequested();
                var source = plan.Sources[item.SourceIndex];
                var archivePath = $"{Sections.Data}/{source.Id}/{item.Relative}";
                var display = $"{source.Name}/{item.Relative}";
                Report(display);

                var stream = await OpenWithRetryAsync(item.FullPath, result.Report, ct).ConfigureAwait(false);
                if (stream is null)
                {
                    doneBytes += item.Size;
                    filesDone++;
                    continue;
                }

                long before = doneBytes;
                try
                {
                    await using (stream)
                    {
                        var (bytes, sha) = await writer.AddFileAsync(archivePath, stream, item.LastWriteUtc,
                            n => { doneBytes += n; Report(display); }, ct).ConfigureAwait(false);

                        manifest.Files.Add(new FileRecord
                        {
                            Path = archivePath,
                            Section = Sections.Data,
                            Category = source.Id,
                            Relative = item.Relative,
                            Size = bytes,
                            LastWriteUtc = item.LastWriteUtc,
                            Attributes = item.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive),
                            Sha256 = sha,
                        });
                        result.FilesWritten++;
                        result.BytesWritten += bytes;
                    }
                }
                catch (IOException ex) when (!IsDiskFull(ex) && ex is not EndOfStreamException && plan.Format == BackupFormat.Folder)
                {
                    // Error de lectura a mitad de archivo: en carpeta se puede omitir sin dañar el resto.
                    result.Report.Skipped.Add(new SkippedItem { Path = Log.P(item.FullPath), Reason = SkipReason.Error, Detail = ex.Message });
                }
                doneBytes = before + item.Size;
                filesDone++;
            }

            foreach (var (sourceIndex, relative) in inventory.EmptyDirectories)
            {
                var source = plan.Sources[sourceIndex];
                await writer.AddDirectoryAsync($"{Sections.Data}/{source.Id}/{relative}", ct).ConfigureAwait(false);
                manifest.EmptyDirectories.Add(new DirectoryRecord { Category = source.Id, Relative = relative });
            }

            // 2. Configuración de Windows Reborn, instaladores y listas de apps.
            foreach (var extra in plan.Extras)
            {
                ct.ThrowIfCancellationRequested();
                Report(extra.ArchivePath, force: true);
                Stream stream = extra.Content is not null
                    ? new MemoryStream(extra.Content, writable: false)
                    : new FileStream(extra.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                await using (stream)
                {
                    var lastWrite = extra.SourcePath is not null ? File.GetLastWriteTimeUtc(extra.SourcePath) : DateTime.UtcNow;
                    var (bytes, sha) = await writer.AddFileAsync(extra.ArchivePath, stream, lastWrite, n => { doneBytes += n; Report(extra.ArchivePath); }, ct).ConfigureAwait(false);
                    manifest.Files.Add(new FileRecord
                    {
                        Path = extra.ArchivePath,
                        Section = extra.Section,
                        Size = bytes,
                        LastWriteUtc = lastWrite,
                        Sha256 = sha,
                    });
                }
                filesDone++;
            }

            // 3. Estadísticas por categoría, informe y manifiesto (el manifiesto, al final).
            foreach (var category in manifest.Categories)
            {
                var files = manifest.Files.Where(f => f.Category == category.Id).ToList();
                category.FileCount = files.Count;
                category.Bytes = files.Sum(f => f.Size);
            }

            await AddJsonAsync(writer, BackupManifest.ReportFileName, result.Report, ct).ConfigureAwait(false);
            await AddJsonAsync(writer, BackupManifest.FileName, manifest, ct).ConfigureAwait(false);

            // 4. Cierre del archivo.
            progress?.Report(new BackupProgress(BackupPhase.Finalizing, totalBytes, totalBytes, filesDone, filesTotal, null));
            await writer.CompleteAsync(s => progress?.Report(new BackupProgress(BackupPhase.Finalizing, totalBytes, totalBytes, filesDone, filesTotal, s)), ct).ConfigureAwait(false);

            // 5. Verificación completa leyendo lo escrito.
            var verifyProgress = new Progress<VerificationProgress>(v =>
                progress?.Report(new BackupProgress(BackupPhase.Verifying, v.BytesDone, v.BytesTotal, v.FilesDone, v.FilesTotal, v.Current)));
            using (var reader = writer.OpenTempForVerification(plan.Password))
            {
                var readManifest = BackupReader.ReadManifest(reader);
                if (readManifest.Files.Count != manifest.Files.Count)
                    throw new InvalidDataException("Manifest mismatch after writing.");
                result.Verification = await BackupVerifier.VerifyAsync(reader, readManifest, null, verifyProgress, ct).ConfigureAwait(false);
            }

            if (!result.Verification.IsValid)
            {
                writer.Abort();
                result.Status = BackupStatus.Failed;
                result.Error = "verification-failed";
                Log.Error($"Backup verification failed: {result.Verification.HashMismatch.Count} mismatches, {result.Verification.Missing.Count} missing, {result.Verification.Unreadable.Count} unreadable");
                return result;
            }

            // 6. Finalización atómica.
            progress?.Report(new BackupProgress(BackupPhase.Committing, totalBytes, totalBytes, filesDone, filesTotal, null));
            if (File.Exists(plan.FinalPath) || Directory.Exists(plan.FinalPath))
                throw new IOException("destination-exists");
            writer.Commit();
            result.FinalPath = plan.FinalPath;
            result.ArchiveSize = plan.Format == BackupFormat.Folder ? result.BytesWritten : new FileInfo(plan.FinalPath).Length;
            result.Status = result.Report.Skipped.Any(s => s.Reason is not SkipReason.Excluded) ? BackupStatus.SuccessWithSkips : BackupStatus.Success;
            progress?.Report(new BackupProgress(BackupPhase.Done, totalBytes, totalBytes, filesDone, filesTotal, null));
            Log.Info($"Backup completed and verified: {result.FilesWritten} files, {result.Report.Skipped.Count} skipped");
            return result;
        }
        catch (OperationCanceledException)
        {
            writer?.Abort();
            result.Status = BackupStatus.Cancelled;
            Log.Warn("Backup cancelled by the user; temporary files removed");
            return result;
        }
        catch (BackupValidationException ex)
        {
            writer?.Abort();
            result.Status = BackupStatus.Failed;
            result.Error = string.Join(";", ex.Errors);
            return result;
        }
        catch (Exception ex)
        {
            writer?.Abort();
            result.Status = BackupStatus.Failed;
            result.Error = IsDiskFull(ex) ? "disk-full" : ex.Message;
            Log.Error("Backup failed", ex);
            return result;
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync().ConfigureAwait(false);
            result.Duration = sw.Elapsed;
        }
    }

    private static BackupManifest CreateManifest(BackupPlan plan)
    {
        var win = WindowsVersion.Current;
        return new BackupManifest
        {
            AppVersion = plan.AppVersion,
            CreatedUtc = DateTime.UtcNow,
            MachineName = plan.AnonymizeManifest ? null : Environment.MachineName,
            UserName = plan.AnonymizeManifest ? null : Environment.UserName,
            Windows = new WindowsInfo { ProductName = win.ProductName, DisplayVersion = win.DisplayVersion, Build = win.Build },
            Format = plan.Format.Id(),
            Encrypted = plan.Format == BackupFormat.SevenZip && !string.IsNullOrEmpty(plan.Password),
            Categories = plan.Sources.Select(s => new CategoryInfo
            {
                Id = s.Id,
                Name = s.Name,
                Root = s.Root,
                SubPath = s.SubPath,
                OriginalPath = plan.AnonymizeManifest ? null : s.AbsolutePath,
                Kind = s.Kind switch { SourceKind.CustomFolder => "customFolder", SourceKind.AppConfig => "appConfig", _ => "knownFolder" },
                Adapter = s.AdapterId,
                ProcessNames = s.ProcessNames.ToList(),
            }).ToList(),
        };
    }

    private static async Task AddJsonAsync<T>(IBackupWriter writer, string path, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        using var ms = new MemoryStream(bytes);
        await writer.AddFileAsync(path, ms, DateTime.UtcNow, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Abre un archivo permitiendo que otros lo tengan abierto. Si está bloqueado en
    /// exclusiva, reintenta unas pocas veces y después lo registra como omitido.
    /// </summary>
    private static async Task<Stream?> OpenWithRetryAsync(string path, CreationReport report, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);
            }
            catch (UnauthorizedAccessException)
            {
                report.Skipped.Add(new SkippedItem { Path = Log.P(path), Reason = SkipReason.AccessDenied });
                return null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                report.Skipped.Add(new SkippedItem { Path = Log.P(path), Reason = SkipReason.NotFound });
                return null;
            }
            catch (PathTooLongException)
            {
                report.Skipped.Add(new SkippedItem { Path = Log.P(path), Reason = SkipReason.PathTooLong });
                return null;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (attempt >= LockRetries)
                {
                    report.Skipped.Add(new SkippedItem { Path = Log.P(path), Reason = SkipReason.Locked });
                    return null;
                }
                await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                report.Skipped.Add(new SkippedItem { Path = Log.P(path), Reason = SkipReason.Error, Detail = ex.Message });
                return null;
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    internal static bool IsDiskFull(Exception ex) => ex is IOException io && (io.HResult & 0xFFFF) is 112 or 39;

    private static long SafeLength(string? path)
    {
        try { return path is null ? 0 : new FileInfo(path).Length; }
        catch { return 0; }
    }
}

public sealed class BackupValidationException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
