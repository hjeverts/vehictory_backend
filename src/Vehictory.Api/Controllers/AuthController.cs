using Vehictory.Api.Data;
using Vehictory.Api.DTOs;
using Vehictory.Api.Models;
using Vehictory.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Vehictory.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    VehictoryDbContext db,
    JwtTokenService jwtService,
    RefreshTokenService refreshTokenService,
    EmailService emailService,
    IMemoryCache cache,
    ILogger<AuthController> logger) : ControllerBase
{
    private const long MaxImageSize = 2 * 1024 * 1024;
    private const int AvatarMaxDimension = 512;
    private const int JpegQuality = 82;
    private const int ThumbnailJpegQuality = 75;
    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LoginLockoutWindow = TimeSpan.FromMinutes(15);
    private const int MaxRegisterAttemptsPerIp = 10;
    private static readonly TimeSpan RegisterWindow = TimeSpan.FromHours(1);
    private const int MaxPasswordResetRequestsPerEmail = 5;
    private static readonly TimeSpan PasswordResetWindow = TimeSpan.FromHours(1);

    // Alleen de Android-app stuurt deze header mee; de webapp niet, en krijgt de
    // refresh-token dus uitsluitend via de httpOnly cookie (zie IssueSessionAsync).
    private const string ClientTypeHeader = "X-Client-Type";
    private const string RefreshTokenHeader = "X-Refresh-Token";
    private const string RefreshCookieName = "vehictory_refresh";

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var registerKey = $"register-attempts:{GetClientIp() ?? "unknown"}";
        if (cache.TryGetValue<int>(registerKey, out var registerAttempts) && registerAttempts >= MaxRegisterAttemptsPerIp)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "Te veel registratiepogingen vanaf dit adres. Probeer het later opnieuw.");
        cache.Set(registerKey, registerAttempts + 1, RegisterWindow);

        if (request.Password.Length < 8)
            return BadRequest("Het wachtwoord moet minimaal 8 tekens bevatten.");
        if (await db.Users.AnyAsync(u => u.Email == request.Email, cancellationToken))
            return Conflict("Er bestaat al een account met dit e-mailadres.");

        var user = new User
        {
            Email = request.Email.Trim().ToLowerInvariant(),
            Name = request.Name,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(await IssueSessionAsync(user, cancellationToken));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var attemptsKey = $"login-attempts:{email}";
        if (cache.TryGetValue<int>(attemptsKey, out var attempts) && attempts >= MaxFailedLoginAttempts)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "Te veel mislukte inlogpogingen. Probeer het over enkele minuten opnieuw.");

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            cache.Set(attemptsKey, attempts + 1, LoginLockoutWindow);
            return Unauthorized("Ongeldige inloggegevens.");
        }

        cache.Remove(attemptsKey);
        return Ok(await IssueSessionAsync(user, cancellationToken));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken cancellationToken)
    {
        var incoming = GetIncomingRefreshToken();
        if (incoming is null) return Unauthorized();

        var outcome = await refreshTokenService.RotateAsync(incoming, GetClientIp(), GetUserAgent(), cancellationToken);
        if (outcome.Result != RefreshResult.Success || outcome.Entity is null || outcome.RawToken is null)
        {
            ClearRefreshCookie();
            return Unauthorized("Sessie verlopen, log opnieuw in.");
        }

        var user = await db.Users.SingleAsync(u => u.Id == outcome.Entity.UserId, cancellationToken);
        SetRefreshCookie(outcome.RawToken, outcome.Entity.ExpiresAt);
        var accessToken = jwtService.GenerateToken(user);
        return Ok(ToAuthResponse(user, accessToken, IsAndroidClient() ? outcome.RawToken : null));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var incoming = GetIncomingRefreshToken();
        if (incoming is not null)
            await refreshTokenService.RevokeAsync(incoming, cancellationToken);
        ClearRefreshCookie();
        return NoContent();
    }

    [Authorize]
    [HttpGet("sessions")]
    public async Task<ActionResult<List<SessionResponse>>> GetSessions(CancellationToken cancellationToken)
    {
        var incoming = GetIncomingRefreshToken();
        var currentHash = incoming is null ? null : RefreshTokenService.Hash(incoming);
        var sessions = await refreshTokenService.ListActiveAsync(this.GetUserId(), cancellationToken);

        return Ok(sessions.Select(s => new SessionResponse(
            s.Id,
            s.CreatedAt,
            s.LastUsedAt,
            s.ExpiresAt,
            s.UserAgent,
            s.CreatedByIp,
            currentHash is not null && currentHash.SequenceEqual(s.TokenHash))).ToList());
    }

    [Authorize]
    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken cancellationToken)
    {
        var revoked = await refreshTokenService.RevokeByIdAsync(id, this.GetUserId(), cancellationToken);
        if (!revoked) return NotFound();
        return NoContent();
    }

    private async Task<AuthResponse> IssueSessionAsync(User user, CancellationToken cancellationToken)
    {
        var (rawRefreshToken, entity) = await refreshTokenService.IssueAsync(
            user.Id, GetClientIp(), GetUserAgent(), cancellationToken);
        SetRefreshCookie(rawRefreshToken, entity.ExpiresAt);
        var accessToken = jwtService.GenerateToken(user);
        return ToAuthResponse(user, accessToken, IsAndroidClient() ? rawRefreshToken : null);
    }

    private bool IsAndroidClient() =>
        string.Equals(Request.Headers[ClientTypeHeader].ToString(), "android", StringComparison.OrdinalIgnoreCase);

    private string? GetIncomingRefreshToken()
    {
        var headerValue = Request.Headers[RefreshTokenHeader].ToString();
        if (!string.IsNullOrEmpty(headerValue)) return headerValue;
        return Request.Cookies.TryGetValue(RefreshCookieName, out var cookieValue) ? cookieValue : null;
    }

    private void SetRefreshCookie(string rawToken, DateTime expiresAt)
    {
        Response.Cookies.Append(RefreshCookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            Expires = new DateTimeOffset(expiresAt, TimeSpan.Zero),
        });
    }

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions { Path = "/api/auth" });

    private string? GetClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? GetUserAgent() => Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    [HttpPost("password-reset")]
    public async Task<IActionResult> RequestPasswordReset(
        RequestPasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        if (!emailService.IsConfigured)
        {
            logger.LogError("Wachtwoordreset aangevraagd terwijl e-mail niet is geconfigureerd.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Wachtwoord resetten is tijdelijk niet beschikbaar.");
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var resetKey = $"password-reset-attempts:{email}";
        if (cache.TryGetValue<int>(resetKey, out var resetAttempts) && resetAttempts >= MaxPasswordResetRequestsPerEmail)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "Te veel reset-aanvragen voor dit e-mailadres. Probeer het later opnieuw.");
        cache.Set(resetKey, resetAttempts + 1, PasswordResetWindow);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
            return Accepted();

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        user.PasswordResetTokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        user.PasswordResetExpiresAt = DateTime.UtcNow.AddHours(1);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await emailService.SendPasswordResetAsync(user.Email, token, cancellationToken);
        }
        catch (SmtpException exception)
        {
            user.PasswordResetTokenHash = null;
            user.PasswordResetExpiresAt = null;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Versturen van wachtwoordreset naar {Email} mislukt.", user.Email);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Wachtwoord resetten is tijdelijk niet beschikbaar.");
        }

        return Accepted();
    }

    [HttpPost("password-reset/confirm")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (request.NewPassword.Length < 8)
            return BadRequest("Het nieuwe wachtwoord moet minimaal 8 tekens bevatten.");

        var user = await db.Users.SingleOrDefaultAsync(
            u => u.Email == request.Email.Trim().ToLowerInvariant(),
            cancellationToken);
        if (user?.PasswordResetTokenHash is null
            || user.PasswordResetExpiresAt <= DateTime.UtcNow
            || !TryGetTokenHash(request.Token, out var tokenHash)
            || !CryptographicOperations.FixedTimeEquals(tokenHash, user.PasswordResetTokenHash))
            return BadRequest("Deze resetlink is ongeldig of verlopen.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [Authorize]
    [HttpGet("profile")]
    public async Task<ActionResult<ProfileResponse>> GetProfile()
    {
        var user = await GetCurrentUser();
        return Ok(new ProfileResponse(user.Email, user.Name, ToDataUrl(user.AvatarContentType, user.Avatar), user.IsAdmin));
    }

    [Authorize]
    [HttpPut("profile")]
    public async Task<ActionResult<AuthResponse>> UpdateProfile(UpdateProfileRequest request)
    {
        var user = await GetCurrentUser();
        var email = request.Email.Trim().ToLowerInvariant();
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name))
            return BadRequest("Naam en e-mailadres zijn verplicht.");
        if (await db.Users.AnyAsync(u => u.Email == email && u.Id != user.Id))
            return Conflict("Er bestaat al een account met dit e-mailadres.");

        user.Email = email;
        user.Name = name;
        await db.SaveChangesAsync();
        return Ok(ToAuthResponse(user, jwtService.GenerateToken(user)));
    }

    [Authorize]
    [HttpPut("profile/password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await GetCurrentUser();
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest("Het huidige wachtwoord is onjuist.");
        if (request.NewPassword.Length < 8)
            return BadRequest("Het nieuwe wachtwoord moet minimaal 8 tekens bevatten.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize]
    [HttpPut("profile/avatar")]
    public async Task<ActionResult<ProfileResponse>> UpdateAvatar(IFormFile file)
    {
        var image = await ReadImage(file, AvatarMaxDimension);
        if (image.Error is not null) return BadRequest(image.Error);

        var user = await GetCurrentUser();
        user.Avatar = image.Content;
        user.AvatarContentType = image.ContentType;
        await db.SaveChangesAsync();
        return Ok(new ProfileResponse(user.Email, user.Name, ToDataUrl(user.AvatarContentType, user.Avatar), user.IsAdmin));
    }

    private async Task<User> GetCurrentUser() =>
        await db.Users.SingleAsync(u => u.Id == this.GetUserId());

    private static AuthResponse ToAuthResponse(User user, string token, string? refreshToken = null) =>
        new(token, user.Email, user.Name, ToDataUrl(user.AvatarContentType, user.Avatar), user.IsAdmin, refreshToken);

    // Decodeert de upload, corrigeert EXIF-rotatie en hercomprimeert naar JPEG op maxDimension
    // (langste zijde, in pixels). Geef thumbnailDimension mee om ook een kleine variant te
    // laten meegenereren (bv. voor lijstweergaves die geen volledige foto nodig hebben).
    internal static async Task<(byte[]? Content, string? ContentType, byte[]? Thumbnail, string? Error)> ReadImage(
        IFormFile? file, int maxDimension, int? thumbnailDimension = null)
    {
        if (file is null || file.Length == 0) return (null, null, null, "Selecteer een afbeelding.");
        if (file.Length > MaxImageSize) return (null, null, null, "De afbeelding mag maximaal 2 MB groot zijn.");

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var raw = memory.ToArray();
        var isValidFormat = raw switch
        {
            [0xFF, 0xD8, ..] => true,
            [0x89, 0x50, 0x4E, 0x47, ..] => true,
            [0x52, 0x49, 0x46, 0x46, ..] when raw.Length >= 12
                && raw.AsSpan(8, 4).SequenceEqual("WEBP"u8) => true,
            _ => false,
        };
        if (!isValidFormat) return (null, null, null, "Gebruik een JPEG-, PNG- of WebP-afbeelding.");

        try
        {
            var (content, contentType, thumbnail) = ProcessImage(raw, maxDimension, thumbnailDimension);
            return (content, contentType, thumbnail, null);
        }
        catch (ImageFormatException)
        {
            return (null, null, null, "Gebruik een JPEG-, PNG- of WebP-afbeelding.");
        }
    }

    // Decodeert al-gevalideerde afbeeldingsbytes, corrigeert EXIF-rotatie en hercomprimeert naar
    // JPEG op maxDimension (langste zijde, in pixels), met optioneel een kleinere thumbnail-variant.
    // Wordt zowel gebruikt bij nieuwe uploads (ReadImage) als bij het eenmalig backfillen van
    // bestaande, vóór deze functionaliteit opgeslagen foto's (zie Program.cs).
    internal static (byte[] Content, string ContentType, byte[]? Thumbnail) ProcessImage(
        byte[] raw, int maxDimension, int? thumbnailDimension = null)
    {
        using var image = Image.Load(raw);
        image.Mutate(x => x.AutoOrient());

        var content = EncodeJpeg(image, maxDimension, JpegQuality);
        var thumbnail = thumbnailDimension is int t ? EncodeJpeg(image, t, ThumbnailJpegQuality) : null;
        return (content, "image/jpeg", thumbnail);
    }

    private static byte[] EncodeJpeg(Image source, int maxDimension, int quality)
    {
        using var resized = source.Clone(x =>
        {
            if (source.Width > maxDimension || source.Height > maxDimension)
                x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxDimension, maxDimension) });
        });
        using var output = new MemoryStream();
        resized.SaveAsJpeg(output, new JpegEncoder { Quality = quality });
        return output.ToArray();
    }

    internal static string? ToDataUrl(string? contentType, byte[]? content) =>
        content is null || contentType is null ? null : $"data:{contentType};base64,{Convert.ToBase64String(content)}";

    private static bool TryGetTokenHash(string token, out byte[] hash)
    {
        hash = [];
        try
        {
            var tokenBytes = WebEncoders.Base64UrlDecode(token);
            if (tokenBytes.Length != 32) return false;

            hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
