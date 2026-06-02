namespace ZenRead.Services.Email;

public sealed class SmtpEmailOptions
{
    public string Host { get; set; } = "smtp.gmail.com";

    public int Port { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromEmail { get; set; } = string.Empty;

    public string FromName { get; set; } = "LemonInk";

    public bool UseSsl { get; set; } = true;

    public bool LogEmailsInsteadOfSending { get; set; }
}
