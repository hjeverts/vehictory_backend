using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Vehictory.Api.Data;
using Vehictory.Api.Models;

namespace Vehictory.Api.Services;

public enum RefreshResult { Success, NotFound, Expired, Reused }

public record RefreshOutcome(RefreshResult Result, string? RawToken, RefreshToken? Entity);

public class RefreshTokenService(VehictoryDbContext db)
{
    private const int ExpiryDays = 60;

    public async Task<(string RawToken, RefreshToken Entity)> IssueAsync(
        Guid userId, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var raw = GenerateRaw();
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(ExpiryDays),
            CreatedByIp = ip,
            UserAgent = Truncate(userAgent, 256),
        };
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return (raw, entity);
    }

    // Valideert en roteert een refresh-token (single-use): de aangeboden token wordt
    // ingetrokken en een nieuwe wordt uitgegeven. Als een al-ingetrokken (dus al eerder
    // geroteerde) token opnieuw wordt aangeboden, is dat een teken van mogelijke diefstal
    // (bv. een gekopieerde cookie) en worden voor de zekerheid alle sessies van de
    // gebruiker ingetrokken.
    public async Task<RefreshOutcome> RotateAsync(
        string rawToken, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var entity = await db.RefreshTokens.SingleOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (entity is null)
            return new RefreshOutcome(RefreshResult.NotFound, null, null);

        if (entity.RevokedAt is not null)
        {
            await RevokeAllForUserAsync(entity.UserId, ct);
            return new RefreshOutcome(RefreshResult.Reused, null, null);
        }

        if (entity.ExpiresAt <= DateTime.UtcNow)
            return new RefreshOutcome(RefreshResult.Expired, null, null);

        var (newRaw, newEntity) = await IssueAsync(entity.UserId, ip, userAgent, ct);
        entity.RevokedAt = DateTime.UtcNow;
        entity.ReplacedByTokenId = newEntity.Id;
        entity.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new RefreshOutcome(RefreshResult.Success, newRaw, newEntity);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var entity = await db.RefreshTokens.SingleOrDefaultAsync(r => r.TokenHash == hash, ct);
        if (entity is null || entity.RevokedAt is not null) return;
        entity.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RevokeByIdAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var entity = await db.RefreshTokens.SingleOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (entity is null || entity.RevokedAt is not null) return false;
        entity.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var active = await db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in active) token.RevokedAt = DateTime.UtcNow;
        if (active.Count > 0) await db.SaveChangesAsync(ct);
    }

    public async Task<List<RefreshToken>> ListActiveAsync(Guid userId, CancellationToken ct = default) =>
        await db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null && r.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(r => r.LastUsedAt ?? r.CreatedAt)
            .ToListAsync(ct);

    public static string GenerateRaw() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string rawToken) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken));

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
