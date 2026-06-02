using System.Globalization;
using System.Net;

namespace ZenRead.Services.Email;

public sealed class LemonInkEmailTemplateRenderer : IEmailTemplateRenderer
{
    private const string BrandName = "LemonInk";

    public EmailContent RenderLoginOtp(string code, TimeSpan validFor)
    {
        var minutes = ValidMinutes(validFor);
        return new EmailContent(
            "Mã đăng nhập LemonInk",
            RenderLayout(
                $"Mã đăng nhập {BrandName} của bạn là {code}. Mã có hiệu lực trong {minutes} phút.",
                "Mã đăng nhập của bạn",
                "Dùng mã bên dưới để tiếp tục đăng nhập vào LemonInk.",
                RenderCodeBox(code),
                $"Mã này sẽ hết hạn sau <strong style=\"color:#151b3a;\">{minutes} phút</strong>.",
                "Nếu bạn không yêu cầu đăng nhập, bạn có thể bỏ qua email này. Không chia sẻ mã này với bất kỳ ai."),
            $"Mã đăng nhập LemonInk của bạn là {code}. Mã có hiệu lực trong {minutes} phút. Không chia sẻ mã này với bất kỳ ai.");
    }

    public EmailContent RenderVerifyEmail(string displayName, string verificationUrl, TimeSpan validFor)
    {
        var minutes = ValidMinutes(validFor);
        var name = H(displayName);
        return new EmailContent(
            "Xác minh email để bắt đầu với LemonInk",
            RenderLayout(
                "Xác minh email để hoàn tất tài khoản LemonInk của bạn.",
                "Xác minh email của bạn",
                $"Chào {name}, cảm ơn bạn đã đăng ký LemonInk. Xác minh email để bắt đầu lưu sách, nghe tóm tắt và sử dụng LemonAI.",
                RenderButton("Xác minh email", verificationUrl),
                $"Liên kết này sẽ hết hạn sau <strong style=\"color:#151b3a;\">{minutes} phút</strong>.",
                "Nếu bạn không tạo tài khoản LemonInk, hãy bỏ qua email này."),
            $"Chào {Plain(displayName)}, xác minh email LemonInk tại: {verificationUrl}. Liên kết có hiệu lực trong {minutes} phút.");
    }

    public EmailContent RenderPasswordResetOtp(string code, TimeSpan validFor)
    {
        var minutes = ValidMinutes(validFor);
        return new EmailContent(
            "Mã đặt lại mật khẩu LemonInk",
            RenderLayout(
                $"Mã đặt lại mật khẩu LemonInk của bạn là {code}.",
                "Đặt lại mật khẩu",
                "Chúng tôi nhận được yêu cầu đặt lại mật khẩu của bạn. Nhập mã bên dưới để tiếp tục.",
                RenderCodeBox(code),
                $"Mã này sẽ hết hạn sau <strong style=\"color:#151b3a;\">{minutes} phút</strong>.",
                "LemonInk không bao giờ hỏi mã này qua tin nhắn. Nếu bạn không yêu cầu đặt lại mật khẩu, hãy bỏ qua email này."),
            $"Mã đặt lại mật khẩu LemonInk của bạn là {code}. Mã có hiệu lực trong {minutes} phút.");
    }

    public EmailContent RenderPasswordChanged(string displayName, DateTimeOffset changedAt, string securityUrl)
    {
        var timestamp = FormatDate(changedAt);
        return new EmailContent(
            "Mật khẩu LemonInk của bạn đã được thay đổi",
            RenderLayout(
                "Mật khẩu LemonInk của bạn vừa được thay đổi.",
                "Mật khẩu đã thay đổi",
                $"Chào {H(displayName)}, mật khẩu của tài khoản LemonInk đã được thay đổi thành công vào <strong style=\"color:#151b3a;\">{H(timestamp)}</strong>.",
                RenderButton("Bảo vệ tài khoản", securityUrl),
                "Nếu đây là bạn, bạn không cần làm gì thêm.",
                "Nếu bạn không thực hiện thay đổi này, hãy kiểm tra tài khoản và đổi mật khẩu ngay."),
            $"Chào {Plain(displayName)}, mật khẩu LemonInk của bạn đã được thay đổi lúc {timestamp}. Nếu không phải bạn, truy cập: {securityUrl}");
    }

