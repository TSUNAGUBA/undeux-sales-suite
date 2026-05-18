using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UndeuxSales.Api.Configuration;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Core.Parsing;
using UndeuxSales.Infrastructure.Import;

namespace UndeuxSales.Api.Controllers;

/// <summary>週次CSVの取込と取込バッチ履歴を提供する。</summary>
[ApiController]
[Authorize]
[Route("api/imports")]
public sealed class ImportsController : ControllerBase
{
    // RequestSizeLimit 属性は定数のみ受け付ける。実効上限は ImportSettings で別途検証する。
    private const long HardSizeLimitBytes = 52_428_800; // 50 MB
    private const int DefaultHistoryLimit = 50;
    private const int MaxHistoryLimit = 200;
    private const int MaxReportedErrors = 100;

    private readonly SalesImportService _importService;
    private readonly ImportBatchRepository _batchRepository;
    private readonly ImportSettings _importSettings;
    private readonly ILogger<ImportsController> _logger;

    public ImportsController(
        SalesImportService importService,
        ImportBatchRepository batchRepository,
        IOptions<ImportSettings> importSettings,
        ILogger<ImportsController> logger)
    {
        _importService = importService;
        _batchRepository = batchRepository;
        _importSettings = importSettings.Value;
        _logger = logger;
    }

    /// <summary>取込バッチ履歴を新しい順に取得する。</summary>
    [HttpGet]
    public Task<IReadOnlyList<ImportBatchInfo>> List(
        [FromQuery] int limit, CancellationToken cancellationToken)
        => _batchRepository.ListRecentAsync(
            limit <= 0 ? DefaultHistoryLimit : Math.Min(limit, MaxHistoryLimit),
            cancellationToken);

    /// <summary>週次売上参照CSVを取り込む（全件検証後に一括取込。エラー行があれば取込中止）。</summary>
    [HttpPost]
    [RequestSizeLimit(HardSizeLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = HardSizeLimitBytes)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            throw new AppException(ErrorCodes.ImportFileMissing, 400);
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            throw new AppException(ErrorCodes.ImportFileMissing, 400);
        }

        if (file.Length > _importSettings.MaxFileSizeBytes)
        {
            throw new AppException(ErrorCodes.ImportFileTooLarge, 413,
                $"ファイルサイズ {file.Length:N0} バイトが上限 "
                + $"{_importSettings.MaxFileSizeBytes:N0} バイトを超えています。");
        }

        CsvParseResult parsed;
        using (var stream = file.OpenReadStream())
        using (var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            parsed = SalesCsvReader.Parse(reader);
        }

        if (parsed.HasErrors)
        {
            var details = parsed.Errors
                .Take(MaxReportedErrors)
                .Select(error => $"{error.RowNumber}行目: {error.Message}")
                .ToList();
            if (parsed.Errors.Count > MaxReportedErrors)
            {
                details.Add($"...ほか {parsed.Errors.Count - MaxReportedErrors} 件");
            }

            throw new AppException(ErrorCodes.ImportRowInvalid, 422,
                $"{parsed.Errors.Count} 件のエラー行があります。取込は実行されていません。", details);
        }

        var result = await _importService.ImportAsync(
            parsed.Records, ImportSourceType.WeeklyCsv, file.FileName, cancellationToken);

        _logger.LogInformation(
            "週次CSV取込が完了しました（バッチ #{BatchId}, {Rows} 行）。",
            result.BatchId, result.RowCount);

        return Ok(result);
    }
}
