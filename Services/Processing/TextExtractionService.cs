using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using ZenRead.Entities;
using ZenRead.Services.Ai;
using ZenRead.Services.Summarization;

namespace ZenRead.Services.Processing;

public partial class TextExtractionService : ITextExtractionService
{
    private const int MinimumReadableCharacters = 80;
    private const double MinimumReadableWordRatio = 0.45;
    private const double MaximumControlCharacterRatio = 0.01;
    private const double MaximumReplacementCharacterRatio = 0.01;
    private const int MaximumDirectOcrPages = 40;
    private const int MinimumAveragePdfCharactersPerPage = 80;
    private const double MinimumReadablePdfPageRatio = 0.2;
    private const int MaxOcrProviderRetries = 4;

    private readonly IWebHostEnvironment _environment;
    private readonly HttpClient _httpClient;
    private readonly IAiModelRouter _modelRouter;
    private readonly AiSummarizationOptions _options;

    public TextExtractionService(
        IWebHostEnvironment environment,
        HttpClient httpClient,
        IAiModelRouter modelRouter,
        IOptions<AiSummarizationOptions> options)
    {
        _environment = environment;
        _httpClient = httpClient;
        _modelRouter = modelRouter;
        _options = options.Value;
    }

    public async Task<string> ExtractAsync(BookFile file, CancellationToken cancellationToken)
    {
        var physicalPath = Path.Combine(_environment.ContentRootPath, file.StoredFilePath);
        if (!File.Exists(physicalPath))
        {
            throw new FileNotFoundException("Không tìm thấy file sách đã upload.", physicalPath);
        }

        var extractedText = file.FileType switch
        {
            BookFileType.Txt => await ExtractTxtAsync(physicalPath, cancellationToken),
            BookFileType.Epub => await ExtractEpubAsync(physicalPath, cancellationToken),
            BookFileType.Pdf => await ExtractPdfAsync(physicalPath, cancellationToken),
            _ => throw new NotSupportedException("Định dạng file này chưa được hỗ trợ để trích xuất nội dung.")
        };

        if (!LooksLikeReadableText(extractedText))
        {
            throw new InvalidOperationException("File đã upload không có nội dung chữ đọc được. Nếu đây là PDF scan ảnh hoặc file bị mã hóa, LemonInk cần OCR để đọc trực tiếp.");
        }

        return extractedText;
    }

    private static async Task<string> ExtractTxtAsync(string physicalPath, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(physicalPath, Encoding.UTF8, cancellationToken);
        return NormalizeText(content);
    }

