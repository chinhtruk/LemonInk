using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ZenRead.Entities;
using ZenRead.Services.Ai;
using ZenRead.Services.Summarization;

namespace ZenRead.Services.Audio;

public partial class GeminiAudioGenerationService : IAudioGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly IWebHostEnvironment _environment;
    private readonly IAiModelRouter _modelRouter;
    private readonly ILogger<GeminiAudioGenerationService> _logger;
    private readonly AudioGenerationOptions _audioOptions;
    private readonly AiSummarizationOptions _aiOptions;

    public GeminiAudioGenerationService(
        HttpClient httpClient,
        IWebHostEnvironment environment,
        IAiModelRouter modelRouter,
        ILogger<GeminiAudioGenerationService> logger,
        IOptions<AudioGenerationOptions> audioOptions,
        IOptions<AiSummarizationOptions> aiOptions)
    {
        _httpClient = httpClient;
        _environment = environment;
        _modelRouter = modelRouter;
        _logger = logger;
        _audioOptions = audioOptions.Value;
        _aiOptions = aiOptions.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public async Task<BookAudioGenerationResult> GenerateAsync(
        Book book,
        GeneratedBookSummary summary,
        IReadOnlyList<BookSummarySection> sections,
        IReadOnlyList<BookTakeaway> takeaways,
        Func<BookAudioGenerationProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        if (!_audioOptions.Provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase) &&
            !_audioOptions.Provider.Equals("Google", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Provider tạo audio hiện tại chưa được hỗ trợ.");
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Chưa cấu hình Gemini API key để tạo audio. Hãy set Audio__Gemini__ApiKey, AI__Gemini__ApiKey hoặc GEMINI_API_KEY.");
        }

        var script = BuildAudioScript(book, summary, sections, takeaways);
        var chunks = SplitAudioScript(script, ResolveMaxChunkCharacters());
        var minimumDurationSeconds = EstimateMinimumDurationSeconds(script);

        Exception? lastException = null;

        foreach (var model in await ResolveModelsForGenerationAsync(book, chunks, cancellationToken))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await GenerateWithModelAsync(
                    book,
                    chunks,
                    script.Length,
                    minimumDurationSeconds,
                    apiKey,
                    model,
                    progressCallback,
                    cancellationToken);
                await _modelRouter.ReportSuccessAsync(AiModelTask.TextToSpeech, model, stopwatch.Elapsed, cancellationToken);
                return result;
            }
            catch (Exception exception) when (ShouldTryNextModel(exception))
            {
                await _modelRouter.ReportFailureAsync(AiModelTask.TextToSpeech, model, exception, stopwatch.Elapsed, cancellationToken);
                lastException = exception;
            }
        }

        throw lastException ?? new InvalidOperationException("Gemini TTS chưa tạo được audio lúc này.");
    }

    private async Task<BookAudioGenerationResult> GenerateWithModelAsync(
        Book book,
        IReadOnlyList<string> chunks,
        int scriptCharacterCount,
        int minimumDurationSeconds,
        string apiKey,
        string model,
        Func<BookAudioGenerationProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("Không có nội dung tóm tắt để tạo audio.");
        }

        var checkpointFolder = ResolveCheckpointFolder(book, chunks, model);
        var results = new List<byte[]>(chunks.Count);
        var nextRequestAt = DateTimeOffset.MinValue;

        for (var index = 0; index < chunks.Count; index++)
        {
            var segmentPath = ResolveSegmentPath(checkpointFolder, index);
            var pcmBytes = await TryLoadValidSegmentAsync(segmentPath, chunks[index], cancellationToken);
            var loadedFromCheckpoint = pcmBytes is not null;

            if (pcmBytes is null)
            {
                var chunkInput = BuildChunkPrompt(chunks[index], index + 1, chunks.Count, _audioOptions.Gemini.VoiceName);
                pcmBytes = await GenerateChunkWithRetriesAsync(
                    chunkInput,
                    chunks[index],
                    index,
                    chunks.Count,
                    apiKey,
                    model,
                    async token =>
                    {
                        await WaitUntilAsync(nextRequestAt, token);
                        nextRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(ResolveMinimumRequestIntervalMilliseconds());
                    },
                    cancellationToken);

                await SaveSegmentAsync(segmentPath, pcmBytes, cancellationToken);
            }

            results.Add(pcmBytes);

            if (progressCallback is not null)
            {
                await progressCallback(
                    new BookAudioGenerationProgress
                    {
                        CompletedSegments = index + 1,
                        TotalSegments = chunks.Count,
                        Model = model,
                        LoadedFromCheckpoint = loadedFromCheckpoint
                    },
                    cancellationToken);
            }
        }

        var pcmBuffer = new List<byte>();
        for (var index = 0; index < results.Count; index++)
        {
            pcmBuffer.AddRange(results[index]);

            if (index < results.Count - 1)
            {
                pcmBuffer.AddRange(BuildSilence(_audioOptions.Gemini.SampleRate, milliseconds: 380));
            }
        }

        var combinedPcmBytes = NormalizePcm16(pcmBuffer.ToArray());
        var durationSeconds = ValidateDuration(
            EstimateDurationSeconds(combinedPcmBytes.Length, _audioOptions.Gemini.SampleRate),
            minimumDurationSeconds);
        var storedAudioPath = await SaveWavAsync(book, combinedPcmBytes, cancellationToken);
        DeleteBookCheckpoints(book);

        return new BookAudioGenerationResult
        {
            StoredAudioPath = storedAudioPath,
            DurationSeconds = durationSeconds,
            VoiceName = _audioOptions.Gemini.VoiceName,
            Provider = $"Gemini TTS ({model})",
            SegmentCount = chunks.Count,
            ScriptCharacterCount = scriptCharacterCount
        };
    }

    private async Task<byte[]> GenerateChunkWithRetriesAsync(
        string input,
        string scriptChunk,
        int index,
        int chunkCount,
        string apiKey,
        string model,
        Func<CancellationToken, Task> waitForRequestSlot,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(_audioOptions.Gemini.MaxChunkAttempts, 1, 4);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await waitForRequestSlot(cancellationToken);
                var pcmBytes = await GeneratePcmWithModelAsync(input, apiKey, model, cancellationToken);
                ValidateSegmentDuration(pcmBytes, scriptChunk);
                return pcmBytes;
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                IsRetryableChunkError(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Gemini TTS chunk {ChunkNumber}/{ChunkCount} using {Model} failed on attempt {Attempt}. Retrying the same chunk.",
                    index + 1,
                    chunkCount,
                    model,
                    attempt);

                await Task.Delay(ResolveChunkRetryDelayMilliseconds(), cancellationToken);
            }
        }

        throw new InvalidOperationException("Không tạo được segment audio.");
    }

    private async Task<byte[]> GeneratePcmWithModelAsync(
        string input,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(model));
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = input }
                    }
                }
            },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new
                {
                    languageCode = _audioOptions.Gemini.LanguageCode,
                    voiceConfig = new
                    {
                        prebuiltVoiceConfig = new
                        {
                            voiceName = _audioOptions.Gemini.VoiceName
                        }
                    }
                }
            }
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini TTS failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        return ExtractPcmBytes(responseBody);
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_audioOptions.Gemini.ApiKey))
        {
            return _audioOptions.Gemini.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(_aiOptions.Gemini.ApiKey))
        {
            return _aiOptions.Gemini.ApiKey;
        }

        return Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
    }

    private string BuildEndpoint(string modelName)
    {
        var baseUrl = _audioOptions.Gemini.EndpointBaseUrl.TrimEnd('/');
        var model = Uri.EscapeDataString(modelName);
        return $"{baseUrl}/{model}:generateContent";
    }

    private IReadOnlyList<string> ResolveTtsModels()
    {
        var models = new List<string>();
        models.AddRange(_audioOptions.Gemini.Models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim()));

        if (!string.IsNullOrWhiteSpace(_audioOptions.Gemini.Model))
        {
            models.Add(_audioOptions.Gemini.Model.Trim());
        }

        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> ResolveModelsForGenerationAsync(
        Book book,
        IReadOnlyList<string> chunks,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var coolingDownModels = (await _modelRouter.GetSnapshotAsync(cancellationToken))
            .Where(item =>
                item.Task == AiModelTask.TextToSpeech &&
                item.CooldownUntil.HasValue &&
                item.CooldownUntil.Value > now)
            .Select(item => item.Model)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (await _modelRouter
            .OrderModelsAsync(AiModelTask.TextToSpeech, ResolveTtsModels(), cancellationToken))
            .Select((model, index) => new
            {
                Model = model,
                OriginalOrder = index,
                IsCoolingDown = coolingDownModels.Contains(model),
                CompletedSegments = CountStoredSegments(ResolveCheckpointFolder(book, chunks, model), chunks.Count)
            })
            .OrderBy(item => item.IsCoolingDown)
            .ThenByDescending(item => item.CompletedSegments)
            .ThenBy(item => item.OriginalOrder)
            .Select(item => item.Model)
            .ToList();
    }

    private static bool ShouldTryNextModel(Exception exception)
    {
        return exception is InvalidOperationException ||
            exception is TaskCanceledException ||
            exception is TimeoutException ||
            exception is HttpRequestException;
    }

    private async Task<string> SaveWavAsync(Book book, byte[] pcmBytes, CancellationToken cancellationToken)
    {
        var ownerFolder = string.IsNullOrWhiteSpace(book.OwnerUserId) ? "system" : book.OwnerUserId;
        var folder = Path.Combine(_environment.ContentRootPath, "App_Data", "audio", ownerFolder);
        Directory.CreateDirectory(folder);

        var fileName = $"book-{book.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.wav";
        var physicalPath = Path.Combine(folder, fileName);
        var wavBytes = BuildWavFile(pcmBytes, _audioOptions.Gemini.SampleRate);
        await File.WriteAllBytesAsync(physicalPath, wavBytes, cancellationToken);

        return Path.Combine("App_Data", "audio", ownerFolder, fileName);
    }

    private string ResolveCheckpointFolder(Book book, IReadOnlyList<string> chunks, string model)
    {
        var ownerFolder = string.IsNullOrWhiteSpace(book.OwnerUserId) ? "system" : book.OwnerUserId;
        var identity = string.Join("\n\n", chunks) +
            $"\nmodel={model}\nvoice={_audioOptions.Gemini.VoiceName}\nrate={_audioOptions.Gemini.SampleRate}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..20];

        return Path.Combine(
            _environment.ContentRootPath,
            "App_Data",
            "audio-segments",
            ownerFolder,
            $"book-{book.Id}",
            fingerprint);
    }

    private static string ResolveSegmentPath(string checkpointFolder, int index)
    {
        return Path.Combine(checkpointFolder, $"chunk-{index + 1:D4}.pcm");
    }

    private int CountStoredSegments(string checkpointFolder, int chunkCount)
    {
        if (!Directory.Exists(checkpointFolder))
        {
            return 0;
        }

        var completed = 0;
        for (var index = 0; index < chunkCount; index++)
        {
            var path = ResolveSegmentPath(checkpointFolder, index);
            if (File.Exists(path) && new FileInfo(path).Length > 1)
            {
                completed++;
            }
        }

        return completed;
    }

    private async Task<byte[]?> TryLoadValidSegmentAsync(
        string segmentPath,
        string scriptChunk,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(segmentPath))
        {
            return null;
        }

        var pcmBytes = await File.ReadAllBytesAsync(segmentPath, cancellationToken);
        try
        {
            ValidateSegmentDuration(pcmBytes, scriptChunk);
            return pcmBytes;
        }
        catch (InvalidOperationException)
        {
            File.Delete(segmentPath);
            return null;
        }
    }

    private static async Task SaveSegmentAsync(
        string segmentPath,
        byte[] pcmBytes,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(segmentPath)
            ?? throw new InvalidOperationException("Không xác định được thư mục lưu segment audio.");
        Directory.CreateDirectory(folder);

        var temporaryPath = $"{segmentPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, pcmBytes, cancellationToken);
        File.Move(temporaryPath, segmentPath, overwrite: true);
    }

    private void DeleteBookCheckpoints(Book book)
    {
        var ownerFolder = string.IsNullOrWhiteSpace(book.OwnerUserId) ? "system" : book.OwnerUserId;
        var folder = Path.Combine(
            _environment.ContentRootPath,
            "App_Data",
            "audio-segments",
            ownerFolder,
            $"book-{book.Id}");

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(DateTimeOffset target, CancellationToken cancellationToken)
    {
        var delay = target - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private int ResolveMinimumRequestIntervalMilliseconds()
    {
        return Math.Clamp(_audioOptions.Gemini.MinimumRequestIntervalMilliseconds, 0, 120000);
    }

    private int ResolveChunkRetryDelayMilliseconds()
    {
        return Math.Clamp(_audioOptions.Gemini.ChunkRetryDelayMilliseconds, 1000, 180000);
    }

    private static bool IsRetryableChunkError(Exception exception)
    {
        var message = exception.Message;
        return exception is TaskCanceledException ||
            exception is TimeoutException ||
            exception is HttpRequestException ||
            message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("500", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("504", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    private void ValidateSegmentDuration(byte[] pcmBytes, string scriptChunk)
    {
        ValidateDuration(
            EstimateDurationSeconds(pcmBytes.Length, _audioOptions.Gemini.SampleRate),
            EstimateMinimumDurationSeconds(scriptChunk));
    }

    private static byte[] ExtractPcmBytes(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Gemini TTS response không có candidates.");
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (TryReadInlineData(part, out var data))
                {
                    return Convert.FromBase64String(data);
                }
            }
        }

        throw new InvalidOperationException("Gemini TTS response không trả về audio inline data.");
    }

    private static bool TryReadInlineData(JsonElement part, out string data)
    {
        data = string.Empty;

        if (!part.TryGetProperty("inlineData", out var inlineData) &&
            !part.TryGetProperty("inline_data", out inlineData))
        {
            return false;
        }

        if (!inlineData.TryGetProperty("data", out var dataElement) ||
            dataElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        data = dataElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(data);
    }

    private static byte[] BuildWavFile(byte[] pcmBytes, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = channels * bitsPerSample / 8;
        var dataSize = pcmBytes.Length;
        var fileSizeMinus8 = 36 + dataSize;

        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(fileSizeMinus8);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        writer.Write(pcmBytes);
        writer.Flush();

        return stream.ToArray();
    }

    private static byte[] NormalizePcm16(byte[] pcmBytes)
    {
        if (pcmBytes.Length < 2)
        {
            return pcmBytes;
        }

        var max = 0;
        for (var index = 0; index + 1 < pcmBytes.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(pcmBytes, index);
            max = Math.Max(max, Math.Abs((int)sample));
        }

        if (max == 0)
        {
            return pcmBytes;
        }

        const double targetPeak = short.MaxValue * 0.86;
        var scale = targetPeak / max;
        if (Math.Abs(scale - 1) < 0.03)
        {
            return pcmBytes;
        }

        var normalized = new byte[pcmBytes.Length];
        for (var index = 0; index + 1 < pcmBytes.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(pcmBytes, index);
            var value = (int)Math.Round(sample * scale);
            value = Math.Clamp(value, short.MinValue, short.MaxValue);
            normalized[index] = (byte)(value & 0xff);
            normalized[index + 1] = (byte)((value >> 8) & 0xff);
        }

        return normalized;
    }

    private static string BuildAudioScript(
        Book book,
        GeneratedBookSummary summary,
        IReadOnlyList<BookSummarySection> sections,
        IReadOnlyList<BookTakeaway> takeaways)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(book.AuthorName))
        {
            builder.AppendLine($"Chào bạn, đây là bản tóm tắt sách \"{book.Title}\" của tác giả {book.AuthorName} trên LemonInk.");
        }
        else
        {
            builder.AppendLine($"Chào bạn, đây là bản tóm tắt sách \"{book.Title}\" trên LemonInk.");
        }

        var overview = sections
            .Where(item => item.SectionType == SummarySectionType.Overview)
            .OrderBy(item => item.SortOrder)
            .Select(item => ToPlainText(item.ContentHtml))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));

        var introduction = !string.IsNullOrWhiteSpace(book.Introduction)
            ? book.Introduction
            : summary.ShortSummary;

        if (!string.IsNullOrWhiteSpace(overview))
        {
            builder.AppendLine();
            builder.AppendLine("Tổng quan.");
            builder.AppendLine(CleanForSpeech(overview));
        }
        else if (!string.IsNullOrWhiteSpace(summary.LongSummary))
        {
            builder.AppendLine();
            builder.AppendLine("Tổng quan.");
            builder.AppendLine(CleanForSpeech(summary.LongSummary));
        }
        else if (!string.IsNullOrWhiteSpace(introduction))
        {
            builder.AppendLine();
            builder.AppendLine("Giới thiệu nhanh.");
            builder.AppendLine(CleanForSpeech(introduction));
        }

        if (takeaways.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Những ý chính cần nhớ.");
            foreach (var takeaway in takeaways.OrderBy(item => item.SortOrder))
            {
                builder.AppendLine($"- {CleanForSpeech(takeaway.Content)}");
            }
        }

        foreach (var section in sections
            .Where(item => item.SectionType == SummarySectionType.Chapter)
            .OrderBy(item => item.SortOrder))
        {
            builder.AppendLine();
            var chapterLabel = section.ChapterNumber.HasValue
                ? $"Chương {section.ChapterNumber}: {section.Title}"
                : section.Title;
            builder.AppendLine(chapterLabel);
            builder.AppendLine(CleanForSpeech(section.ContentHtml));
        }

        return NormalizeScript(builder.ToString());
    }

    private int ResolveMaxChunkCharacters()
    {
        return Math.Clamp(_audioOptions.MaxInputCharacters, 1800, 5200);
    }

    private static string BuildChunkPrompt(string chunk, int chunkNumber, int chunkCount, string voiceName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Bạn là một giọng đọc audio sách chuyên nghiệp của LemonInk.");
        
        var genderDesc = "nữ";
        var voiceLower = voiceName.ToLowerInvariant();
        if (voiceLower.Contains("puck") || voiceLower.Contains("charon") || voiceLower.Contains("fenrir") || voiceLower.Contains("zephyr"))
        {
            genderDesc = "nam";
        }

        builder.AppendLine($"YÊU CẦU: Hãy chuyển chính xác phần SCRIPT bên dưới thành giọng nói tiếng Việt chuẩn, tự nhiên, rõ ràng, ấm áp.");
        builder.AppendLine($"Bạn PHẢI sử dụng một giọng đọc {genderDesc} duy nhất (giọng {voiceName}), giữ nguyên tông giọng, âm lượng, cảm xúc và tốc độ đọc nhất quán tuyệt đối xuyên suốt văn bản. Không được tự ý thay đổi giọng đọc hoặc chuyển sang giọng khác.");
        builder.AppendLine("Không tóm tắt, không diễn giải lại, không bỏ câu, không đọc các dòng hướng dẫn này.");

        if (chunkCount > 1)
        {
            builder.AppendLine($"Đây là phần {chunkNumber} trên tổng số {chunkCount} phần của sách; chỉ đọc duy nhất nội dung chữ trong mục SCRIPT bên dưới.");
        }

        builder.AppendLine("SCRIPT:");
        builder.AppendLine(chunk.Trim());
        return builder.ToString().Trim();
    }

    private static IReadOnlyList<string> SplitAudioScript(string script, int maxCharacters)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var paragraph in SplitParagraphs(script))
        {
            if (paragraph.Length > maxCharacters)
            {
                FlushChunk(chunks, current);
                foreach (var part in SplitLongParagraph(paragraph, maxCharacters))
                {
                    chunks.Add(part);
                }

                continue;
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 2 > maxCharacters)
            {
                FlushChunk(chunks, current);
            }

            if (current.Length > 0)
            {
                current.AppendLine();
                current.AppendLine();
            }

            current.Append(paragraph);
        }

        FlushChunk(chunks, current);
        return chunks;
    }

    private static IEnumerable<string> SplitParagraphs(string script)
    {
        return script
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CollapseWhitespace)
            .Where(item => !string.IsNullOrWhiteSpace(item));
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph, int maxCharacters)
    {
        var remaining = paragraph.Trim();
        while (remaining.Length > maxCharacters)
        {
            var splitAt = FindSplitPoint(remaining, maxCharacters);
            yield return remaining[..splitAt].Trim();
            remaining = remaining[splitAt..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(remaining))
        {
            yield return remaining;
        }
    }

    private static int FindSplitPoint(string value, int maxCharacters)
    {
        var window = value[..Math.Min(value.Length, maxCharacters)];
        var sentenceEnd = Math.Max(
            window.LastIndexOf('.'),
            Math.Max(
                window.LastIndexOf('!'),
                window.LastIndexOf('?')));

        if (sentenceEnd > maxCharacters / 2)
        {
            return sentenceEnd + 1;
        }

        var lastSpace = window.LastIndexOf(' ');
        return lastSpace > maxCharacters / 2 ? lastSpace : window.Length;
    }

    private static void FlushChunk(List<string> chunks, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        chunks.Add(current.ToString().Trim());
        current.Clear();
    }

    private static string CleanForSpeech(string value)
    {
        return CollapseWhitespace(ToPlainText(value));
    }

    private static string ToPlainText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutTags = HtmlTagRegex().Replace(value, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string NormalizeScript(string value)
    {
        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => CollapseWhitespace(line))
            .ToList();

        var builder = new StringBuilder();
        var previousBlank = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!previousBlank && builder.Length > 0)
                {
                    builder.AppendLine();
                }

                previousBlank = true;
                continue;
            }

            builder.AppendLine(line);
            previousBlank = false;
        }

        return builder.ToString().Trim();
    }

    private static string CollapseWhitespace(string value)
    {
        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    private static byte[] BuildSilence(int sampleRate, int milliseconds)
    {
        const int bytesPerSample = 2;
        var sampleCount = Math.Max(0, sampleRate * milliseconds / 1000);
        return new byte[sampleCount * bytesPerSample];
    }

    private static int EstimateDurationSeconds(int pcmByteLength, int sampleRate)
    {
        const int bytesPerSample = 2;
        return Math.Max(1, pcmByteLength / (sampleRate * bytesPerSample));
    }

    private static int EstimateMinimumDurationSeconds(string script)
    {
        var wordCount = WordRegex().Matches(script).Count;
        if (wordCount < 80)
        {
            return 1;
        }

        // 4.2 words/sec is already faster than a comfortable Vietnamese narration.
        // Anything shorter than this is likely a truncated TTS result.
        return Math.Max(20, (int)Math.Floor(wordCount / 4.2));
    }

    private static int ValidateDuration(int durationSeconds, int minimumDurationSeconds)
    {
        if (minimumDurationSeconds > 1 && durationSeconds < minimumDurationSeconds)
        {
            throw new InvalidOperationException(
                $"Gemini TTS trả về audio ngắn bất thường ({durationSeconds}s) so với nội dung cần đọc (tối thiểu khoảng {minimumDurationSeconds}s).");
        }

        return durationSeconds;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("[\\p{L}\\p{N}]+")]
    private static partial Regex WordRegex();
}
