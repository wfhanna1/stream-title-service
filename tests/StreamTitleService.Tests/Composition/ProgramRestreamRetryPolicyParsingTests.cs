using FluentAssertions;
using StreamTitleService.Composition;
using Xunit;

namespace StreamTitleService.Tests.Composition;

public class ProgramRestreamRetryPolicyParsingTests
{
    [Fact]
    public void Parse_AllEnvVarsAbsent_UsesDefaults()
    {
        var policy = RestreamRetryPolicyParser.FromEnvironment(new Dictionary<string, string?>());

        policy.MaxAttempts.Should().Be(3);
        policy.InitialVerifyWait.Should().Be(TimeSpan.FromSeconds(5));
        policy.BackoffSchedule.Should().Equal(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Parse_AllEnvVarsPresent_HonorsOverrides()
    {
        var env = new Dictionary<string, string?>
        {
            ["RESTREAM_VERIFY_MAX_ATTEMPTS"] = "5",
            ["RESTREAM_VERIFY_INITIAL_WAIT_SECONDS"] = "7",
            ["RESTREAM_VERIFY_BACKOFF_SECONDS"] = "3,6,12,24"
        };

        var policy = RestreamRetryPolicyParser.FromEnvironment(env);

        policy.MaxAttempts.Should().Be(5);
        policy.InitialVerifyWait.Should().Be(TimeSpan.FromSeconds(7));
        policy.BackoffSchedule.Should().Equal(
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(24));
    }

    [Fact]
    public void Parse_BackoffSecondsHasWhitespace_TrimsAndParses()
    {
        var env = new Dictionary<string, string?>
        {
            ["RESTREAM_VERIFY_BACKOFF_SECONDS"] = " 1 , 2 , 3 "
        };

        var policy = RestreamRetryPolicyParser.FromEnvironment(env);

        policy.BackoffSchedule.Should().Equal(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Parse_UnparseableMaxAttempts_FallsBackToDefault()
    {
        var env = new Dictionary<string, string?>
        {
            ["RESTREAM_VERIFY_MAX_ATTEMPTS"] = "not-a-number"
        };

        var policy = RestreamRetryPolicyParser.FromEnvironment(env);

        policy.MaxAttempts.Should().Be(3);
    }
}
