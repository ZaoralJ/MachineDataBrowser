using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Credentials;

public readonly record struct EndpointCredentialKey(string Endpoint, string UserName)
{
    public string Id => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Endpoint}\0{UserName}")));

    public static EndpointCredentialKey Create(string endpoint, string? userName)
    {
        var safeEndpoint = EndpointUrlTools.WithoutPassword(endpoint).Trim();
        if (Uri.TryCreate(safeEndpoint, UriKind.Absolute, out var uri))
        {
            safeEndpoint = new UriBuilder(uri)
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = uri.Host.ToLowerInvariant(),
            }.Uri.AbsoluteUri;
        }

        var effectiveUserName = string.IsNullOrWhiteSpace(userName)
            ? EndpointUrlTools.UserName(endpoint) ?? string.Empty
            : userName.Trim();
        return new EndpointCredentialKey(safeEndpoint, effectiveUserName);
    }
}

public interface IEndpointCredentialStore
{
    Task<string?> GetAsync(EndpointCredentialKey key, CancellationToken cancellationToken = default);

    Task SetAsync(EndpointCredentialKey key, string password, CancellationToken cancellationToken = default);

    Task DeleteAsync(EndpointCredentialKey key, CancellationToken cancellationToken = default);
}

/// <summary>Stores endpoint passwords in the current user's native credential store.</summary>
public sealed class NativeEndpointCredentialStore : IEndpointCredentialStore
{
    private const string Service = "MachineDataBrowser.Endpoint";

    public Task<string?> GetAsync(EndpointCredentialKey key, CancellationToken cancellationToken = default) =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? Task.Run(() => OperatingSystem.IsMacOS() ? MacKeychain.Get(key) : WindowsCredentials.Get(key), cancellationToken)
            : OperatingSystem.IsLinux()
                ? LinuxSecretService.GetAsync(key, cancellationToken)
                : Task.FromException<string?>(new PlatformNotSupportedException("No native credential store is supported on this platform."));

    public Task SetAsync(EndpointCredentialKey key, string password, CancellationToken cancellationToken = default) =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? Task.Run(() =>
            {
                if (OperatingSystem.IsMacOS())
                {
                    MacKeychain.Set(key, password);
                }
                else
                {
                    WindowsCredentials.Set(key, password);
                }
            }, cancellationToken)
            : OperatingSystem.IsLinux()
                ? LinuxSecretService.SetAsync(key, password, cancellationToken)
                : Task.FromException(new PlatformNotSupportedException("No native credential store is supported on this platform."));

    public Task DeleteAsync(EndpointCredentialKey key, CancellationToken cancellationToken = default) =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? Task.Run(() =>
            {
                if (OperatingSystem.IsMacOS())
                {
                    MacKeychain.Delete(key);
                }
                else
                {
                    WindowsCredentials.Delete(key);
                }
            }, cancellationToken)
            : OperatingSystem.IsLinux()
                ? LinuxSecretService.DeleteAsync(key, cancellationToken)
                : Task.FromException(new PlatformNotSupportedException("No native credential store is supported on this platform."));

    private static class MacKeychain
    {
        private const int ItemNotFound = -25300;