    private static async Task<string> ExtractEpubAsync(string physicalPath, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        using var archive = ZipFile.OpenRead(physicalPath);
        var entries = archive.Entries
            .Where(entry => IsEpubContentEntry(entry.FullName))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var markup = await reader.ReadToEndAsync(cancellationToken);
            var text = StripMarkup(markup);

            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine(text);
                builder.AppendLine();
            }
        }

        return NormalizeText(builder.ToString());
    }

    private async Task<string> ExtractPdfAsync(string physicalPath, CancellationToken cancellationToken)
    {
        var parsed = ExtractPdfWithParser(physicalPath);
        if (HasUsablePdfCoverage(parsed))
        {
            return parsed.Text;
        }

        if (parsed.PageCount > MaximumDirectOcrPages)
        {
            throw new InvalidOperationException(
                $"PDF có {parsed.PageCount} trang nhưng LemonInk chỉ đọc được {parsed.ReadablePageCount} trang chữ ({parsed.Text.Length:N0} ký tự). " +
                "LemonInk đã dừng trước khi tạo bản tóm tắt thiếu nội dung. Hãy upload PDF có thể bôi đen/copy chữ hoặc chia tài liệu scan thành các phần nhỏ để OCR.");
        }

        var ocrText = await ExtractPdfWithGeminiOcrAsync(physicalPath, cancellationToken);
        if (LooksLikeReadableText(ocrText) &&
            (parsed.PageCount == 0 || ocrText.Length >= ResolveMinimumPdfCharacters(parsed.PageCount)))
        {
            return ocrText;
        }

        throw new InvalidOperationException(
            "OCR đã chạy nhưng nội dung đọc được chưa đủ độ phủ để tạo bản tóm tắt đáng tin cậy. File có thể quá mờ, xoay trang sai hướng, hoặc cần chia nhỏ trước khi OCR.");
    }

    private static PdfExtractionResult ExtractPdfWithParser(string physicalPath)
    {
        try
        {
            using var document = PdfDocument.Open(physicalPath);
            var builder = new StringBuilder();
            var readablePages = 0;

            foreach (var page in document.GetPages())
            {
                var text = NormalizeText(ContentOrderTextExtractor.GetText(page));
                if (LooksLikeReadableText(text))
                {
                    readablePages++;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text);
                    builder.AppendLine();
                }
            }

            return new PdfExtractionResult(
                document.NumberOfPages,
                readablePages,
                NormalizeText(builder.ToString()));
        }
        catch
        {
            return new PdfExtractionResult(0, 0, string.Empty);
        }
    }

    private static bool HasUsablePdfCoverage(PdfExtractionResult result)
    {
        if (result.PageCount == 0 || !LooksLikeReadableText(result.Text))
        {
            return false;
        }

        if (result.PageCount <= 5)
        {
            return true;
        }

        var requiredReadablePages = (int)Math.Ceiling(result.PageCount * MinimumReadablePdfPageRatio);
        return result.ReadablePageCount >= requiredReadablePages &&
            result.Text.Length >= ResolveMinimumPdfCharacters(result.PageCount);
    }

    private static int ResolveMinimumPdfCharacters(int pageCount)
    {
        return Math.Max(MinimumReadableCharacters, pageCount * MinimumAveragePdfCharactersPerPage);
    }

    private static async Task<string> ExtractPdfBestEffortAsync(string physicalPath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(physicalPath, cancellationToken);
        var raw = Encoding.Latin1.GetString(bytes);
        var builder = new StringBuilder();

        foreach (Match match in PdfLiteralTextRegex().Matches(raw))
        {
            var value = UnescapePdfLiteral(match.Groups[1].Value);
            if (LooksReadable(value))
            {
                builder.Append(value).Append(' ');
            }
        }

        foreach (Match match in PdfArrayTextRegex().Matches(raw))
        {
            foreach (Match inner in PdfArrayLiteralRegex().Matches(match.Groups[1].Value))
            {
                var value = UnescapePdfLiteral(inner.Groups[1].Value);
                if (LooksReadable(value))
                {
                    builder.Append(value).Append(' ');
                }
            }
        }

        foreach (Match match in PdfHexTextRegex().Matches(raw))
        {
            var value = DecodePdfHex(match.Groups[1].Value);
            if (LooksReadable(value))
            {
                builder.Append(value).Append(' ');
            }
        }

        return NormalizeText(builder.ToString());
    }

    private sealed record PdfExtractionResult(int PageCount, int ReadablePageCount, string Text);

    private static bool IsEpubContentEntry(string path)
    {
        if (path.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripMarkup(string markup)
    {
        var withoutScripts = ScriptAndStyleRegex().Replace(markup, " ");
        var withLineBreaks = BlockTagRegex().Replace(withoutScripts, "\n");
        var withoutTags = HtmlTagRegex().Replace(withLineBreaks, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string NormalizeText(string text)
    {
        var normalizedLineEndings = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var compactSpaces = HorizontalWhitespaceRegex().Replace(normalizedLineEndings, " ");
        var compactLines = ManyBlankLinesRegex().Replace(compactSpaces, "\n\n");
        return compactLines.Trim();
    }

    private static string UnescapePdfLiteral(string value)
    {
        return value
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal)
            .Replace("\\(", "(", StringComparison.Ordinal)
            .Replace("\\)", ")", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string DecodePdfHex(string hex)
    {
        var cleaned = WhitespaceRegex().Replace(hex, string.Empty);
        if (cleaned.Length < 2)
        {
            return string.Empty;
        }

        if (cleaned.Length % 2 == 1)
        {
            cleaned += "0";
        }

        var bytes = new byte[cleaned.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }

        if (bytes.Length > 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static bool LooksReadable(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
        {
            return false;
        }

        var letters = value.Count(char.IsLetter);
        return letters >= Math.Max(2, value.Length / 5);
    }

    private static bool LooksLikeReadableText(string text)
    {
        var clean = NormalizeText(text);
        if (clean.Length < MinimumReadableCharacters)
        {
            return false;
        }

        var controlCharacters = clean.Count(character => char.IsControl(character) && !char.IsWhiteSpace(character));
        if (controlCharacters / (double)clean.Length > MaximumControlCharacterRatio)
        {
            return false;
        }

        var replacementCharacters = clean.Count(character => character == '\uFFFD');
        if (replacementCharacters / (double)clean.Length > MaximumReplacementCharacterRatio)
        {
            return false;
        }

        var words = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 20)
        {
            return false;
        }

        var readableWords = words.Count(IsReadableWord);
        return readableWords / (double)words.Length >= MinimumReadableWordRatio;
    }

    private async Task<string> ExtractPdfWithGeminiOcrAsync(string physicalPath, CancellationToken cancellationToken)
    {
        var apiKey = GetGeminiApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("PDF này là ảnh/scan nên cần OCR, nhưng LemonInk chưa có API key để đọc ảnh. Hãy set AI__Gemini__ApiKey hoặc GEMINI_API_KEY.");
        }

        var fileInfo = new FileInfo(physicalPath);
        if (fileInfo.Length > _options.Gemini.OcrMaxInlineBytes)
        {
            throw new InvalidOperationException("PDF ảnh này quá lớn để OCR trực tiếp. Hãy giảm dung lượng file hoặc tách PDF thành file nhỏ hơn.");
        }

        var fileBytes = await File.ReadAllBytesAsync(physicalPath, cancellationToken);
        Exception? lastException = null;
        var request = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new
                        {
                            text = """
                            Bạn là bộ OCR tiếng Việt cho LemonInk.

                            Hãy đọc toàn bộ chữ trong file PDF đính kèm và trả về nguyên văn nội dung theo thứ tự trang.

                            Yêu cầu:
                            - Chỉ trả về text đã OCR, không markdown, không tóm tắt, không giải thích.
                            - Giữ xuống dòng giữa các đoạn khi có thể.
                            - Nếu gặp lỗi chính tả do scan mờ, hãy sửa nhẹ để câu tiếng Việt dễ đọc nhưng không thêm ý mới.
                            - Nếu tài liệu không có chữ đọc được, trả về chuỗi rỗng.
                            """
                        },
                        new
                        {
                            inlineData = new
                            {
                                mimeType = "application/pdf",
                                data = Convert.ToBase64String(fileBytes)
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0,
                maxOutputTokens = Math.Clamp(_options.Gemini.OcrMaxOutputTokens, 1000, 30000)
            }
        };

        foreach (var model in await _modelRouter.OrderModelsAsync(AiModelTask.Ocr, ResolveOcrModels(), cancellationToken))
        {
            var endpoint = BuildGeminiEndpoint(apiKey, model);

            for (var attempt = 1; attempt <= MaxOcrProviderRetries; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await _httpClient.PostAsJsonAsync(endpoint, request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await _modelRouter.ReportSuccessAsync(AiModelTask.Ocr, model, stopwatch.Elapsed, cancellationToken);
                    return NormalizeText(ExtractGeminiOutputText(responseBody));
                }

                if (IsTransientOcrProviderError(response.StatusCode))
                {
                    lastException = new OcrProviderTemporaryException(
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        $"LemonInk OCR đang chờ AI đọc PDF ảnh ổn định trở lại bằng {model} ({(int)response.StatusCode} {response.ReasonPhrase}). LemonInk sẽ tự thử lại.",
                        BuildOcrAttemptDelay(attempt),
                        responseBody);
                }
                else
                {
                    lastException = new InvalidOperationException($"LemonInk OCR chưa đọc được PDF ảnh bằng {model} ({(int)response.StatusCode} {response.ReasonPhrase}). Bạn thử lại sau hoặc upload PDF có thể copy chữ.");
                }

                await _modelRouter.ReportFailureAsync(AiModelTask.Ocr, model, lastException, stopwatch.Elapsed, cancellationToken);
                if (IsTransientOcrProviderError(response.StatusCode) && attempt < MaxOcrProviderRetries)
                {
                    await Task.Delay(BuildOcrAttemptDelay(attempt), cancellationToken);
                    continue;
                }

                break;
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }

        throw new InvalidOperationException("LemonInk OCR chưa có model Gemini nào để đọc PDF ảnh.");
    }

    private static bool IsTransientOcrProviderError(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan BuildOcrAttemptDelay(int attempt)
    {
        var seconds = attempt switch
        {
            <= 1 => 8,
            2 => 18,
            3 => 35,
            _ => 60
        };

        return TimeSpan.FromSeconds(seconds);
    }

    private string? GetGeminiApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.Gemini.ApiKey))
        {
            return _options.Gemini.ApiKey;
        }

        return Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    }

    private IReadOnlyList<string> ResolveOcrModels()
    {
        var models = new List<string>();

        if (!string.IsNullOrWhiteSpace(_options.Gemini.Model))
        {
            models.Add(_options.Gemini.Model.Trim());
        }

        models.AddRange(_options.Gemini.Models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim()));

        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string BuildGeminiEndpoint(string apiKey, string modelName)
    {
        var baseUrl = _options.Gemini.EndpointBaseUrl.TrimEnd('/');
        var model = Uri.EscapeDataString(modelName);
        var encodedKey = Uri.EscapeDataString(apiKey);

        return $"{baseUrl}/{model}:generateContent?key={encodedKey}";
    }

    private static string ExtractGeminiOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("candidates", out var candidatesElement) ||
            candidatesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("LemonInk OCR chưa nhận được nội dung hợp lệ.");
        }

        var builder = new StringBuilder();
        foreach (var candidate in candidatesElement.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var contentElement) ||
                !contentElement.TryGetProperty("parts", out var partsElement) ||
                partsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in partsElement.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    builder.AppendLine(textElement.GetString());
                }
            }
        }

        return builder.ToString();
    }

    private static bool IsReadableWord(string word)
    {
        if (word.Length is < 1 or > 48)
        {
            return false;
        }

        var hasLetterOrDigit = false;
        var readableCharacters = 0;
        foreach (var character in word)
        {
            if (char.IsLetterOrDigit(character))
            {
                hasLetterOrDigit = true;
                readableCharacters++;
                continue;
            }

            if (char.IsPunctuation(character) || character is '\'' or '’' or '-' or '/')
            {
                readableCharacters++;
            }
        }

        return hasLetterOrDigit && readableCharacters / (double)word.Length >= 0.75;
    }

    [GeneratedRegex("<(script|style)[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex("</?(p|div|section|article|chapter|h[1-6]|li|br|tr|blockquote)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("[ \\t\\f\\v]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex ManyBlankLinesRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("\\(([^()]*(?:\\\\.[^()]*)*)\\)\\s*Tj")]
    private static partial Regex PdfLiteralTextRegex();

    [GeneratedRegex("\\[(.*?)\\]\\s*TJ", RegexOptions.Singleline)]
    private static partial Regex PdfArrayTextRegex();

    [GeneratedRegex("\\(([^()]*(?:\\\\.[^()]*)*)\\)")]
    private static partial Regex PdfArrayLiteralRegex();

    [GeneratedRegex("<([0-9A-Fa-f\\s]+)>\\s*Tj")]
    private static partial Regex PdfHexTextRegex();
}
