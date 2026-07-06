namespace vTorrent.Server.Models;

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
