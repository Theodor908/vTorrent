namespace vTorrent.Server.Models;

public record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn);
