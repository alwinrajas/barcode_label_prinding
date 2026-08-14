using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BarcodePrinter.Application.Auth;
using Microsoft.IdentityModel.Tokens;

namespace BarcodePrinter.Api.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "BarcodePrinter";
    public string Audience { get; set; } = "BarcodePrinter";
    /// <summary>≥256-bit key. Generated at install in production (§19.4);
    /// never defaulted here — a missing key must fail loudly.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
}

public static class AppClaimTypes
{
    public const string UserId = "sub";
    public const string Username = "username";
    public const string SecurityStamp = "sstamp";
    public const string Permission = "perm";
    public const string Role = ClaimTypes.Role;
}

/// <summary>Issues short-lived access tokens (15 min) carrying roles,
/// permission codes and the security stamp (blueprint §19.3 / B-10).</summary>
public sealed class JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _opt = options.Value;

    public (string Token, DateTime ExpiresUtc) Issue(AuthResult auth)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_opt.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(AppClaimTypes.UserId, auth.User.Id.ToString()),
            new(AppClaimTypes.Username, auth.User.Username),
            new(AppClaimTypes.SecurityStamp, auth.User.SecurityStamp),
        };
        claims.AddRange(auth.Roles.Select(r => new Claim(AppClaimTypes.Role, r)));
        claims.AddRange(auth.Permissions.Select(p => new Claim(AppClaimTypes.Permission, p)));

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
