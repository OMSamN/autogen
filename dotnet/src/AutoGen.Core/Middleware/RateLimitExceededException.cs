// Copyright (c) Microsoft Corporation. All rights reserved.
// RateLimitExceededException.cs

using System;
using System.Runtime.Serialization;
using System.Text;

namespace AutoGen.Core;

public struct RateLimitInfo
{
    public DateTimeOffset? TimeStamp { get; set; }
    public int? LimitRequests { get; set; }
    public int? LimitTokens { get; set; }
    public int? RemainingRequests { get; set; }
    public int? RemainingTokens { get; set; }
    public TimeSpan? ResetRequests { get; set; }
    public TimeSpan? ResetTokens { get; set; }

    public RateLimitInfo(
        DateTimeOffset? timeStamp,
        int? limitRequests,
        int? limitTokens,
        int? remainingRequests,
        int? remainingTokens,
        TimeSpan? resetRequests,
        TimeSpan? resetTokens
    )
    {
        TimeStamp = timeStamp;
        LimitRequests = limitRequests;
        LimitTokens = limitTokens;
        RemainingRequests = remainingRequests;
        RemainingTokens = remainingTokens;
        ResetRequests = resetRequests;
        ResetTokens = resetTokens;
    }

    public bool IsExceeded =>
        RemainingRequests.HasValue && RemainingRequests.Value <= 0
        || RemainingTokens.HasValue && RemainingTokens.Value <= 0;

    public TimeSpan? TimeUntilReset => ResetRequests > ResetTokens ? ResetRequests : ResetTokens;
}

[Serializable]
public class RateLimitExceededException : Exception
{
    public RateLimitExceededException(RateLimitInfo info)
        : base(GetMessage(info))
    {
        Info = info;
    }

    public RateLimitExceededException(RateLimitInfo info, Exception innerException)
        : base(GetMessage(info), innerException)
    {
        Info = info;
    }

    protected RateLimitExceededException(SerializationInfo info, StreamingContext context)
        : base(info, context) { }

    public RateLimitInfo Info { get; }

    private static string GetMessage(RateLimitInfo info)
    {
        var message = new StringBuilder("Rate limit exceeded. ");

        var timeUntilReset = info.TimeUntilReset;
        if (timeUntilReset.HasValue)
        {
            message.Append($"Time until reset {timeUntilReset.Value:hh:mm:ss.fff}.");

            if (info.TimeStamp.HasValue)
            {
                message.Append(
                    $". This should occur by {info.TimeStamp.Value.Add(timeUntilReset.Value)}."
                );
            }
        }
        else
        {
            message.Append($"See the {nameof(Info)} property for available details.");
        }

        return message.ToString();
    }
}
