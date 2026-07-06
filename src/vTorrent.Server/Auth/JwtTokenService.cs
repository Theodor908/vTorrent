using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Server.Auth;

public class JwtTokenService
{
    private readonly IOptionsMonitor<ServerSettings> _serverMonitor;

    public JwtTokenService(IOptionsMonitor<ServerSettings> serverMonitor)
    {
        _serverMonitor = serverMonitor;
    }

    public string MintAccessToken(string subject)
    {
        var settings = _serverMonitor.CurrentValue;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtSecret))
        {
            KeyId = "vtorrent-signing-key"
        };
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            },
            expires: DateTime.UtcNow.AddMinutes(settings.JwtAccessTokenLifetimeMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string GenerateJwtSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
