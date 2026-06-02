using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZenRead.Entities;

namespace ZenRead.Services.Covers;

public partial class GeneratedBookCoverService : IBookCoverService
{
    private static readonly CoverPalette[] Palettes =
    [
        new("#243b55", "#f8bb16", "#f7f2df", "#1b2135"),
        new("#164e63", "#f97316", "#eef7f5", "#182238"),
        new("#0f766e", "#f59e0b", "#f4f1ea", "#172033"),
        new("#7c2d12", "#38bdf8", "#fff4de", "#182033"),
        new("#312e81", "#22c55e", "#f8f3df", "#171b2f"),
        new("#14532d", "#fb7185", "#f7f5eb", "#172033"),
        new("#334155", "#facc15", "#f8fafc", "#111827")
    ];

    private readonly IWebHostEnvironment _environment;

    public GeneratedBookCoverService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<string> EnsureCoverAsync(Book book, CancellationToken cancellationToken)
    {
        var fileName = $"{Slugify(book.Slug)}-{ShortHash(book.Title, book.AuthorName, book.Category)}.svg";
        var relativeUrl = $"/images/generated-covers/{fileName}";
        var folder = Path.Combine(_environment.WebRootPath, "images", "generated-covers");
        Directory.CreateDirectory(folder);

        var physicalPath = Path.Combine(folder, fileName);
        if (!File.Exists(physicalPath))
        {
            var svg = BuildSvg(book);
            await File.WriteAllTextAsync(physicalPath, svg, Encoding.UTF8, cancellationToken);
        }

        return relativeUrl;
    }

    public string BuildGradient(Book book)
    {
        var palette = PickPalette(book);
        return $"linear-gradient(135deg, {palette.Dark} 0%, {palette.Accent} 100%)";
    }

    private static string BuildSvg(Book book)
    {
        var palette = PickPalette(book);
        var titleLines = WrapText(book.Title, 12, 5);
        var titleFontSize = titleLines.Count >= 4 ? 29 : titleLines.Count == 3 ? 31 : 33;
        var titleLineHeight = titleFontSize + 7;
        var author = string.IsNullOrWhiteSpace(book.AuthorName) ? "LemonInk" : book.AuthorName.Trim();
        var category = string.IsNullOrWhiteSpace(book.Category) ? "SACH" : book.Category.Trim().ToUpperInvariant();
        var initials = BuildInitials(book.Title);
        var titleTspans = BuildTspans(titleLines, 58, 0, titleLineHeight);

        return $$"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 360 540" role="img" aria-label="{{Escape(book.Title)}}">
          <defs>
            <linearGradient id="bg" x1="0" x2="1" y1="0" y2="1">
              <stop offset="0%" stop-color="{{palette.Dark}}"/>
              <stop offset="100%" stop-color="{{palette.Accent}}"/>
            </linearGradient>
            <filter id="softShadow" x="-20%" y="-20%" width="140%" height="140%">
              <feDropShadow dx="0" dy="18" stdDeviation="18" flood-color="#111827" flood-opacity=".24"/>
            </filter>
          </defs>
          <rect width="360" height="540" rx="34" fill="url(#bg)"/>
          <circle cx="292" cy="92" r="86" fill="{{palette.Light}}" opacity=".18"/>
          <circle cx="60" cy="458" r="92" fill="{{palette.Light}}" opacity=".12"/>
          <rect x="34" y="38" width="292" height="464" rx="26" fill="{{palette.Paper}}" opacity=".94" filter="url(#softShadow)"/>
          <rect x="58" y="70" width="112" height="6" rx="3" fill="{{palette.Accent}}"/>
          <text x="58" y="112" font-family="Inter, Arial, sans-serif" font-size="15" font-weight="800" letter-spacing="2" fill="{{palette.Ink}}" opacity=".68">{{Escape(category)}}</text>
          <text x="58" y="182" font-family="Georgia, 'Times New Roman', serif" font-size="{{titleFontSize}}" font-weight="800" fill="{{palette.Ink}}">{{titleTspans}}</text>
          <text x="58" y="430" font-family="Inter, Arial, sans-serif" font-size="18" font-weight="700" fill="{{palette.Ink}}" opacity=".78">{{Escape(author)}}</text>
          <circle cx="286" cy="442" r="26" fill="{{palette.Accent}}" opacity=".28"/>
          <text x="286" y="451" text-anchor="middle" font-family="Inter, Arial, sans-serif" font-size="18" font-weight="900" fill="{{palette.Ink}}">{{Escape(initials)}}</text>
          <text x="58" y="478" font-family="Inter, Arial, sans-serif" font-size="13" font-weight="900" letter-spacing="2" fill="{{palette.Ink}}" opacity=".5">LEMONINK</text>
        </svg>
        """;
    }

    private static CoverPalette PickPalette(Book book)
    {
        var key = $"{book.Title}|{book.AuthorName}|{book.Category}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Palettes[hash[0] % Palettes.Length];
    }

    private static string ShortHash(params string?[] values)
    {
        var input = string.Join('|', values.Select(value => value ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }

    private static string Slugify(string value)
    {
        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        var slug = NonSlugCharactersRegex().Replace(normalized, "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "book-cover" : slug;
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static List<string> WrapText(string value, int maxChars, int maxLines)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (candidate.Length <= maxChars)
            {
                current.Clear();
                current.Append(candidate);
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (word.Length > maxChars)
            {
                lines.Add(TrimToCoverLine(word, maxChars));
                if (lines.Count == maxLines)
                {
                    break;
                }

                continue;
            }

            current.Append(word);
            if (lines.Count == maxLines - 1)
            {
                break;
            }
        }

        if (current.Length > 0 && lines.Count < maxLines)
        {
            lines.Add(current.ToString());
        }

        return lines.Count == 0 ? ["LemonInk"] : lines;
    }

    private static string TrimToCoverLine(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return $"{value[..Math.Max(1, maxChars - 3)]}...";
    }

    private static string BuildTspans(IReadOnlyList<string> lines, int x, int firstDy, int lineHeight)
    {
        return string.Join(
            string.Empty,
            lines.Select((line, index) =>
                $"<tspan x=\"{x}\" dy=\"{(index == 0 ? firstDy : lineHeight)}\">{Escape(line)}</tspan>"));
    }

    private static string BuildInitials(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharactersRegex();

    private sealed record CoverPalette(string Dark, string Accent, string Paper, string Ink, string Light = "#ffffff");
}
