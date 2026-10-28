using MachineDataBrowser.Credentials;

namespace MachineDataBrowser.App.Tests;

internal sealed class TestCredentialStore : IEndpointCredentialStore
{
    private readonly Dictionary<string, string> _passwords = [];

    public Task<string?> GetAsync(EndpointCredentialKey key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_passwords.GetValueOrDefault(key.Id));

    public Task SetAsync(EndpointCredentialKey key, string password, CancellationToken cancellationToken = default)
    {
        _passwords[key.Id] = password;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(EndpointCredentialKey key, CancellationToken cancellationToken = default)
    {
        _passwords.Remove(key.Id);
        return Task.CompletedTask;
    }
}
