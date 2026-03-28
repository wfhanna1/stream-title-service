using FluentAssertions;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Configuration;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class LocationPlatformMappingTests
{
    private readonly LocationPlatformMapping _mapping = new();

    [Theory]
    [InlineData("virtual", "restream")]
    [InlineData("st. mary and st. joseph", "restream")]
    [InlineData("st. anthony chapel", "youtube")]
    public void GetPlatform_KnownLocation_ShouldReturnCorrectPlatform(
        string location, string expectedPlatform)
    {
        var loc = new Location(location);
        var platform = _mapping.GetPlatform(loc);
        platform.Value.Should().Be(expectedPlatform);
    }
}
