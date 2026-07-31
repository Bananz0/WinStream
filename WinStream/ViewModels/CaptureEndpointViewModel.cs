#nullable enable

using System;
using Microsoft.UI.Xaml;
using WinStream.Core.Audio;

namespace WinStream.ViewModels;

/// <summary>
/// Splits a WASAPI friendly name such as "3 - Odyssey G95NC (AMD High Definition
/// Audio Device)" into a readable device name and a secondary adapter line.
/// </summary>
public sealed class CaptureEndpointViewModel
{
    public CaptureEndpointViewModel(RenderEndpointInfo endpoint)
    {
        Endpoint = endpoint;
        var (name, adapter) = Split(endpoint.FriendlyName);
        Name = name;
        Detail = BuildDetail(adapter, endpoint.IsDefault);
    }

    public RenderEndpointInfo Endpoint { get; }

    public string Id => Endpoint.Id;

    public string Name { get; }

    public string Detail { get; }

    public Visibility DetailVisibility =>
        string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;

    private static string BuildDetail(string adapter, bool isDefault)
    {
        if (string.IsNullOrEmpty(adapter))
        {
            return isDefault ? "Default device" : string.Empty;
        }

        return isDefault ? $"{adapter}  •  Default device" : adapter;
    }

    private static (string Name, string Adapter) Split(string? friendlyName)
    {
        var value = friendlyName?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return ("Unknown device", string.Empty);
        }

        value = StripIndexPrefix(value);

        // Drivers append the adapter in trailing parentheses.
        if (value.EndsWith(')'))
        {
            var open = value.LastIndexOf('(');
            if (open > 0)
            {
                var adapter = value[(open + 1)..^1].Trim();
                var name = value[..open].Trim();
                if (name.Length > 0 && adapter.Length > 0)
                {
                    return (name, adapter);
                }
            }
        }

        return (value, string.Empty);
    }

    private static string StripIndexPrefix(string value)
    {
        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return value;
        }

        for (var i = 0; i < separator; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return value;
            }
        }

        return value[(separator + 3)..].Trim();
    }
}
