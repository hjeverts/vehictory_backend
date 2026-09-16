using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Vehictory.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace Vehictory.Api.Services;

public class JwtOptions
{
    public required string Key { get; set; }
    public required string Issuer { get; set; }
    public required string Audience { get; set; }
    // Kort houden: de refresh-token (zie RefreshTokenService) zorgt voor een lange sessie
    // zonder opnieuw inloggen, en is intrekbaar; deze access-token is dat niet.
    public int ExpiryMinutes { get; set; } = 15;
}

public class JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public string GenerateToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("name", user.Name),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
