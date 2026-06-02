using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Diagnostics;
using System.Security.Claims;
using ZenRead.Entities;
using ZenRead.Services.Auth;
using ZenRead.Services.Email;

namespace ZenRead.Controllers;

public class AccountController : Controller
{
    private const long MaxAvatarBytes = 30 * 1024 * 1024;
    private const long MaxExternalAvatarBytes = 5 * 1024 * 1024;
    private const string KnownDeviceCookieName = "lemon_known_device";
    private const string GenericOtpSentMessage = "Mã xác minh đã được gửi đến email của bạn.";
    private const string GenericResetOtpSentMessage = "Nếu email đã đăng ký, mã đặt lại mật khẩu sẽ được gửi đến hộp thư của bạn.";
    private const string GenericOtpInvalidMessage = "Mã xác minh không hợp lệ hoặc đã hết hạn.";
    private const string GenericOtpDeliveryFailureMessage = "Chưa gửi được mã qua email. Bạn thử lại sau nhé.";

    private static readonly HashSet<string> AllowedAvatarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif",
        ".heic",
        ".heif",
        ".dng"
    };

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IEmailOtpService _emailOtpService;
    private readonly IPasswordResetOtpService _passwordResetOtpService;
    private readonly IEmailNotificationService _emailNotifications;
    private readonly IAuthenticationAuditService _authenticationAudit;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _deviceProtector;
    private readonly IDataProtector _emailRegistrationProtector;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IEmailOtpService emailOtpService,
        IPasswordResetOtpService passwordResetOtpService,
        IEmailNotificationService emailNotifications,
        IAuthenticationAuditService authenticationAudit,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _environment = environment;
        _configuration = configuration;
        _emailOtpService = emailOtpService;
        _passwordResetOtpService = passwordResetOtpService;
        _emailNotifications = emailNotifications;
        _authenticationAudit = authenticationAudit;
        _httpClientFactory = httpClientFactory;
        _deviceProtector = dataProtectionProvider.CreateProtector("LemonInk.Auth.KnownDevice.v1");
        _emailRegistrationProtector = dataProtectionProvider.CreateProtector("LemonInk.Auth.EmailOtpRegistration.v1");
        _logger = logger;
    }

    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ViewData["Error"] = "Vui lòng nhập đầy đủ email và mật khẩu.";
            return View();
        }

        var user = await _userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            await _authenticationAudit.RecordAsync(
                AuthenticationAuditActions.PasswordLogin,
                false,
                NormalizeAuditEmail(email),
                detail: "unknown-account",
                cancellationToken: HttpContext.RequestAborted);
            ViewData["Error"] = "Email hoặc mật khẩu không đúng.";
            return View();
        }

        var result = await _signInManager.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            await RecordPasswordLoginFailureAsync(user, "locked-out");
            ViewData["Error"] = "Tài khoản đang tạm khóa vì đăng nhập sai nhiều lần. Vui lòng thử lại sau.";
            return View();
        }

        if (result.IsNotAllowed)
        {
            await RecordPasswordLoginFailureAsync(user, "not-allowed");
            ViewData["Error"] = "Email hoặc mật khẩu không đúng.";
            return View();
        }

        if (!result.Succeeded)
        {
            await RecordPasswordLoginFailureAsync(user, "invalid-credentials");
            ViewData["Error"] = "Email hoặc mật khẩu không đúng.";
            return View();
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);
        ClearLegacyAuthCookies();
        await MarkKnownDeviceAndNotifyAsync(user, notify: true, HttpContext.RequestAborted);
        await _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.PasswordLogin,
            true,
            NormalizeAuditEmail(user.Email),
            user.Id,
            cancellationToken: HttpContext.RequestAborted);

        return RedirectToLocal(returnUrl);
    }

    [HttpGet]
    [EnableRateLimiting("auth")]
    public IActionResult ExternalLogin(string provider, string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        var scheme = NormalizeExternalProvider(provider);
        if (scheme is null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            ViewData["Error"] = "Nhà cung cấp đăng nhập này chưa được hỗ trợ.";
            return View("Login");
        }

        if (!IsExternalProviderConfigured(scheme))
        {
            ViewData["ReturnUrl"] = returnUrl;
            ViewData["Error"] = $"Đăng nhập {GetExternalProviderDisplayName(scheme)} chưa được cấu hình Client ID/Secret.";
            return View("Login");
        }

        var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { returnUrl });
        var properties = _signInManager.ConfigureExternalAuthenticationProperties(scheme, redirectUrl);
        return Challenge(properties, scheme);
    }

    [HttpGet]
    public async Task<IActionResult> ExternalLoginCallback(string? returnUrl = null, string? remoteError = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (!string.IsNullOrWhiteSpace(remoteError))
        {
            ViewData["Error"] = $"Đăng nhập bên ngoài bị hủy hoặc lỗi: {remoteError}";
            return View("Login");
        }

        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            ViewData["Error"] = "Không đọc được thông tin đăng nhập bên ngoài. Bạn thử lại nhé.";
            return View("Login");
        }

        var signInResult = await _signInManager.ExternalLoginSignInAsync(
            info.LoginProvider,
            info.ProviderKey,
            isPersistent: true,
            bypassTwoFactor: true);

        if (signInResult.Succeeded)
        {
            var signedInUser = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (signedInUser is not null)
            {
                signedInUser.LastLoginAt = DateTime.UtcNow;
                await _userManager.UpdateAsync(signedInUser);
                await TryImportExternalAvatarAsync(signedInUser, info, HttpContext.RequestAborted);
                await MarkKnownDeviceAndNotifyAsync(signedInUser, notify: true, HttpContext.RequestAborted);
                await _authenticationAudit.RecordAsync(
                    AuthenticationAuditActions.ExternalLogin,
                    true,
                    NormalizeAuditEmail(signedInUser.Email),
                    signedInUser.Id,
                    info.LoginProvider,
                    HttpContext.RequestAborted);
            }

            ClearLegacyAuthCookies();
            return RedirectToLocal(returnUrl);
        }

        var email = ResolveExternalEmail(info);

        var user = await _userManager.FindByEmailAsync(email);
        var isNewAccount = user is null;
        if (user is null)
        {
            user = new ApplicationUser
            {
                FullName = ResolveExternalFullName(info, email),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };

            var createResult = await _userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                ViewData["Error"] = BuildIdentityErrorMessage(createResult);
                return View("Login");
            }

            await _userManager.AddToRoleAsync(user, "Reader");
            await TryImportExternalAvatarAsync(user, info, HttpContext.RequestAborted);
        }
        else
        {
            user.EmailConfirmed = true;
            user.LastLoginAt = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);
            await TryImportExternalAvatarAsync(user, info, HttpContext.RequestAborted);
        }

        var addLoginResult = await _userManager.AddLoginAsync(user, info);
        if (!addLoginResult.Succeeded &&
            !addLoginResult.Errors.Any(error => error.Code.Contains("LoginAlreadyAssociated", StringComparison.OrdinalIgnoreCase)))
        {
            ViewData["Error"] = BuildIdentityErrorMessage(addLoginResult);
            return View("Login");
        }

        await _signInManager.SignInAsync(user, isPersistent: true);
        ClearLegacyAuthCookies();
        await MarkKnownDeviceAndNotifyAsync(user, notify: !isNewAccount, HttpContext.RequestAborted);
        await _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.ExternalLogin,
            true,
            NormalizeAuditEmail(user.Email),
            user.Id,
            info.LoginProvider,
            HttpContext.RequestAborted);
        return RedirectToLocal(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> SendEmailOtp(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || !normalizedEmail.Contains('@'))
        {
            return BadRequest(new { ok = false, message = "Bạn nhập email hợp lệ nhé." });
        }

        try
        {
            var result = await _emailOtpService.SendAsync(normalizedEmail, cancellationToken);
            return Json(new
            {
                ok = true,
                message = GenericOtpSentMessage,
                expiresInSeconds = result.ExpiresInSeconds,
                resendAfterSeconds = result.ResendAfterSeconds
            });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(exception, "SMTP has not been configured for email OTP login.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ok = false, message = GenericOtpDeliveryFailureMessage });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not send email OTP to {Email}.", normalizedEmail);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ok = false, message = GenericOtpDeliveryFailureMessage });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> LoginWithEmailOtp(
        string email,
        string code,
        string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || !normalizedEmail.Contains('@'))
        {
            return BadRequest(new { ok = false, message = "Email không hợp lệ." });
        }

        var result = await _emailOtpService.VerifyAsync(normalizedEmail, code, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { ok = false, message = GenericOtpInvalidMessage });
        }

        var user = await FindUserByLoginEmailAsync(normalizedEmail);
        if (user is null)
        {
            return Json(new
            {
                ok = true,
                requiresRegistration = true,
                registrationToken = CreateEmailRegistrationToken(normalizedEmail)
            });
        }

        user.EmailConfirmed = true;
        user.LastLoginAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);
        await _signInManager.SignInAsync(user, isPersistent: true);
        ClearLegacyAuthCookies();
        await MarkKnownDeviceAndNotifyAsync(user, notify: true, cancellationToken);
        await _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.EmailOtpVerified,
            true,
            NormalizeAuditEmail(user.Email),
            user.Id,
            "session-created",
            cancellationToken);

        return Json(new { ok = true, redirectUrl = ResolveLocalRedirectUrl(returnUrl) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> CompleteEmailOtpRegistration(
        string fullName,
        string registrationToken,
        string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullName) ||
            !TryReadEmailRegistrationToken(registrationToken, out var email))
        {
            return BadRequest(new { ok = false, message = "Phiên xác minh đã hết hạn. Bạn gửi lại mã nhé." });
        }

        var user = await FindUserByLoginEmailAsync(email);
        if (user is not null)
        {
            return BadRequest(new { ok = false, message = "Phiên tạo tài khoản đã được sử dụng. Bạn đăng nhập lại nhé." });
        }

        user = new ApplicationUser
        {
            FullName = fullName.Trim(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            return BadRequest(new { ok = false, message = "Chưa thể tạo tài khoản lúc này. Bạn thử lại nhé." });
        }

        await _userManager.AddToRoleAsync(user, "Reader");
        await _signInManager.SignInAsync(user, isPersistent: true);
        ClearLegacyAuthCookies();
        await MarkKnownDeviceAndNotifyAsync(user, notify: false, cancellationToken);
        await _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.EmailOtpRegistrationCompleted,
            true,
            NormalizeAuditEmail(user.Email),
            user.Id,
            cancellationToken: cancellationToken);

        return Json(new { ok = true, redirectUrl = ResolveLocalRedirectUrl(returnUrl) });
    }

    public IActionResult Register(string? returnUrl = null)
    {
        return RedirectToAction("Login", new { returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(string fullName, string email, string password, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ViewData["Error"] = "Vui lòng nhập đầy đủ thông tin.";
            return View("Login");
        }

        if (password.Length < 12)
        {
            ViewData["Error"] = "Mật khẩu phải có ít nhất 12 ký tự.";
            return View("Login");
        }

        var normalizedEmail = email.Trim();
        var user = new ApplicationUser
        {
            FullName = fullName.Trim(),
            UserName = normalizedEmail,
            Email = normalizedEmail,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };

        var result = await _userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            ViewData["Error"] = result.Errors.Any(error =>
                error.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
                ? "Không thể tạo tài khoản với thông tin này. Bạn có thể đăng nhập hoặc dùng mã một lần."
                : BuildIdentityErrorMessage(result);
            return View("Login");
        }

        await _userManager.AddToRoleAsync(user, "Reader");
        await TrySendVerificationEmailAsync(user, HttpContext.RequestAborted);
        TempData["AccountSuccess"] = "Tài khoản đã được tạo. Hãy kiểm tra email để xác minh trước khi đăng nhập.";
        return RedirectToAction(nameof(Login), new { returnUrl });
    }

    [HttpGet]
    public async Task<IActionResult> ConfirmEmail(string userId, string token)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null || string.IsNullOrWhiteSpace(token))
        {
            TempData["AccountError"] = "Liên kết xác minh email không hợp lệ.";
            return RedirectToAction("Login");
        }

        var result = await _userManager.ConfirmEmailAsync(user, token);
        TempData[result.Succeeded ? "AccountSuccess" : "AccountError"] = result.Succeeded
            ? "Email của bạn đã được xác minh."
            : "Liên kết xác minh email đã hết hạn hoặc không hợp lệ.";

        return User.Identity?.IsAuthenticated == true
            ? RedirectToAction("Profile")
            : RedirectToAction("Login");
    }

    [HttpGet]
    public async Task<IActionResult> ConfirmEmailChange(string userId, string token)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(user.PendingEmail))
        {
            TempData["AccountError"] = "Liên kết xác nhận email mới không hợp lệ.";
            return User.Identity?.IsAuthenticated == true
                ? RedirectToAction("Profile")
                : RedirectToAction("Login");
        }

        var pendingEmail = user.PendingEmail;
        var previousEmail = user.Email;
        var existingUser = await _userManager.FindByEmailAsync(pendingEmail);
        if (existingUser is not null && existingUser.Id != user.Id)
        {
            user.PendingEmail = null;
            user.PendingEmailRequestedAt = null;
            await _userManager.UpdateAsync(user);
            TempData["AccountError"] = "Email này đã được sử dụng bởi tài khoản khác.";
            return User.Identity?.IsAuthenticated == true
                ? RedirectToAction("Profile")
                : RedirectToAction("Login");
        }

        var changeResult = await _userManager.ChangeEmailAsync(user, pendingEmail, token);
        if (!changeResult.Succeeded)
        {
            TempData["AccountError"] = "Liên kết xác nhận email mới đã hết hạn hoặc không hợp lệ.";
            return User.Identity?.IsAuthenticated == true
                ? RedirectToAction("Profile")
                : RedirectToAction("Login");
        }

        var userNameResult = await _userManager.SetUserNameAsync(user, pendingEmail);
        if (!userNameResult.Succeeded)
        {
            _logger.LogWarning("Could not synchronize username after confirmed email change for user {UserId}.", user.Id);
        }

        user.PendingEmail = null;
        user.PendingEmailRequestedAt = null;
        await _userManager.UpdateAsync(user);

        if (User.Identity?.IsAuthenticated == true)
        {
            await _signInManager.RefreshSignInAsync(user);
        }

        await TrySendEmailChangedAsync(previousEmail, user, HttpContext.RequestAborted);
        TempData["AccountSuccess"] = "Email mới đã được xác nhận và cập nhật.";
        return User.Identity?.IsAuthenticated == true
            ? RedirectToAction("Profile")
            : RedirectToAction("Login");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> ResendVerificationEmail(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (user.EmailConfirmed)
        {
            TempData["AccountSuccess"] = "Email của bạn đã được xác minh.";
            return RedirectToAction("Profile");
        }

        await TrySendVerificationEmailAsync(user, cancellationToken);
        TempData["AccountSuccess"] = "Đã gửi lại email xác minh.";
        return RedirectToAction("Profile");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> SendPasswordResetOtp(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || !normalizedEmail.Contains('@'))
        {
            return BadRequest(new { ok = false, message = "Bạn nhập email hợp lệ nhé." });
        }

        var user = await _userManager.FindByEmailAsync(normalizedEmail);
        if (user is null || IsExternalPlaceholderEmail(user.Email))
        {
            return Json(new { ok = true, message = GenericResetOtpSentMessage, expiresInSeconds = 300, resendAfterSeconds = 45 });
        }

        try
        {
            var result = await _passwordResetOtpService.SendAsync(normalizedEmail, cancellationToken);
            return Json(new
            {
                ok = true,
                message = GenericResetOtpSentMessage,
                expiresInSeconds = result.ExpiresInSeconds,
                resendAfterSeconds = result.ResendAfterSeconds
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not send a password reset OTP to {Email}.", normalizedEmail);
            return Json(new { ok = true, message = GenericResetOtpSentMessage, expiresInSeconds = 300, resendAfterSeconds = 45 });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> ResetPasswordWithOtp(
        string email,
        string code,
        string newPassword,
        string confirmPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(newPassword)
            || newPassword != confirmPassword)
        {
            return BadRequest(new { ok = false, message = "Thông tin đặt lại mật khẩu chưa hợp lệ." });
        }

        var user = await _userManager.FindByEmailAsync(email.Trim());
        if (user is null || IsExternalPlaceholderEmail(user.Email))
        {
            return BadRequest(new { ok = false, message = GenericOtpInvalidMessage });
        }

        var verifyResult = await _passwordResetOtpService.VerifyAsync(email, code, cancellationToken);
        if (!verifyResult.Succeeded)
        {
            return BadRequest(new { ok = false, message = GenericOtpInvalidMessage });
        }

        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await _userManager.ResetPasswordAsync(user, resetToken, newPassword);
        if (!resetResult.Succeeded)
        {
            return BadRequest(new { ok = false, message = BuildIdentityErrorMessage(resetResult) });
        }

        await TrySendPasswordChangedAsync(user, cancellationToken);
        await _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.PasswordResetCompleted,
            true,
            NormalizeAuditEmail(user.Email),
            user.Id,
            cancellationToken: cancellationToken);
        return Json(new { ok = true, message = "Đã đổi mật khẩu. Bạn có thể đăng nhập lại." });
    }

    [Authorize]
    public async Task<IActionResult> Profile()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }

        var roles = await _userManager.GetRolesAsync(user);

        var hasPlaceholderEmail = IsExternalPlaceholderEmail(user.Email);

        ViewData["UserName"] = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;
        ViewData["UserEmail"] = hasPlaceholderEmail ? string.Empty : user.Email;
        ViewData["HasPlaceholderEmail"] = hasPlaceholderEmail;
        ViewData["PendingEmail"] = user.PendingEmail;
        ViewData["UserRole"] = roles.Contains("Admin") ? "Quản trị viên" : "Bạn đọc";
        ViewData["AvatarUrl"] = user.AvatarUrl;
        return View();
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateEmail(string email)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }

        var normalizedEmail = email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedEmail) || !normalizedEmail.Contains('@'))
        {
            TempData["AccountError"] = "Bạn nhập một email hợp lệ nhé.";
            return RedirectToAction("Profile");
        }

        if (string.Equals(normalizedEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            TempData["AccountSuccess"] = "Đây đang là email của tài khoản bạn.";
            return RedirectToAction("Profile");
        }

        var existingUser = await _userManager.FindByEmailAsync(normalizedEmail);
        if (existingUser is not null && existingUser.Id != user.Id)
        {
            TempData["AccountError"] = "Email này đã được sử dụng.";
            return RedirectToAction("Profile");
        }

        user.PendingEmail = normalizedEmail;
        user.PendingEmailRequestedAt = DateTime.UtcNow;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            TempData["AccountError"] = BuildIdentityErrorMessage(result);
            return RedirectToAction("Profile");
        }

        try
        {
            var token = await _userManager.GenerateChangeEmailTokenAsync(user, normalizedEmail);
            var confirmationUrl = Url.Action(
                nameof(ConfirmEmailChange),
                "Account",
                new { userId = user.Id, token },
                Request.Scheme);
            if (string.IsNullOrWhiteSpace(confirmationUrl))
            {
                throw new InvalidOperationException("Could not generate change-email confirmation URL.");
            }

            await _emailNotifications.SendAccountVerificationAsync(
                normalizedEmail,
                user.FullName,
                confirmationUrl,
                TimeSpan.FromHours(24),
                HttpContext.RequestAborted);
            TempData["AccountSuccess"] = "Đã gửi liên kết xác nhận đến email mới. Email đăng nhập hiện tại vẫn giữ nguyên đến khi bạn xác nhận.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not send change-email verification for user {UserId}.", user.Id);
            user.PendingEmail = null;
            user.PendingEmailRequestedAt = null;
            await _userManager.UpdateAsync(user);
            TempData["AccountError"] = "Chưa gửi được liên kết xác nhận email mới. Email hiện tại của bạn chưa thay đổi.";
        }

        return RedirectToAction("Profile");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadAvatar(IFormFile? avatar)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }

        if (avatar is null || avatar.Length == 0)
        {
            TempData["AccountError"] = "Bạn chọn một ảnh avatar trước nhé.";
            return RedirectToAction("Profile");
        }

        if (avatar.Length > MaxAvatarBytes)
        {
            TempData["AccountError"] = "Ảnh avatar tối đa 30MB.";
            return RedirectToAction("Profile");
        }

        var extension = Path.GetExtension(avatar.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedAvatarExtensions.Contains(extension))
        {
            TempData["AccountError"] = "Avatar chỉ hỗ trợ JPG, PNG, WEBP, GIF, HEIC, HEIF hoặc DNG.";
            return RedirectToAction("Profile");
        }

        var folder = Path.Combine(_environment.WebRootPath, "uploads", "avatars");
        Directory.CreateDirectory(folder);

        var safeUserId = new string(user.Id.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var normalizedExtension = extension.ToLowerInvariant();
        var shouldConvertAppleImage = normalizedExtension is ".heic" or ".heif" or ".dng";
        var fileName = $"{safeUserId}-{stamp}{(shouldConvertAppleImage ? ".jpg" : normalizedExtension)}";
        var physicalPath = Path.Combine(folder, fileName);

        if (shouldConvertAppleImage)
        {
            var tempPath = Path.Combine(folder, $"{safeUserId}-{stamp}{normalizedExtension}");
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await avatar.CopyToAsync(stream);
            }

            try
            {
                await ConvertAppleImageToJpegAsync(tempPath, physicalPath);
            }
            catch
            {
                DeleteLocalFile(tempPath);
                DeleteLocalFile(physicalPath);
                TempData["AccountError"] = "Không convert được ảnh HEIC/HEIF/DNG. Bạn thử chọn ảnh JPG hoặc PNG nhé.";
                return RedirectToAction("Profile");
            }

            DeleteLocalFile(tempPath);
        }
        else
        {
            await using var stream = System.IO.File.Create(physicalPath);
            await avatar.CopyToAsync(stream);
        }

        var previousAvatarUrl = user.AvatarUrl;
        user.AvatarUrl = $"/uploads/avatars/{fileName}";
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            DeleteLocalFile(physicalPath);
            TempData["AccountError"] = BuildIdentityErrorMessage(result);
            return RedirectToAction("Profile");
        }

        DeleteLocalAvatar(previousAvatarUrl);
        await _signInManager.RefreshSignInAsync(user);
        TempData["AccountSuccess"] = "Đã cập nhật avatar.";
        return RedirectToAction("Profile");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }

        if (string.IsNullOrWhiteSpace(currentPassword) ||
            string.IsNullOrWhiteSpace(newPassword) ||
            string.IsNullOrWhiteSpace(confirmPassword))
        {
            TempData["AccountError"] = "Vui lòng nhập đầy đủ mật khẩu hiện tại và mật khẩu mới.";
            return RedirectToAction("Profile");
        }

        if (newPassword != confirmPassword)
        {
            TempData["AccountError"] = "Mật khẩu mới và xác nhận mật khẩu chưa khớp.";
            return RedirectToAction("Profile");
        }

        if (newPassword.Length < 12)
        {
            TempData["AccountError"] = "Mật khẩu mới phải có ít nhất 12 ký tự.";
            return RedirectToAction("Profile");
        }

        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            TempData["AccountError"] = BuildIdentityErrorMessage(result);
            return RedirectToAction("Profile");
        }

        await _signInManager.RefreshSignInAsync(user);
        await TrySendPasswordChangedAsync(user, HttpContext.RequestAborted);
        await _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.PasswordChanged,
            true,
            NormalizeAuditEmail(user.Email),
            user.Id,
            cancellationToken: HttpContext.RequestAborted);
        TempData["AccountSuccess"] = "Đã đổi mật khẩu.";
        return RedirectToAction("Profile");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeOtherSessions()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await _signInManager.RefreshSignInAsync(user);
        await _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.SessionsRevoked,
            true,
            NormalizeAuditEmail(user.Email),
            user.Id,
            cancellationToken: HttpContext.RequestAborted);

        TempData["AccountSuccess"] = "Đã thu hồi phiên đăng nhập trên các thiết bị khác.";
        return RedirectToAction("Profile");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        ClearLegacyAuthCookies();
        return RedirectToAction("Login");
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        return Redirect(ResolveLocalRedirectUrl(returnUrl));
    }

    private string ResolveLocalRedirectUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action("Index", "Home") ?? "/";
    }

    private async Task<ApplicationUser?> FindUserByLoginEmailAsync(string email)
    {
        return await _userManager.FindByEmailAsync(email)
            ?? await _userManager.FindByNameAsync(email);
    }

    private string CreateEmailRegistrationToken(string email)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        return _emailRegistrationProtector.Protect($"{expiresAt}|{email}");
    }

    private bool TryReadEmailRegistrationToken(string? token, out string email)
    {
        email = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var payload = _emailRegistrationProtector.Unprotect(token);
            var separatorIndex = payload.IndexOf('|');
            if (separatorIndex <= 0 ||
                !long.TryParse(payload[..separatorIndex], out var expiresAt) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt)
            {
                return false;
            }

            email = payload[(separatorIndex + 1)..].Trim();
            return email.Contains('@');
        }
        catch
        {
            return false;
        }
    }

    private void ClearLegacyAuthCookies()
    {
        Response.Cookies.Delete("zen_user");
        Response.Cookies.Delete("zen_email");
        Response.Cookies.Delete("zen_auth_provider");
    }

    private async Task TrySendVerificationEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.Email) || IsExternalPlaceholderEmail(user.Email))
        {
            return;
        }

        try
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var verificationUrl = Url.Action(
                nameof(ConfirmEmail),
                "Account",
                new { userId = user.Id, token },
                Request.Scheme);

            if (!string.IsNullOrWhiteSpace(verificationUrl))
            {
                await _emailNotifications.SendAccountVerificationAsync(
                    user.Email,
                    user.FullName,
                    verificationUrl,
                    TimeSpan.FromHours(24),
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send verification email to user {UserId}.", user.Id);
        }
    }

    private async Task TrySendPasswordChangedAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        try
        {
            await _emailNotifications.SendPasswordChangedAsync(
                user.Email,
                user.FullName,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send password changed notice for user {UserId}.", user.Id);
        }
    }

    private async Task TrySendEmailChangedAsync(string? previousEmail, ApplicationUser user, CancellationToken cancellationToken)
    {
        try
        {
            await _emailNotifications.SendEmailChangedAsync(
                previousEmail,
                user.FullName,
                user.Email ?? string.Empty,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send email changed notice for user {UserId}.", user.Id);
        }
    }

    private async Task MarkKnownDeviceAndNotifyAsync(
        ApplicationUser user,
        bool notify,
        CancellationToken cancellationToken)
    {
        var isKnownDevice = false;
        if (Request.Cookies.TryGetValue(KnownDeviceCookieName, out var cookieValue))
        {
            try
            {
                isKnownDevice = _deviceProtector.Unprotect(cookieValue) == user.Id;
            }
            catch
            {
                isKnownDevice = false;
            }
        }

        if (isKnownDevice)
        {
            return;
        }

        Response.Cookies.Append(
            KnownDeviceCookieName,
            _deviceProtector.Protect(user.Id),
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });

        if (!notify)
        {
            return;
        }

        var deviceName = ResolveDeviceName(Request.Headers["User-Agent"].ToString());
        var remoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var location = string.IsNullOrWhiteSpace(remoteAddress) ? "Không xác định" : $"IP {remoteAddress}";

        try
        {
            await _authenticationAudit.RecordAsync(
                AuthenticationAuditActions.NewDeviceLogin,
                true,
                NormalizeAuditEmail(user.Email),
                user.Id,
                $"{deviceName}; {location}",
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record new-device audit for user {UserId}.", user.Id);
        }

        try
        {
            await _emailNotifications.SendNewDeviceLoginAsync(
                user.Email,
                user.FullName,
                deviceName,
                location,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send new-device notice for user {UserId}.", user.Id);
        }
    }

    private static string ResolveDeviceName(string userAgent)
    {
        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            return "Trình duyệt trên iPhone";
        }

        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Trình duyệt trên Android";
        }

        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
        {
            return "Trình duyệt trên macOS";
        }

        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "Trình duyệt trên Windows";
        }

        return "Trình duyệt mới";
    }

    private void DeleteLocalAvatar(string? avatarUrl)
    {
        if (!IsLocalAvatar(avatarUrl))
        {
            return;
        }

        var fileName = Path.GetFileName(avatarUrl);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var path = Path.Combine(_environment.WebRootPath, "uploads", "avatars", fileName);
        DeleteLocalFile(path);
    }

    private async Task TryImportExternalAvatarAsync(
        ApplicationUser user,
        ExternalLoginInfo info,
        CancellationToken cancellationToken)
    {
        if (IsLocalAvatar(user.AvatarUrl))
        {
            return;
        }

        var avatarUrl = ResolveExternalAvatarUrl(info);
        if (string.IsNullOrWhiteSpace(avatarUrl) ||
            !Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri) ||
            avatarUri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        string? physicalPath = null;
        try
        {
            var client = _httpClientFactory.CreateClient("ExternalAvatars");
            using var response = await client.GetAsync(
                avatarUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaxExternalAvatarBytes)
            {
                return;
            }

            var extension = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => null
            };
            if (extension is null)
            {
                return;
            }

            var folder = Path.Combine(_environment.WebRootPath, "uploads", "avatars");
            Directory.CreateDirectory(folder);
            var safeUserId = new string(user.Id.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
            var fileName = $"{safeUserId}-external-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
            physicalPath = Path.Combine(folder, fileName);

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = System.IO.File.Create(physicalPath))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaxExternalAvatarBytes)
                    {
                        throw new InvalidDataException("External avatar exceeds size limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
            }

            var previousAvatarUrl = user.AvatarUrl;
            user.AvatarUrl = $"/uploads/avatars/{fileName}";
            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                user.AvatarUrl = previousAvatarUrl;
                DeleteLocalFile(physicalPath);
                return;
            }

            DeleteLocalAvatar(previousAvatarUrl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(physicalPath))
            {
                DeleteLocalFile(physicalPath);
            }

            _logger.LogWarning(exception, "Could not import external avatar for user {UserId}.", user.Id);
        }
    }

    private static bool IsLocalAvatar(string? avatarUrl)
    {
        return !string.IsNullOrWhiteSpace(avatarUrl) &&
            avatarUrl.StartsWith("/uploads/avatars/", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteLocalFile(string path)
    {
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task ConvertAppleImageToJpegAsync(string inputPath, string outputPath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException("HEIC conversion is only available on macOS.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/sips",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("format");
        startInfo.ArgumentList.Add("jpeg");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Cannot start image converter.");
        }

        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !System.IO.File.Exists(outputPath))
        {
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
        }
    }

    private static string BuildIdentityErrorMessage(IdentityResult result)
    {
        var descriptions = result.Errors
            .Select(MapIdentityError)
            .Where(description => !string.IsNullOrWhiteSpace(description))
            .ToList();

        return descriptions.Count == 0
            ? "Không thể xử lý tài khoản. Vui lòng thử lại."
            : string.Join(" ", descriptions);
    }

    private bool IsExternalProviderConfigured(string provider)
    {
        var section = _configuration.GetSection($"Authentication:OAuth:{provider}");
        return !string.IsNullOrWhiteSpace(section["ClientId"]) &&
            !string.IsNullOrWhiteSpace(section["ClientSecret"]);
    }

    private static string? NormalizeExternalProvider(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            "google" => "Google",
            _ => null
        };
    }

    private static string GetExternalProviderDisplayName(string provider)
    {
        return NormalizeExternalProvider(provider) switch
        {
            "Google" => "Google",
            _ => provider
        };
    }

    private static string ResolveExternalEmail(ExternalLoginInfo info)
    {
        var email = info.Principal.FindFirstValue(ClaimTypes.Email)?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        var provider = info.LoginProvider.Trim().ToLowerInvariant();
        var providerKey = new string(info.ProviderKey
            .Where(char.IsLetterOrDigit)
            .Take(80)
            .ToArray());

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            providerKey = Guid.NewGuid().ToString("N");
        }

        return $"{provider}-{providerKey}@external.lemonink.local";
    }

    private static bool IsExternalPlaceholderEmail(string? email)
    {
        return !string.IsNullOrWhiteSpace(email) &&
            email.EndsWith("@external.lemonink.local", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExternalFullName(ExternalLoginInfo info, string email)
    {
        var name = info.Principal.FindFirstValue(ClaimTypes.Name);
        if (!string.IsNullOrWhiteSpace(name) && !name.Contains('@'))
        {
            return name.Trim();
        }

        return email.Split('@')[0].Trim();
    }

    private static string? ResolveExternalAvatarUrl(ExternalLoginInfo info)
    {
        var avatarUrl = info.Principal.FindFirstValue("urn:lemonink:avatar");
        return string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
    }

    private static string MapIdentityError(IdentityError error)
    {
        return error.Code switch
        {
            "PasswordMismatch" => "Mật khẩu hiện tại không đúng.",
            "PasswordTooShort" => "Mật khẩu quá ngắn.",
            "DuplicateEmail" => "Email này đã được sử dụng.",
            "DuplicateUserName" => "Email này đã được sử dụng.",
            _ => string.IsNullOrWhiteSpace(error.Description)
                ? "Không thể xử lý tài khoản. Vui lòng thử lại."
                : error.Description
        };
    }

    private Task RecordPasswordLoginFailureAsync(ApplicationUser user, string detail)
    {
        return _authenticationAudit.RecordAsync(
            AuthenticationAuditActions.PasswordLogin,
            false,
            NormalizeAuditEmail(user.Email),
            user.Id,
            detail,
            HttpContext.RequestAborted);
    }

    private static string? NormalizeAuditEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToUpperInvariant();
    }
}
