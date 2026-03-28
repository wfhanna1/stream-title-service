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
