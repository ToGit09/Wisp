using System.Net.NetworkInformation;

namespace Wisp.Core.Sync;

public enum ConnectionState
{
    Offline,
    Connecting,
    Online,
    /// <summary>Reachable but slow or failing intermittently.</summary>
    Degraded,
    /// <summary>The server rejected the login. Needs the user.</summary>
    AuthFailed,
    /// <summary>Permanent problem (e.g. invalid certificate). Needs the user.</summary>
    Error,
}

/// <summary>Watches the operating system's network state and wakes workers when it changes.</summary>
public sealed class Connectivity : IDisposable
{
    public event Action? Changed;

    public Connectivity()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    /// <summary>False only if the OS is sure there is no network at all; true does not guarantee internet access.</summary>
    public bool NetworkAvailable
    {
        get
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch { return true; }
        }
    }

    void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Changed?.Invoke();
    void OnAddressChanged(object? sender, EventArgs e) => Changed?.Invoke();

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }
}
