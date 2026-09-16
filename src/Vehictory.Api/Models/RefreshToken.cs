namespace Vehictory.Api.Models;

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid UserId { get; set; }
    public User? User { get; set; }

    // Nooit de ruwe token opslaan, alleen de hash (zelfde patroon als PasswordResetTokenHash).
    public required byte[] TokenHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public required DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    // Gezet zodra deze token via /auth/refresh is vervangen door een nieuwe (rotation).
    // Wordt gebruikt om hergebruik van een al-geroteerde token te detecteren (mogelijke diefstal).
    public Guid? ReplacedByTokenId { get; set; }
    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
