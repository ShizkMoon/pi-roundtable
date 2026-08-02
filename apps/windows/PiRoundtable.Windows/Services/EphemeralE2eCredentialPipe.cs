using System.IO.Pipes;
using System.Text;

namespace PiRoundtable.Windows.Services;

internal static class EphemeralE2eCredentialPipe
{
    private const string ReferencePrefix = "e2e-pipe://";
    private const string EnableVariable = "PI_ROUNDTABLE_E2E_CREDENTIAL_PIPE";
    private const int MaximumCredentialCharacters = 16 * 1024;

    public static bool CanResolve(string credentialReference)
    {
        return string.Equals(
                   Environment.GetEnvironmentVariable(EnableVariable),
                   "1",
                   StringComparison.Ordinal) &&
               TryGetPipeName(credentialReference, out _);
    }

    public static async Task<string?> ReadOnceAsync(
        string credentialReference,
        CancellationToken cancellationToken)
    {
        if (!CanResolve(credentialReference) ||
            !TryGetPipeName(credentialReference, out var pipeName))
        {
            return null;
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var buffer = new char[MaximumCredentialCharacters + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    break;
                }
                length += read;
            }
            if (length > MaximumCredentialCharacters)
            {
                throw new InvalidDataException("The one-time credential exceeds the allowed size.");
            }
            return length == 0 ? null : new string(buffer, 0, length);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static bool TryGetPipeName(string credentialReference, out string pipeName)
    {
        pipeName = string.Empty;
        if (!credentialReference.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = credentialReference[ReferencePrefix.Length..];
        if (candidate.Length is < 8 or > 128 ||
            candidate.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            return false;
        }

        pipeName = candidate;
        return true;
    }
}