    public EmailContent RenderEmailChanged(string displayName, string newEmail, DateTimeOffset changedAt, string securityUrl)
    {
        var timestamp = FormatDate(changedAt);
        return new EmailContent(
            "Email tài khoản LemonInk đã được cập nhật",
            RenderLayout(
                "Email tài khoản LemonInk của bạn vừa được cập nhật.",
                "Email đã được cập nhật",
                $"Chào {H(displayName)}, địa chỉ email tài khoản vừa được đổi thành <strong style=\"color:#151b3a;\">{H(newEmail)}</strong> vào {H(timestamp)}.",
                RenderButton("Kiểm tra tài khoản", securityUrl),
                "Địa chỉ mới sẽ được dùng cho các thông báo và bước xác thực tiếp theo.",
                "Nếu bạn không thực hiện thay đổi này, hãy bảo vệ tài khoản ngay."),
            $"Chào {Plain(displayName)}, email LemonInk của bạn đã được đổi thành {Plain(newEmail)} lúc {timestamp}. Nếu không phải bạn, truy cập: {securityUrl}");
    }

    public EmailContent RenderNewDeviceLogin(
        string displayName,
        string deviceName,
        string approximateLocation,
        DateTimeOffset signedInAt,
        string securityUrl)
    {
        var timestamp = FormatDate(signedInAt);
        var details = RenderDetailRows(
            ("Thiết bị", deviceName),
            ("Vị trí gần đúng", approximateLocation),
            ("Thời gian", timestamp));
        return new EmailContent(
            "Đăng nhập mới vào tài khoản LemonInk",
            RenderLayout(
                "Phát hiện một lần đăng nhập mới vào tài khoản LemonInk.",
                "Đăng nhập từ thiết bị mới",
                $"Chào {H(displayName)}, tài khoản của bạn vừa được đăng nhập từ một thiết bị mới.",
                details + RenderButton("Kiểm tra hoạt động", securityUrl),
                "Nếu đây là bạn, bạn có thể bỏ qua email này.",
                "Nếu bạn không nhận ra hoạt động này, hãy đổi mật khẩu và đăng xuất khỏi các thiết bị khác."),
            $"Chào {Plain(displayName)}, có đăng nhập mới vào LemonInk: {Plain(deviceName)}, {Plain(approximateLocation)}, {timestamp}. Kiểm tra tài khoản: {securityUrl}");
    }

    public EmailContent RenderBookUploadReceived(string displayName, string bookTitle, string trackingUrl)
    {
        return new EmailContent(
            "LemonInk đã nhận sách của bạn",
            RenderLayout(
                $"LemonInk đã nhận tài liệu {Plain(bookTitle)} của bạn.",
                "Sách đã được tải lên",
                $"Chào {H(displayName)}, LemonInk đã nhận <strong style=\"color:#151b3a;\">&ldquo;{H(bookTitle)}&rdquo;</strong> và bắt đầu trích xuất nội dung, tạo tóm tắt và audio.",
                RenderButton("Theo dõi xử lý", trackingUrl),
                "Thời gian xử lý phụ thuộc vào độ dài và chất lượng tài liệu.",
                "Chúng tôi sẽ thông báo khi sách sẵn sàng để đọc và nghe."),
            $"Chào {Plain(displayName)}, LemonInk đã nhận sách \"{Plain(bookTitle)}\" và bắt đầu xử lý. Theo dõi tại: {trackingUrl}");
    }

    public EmailContent RenderBookReady(
        string displayName,
        string bookTitle,
        string author,
        string readingUrl,
        string? audioDuration = null,
        int? chapterCount = null)
    {
        var details = new List<(string Label, string Value)>();
        if (!string.IsNullOrWhiteSpace(author))
        {
            details.Add(("Tác giả", author));
        }

        if (chapterCount is > 0)
        {
            details.Add(("Nội dung", $"{chapterCount} chương"));
        }

        if (!string.IsNullOrWhiteSpace(audioDuration))
        {
            details.Add(("Audio", audioDuration));
        }

        return new EmailContent(
            $"Sách \"{SubjectValue(bookTitle)}\" đã sẵn sàng",
            RenderLayout(
                $"Sách {Plain(bookTitle)} đã sẵn sàng để đọc và nghe.",
                "Sách của bạn đã sẵn sàng",
                $"Chào {H(displayName)}, bản tóm tắt và audio cho <strong style=\"color:#151b3a;\">&ldquo;{H(bookTitle)}&rdquo;</strong> đã hoàn tất.",
                RenderDetailRows(details.ToArray()) + RenderButton("Đọc và nghe ngay", readingUrl),
                "Mở sách để xem các ý chính, chương và bắt đầu nghe audio.",
                "Cảm ơn bạn đã đọc cùng LemonInk."),
            $"Chào {Plain(displayName)}, \"{Plain(bookTitle)}\" của {Plain(author)} đã sẵn sàng. Mở sách tại: {readingUrl}");
    }

