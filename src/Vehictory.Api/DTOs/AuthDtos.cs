namespace Vehictory.Api.DTOs;

public record RegisterRequest(string Email, string Password, string Name);
public record LoginRequest(string Email, string Password);

// RefreshToken is alleen gevuld voor de Android-app (herkend aan de X-Client-Type-header):
// de webapp krijgt de refresh-token uitsluitend als httpOnly cookie, nooit in de JSON-body,
// anders zou een XSS-kwetsbaarheid 'm net zo goed kunnen stelen als de access-token.
public record AuthResponse(string Token, string Email, string Name, string? AvatarDataUrl, bool IsAdmin, string? RefreshToken = null);
public record ProfileResponse(string Email, string Name, string? AvatarDataUrl, bool IsAdmin);
public record SessionResponse(Guid Id, DateTime CreatedAt, DateTime? LastUsedAt, DateTime ExpiresAt, string? UserAgent, string? CreatedByIp, bool IsCurrent);
public record UpdateProfileRequest(string Email, string Name);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record RequestPasswordResetRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);
public record AdminUserResponse(Guid Id, string Email, string Name, bool IsAdmin, DateTime CreatedAt);
public record UpdateAdminUserRequest(string Email, string Name, bool IsAdmin);
