using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MarketHub.Modules.Identity.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MarketHub.Modules.Identity.Services;

/// <summary>Issues JWT access tokens and opaque refresh tokens.</summary>
public interface IAuthTokenService
{
    (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user);
    string NewRefreshToken();
}

public sealed class JwtTokenService(IConfiguration config) : IAuthTokenService
{
    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user)
    {
        var section = config.GetSection("Jwt");
        var minutes = int.TryParse(section["AccessTokenMinutes"], out var m) ? m : 15;
        var expires = DateTime.UtcNow.AddMinutes(minutes);
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("email", user.Email),
            new("role", user.Role.ToString()),
        };
        if (user.Role == UserRole.Vendor) claims.Add(new Claim("vendorId", user.Id.ToString()));
        if (user.Role == UserRole.Admin && user.AdminRole.HasValue) claims.Add(new Claim("adminRole", user.AdminRole.Value.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(section["Key"] ?? throw new InvalidOperationException("Jwt:Key missing")));
        var token = new JwtSecurityToken(
            issuer: section["Issuer"] ?? "MarketHub",
            audience: section["Audience"] ?? "MarketHubClients",
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public string NewRefreshToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>BCrypt password hashing (per-User salt built into the hash).</summary>
public static class PasswordHasher
{
    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
    public static bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}

public sealed record AuthResponse(Guid UserId, string Email, string FullName, string Role, string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken);
