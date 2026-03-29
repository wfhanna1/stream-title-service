using FluentAssertions;
using Moq;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class LazyYouTubeServiceWrapperTests
{
    [Fact]
    public void Constructor_DoesNotCallFactory()
    {
        var factoryCalled = false;
        Func<Task<IYouTubeServiceWrapper>> factory = () =>
        {
            factoryCalled = true;
            return Task.FromResult(new Mock<IYouTubeServiceWrapper>().Object);
        };

        var wrapper = new LazyYouTubeServiceWrapper(factory);

        factoryCalled.Should().BeFalse("factory should not be called at construction time");
    }

    [Fact]
    public async Task FirstApiCall_TriggersFactory()
    {
        var factoryCalled = false;
        var inner = new Mock<IYouTubeServiceWrapper>();
        inner.Setup(y => y.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_test");

        Func<Task<IYouTubeServiceWrapper>> factory = () =>
        {
            factoryCalled = true;
            return Task.FromResult(inner.Object);
        };

        var wrapper = new LazyYouTubeServiceWrapper(factory);
        await wrapper.GetMyChannelIdAsync(CancellationToken.None);

        factoryCalled.Should().BeTrue("first API call should trigger lazy initialization");
    }

    [Fact]
    public async Task SecondApiCall_DoesNotCallFactoryAgain()
    {
        var callCount = 0;
        var inner = new Mock<IYouTubeServiceWrapper>();
        inner.Setup(y => y.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_test");
        inner.Setup(y => y.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>());

        Func<Task<IYouTubeServiceWrapper>> factory = () =>
        {
            callCount++;
            return Task.FromResult(inner.Object);
        };

        var wrapper = new LazyYouTubeServiceWrapper(factory);
        await wrapper.GetMyChannelIdAsync(CancellationToken.None);
        await wrapper.ListActiveBroadcastsAsync(CancellationToken.None);

        callCount.Should().Be(1, "factory should only be called once");
    }

    [Fact]
    public async Task FactoryThrows_PropagatesException()
    {
        Func<Task<IYouTubeServiceWrapper>> factory = () =>
            throw new InvalidOperationException("Blob storage unavailable");

        var wrapper = new LazyYouTubeServiceWrapper(factory);

        var act = () => wrapper.GetMyChannelIdAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Blob storage*");
    }
}
