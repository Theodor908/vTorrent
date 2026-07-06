using FluentAssertions;
using Xunit;
using vTorrent.Cli.Commands;

namespace vTorrent.Cli.Tests.Commands;

public class CommandHelperTests
{
    [Fact]
    public void EnrichError_ConnectionError_SuggestsServeAndProfileCheck()
    {
        var enriched = CommandHelper.EnrichErrorMessage(
            "Connection failed: No connection could be made",
            "CONNECTION_ERROR");
        enriched.Should().Contain("vtorrent serve");
        enriched.Should().Contain("server");
    }

    [Fact]
    public void EnrichError_Timeout_SuggestsTimeoutFlag()
    {
        var enriched = CommandHelper.EnrichErrorMessage(
            "Request timed out", "TIMEOUT");
        enriched.Should().Contain("--timeout");
    }

    [Fact]
    public void EnrichError_Unauthorized_SuggestsLogin()
    {
        var enriched = CommandHelper.EnrichErrorMessage(
            "Unauthorized", "UNKNOWN", statusCode: 401);
        enriched.Should().Contain("vtorrent login");
    }

    [Fact]
    public void EnrichError_UnknownError_ReturnsOriginalWithCode()
    {
        var enriched = CommandHelper.EnrichErrorMessage(
            "Some weird error", "WEIRD");
        enriched.Should().Be("Some weird error (WEIRD)");
    }

    [Fact]
    public void EnrichError_IpBanned_ShowsBanHint()
    {
        var enriched = CommandHelper.EnrichErrorMessage(
            "IP temporarily banned", "IP_BANNED", statusCode: 403);
        enriched.Should().Contain("banned");
        enriched.Should().Contain("failed login");
    }

    [Fact]
    public void EnrichError_SecurityViolation_ShowsGenericHint()
    {
        var enriched = CommandHelper.EnrichErrorMessage(
            "Forbidden", "SECURITY_VIOLATION", statusCode: 403);
        enriched.Should().Contain("security policy");
    }
}
