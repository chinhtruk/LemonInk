namespace ZenRead.Services.Email;

public sealed record EmailContent(
    string Subject,
    string HtmlBody,
    string TextBody);