    public EmailContent RenderBookProcessingFailed(string displayName, string bookTitle, string failedStep, string retryUrl)
    {
        return new EmailContent(
            $"Cần xử lý lại sách \"{SubjectValue(bookTitle)}\"",
            RenderLayout(
                $"LemonInk chưa thể hoàn tất xử lý sách {Plain(bookTitle)}.",
                "Xử lý sách chưa hoàn tất",
                $"Chào {H(displayName)}, LemonInk gặp sự cố khi xử lý <strong style=\"color:#151b3a;\">&ldquo;{H(bookTitle)}&rdquo;</strong>.",
                RenderStatusBox("Bước gặp lỗi", failedStep, true) + RenderButton("Thử lại", retryUrl),
                "Bạn có thể thử lại ngay. Nếu file là PDF ảnh, chất lượng ảnh rõ sẽ giúp OCR tốt hơn.",
                "Nếu lỗi tiếp tục xảy ra, vui lòng tải lên tệp khác hoặc liên hệ hỗ trợ."),
            $"Chào {Plain(displayName)}, xử lý \"{Plain(bookTitle)}\" thất bại ở bước {Plain(failedStep)}. Thử lại tại: {retryUrl}");
    }

    public EmailContent RenderReviewReply(string displayName, string bookTitle, string replyExcerpt, string reviewUrl)
    {
        return new EmailContent(
            $"Có phản hồi mới về \"{SubjectValue(bookTitle)}\"",
            RenderLayout(
                $"Bạn có phản hồi mới về sách {Plain(bookTitle)}.",
                "Có phản hồi mới",
                $"Chào {H(displayName)}, có người vừa phản hồi đánh giá của bạn về <strong style=\"color:#151b3a;\">&ldquo;{H(bookTitle)}&rdquo;</strong>.",
                RenderQuote(replyExcerpt) + RenderButton("Xem phản hồi", reviewUrl),
                "Bạn có thể quay lại cuốn sách để tiếp tục cuộc trò chuyện.",
                "Bạn nhận được email này vì đã tương tác với sách trên LemonInk."),
            $"Chào {Plain(displayName)}, có phản hồi mới về \"{Plain(bookTitle)}\": {Plain(replyExcerpt)}. Xem tại: {reviewUrl}");
    }

