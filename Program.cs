using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Audio;
using ZenRead.Services.Ai;
using ZenRead.Services.Auth;
using ZenRead.Services.Books;
using ZenRead.Services.Chat;
using ZenRead.Services.Covers;
using ZenRead.Services.Email;
using ZenRead.Services.Processing;
using ZenRead.Services.Summarization;
using ZenRead.Services.Uploads;
using ZenRead.Services.UserLibrary;

var builder = WebApplication.CreateBuilder(args);
const long MaxUploadRequestBytes = 31 * 1024 * 1024;

builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.WebHost.UseUrls("http://localhost:3000");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxUploadRequestBytes;
});

builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        const string message = "Bạn thao tác quá nhanh. Vui lòng chờ một chút rồi thử lại.";
        if (context.HttpContext.Request.Headers.Accept.Any(value =>
                value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            await context.HttpContext.Response.WriteAsJsonAsync(
                new { ok = false, message },
                cancellationToken: cancellationToken);
            return;
        }

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(message, cancellationToken);
    };

    options.AddPolicy("auth", context => CreateFixedWindowPartition(
        $"auth:{ResolveRequestClientKey(context, preferUser: false)}",
        permitLimit: 12,
        window: TimeSpan.FromMinutes(1)));
    options.AddPolicy("otp", context => CreateFixedWindowPartition(
        $"otp:{ResolveRequestClientKey(context, preferUser: false)}",
        permitLimit: 5,
        window: TimeSpan.FromMinutes(10)));
    options.AddPolicy("upload", context => CreateFixedWindowPartition(
        $"upload:{ResolveRequestClientKey(context, preferUser: true)}",
        permitLimit: 6,
        window: TimeSpan.FromHours(1)));
    options.AddPolicy("ai", context => CreateFixedWindowPartition(
        $"ai:{ResolveRequestClientKey(context, preferUser: true)}",
        permitLimit: 12,
        window: TimeSpan.FromMinutes(1)));
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadRequestBytes;
    options.ValueLengthLimit = 1024 * 1024;
    options.MultipartHeadersLengthLimit = 64 * 1024;
});
builder.Services.Configure<SmtpEmailOptions>(builder.Configuration.GetSection("Email:Smtp"));
builder.Services.Configure<EmailOtpOptions>(builder.Configuration.GetSection("Authentication:EmailOtp"));
builder.Services.AddSingleton<IAiModelRouter, AiModelRouter>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IEmailTemplateRenderer, LemonInkEmailTemplateRenderer>();
builder.Services.AddScoped<IEmailNotificationService, EmailNotificationService>();
builder.Services.AddScoped<IAuthenticationAuditService, AuthenticationAuditService>();
builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();
builder.Services.AddScoped<IPasswordResetOtpService, PasswordResetOtpService>();
builder.Services.AddScoped<IBookService, BookService>();
builder.Services.AddScoped<IBookUploadService, BookUploadService>();
builder.Services.AddScoped<IBookCoverService, GeneratedBookCoverService>();
builder.Services.Configure<AiSummarizationOptions>(builder.Configuration.GetSection("AI"));
builder.Services.Configure<AudioGenerationOptions>(builder.Configuration.GetSection("Audio"));
builder.Services.AddHttpClient<ITextExtractionService, TextExtractionService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddHttpClient<OpenAiBookSummarizationService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient<GeminiBookSummarizationService>(client =>
{
    // Long-book synthesis can legitimately take longer than a normal chat turn.
    // Avoid cancelling a usable Gemini response and immediately spending another quota attempt.
    client.Timeout = TimeSpan.FromMinutes(6);
});
builder.Services.AddHttpClient<IAudioGenerationService, GeminiAudioGenerationService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(6);
});
builder.Services.AddHttpClient<IChatService, GeminiChatService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient("ExternalAvatars", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LemonInk/1.0");
});
builder.Services.AddScoped<DraftBookSummarizationService>();
builder.Services.AddScoped<IBookSummarizationService, HybridBookSummarizationService>();
builder.Services.AddScoped<IProcessingJobService, ProcessingJobService>();
builder.Services.AddScoped<IBookmarkService, BookmarkService>();
builder.Services.AddScoped<IReadingProgressService, ReadingProgressService>();
builder.Services.AddHostedService<ProcessingJobWorker>();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    // Keep session revocation responsive without validating the stamp on every request.
    options.ValidationInterval = TimeSpan.FromMinutes(2);
});

var externalAuthBuilder = builder.Services.AddAuthentication();
ConfigureGoogleOAuth(externalAuthBuilder, builder.Configuration);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/Login";
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        context.Context.Response.Headers["Pragma"] = "no-cache";
        context.Context.Response.Headers["Expires"] = "0";
    }
});
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.TryAdd("X-Content-Type-Options", "nosniff");
    headers.TryAdd("X-Frame-Options", "DENY");
    headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    headers.TryAdd("Cross-Origin-Opener-Policy", "same-origin-allow-popups");
    headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
    headers.TryAdd(
        "Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com data:; " +
        "img-src 'self' data: blob: https:; " +
        "connect-src 'self'; " +
        "media-src 'self' blob: data:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self';");

    await next();
});
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllerRoute(
    name: "admin",
    pattern: "Admin/{action=Index}/{id?}",
    defaults: new { controller = "Admin" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

if (builder.Configuration.GetValue("SeedData:RunOnStartup", true))
{
    await SeedData.InitializeAsync(app.Services);
}

app.Run();

static string ResolveRequestClientKey(HttpContext context, bool preferUser)
{
    if (preferUser)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return $"user:{userId}";
        }
    }

    return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}

static RateLimitPartition<string> CreateFixedWindowPartition(string key, int permitLimit, TimeSpan window)
{
    return RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            Window = window
        });
}

static void ConfigureGoogleOAuth(AuthenticationBuilder authBuilder, IConfiguration configuration)
{
    var section = configuration.GetSection("Authentication:OAuth:Google");
    var clientId = section["ClientId"];
    var clientSecret = section["ClientSecret"];
    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
    {
        return;
    }

    authBuilder.AddOAuth("Google", options =>
    {
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.CallbackPath = "/signin-google";
        options.AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        options.TokenEndpoint = "https://oauth2.googleapis.com/token";
        options.UserInformationEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
        options.SaveTokens = true;
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
        options.ClaimActions.MapJsonKey("urn:lemonink:avatar", "picture");
        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                context.RunClaimActions(payload.RootElement);
            }
        };
    });
}
