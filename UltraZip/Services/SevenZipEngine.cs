using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UltraZip.Models;

namespace UltraZip.Services;

/// <summary>
/// 7-Zip 进程封装：list / extract、进度解析（-bsp1 -bso1）、
/// 按退出码 + 输出文本分类错误（需要密码 / 密码错误 / 缺卷 / 损坏）。
/// </summary>
public class SevenZipEngine
{
    public static string SevenZipPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "7z", "7z.exe");

    private readonly PasswordVault _vault;

    public SevenZipEngine(PasswordVault? vault = null)
    {
        _vault = vault ?? PasswordVault.Instance;
    }

    // ---------- 列表 ----------

    /// <summary>用 7z 最终探测文件类型；退出码非 0 或没有 Type 字段时返回 null。</summary>
    public async Task<string?> ProbeAsync(string archivePath, CancellationToken ct = default)
    {
        var args = $"l -slt -bse1 -sccUTF-8 \"{archivePath}\"";
        var (exit, stdout, _) = await RunAsync(args, null, ct);
        if (exit != 0) return null;
        var types = Regex.Matches(stdout, @"(?m)^Type = (.+)$");
        if (types.Count == 0) return null;
        return types[^1].Groups[1].Value.Trim();
    }

    /// <summary>列出全部归档条目；特殊 ZIP 直接读取内置布局，其余使用 7z -slt。</summary>
    public async Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(string archivePath,
        string? password = null, CancellationToken ct = default)
    {
        var specialEntries = ZipAesEngine.ListEntries(archivePath);
        if (specialEntries != null) return specialEntries;
        var args = new StringBuilder("l -slt -bse1 -sccUTF-8 ");
        AppendPassword(args, password);
        args.Append($" \"{archivePath}\"");
        var (exit, stdout, stderr) = await RunAsync(args.ToString(), null, ct);
        if (exit != 0)
            throw new InvalidDataException(SummarizeError(stdout + "\n" + stderr));
        return ParseArchiveEntries(stdout);
    }

    internal static List<ArchiveEntry> ParseArchiveEntries(string output)
    {
        var entries = new List<ArchiveEntry>();
        ArchiveEntry? current = null;
        bool entriesStarted = false;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("----------", StringComparison.Ordinal))
            {
                entriesStarted = true;
                continue;
            }
            if (!entriesStarted) continue;
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                if (current != null) entries.Add(current);
                current = new ArchiveEntry { Path = line[7..] };
                continue;
            }
            if (current == null) continue;
            int separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator <= 0) continue;
            string key = line[..separator];
            string value = line[(separator + 3)..].Trim();
            switch (key)
            {
                case "Size":
                    if (ulong.TryParse(value, out ulong size)) current.Size = size;
                    break;
                case "Packed Size":
                    if (ulong.TryParse(value, out ulong packed)) current.PackedSize = packed;
                    break;
                case "Modified":
                    if (DateTime.TryParse(value, out var modified)) current.Modified = modified;
                    break;
                case "CRC":
                    current.Crc = value;
                    break;
                case "Method":
                    current.Method = value;
                    break;
                case "Folder":
                    current.IsDirectory = value == "+";
                    break;
            }
        }
        if (current != null) entries.Add(current);
        foreach (var entry in entries)
            entry.IsDirectory |= entry.Path.EndsWith('/') || entry.Path.EndsWith('\\');
        return entries;
    }

    /// <summary>读取压缩包概要（条目数、是否加密）。加密头的包需要密码才能读取。</summary>
    public async Task<ArchiveSummary> GetSummaryAsync(string archivePath, string? password = null,
        CancellationToken ct = default)
    {
        var specialSummary = ZipAesEngine.GetSummary(archivePath);
        if (specialSummary != null) return specialSummary;

        var summary = new ArchiveSummary();
        var args = new StringBuilder("l -slt -bse1 ");
        AppendPassword(args, password);
        args.Append($" \"{archivePath}\"");

        var (exit, stdout, stderr) = await RunAsync(args.ToString(), null, ct);
        var combined = stdout + "\n" + stderr;
        if (combined.Contains("Enter password", StringComparison.OrdinalIgnoreCase))
        {
            summary.IsEncrypted = true;
            return summary;
        }

        // 首个 "Path = " 是压缩包自身，之后每个块是一个条目
        var matches = Regex.Matches(stdout, @"(?m)^Path = ");
        summary.EntryCount = Math.Max(0, matches.Count - 1);
        summary.IsEncrypted = Regex.IsMatch(stdout, @"(?m)^Encrypted = \+");
        var type = Regex.Match(stdout, @"(?m)^Type = (.+)$");
        if (type.Success) summary.TypeName = type.Groups[1].Value.Trim();
        _ = exit;
        return summary;
    }

    // ---------- 解压 ----------

    /// <summary>单次解压尝试。</summary>
    public async Task<ExtractResult> ExtractAsync(string archivePath, string destDir, string? password,
        IProgress<ExtractProgress>? progress = null, CancellationToken ct = default)
    {
        var specialResult = await ZipAesEngine.ExtractAsync(archivePath, destDir, password, progress, ct);
        if (specialResult != null) return specialResult;

        Directory.CreateDirectory(destDir);
        var args = new StringBuilder($"x \"{archivePath}\" -o\"{destDir}\" -y -bsp1 -bso1 -bse1 -sccUTF-8");
        AppendPassword(args, password);

        var progressSink = new Progress<string>(line => ReportProgress(line, progress));

        int exit;
        string stdout, stderr;
        try
        {
            (exit, stdout, stderr) = await RunAsync(args.ToString(), progressSink, ct);
        }
        catch (OperationCanceledException)
        {
            return ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
        }

        // -bse1 已把错误并入 stdout；stderr 兜底（个别版本/场景仍走 stderr）
        var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
        if (exit == 0 && text.Contains("Everything is Ok"))
            return ExtractResult.Ok(password);

        return ExtractResult.Fail(Classify(text, password), SummarizeError(text));
    }

    /// <summary>
    /// 带密码处理的解压：先试无密码，遇加密则自动依次尝试密码库中的全部密码。
    /// 全部失败时返回 NeedsPassword/WrongPassword，由 UI 弹窗询问。
    /// </summary>
    public async Task<ExtractResult> ExtractWithVaultAsync(string archivePath, string destDir,
        IProgress<ExtractProgress>? progress = null, CancellationToken ct = default,
        Func<Task<string?>>? askPassword = null)
    {
        var result = await ExtractAsync(archivePath, destDir, null, progress, ct);
        if (result.Success || result.ErrorKind is ExtractErrorKind.Canceled
            or ExtractErrorKind.MissingVolume or ExtractErrorKind.Corrupt)
            return result;

        // 需要密码：自动尝试密码库
        foreach (var entry in _vault.GetCandidatesOrdered())
        {
            ct.ThrowIfCancellationRequested();
            result = await ExtractAsync(archivePath, destDir, entry.Password, progress, ct);
            if (result.Success)
            {
                _vault.RecordHit(entry.Password);
                return result;
            }
            if (result.ErrorKind != ExtractErrorKind.WrongPassword &&
                result.ErrorKind != ExtractErrorKind.NeedsPassword)
                return result;
        }

        // 询问用户（可多次重试，由调用方循环）
        if (askPassword != null)
        {
            var pwd = await askPassword();
            if (pwd == null)
                return ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
            result = await ExtractAsync(archivePath, destDir, pwd, progress, ct);
        }
        return result;
    }

    /// <summary>只解压指定条目，目录条目会包含其子路径。</summary>
    public async Task<ExtractResult> ExtractEntriesAsync(string archivePath, string destDir,
        IReadOnlyList<string> paths, string? password, IProgress<ExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (paths.Count == 0) return ExtractResult.Ok(password);
        var specialResult = await ZipAesEngine.ExtractAsync(archivePath, destDir, password, progress, ct, paths);
        if (specialResult != null) return specialResult;

        Directory.CreateDirectory(destDir);
        var args = new StringBuilder($"x \"{archivePath}\" -o\"{destDir}\" -y -bsp1 -bso1 -bse1 -sccUTF-8");
        AppendPassword(args, password);
        foreach (string path in paths) args.Append($" \"{path}\"");
        var progressSink = new Progress<string>(line => ReportProgress(line, progress));
        try
        {
            var (exit, stdout, stderr) = await RunAsync(args.ToString(), progressSink, ct);
            string text = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
            if (exit == 0 && text.Contains("Everything is Ok", StringComparison.OrdinalIgnoreCase))
                return ExtractResult.Ok(password);
            return ExtractResult.Fail(Classify(text, password), SummarizeError(text));
        }
        catch (OperationCanceledException)
        {
            return ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
        }
    }

    public async Task<ExtractResult> ExtractEntriesWithVaultAsync(string archivePath, string destDir,
        IReadOnlyList<string> paths, IProgress<ExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = await ExtractEntriesAsync(archivePath, destDir, paths, null, progress, ct);
        if (result.Success || result.ErrorKind is ExtractErrorKind.Canceled
            or ExtractErrorKind.MissingVolume or ExtractErrorKind.Corrupt)
            return result;
        foreach (var entry in _vault.GetCandidatesOrdered())
        {
            ct.ThrowIfCancellationRequested();
            result = await ExtractEntriesAsync(archivePath, destDir, paths, entry.Password, progress, ct);
            if (result.Success)
            {
                _vault.RecordHit(entry.Password);
                return result;
            }
            if (result.ErrorKind is not (ExtractErrorKind.WrongPassword or ExtractErrorKind.NeedsPassword))
                return result;
        }
        return result;
    }

    // ---------- 完整性测试 ----------

    public async Task<ExtractResult> TestAsync(string archivePath, string? password,
        IProgress<ExtractProgress>? progress = null, CancellationToken ct = default)
    {
        if (ZipAesEngine.GetSummary(archivePath) != null)
        {
            string? tempDir = null;
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(), "UltraZip", $"test_{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(tempDir);
                }
                catch (UnauthorizedAccessException)
                {
                    string? parent = Path.GetDirectoryName(Path.GetFullPath(archivePath));
                    if (parent == null) throw;
                    tempDir = Path.Combine(parent, $".ultrazip-test_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);
                }
                return await ExtractAsync(archivePath, tempDir, password, progress, ct);
            }
            finally
            {
                if (tempDir != null)
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        var args = new StringBuilder($"t \"{archivePath}\" -bsp1 -bso1 -bse1 -sccUTF-8 -y");
        AppendPassword(args, password);
        var progressSink = new Progress<string>(line => ReportProgress(line, progress));
        try
        {
            var (exit, stdout, stderr) = await RunAsync(args.ToString(), progressSink, ct);
            string text = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
            if (exit == 0 && text.Contains("Everything is Ok", StringComparison.OrdinalIgnoreCase))
                return ExtractResult.Ok(password);
            return ExtractResult.Fail(Classify(text, password), SummarizeError(text));
        }
        catch (OperationCanceledException)
        {
            return ExtractResult.Fail(ExtractErrorKind.Canceled, "已取消");
        }
    }

    public async Task<ExtractResult> TestWithVaultAsync(string archivePath,
        IProgress<ExtractProgress>? progress = null, CancellationToken ct = default)
    {
        var result = await TestAsync(archivePath, null, progress, ct);
        if (result.Success || result.ErrorKind is ExtractErrorKind.Canceled
            or ExtractErrorKind.MissingVolume or ExtractErrorKind.Corrupt)
            return result;
        foreach (var entry in _vault.GetCandidatesOrdered())
        {
            ct.ThrowIfCancellationRequested();
            result = await TestAsync(archivePath, entry.Password, progress, ct);
            if (result.Success)
            {
                _vault.RecordHit(entry.Password);
                return result;
            }
            if (result.ErrorKind is not (ExtractErrorKind.WrongPassword or ExtractErrorKind.NeedsPassword))
                return result;
        }
        return result;
    }

    // ---------- 创建 ----------

    /// <summary>创建压缩包：临时目录写入，校验通过后才覆盖目标，失败或取消清理临时产物。</summary>
    public async Task<CreateResult> CreateAsync(IReadOnlyList<string> sources, string destPath,
        CreateOptions options, IProgress<ExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        string outputPath = "";
        string? stagingDir = null;
        try
        {
            ValidateCreateRequest(sources, destPath, options);
            outputPath = Path.GetFullPath(destPath);
            var fullSources = sources.Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string workingDirectory = FindCommonParent(fullSources);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDirectory))
                throw new ArgumentException("输出路径无效。");
            Directory.CreateDirectory(outputDirectory);

            string stagingName = $"pack_{Guid.NewGuid():N}";
            stagingDir = Path.Combine(Path.GetTempPath(), "UltraZip", stagingName);
            try
            {
                Directory.CreateDirectory(stagingDir);
            }
            catch (UnauthorizedAccessException)
            {
                stagingName = ".ultrazip-" + stagingName;
                stagingDir = Path.Combine(outputDirectory, stagingName);
                Directory.CreateDirectory(stagingDir);
                options.Exclusions.Add(stagingName);
            }
            string stagedBase = Path.Combine(stagingDir, Path.GetFileName(outputPath));

            if (IsTarCompressed(options.Format))
            {
                string tarPath = Path.Combine(stagingDir, "payload.tar");
                var tarOptions = new CreateOptions
                {
                    Format = CreateFormat.Tar,
                    Level = 0,
                    VerifyAfterCreate = false,
                    Exclusions = new List<string>(options.Exclusions)
                };
                await RunCreateStageAsync(fullSources, workingDirectory, tarPath, tarOptions, progress, ct);
                if (options.VerifyAfterCreate)
                    await VerifyCreatedArchiveAsync(tarPath, null, progress, ct);

                var outerOptions = new CreateOptions
                {
                    Format = OuterCompressionFormat(options.Format),
                    Level = options.Level,
                    VerifyAfterCreate = false
                };
                await RunCreateStageAsync(new[] { tarPath }, stagingDir, stagedBase,
                    outerOptions, progress, ct);
            }
            else
            {
                await RunCreateStageAsync(fullSources, workingDirectory, stagedBase, options, progress, ct);
            }

            ct.ThrowIfCancellationRequested();
            var stagedFiles = GetStagedOutputs(stagedBase, options.VolumeSize);
            if (stagedFiles.Count == 0)
                throw new InvalidDataException("7-Zip 未生成预期的输出文件。");
            if (options.VerifyAfterCreate)
                await VerifyCreatedArchiveAsync(stagedFiles[0], options.Password, progress, ct);

            ct.ThrowIfCancellationRequested();
            var outputFiles = PublishOutputs(stagedFiles, outputPath, options.VolumeSize);
            return CreateResult.Ok(outputPath, outputFiles);
        }
        catch (OperationCanceledException)
        {
            return CreateResult.Fail(ExtractErrorKind.Canceled, "已取消", outputPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException)
        {
            return CreateResult.Fail(ExtractErrorKind.Other, ex.Message, outputPath);
        }
        finally
        {
            if (stagingDir != null)
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            }
        }
    }

    internal static string BuildCreateArgs(IReadOnlyList<string> sources, string workingDirectory,
        string archivePath, CreateOptions options)
    {
        var args = new StringBuilder($"a {GetTypeSwitch(options.Format)} \"{archivePath}\"");
        foreach (var source in sources)
            args.Append($" \"{Path.GetRelativePath(workingDirectory, source)}\"");
        args.Append($" -mx={options.Level} -mmt=on -bsp1 -bso1 -bse1 -sccUTF-8 -y");
        AppendPassword(args, options.Password);
        if (options.Format == CreateFormat.SevenZip && options.EncryptHeaders &&
            !string.IsNullOrEmpty(options.Password))
            args.Append(" -mhe=on");
        if (options.Format == CreateFormat.Zip && !string.IsNullOrEmpty(options.Password))
            args.Append(" -mem=AES256");
        if (!string.IsNullOrWhiteSpace(options.VolumeSize))
            args.Append($" -v{options.VolumeSize.ToLowerInvariant()}");
        foreach (var exclusion in options.Exclusions)
            args.Append($" -x!\"{exclusion}\"");
        return args.ToString();
    }

    private static async Task RunCreateStageAsync(IReadOnlyList<string> sources, string workingDirectory,
        string archivePath, CreateOptions options, IProgress<ExtractProgress>? progress,
        CancellationToken ct)
    {
        string arguments = BuildCreateArgs(sources, workingDirectory, archivePath, options);
        var progressSink = new Progress<string>(line => ReportProgress(line, progress));
        var (exit, stdout, stderr) = await RunAsync(arguments, progressSink, ct, workingDirectory);
        string text = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
        if (exit != 0 || !text.Contains("Everything is Ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("压缩创建失败：" + SummarizeError(text));
    }

    private static async Task VerifyCreatedArchiveAsync(string archivePath, string? password,
        IProgress<ExtractProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ExtractProgress { Percent = 100, CurrentFile = "正在校验压缩包…" });
        var args = new StringBuilder($"t \"{archivePath}\" -bsp1 -bso1 -bse1 -sccUTF-8 -y");
        AppendPassword(args, password);
        var progressSink = new Progress<string>(line => ReportProgress(line, progress));
        var (exit, stdout, stderr) = await RunAsync(args.ToString(), progressSink, ct);
        string text = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
        if (exit != 0 || !text.Contains("Everything is Ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("压缩包校验失败：" + SummarizeError(text));
    }

    private static List<string> GetStagedOutputs(string stagedBase, string? volumeSize)
    {
        if (string.IsNullOrWhiteSpace(volumeSize))
            return File.Exists(stagedBase) ? new List<string> { stagedBase } : new List<string>();
        string? directory = Path.GetDirectoryName(stagedBase);
        string fileName = Path.GetFileName(stagedBase);
        if (directory == null) return new List<string>();
        return Directory.EnumerateFiles(directory, fileName + ".*")
            .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^\.\d{3}$"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> PublishOutputs(IReadOnlyList<string> stagedFiles, string outputPath,
        string? volumeSize)
    {
        bool volumes = !string.IsNullOrWhiteSpace(volumeSize);
        var finalFiles = stagedFiles.Select(stage => volumes
            ? outputPath + Path.GetExtension(stage)
            : outputPath).ToList();
        string? directory = Path.GetDirectoryName(outputPath);
        if (directory == null) throw new ArgumentException("输出路径无效。");

        if (File.Exists(outputPath)) File.Delete(outputPath);
        if (volumes)
        {
            string fileName = Path.GetFileName(outputPath);
            foreach (var oldVolume in Directory.EnumerateFiles(directory, fileName + ".*")
                         .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^\.\d{3}$")))
                File.Delete(oldVolume);
        }

        try
        {
            for (int i = 0; i < stagedFiles.Count; i++)
            {
                File.Copy(stagedFiles[i], finalFiles[i]);
                File.Delete(stagedFiles[i]);
            }
            return finalFiles;
        }
        catch
        {
            foreach (var file in finalFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            throw;
        }
    }

    private static void ValidateCreateRequest(IReadOnlyList<string> sources, string destPath,
        CreateOptions options)
    {
        if (sources.Count == 0) throw new ArgumentException("请至少添加一个源文件或文件夹。");
        if (string.IsNullOrWhiteSpace(destPath)) throw new ArgumentException("请选择输出路径。");
        if (string.IsNullOrEmpty(Path.GetFileName(destPath)))
            throw new ArgumentException("输出路径必须包含文件名。");
        if (!Enum.IsDefined(options.Format)) throw new ArgumentException("不支持的创建格式。");
        if (options.Level is not (0 or 1 or 3 or 5 or 7 or 9))
            throw new ArgumentException("压缩级别必须是 0、1、3、5、7 或 9。");
        foreach (var source in sources)
        {
            if (!File.Exists(source) && !Directory.Exists(source))
                throw new FileNotFoundException("源文件或文件夹不存在：" + source);
            if (Directory.Exists(source) && string.IsNullOrEmpty(Path.GetFileName(
                    Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar))))
                throw new ArgumentException("不能直接压缩整个驱动器，请选择其中的文件夹。");
        }

        bool singleFile = options.Format is CreateFormat.GZip or CreateFormat.BZip2 or CreateFormat.Xz;
        if (singleFile && (sources.Count != 1 || !File.Exists(sources[0])))
            throw new ArgumentException("GZ、BZ2、XZ 只能压缩单个文件；多文件或文件夹请使用 TAR 组合格式。");
        bool canEncrypt = options.Format is CreateFormat.SevenZip or CreateFormat.Zip;
        if (!canEncrypt && !string.IsNullOrEmpty(options.Password))
            throw new ArgumentException("只有 7Z 和 ZIP 支持密码加密。");
        if (!string.IsNullOrWhiteSpace(options.VolumeSize))
        {
            if (options.Format is not (CreateFormat.SevenZip or CreateFormat.Zip))
                throw new ArgumentException("分卷创建仅支持 7Z 和 ZIP。");
            if (!Regex.IsMatch(options.VolumeSize, @"^[1-9]\d*[bkmg]$", RegexOptions.IgnoreCase))
                throw new ArgumentException("分卷大小无效，请使用如 100m、1g 的格式。");
        }
    }

    private static string FindCommonParent(IReadOnlyList<string> sources)
    {
        string? common = null;
        foreach (var source in sources)
        {
            string? parent = Directory.Exists(source)
                ? Directory.GetParent(source)?.FullName
                : Path.GetDirectoryName(source);
            if (parent == null) throw new ArgumentException("无法确定源文件的公共父目录：" + source);
            if (common == null)
            {
                common = parent;
                continue;
            }
            while (common != null)
            {
                string relative = Path.GetRelativePath(common, parent);
                bool contains = relative == "." ||
                    (!Path.IsPathRooted(relative) && relative != ".." &&
                     !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                if (contains) break;
                common = Directory.GetParent(common)?.FullName;
            }
            if (common == null)
                throw new ArgumentException("所有源文件和文件夹必须位于同一驱动器。");
        }
        return common ?? throw new ArgumentException("无法确定源文件的公共父目录。");
    }

    private static bool IsTarCompressed(CreateFormat format) =>
        format is CreateFormat.TarGZip or CreateFormat.TarBZip2 or CreateFormat.TarXz;

    private static CreateFormat OuterCompressionFormat(CreateFormat format) => format switch
    {
        CreateFormat.TarGZip => CreateFormat.GZip,
        CreateFormat.TarBZip2 => CreateFormat.BZip2,
        CreateFormat.TarXz => CreateFormat.Xz,
        _ => throw new ArgumentException("不是 TAR 组合格式。")
    };

    private static string GetTypeSwitch(CreateFormat format) => format switch
    {
        CreateFormat.SevenZip => "-t7z",
        CreateFormat.Zip => "-tzip",
        CreateFormat.Tar => "-ttar",
        CreateFormat.GZip or CreateFormat.TarGZip => "-tgzip",
        CreateFormat.BZip2 or CreateFormat.TarBZip2 => "-tbzip2",
        CreateFormat.Xz or CreateFormat.TarXz => "-txz",
        _ => throw new ArgumentException("不支持的创建格式。")
    };

    // ---------- 进程与解析 ----------

    private static void AppendPassword(StringBuilder args, string? password)
    {
        if (!string.IsNullOrEmpty(password))
            args.Append($" -p\"{password}\"");
    }

    /// <summary>
    /// 启动 7z。stdout 逐行（含 \r 分隔的进度段）回调给 sink。stdin 立即关闭，
    /// 使 7z 遇到 "Enter password" 提示时立即退出而不是挂起。
    /// </summary>
    private static async Task<(int exit, string stdout, string stderr)> RunAsync(
        string arguments, IProgress<string>? lineSink, CancellationToken ct,
        string? workingDirectory = null)
    {
        if (!File.Exists(SevenZipPath))
            throw new FileNotFoundException("未找到 7z.exe：" + SevenZipPath);

        var psi = new ProcessStartInfo
        {
            FileName = SevenZipPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();

        var stdoutAll = new StringBuilder();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // 7z 进度用 \r 刷新，不按行输出；按 \r 和 \n 切段回调
        var buffer = new char[1024];
        var segment = new StringBuilder();
        while (true)
        {
            int n;
            try
            {
                n = await proc.StandardOutput.ReadAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* 忽略 */ }
                throw;
            }
            if (n == 0) break;
            for (int i = 0; i < n; i++)
            {
                char c = buffer[i];
                if (c == '\r' || c == '\n')
                {
                    if (segment.Length > 0)
                    {
                        var s = segment.ToString();
                        stdoutAll.AppendLine(s);
                        lineSink?.Report(s);
                        segment.Clear();
                    }
                }
                else segment.Append(c);
            }
        }
        if (segment.Length > 0)
        {
            var s = segment.ToString();
            stdoutAll.AppendLine(s);
            lineSink?.Report(s);
        }

        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        return (proc.ExitCode, stdoutAll.ToString(), stderr);
    }

    private static readonly Regex PercentRegex = new(@"(?<!\d)(\d{1,3})%", RegexOptions.Compiled);

    private static void ReportProgress(string segment, IProgress<ExtractProgress>? progress)
    {
        if (progress == null) return;
        // 一个段内可能有多个百分比（\r 刷新被合并时），取最后一个
        var matches = PercentRegex.Matches(segment);
        var m = matches.Count > 0 ? matches[^1] : null;
        if (m != null && int.TryParse(m.Groups[1].Value, out var pct) && pct <= 100)
        {
            // 段内可能带当前文件名：" 12% 5 - folder\file.txt"
            string file = "";
            var fm = Regex.Match(segment, @"\d+%\s+\d+\s+-?\s*(?<f>.+)$");
            if (fm.Success) file = fm.Groups["f"].Value.Trim();
            progress.Report(new ExtractProgress { Percent = pct, CurrentFile = file });
        }
        else if (segment.StartsWith("- "))
        {
            progress.Report(new ExtractProgress { Percent = -1, CurrentFile = segment[2..].Trim() });
        }
    }

    /// <summary>按输出文本 + 是否带密码分类错误。</summary>
    internal static ExtractErrorKind Classify(string output, string? password)
    {
        bool hasPassword = !string.IsNullOrEmpty(password);

        if (output.Contains("Enter password", StringComparison.OrdinalIgnoreCase))
            return ExtractErrorKind.NeedsPassword;
        if (output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Data Error in encrypted file", StringComparison.OrdinalIgnoreCase))
            return ExtractErrorKind.WrongPassword;
        if (output.Contains("Missing volume", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Unexpected end of archive", StringComparison.OrdinalIgnoreCase))
            return ExtractErrorKind.MissingVolume;
        if (output.Contains("Cannot open encrypted archive", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase))
            return hasPassword ? ExtractErrorKind.WrongPassword : ExtractErrorKind.NeedsPassword;
        if (output.Contains("Cannot open the file as archive", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Can not open the file as archive", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("is not archive", StringComparison.OrdinalIgnoreCase))
            return ExtractErrorKind.Corrupt;
        if (output.Contains("Headers Error", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Data Error", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("CRC Failed", StringComparison.OrdinalIgnoreCase))
            return hasPassword ? ExtractErrorKind.WrongPassword : ExtractErrorKind.Corrupt;
        return ExtractErrorKind.Other;
    }

    internal static string SummarizeError(string output)
    {
        // 挑出有信息量的行给用户看
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Error", StringComparison.Ordinal) ||
                        l.Contains("Cannot", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Can not", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Missing volume", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Unexpected end", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Take(5)
            .ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "7-Zip 解压失败";
    }
}
