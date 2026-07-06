using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using vTorrent.Server.Services;

namespace vTorrent.Server.Tests.Services;

public class SettingsRedactorTests
{
    private readonly SettingsRedactor _redactor = new();

    [Fact]
    public void Redact_MasksKnownSensitiveFields()
    {
        var json = JsonNode.Parse("""{"jwtSecret":"mysecret","listenPort":8080}""")!.AsObject();
        _redactor.Redact(json);
        json["jwtSecret"]!.GetValue<string>().Should().Be("***");
        json["listenPort"]!.GetValue<int>().Should().Be(8080);
    }

    [Fact]
    public void Redact_MasksFieldsContainingPasswordCaseInsensitive()
    {
        var json = JsonNode.Parse("""{"httpsCertPassword":"certpass","localPasswordHash":"$2a$hash"}""")!.AsObject();
        _redactor.Redact(json);
        json["httpsCertPassword"]!.GetValue<string>().Should().Be("***");
        json["localPasswordHash"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Redact_HandlesNestedSettingsGroups()
    {
        var json = JsonNode.Parse("""{"server":{"jwtSecret":"secret","listenPort":8080},"proxy":{"password":"proxypass"}}""")!.AsObject();
        _redactor.Redact(json);
        json["server"]!["jwtSecret"]!.GetValue<string>().Should().Be("***");
        json["server"]!["listenPort"]!.GetValue<int>().Should().Be(8080);
        json["proxy"]!["password"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void StripRedactedFields_RemovesStarValues()
    {
        var json = JsonNode.Parse("""{"jwtSecret":"***","listenPort":9090}""")!.AsObject();
        _redactor.StripRedactedFields(json);
        json.ContainsKey("jwtSecret").Should().BeFalse();
        json["listenPort"]!.GetValue<int>().Should().Be(9090);
    }

    [Fact]
    public void StripRedactedFields_KeepsEmptyStringForClearing()
    {
        var json = JsonNode.Parse("""{"jwtSecret":"","listenPort":9090}""")!.AsObject();
        _redactor.StripRedactedFields(json);
        json.ContainsKey("jwtSecret").Should().BeTrue();
        json["jwtSecret"]!.GetValue<string>().Should().Be("");
    }
}
