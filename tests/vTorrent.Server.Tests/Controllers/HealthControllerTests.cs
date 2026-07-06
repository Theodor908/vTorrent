// tests/vTorrent.Server.Tests/Controllers/HealthControllerTests.cs
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace vTorrent.Server.Tests.Controllers;

public class HealthControllerTests
{
    [Fact]
    public void GetHealth_ReturnsOkWithStatus()
    {
        var controller = new vTorrent.Server.Controllers.HealthController();
        var result = controller.GetHealth() as OkObjectResult;

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);
    }
}
