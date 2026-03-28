using FluentAssertions;
using StreamTitleService.Domain.Exceptions;
using StreamTitleService.Domain.ValueObjects;
using Xunit;

namespace StreamTitleService.Tests.Domain;

public class LocationTests
{
    [Theory]
    [InlineData("virtual")]
    [InlineData("st. mary and st. joseph")]
    [InlineData("st. anthony chapel")]
    public void Create_WithKnownLocation_ShouldSucceed(string value)
    {
        var location = new Location(value);
        location.Value.Should().Be(value);
    }

    [Theory]
    [InlineData("Virtual")]
    [InlineData("ST. MARY AND ST. JOSEPH")]
    [InlineData("St. Anthony Chapel")]
    public void Create_WithMixedCase_ShouldNormalizeToLowercase(string value)
    {
        var location = new Location(value);
        location.Value.Should().Be(value.ToLowerInvariant());
    }

    [Theory]
    [InlineData("unknown-location")]
    [InlineData("")]
    [InlineData("holy cross")]
    public void Create_WithUnknownLocation_ShouldThrow(string value)
    {
        var act = () => new Location(value);
        act.Should().Throw<UnknownLocationException>()
            .Which.LocationValue.Should().Be(value.ToLowerInvariant());
    }

    [Fact]
    public void Create_WithNull_ShouldThrow()
    {
        var act = () => new Location(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Equals_SameLowercaseValue_ShouldBeEqual()
    {
        var a = new Location("virtual");
        var b = new Location("Virtual");
        a.Should().Be(b);
    }
}