    private static string RenderLayout(
        string preheader,
        string heading,
        string introductionHtml,
        string bodyHtml,
        string closingHtml,
        string securityHtml)
    {
        return $$"""
            <!doctype html>
            <html lang="vi">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{H(heading)}} | LemonInk</title>
            </head>
            <body style="margin:0;padding:0;background:#f5f6f8;font-family:Arial,Helvetica,sans-serif;color:#161c39;">
              <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{{H(preheader)}}</div>
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f5f6f8;margin:0;padding:0;">
                <tr>
                  <td align="center" style="padding:38px 16px;">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border:1px solid #e6e8ee;border-radius:16px;overflow:hidden;">
                      <tr>
                        <td style="height:5px;background:#ffb813;font-size:0;line-height:0;">&nbsp;</td>
                      </tr>
                      <tr>
                        <td align="center" style="padding:34px 44px 18px;">
                          <img src="cid:lemonink-logo" width="118" alt="LemonInk" style="width:118px;max-width:100%;height:auto;display:block;border:0;">
                        </td>
                      </tr>
                      <tr>
                        <td align="center" style="padding:10px 44px 0;">
                          <h1 style="font-family:Georgia,'Times New Roman',serif;font-size:28px;line-height:36px;font-weight:700;color:#151b3a;margin:0 0 10px;">{{H(heading)}}</h1>
                          <p style="font-size:16px;line-height:25px;color:#596176;margin:0;">{{introductionHtml}}</p>
                        </td>
                      </tr>
                      <tr>
                        <td align="center" style="padding:28px 44px 24px;">{{bodyHtml}}</td>
                      </tr>
                      <tr>
                        <td align="center" style="padding:0 44px 30px;">
                          <p style="font-size:14px;line-height:22px;color:#596176;margin:0;">{{closingHtml}}</p>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:0 32px;">
                          <div style="height:1px;background:#eceef3;font-size:0;line-height:0;">&nbsp;</div>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:22px 44px 32px;">
                          <p style="font-size:13px;line-height:21px;color:#7a8191;margin:0;text-align:center;">{{securityHtml}}</p>
                        </td>
                      </tr>
                    </table>
                    <p style="font-size:12px;line-height:20px;color:#8d93a3;margin:20px 0 0;">LemonInk &bull; Đọc ít hơn, hiểu nhiều hơn</p>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string RenderCodeBox(string code)
    {
        return $$"""
            <table role="presentation" cellspacing="0" cellpadding="0" style="width:100%;background:#fff8e6;border:1px solid #ffd881;border-radius:12px;">
              <tr>
                <td align="center" style="padding:21px 12px 19px;">
                  <span style="font-size:34px;line-height:42px;letter-spacing:9px;font-weight:700;color:#151b3a;">{{H(code)}}</span>
                </td>
              </tr>
            </table>
            """;
    }

    private static string RenderButton(string label, string url)
    {
        return $$"""
            <table role="presentation" cellspacing="0" cellpadding="0" style="margin:0 auto;">
              <tr>
                <td align="center" bgcolor="#ffb813" style="border-radius:28px;">
                  <a href="{{H(url)}}" style="display:inline-block;padding:15px 28px;font-size:15px;line-height:20px;font-weight:700;color:#151b3a;text-decoration:none;">{{H(label)}}</a>
                </td>
              </tr>
            </table>
            """;
    }

    private static string RenderDetailRows(params (string Label, string Value)[] rows)
    {
        if (rows.Length == 0)
        {
            return string.Empty;
        }

        var rowHtml = string.Join(
            string.Empty,
            rows.Select(row => $$"""
                <tr>
                  <td style="font-size:13px;line-height:21px;color:#7a8191;padding:5px 12px;text-align:left;">{{H(row.Label)}}</td>
                  <td style="font-size:13px;line-height:21px;color:#151b3a;font-weight:700;padding:5px 12px;text-align:right;">{{H(row.Value)}}</td>
                </tr>
                """));

        return $$"""
            <table role="presentation" cellspacing="0" cellpadding="0" style="width:100%;background:#f8f9fb;border-radius:10px;margin:0 0 20px;">{{rowHtml}}</table>
            """;
    }

    private static string RenderStatusBox(string label, string value, bool error)
    {
        var background = error ? "#fff3f2" : "#f8f9fb";
        var border = error ? "#ffd5d0" : "#e6e8ee";
        return $$"""
            <table role="presentation" cellspacing="0" cellpadding="0" style="width:100%;background:{{background}};border:1px solid {{border}};border-radius:10px;margin:0 0 20px;">
              <tr>
                <td style="padding:13px 16px;font-size:13px;line-height:20px;color:#7a8191;text-align:left;">{{H(label)}}</td>
              </tr>
              <tr>
                <td style="padding:0 16px 14px;font-size:15px;line-height:22px;color:#151b3a;font-weight:700;text-align:left;">{{H(value)}}</td>
              </tr>
            </table>
            """;
    }

    private static string RenderQuote(string excerpt)
    {
        return $$"""
            <table role="presentation" cellspacing="0" cellpadding="0" style="width:100%;background:#f8f9fb;border-left:3px solid #ffb813;border-radius:0 10px 10px 0;margin:0 0 20px;">
              <tr>
                <td style="padding:16px;font-size:14px;line-height:23px;color:#596176;text-align:left;">&ldquo;{{H(excerpt)}}&rdquo;</td>
              </tr>
            </table>
            """;
    }

    private static int ValidMinutes(TimeSpan duration)
    {
        return Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes));
    }

    private static string FormatDate(DateTimeOffset timestamp)
    {
        var vietnamTime = timestamp.ToOffset(TimeSpan.FromHours(7));
        return vietnamTime.ToString("HH:mm, dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private static string H(string? value)
    {
        return WebUtility.HtmlEncode(value?.Trim() ?? string.Empty);
    }

    private static string Plain(string? value)
    {
        return (value ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    }

    private static string SubjectValue(string? value)
    {
        var plain = Plain(value);
        return plain.Length <= 80 ? plain : plain[..77] + "...";
    }
}
