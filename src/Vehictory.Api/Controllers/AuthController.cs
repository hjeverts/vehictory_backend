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

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
        if (request.Password.Length < 8)
            return BadRequest("Het wachtwoord moet minimaal 8 tekens bevatten.");
        if (await db.Users.AnyAsync(u => u.Email == request.Email))
            return Conflict("Er bestaat al een account met dit e-mailadres.");

        var user = new User
        {
            Email = request.Email.Trim().ToLowerInvariant(),
            Name = request.Name,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = jwtService.GenerateToken(user);
        return Ok(ToAuthResponse(user, token));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var attemptsKey = $"login-attempts:{email}";
        if (cache.TryGetValue<int>(attemptsKey, out var attempts) && attempts >= MaxFailedLoginAttempts)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "Te veel mislukte inlogpogingen. Probeer het over enkele minuten opnieuw.");

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            cache.Set(attemptsKey, attempts + 1, LoginLockoutWindow);
            return Unauthorized("Ongeldige inloggegevens.");
        }

        cache.Remove(attemptsKey);
        var token = jwtService.GenerateToken(user);
        return Ok(ToAuthResponse(user, token));
    }

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

    private static AuthResponse ToAuthResponse(User user, string token) =>
        new(token, user.Email, user.Name, ToDataUrl(user.AvatarContentType, user.Avatar), user.IsAdmin);

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
