namespace WinStream.Core.Streaming;

/// <summary>
/// A point-in-time snapshot of the send pump's live counters. Cumulative fields
/// only ever climb while streaming, so the UI can show visible activity and derive
/// a measured packet rate between polls.
/// </summary>
public readonly record struct LiveStreamStats(
    long PacketsSent,
    int QueueDepth,
    long Drops,
    long SlowSends,
    long Reanchors);
