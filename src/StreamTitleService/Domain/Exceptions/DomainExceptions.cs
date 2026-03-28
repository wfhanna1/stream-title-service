namespace StreamTitleService.Domain.Exceptions;

public class UnknownLocationException : Exception
{
    public string LocationValue { get; }

    public UnknownLocationException(string locationValue)
        : base($"Unknown location: '{locationValue}'. Event will be dead-lettered.")
    {
        LocationValue = locationValue;
    }
}

public class TitleResolutionException : Exception
{
    public TitleResolutionException(string message) : base(message) { }
    public TitleResolutionException(string message, Exception inner) : base(message, inner) { }
}

public class TitleUpdateException : Exception
{
    public int ChannelsAttempted { get; }
    public int ChannelsUpdated { get; }

    public TitleUpdateException(string message, int channelsAttempted, int channelsUpdated, Exception? inner = null)
        : base(message, inner)
    {
        ChannelsAttempted = channelsAttempted;
        ChannelsUpdated = channelsUpdated;
    }
}