        public static string? Get(EndpointCredentialKey key)
        {
            var service = Utf8(Service);
            var account = Utf8(key.Id);
            try
            {
                var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Encoding.UTF8.GetByteCount(Service), service,
                    (uint)Encoding.UTF8.GetByteCount(key.Id), account, out var length, out var data, out var item);
                if (status == ItemNotFound)
                {
                    return null;
                }

                Check(status, "read a password from Keychain");
                try
                {
                    var bytes = new byte[checked((int)length)];
                    try
                    {
                        Marshal.Copy(data, bytes, 0, bytes.Length);
                        return Encoding.UTF8.GetString(bytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
                finally
                {
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                    if (item != IntPtr.Zero)
                    {
                        CoreFoundation.CFRelease(item);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(service);
                Marshal.FreeHGlobal(account);
            }
        }

        public static void Set(EndpointCredentialKey key, string password)
        {
            var service = Utf8(Service);
            var account = Utf8(key.Id);
            var secret = Encoding.UTF8.GetBytes(password);
            var secretPointer = Allocate(secret);
            try
            {
                var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Encoding.UTF8.GetByteCount(Service), service,
                    (uint)Encoding.UTF8.GetByteCount(key.Id), account, out _, out var data, out var item);
                if (status == 0)
                {
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                    try
                    {
                        Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secretPointer), "update a password in Keychain");
                    }
                    finally
                    {
                        CoreFoundation.CFRelease(item);
                    }

                    return;
                }

                if (status != ItemNotFound)
                {
                    Check(status, "find a password in Keychain");
                }

                Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Encoding.UTF8.GetByteCount(Service), service,
                    (uint)Encoding.UTF8.GetByteCount(key.Id), account, (uint)secret.Length, secretPointer, out var addedItem), "save a password in Keychain");
                if (addedItem != IntPtr.Zero)
                {
                    CoreFoundation.CFRelease(addedItem);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(service);
                Marshal.FreeHGlobal(account);
                Clear(secretPointer, secret.Length);
                Marshal.FreeHGlobal(secretPointer);
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        public static void Delete(EndpointCredentialKey key)
        {
            var service = Utf8(Service);
            var account = Utf8(key.Id);
            try
            {
                var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Encoding.UTF8.GetByteCount(Service), service,
                    (uint)Encoding.UTF8.GetByteCount(key.Id), account, out _, out var data, out var item);
                if (status == ItemNotFound)
                {
                    return;
                }

                Check(status, "find a password in Keychain");
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                try
                {
                    Check(SecKeychainItemDelete(item), "delete a password from Keychain");
                }
                finally
                {
                    CoreFoundation.CFRelease(item);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(service);
                Marshal.FreeHGlobal(account);
            }
        }

        private static IntPtr Utf8(string value) => Allocate(Encoding.UTF8.GetBytes(value));

        private static IntPtr Allocate(byte[] bytes)
        {
            var pointer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }

            return pointer;
        }

        private static void Check(int status, string action)
        {
            if (status != 0)
            {
                throw new InvalidOperationException($"Could not {action} (Keychain error {status}).");
            }
        }

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceNameLength, IntPtr serviceName,
            uint accountNameLength, IntPtr accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr item);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, IntPtr serviceName,
            uint accountNameLength, IntPtr accountName, uint passwordLength, IntPtr passwordData, out IntPtr item);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, IntPtr data);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemDelete(IntPtr item);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    }

    private static class CoreFoundation
    {
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern void CFRelease(IntPtr value);
    }

    private static class WindowsCredentials
    {
        private const uint Generic = 1;
        private const uint LocalMachine = 2;

        public static string? Get(EndpointCredentialKey key)
        {
            if (!CredRead(Target(key), Generic, 0, out var credential))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1168) // ERROR_NOT_FOUND
                {
                    return null;
                }

                throw new InvalidOperationException($"Could not read a password from Windows Credential Manager (error {error}).");
            }

            try
            {
                var value = Marshal.PtrToStructure<Credential>(credential);
                var bytes = new byte[checked((int)value.CredentialBlobSize)];
                try
                {
                    Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length);
                    return Encoding.Unicode.GetString(bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally
            {
                CredFree(credential);
            }
        }

        public static void Set(EndpointCredentialKey key, string password)
        {
            var bytes = Encoding.Unicode.GetBytes(password);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = Generic,
                    TargetName = Target(key),
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = LocalMachine,
                    UserName = key.UserName,
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new InvalidOperationException($"Could not save a password in Windows Credential Manager (error {Marshal.GetLastWin32Error()}).");
                }
            }
            finally
            {
                Clear(blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public static void Delete(EndpointCredentialKey key)
        {
            if (!CredDelete(Target(key), Generic, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1168) // ERROR_NOT_FOUND
                {
                    throw new InvalidOperationException($"Could not delete a password from Windows Credential Manager (error {error}).");
                }
            }
        }

        private static string Target(EndpointCredentialKey key) => $"MachineDataBrowser.Endpoint/{key.Id}";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("Advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }

    private static class LinuxSecretService
    {
        public static Task<string?> GetAsync(EndpointCredentialKey key, CancellationToken cancellationToken) =>
            RunAsync("lookup", key, null, cancellationToken);

        public static async Task SetAsync(EndpointCredentialKey key, string password, CancellationToken cancellationToken)
        {
            _ = await RunAsync("store", key, password, cancellationToken);
        }

        public static async Task DeleteAsync(EndpointCredentialKey key, CancellationToken cancellationToken)
        {
            _ = await RunAsync("clear", key, null, cancellationToken);
        }

        private static async Task<string?> RunAsync(string operation, EndpointCredentialKey key, string? password, CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = password is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(operation);
            if (operation == "store")
            {
                start.ArgumentList.Add("--label=Machine Data Browser endpoint password");
            }

            start.ArgumentList.Add("service");
            start.ArgumentList.Add(Service);
            start.ArgumentList.Add("key");
            start.ArgumentList.Add(key.Id);

            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("Could not start secret-tool. Install libsecret-tools and make sure a Secret Service keyring is available.", ex);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            if (password is not null)
            {
                await process.StandardInput.WriteAsync(password.AsMemory(), cancellationToken);
                await process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            var missing = (operation is "lookup" or "clear") && process.ExitCode == 1 && string.IsNullOrWhiteSpace(error);
            if (process.ExitCode != 0 && !missing)
            {
                throw new InvalidOperationException($"Could not {operation} a password in the Linux Secret Service: {error.Trim()}");
            }

            return process.ExitCode == 0 && operation == "lookup" ? output.TrimEnd('\r', '\n') : null;
        }
    }

    private static void Clear(IntPtr memory, int length)
    {
        if (memory != IntPtr.Zero && length > 0)
        {
            Marshal.Copy(new byte[length], 0, memory, length);
        }
    }
}
