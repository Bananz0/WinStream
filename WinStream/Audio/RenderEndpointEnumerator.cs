#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using WinStream.Core.Audio;

namespace WinStream.Audio;

public sealed class RenderEndpointEnumerator : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    public IReadOnlyList<RenderEndpointInfo> ListActiveRenderEndpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? defaultId = null;
        try
        {
            defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            // No default device available.
        }

        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var results = new List<RenderEndpointInfo>(devices.Count);
        foreach (var device in devices)
        {
            string? instanceId = null;
            try
            {
                instanceId = device.Properties[PropertyKeys.PKEY_Device_InstanceId]?.Value as string;
            }
            catch
            {
                // Older or synthetic endpoints may not expose a PnP instance id.
            }

            results.Add(new RenderEndpointInfo(
                device.ID,
                device.FriendlyName,
                string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase),
                instanceId));
        }

        return results
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public string? GetDefaultRenderEndpointId()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _enumerator.Dispose();
        _disposed = true;
    }
}
