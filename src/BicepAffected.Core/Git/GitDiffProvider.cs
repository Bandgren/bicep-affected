using System.Diagnostics;
using System.Text;
using BicepAffected.Core.IO;

namespace BicepAffected.Core.Git;

public sealed class GitDiffProvider
{
    private const int MaxStandardOutputBytes = 8 * 1024 * 1024;
    private const int MaxStandardErrorBytes = 1 * 1024 * 1024;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public IReadOnlyList<string> GetChangedFiles(string repoRoot, string fromRef, string toRef)
    {
        var root = PathNormalizer.NormalizeRoot(repoRoot);
        var verifiedFromRef = VerifyCommitReference(root, fromRef, nameof(fromRef));
        var verifiedToRef = VerifyCommitReference(root, toRef, nameof(toRef));

        var output = RunGit(
            root,
            "diff",
            [
                "diff",
                "--no-ext-diff",
                "--name-status",
                "-z",
                "--find-copies-harder",
                "--find-renames",
                "--diff-filter=ACDMRTUXB",
                verifiedFromRef,
                verifiedToRef,
                "--"
            ]);

        return ParseNameStatusOutput(root, output)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string VerifyCommitReference(string root, string reference, string parameterName)
    {
        ValidateReference(reference, parameterName);

        var output = RunGit(
            root,
            "rev-parse",
            [
                "rev-parse",
                "--verify",
                "--quiet",
                "--end-of-options",
                $"{reference}^{{commit}}"
            ]);

        var verifiedReference = DecodeUtf8(output, "git rev-parse output").TrimEnd('\r', '\n');
        if (!IsObjectId(verifiedReference))
        {
            throw new InvalidOperationException($"git rev-parse returned an invalid commit object ID for {parameterName}.");
        }

        return verifiedReference;
    }

    private static void ValidateReference(string reference, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            reference.StartsWith("-", StringComparison.Ordinal) ||
            reference.StartsWith(":", StringComparison.Ordinal) ||
            reference.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException($"{parameterName} must be a non-empty commit-ish revision and must not be an option or pathspec.", parameterName);
        }
    }

    private static bool IsObjectId(string value)
    {
        if (value.Length is not (40 or 64))
        {
            return false;
        }

        return value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f') ||
            (character >= 'A' && character <= 'F'));
    }

    private static IReadOnlyList<string> ParseNameStatusOutput(string root, byte[] output)
    {
        var records = output.AsSpan();
        var offset = 0;
        var paths = new List<string>();

        while (offset < records.Length)
        {
            var status = ReadNulDelimitedField(records, ref offset, "status");
            if (status.Length == 0)
            {
                throw new InvalidOperationException("git diff returned an empty status record.");
            }

            var statusCode = status[0];
            if (statusCode is not ('A' or 'C' or 'D' or 'M' or 'R' or 'T' or 'U' or 'X' or 'B'))
            {
                throw new InvalidOperationException($"git diff returned an unsupported status '{status}'.");
            }

            if (statusCode is 'R' or 'C')
            {
                if (status.Length == 1 || !status[1..].All(char.IsAsciiDigit))
                {
                    throw new InvalidOperationException($"git diff returned a malformed {statusCode} status '{status}'.");
                }

                paths.Add(NormalizeGitPath(root, ReadNulDelimitedField(records, ref offset, "rename or copy source path")));
                paths.Add(NormalizeGitPath(root, ReadNulDelimitedField(records, ref offset, "rename or copy destination path")));
                continue;
            }

            if (status.Length != 1)
            {
                throw new InvalidOperationException($"git diff returned a malformed status '{status}'.");
            }

            paths.Add(NormalizeGitPath(root, ReadNulDelimitedField(records, ref offset, "path")));
        }

        return paths;
    }

    private static string ReadNulDelimitedField(ReadOnlySpan<byte> records, ref int offset, string fieldName)
    {
        var terminatorOffset = records[offset..].IndexOf((byte)0);
        if (terminatorOffset < 0)
        {
            throw new InvalidOperationException($"git diff returned an unterminated {fieldName} field.");
        }

        var value = DecodeUtf8(records.Slice(offset, terminatorOffset), $"git diff {fieldName}");
        offset += terminatorOffset + 1;
        return value;
    }

    private static string NormalizeGitPath(string root, string path)
    {
        if (string.IsNullOrEmpty(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(path))
        {
            throw new InvalidOperationException("git diff returned an invalid repository-relative path.");
        }

        var normalizedPath = PathNormalizer.NormalizeRelativePath(root, path);
        if (normalizedPath.Length == 0 ||
            normalizedPath == ".." ||
            normalizedPath.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("git diff returned a path outside the repository.");
        }

        return normalizedPath;
    }

#pragma warning disable VSTHRD002 // The public API is synchronous; process I/O itself remains fully asynchronous and concurrently drained.
    private static byte[] RunGit(string root, string operation, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartGitProcess(startInfo);
        var outputLimitReached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var standardOutputTask = DrainStreamAsync(
            process.StandardOutput.BaseStream,
            MaxStandardOutputBytes,
            "standard output",
            outputLimitReached);
        var standardErrorTask = DrainStreamAsync(
            process.StandardError.BaseStream,
            MaxStandardErrorBytes,
            "standard error",
            outputLimitReached);
        var exitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(GitTimeout);
        var completedTask = Task.WhenAny(exitTask, timeoutTask, outputLimitReached.Task).GetAwaiter().GetResult();

        var timedOut = completedTask == timeoutTask;
        var exceededOutputLimit = completedTask == outputLimitReached.Task;
        if (timedOut || exceededOutputLimit)
        {
            KillProcessTree(process);
        }

        exitTask.GetAwaiter().GetResult();
        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();

        if (exceededOutputLimit || standardOutput.ExceededLimit || standardError.ExceededLimit)
        {
            var streamName = exceededOutputLimit
                ? outputLimitReached.Task.GetAwaiter().GetResult()
                : standardOutput.ExceededLimit ? "standard output" : "standard error";
            throw new InvalidOperationException($"git {operation} exceeded the {streamName} output limit.");
        }

        if (timedOut)
        {
            throw new TimeoutException($"git {operation} exceeded the {GitTimeout.TotalSeconds:0}-second timeout.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {operation} failed with exit code {process.ExitCode}: {FormatStandardError(standardError.Bytes)}");
        }

        return standardOutput.Bytes;
    }
#pragma warning restore VSTHRD002

    private static Process StartGitProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("Failed to start git process.", exception);
        }
    }

    private static async Task<DrainedStream> DrainStreamAsync(
        Stream stream,
        int maximumBytes,
        string streamName,
        TaskCompletionSource<string> outputLimitReached)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        var totalBytes = 0;
        var exceededLimit = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remainingBytes = maximumBytes - totalBytes;
            if (remainingBytes > 0)
            {
                var bytesToWrite = Math.Min(remainingBytes, read);
                await output.WriteAsync(buffer.AsMemory(0, bytesToWrite)).ConfigureAwait(false);
                totalBytes += bytesToWrite;
            }

            if (read > remainingBytes)
            {
                exceededLimit = true;
                outputLimitReached.TrySetResult(streamName);
            }
        }

        return new DrainedStream(output.ToArray(), exceededLimit);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static string FormatStandardError(byte[] standardError)
    {
        var error = Encoding.UTF8.GetString(standardError).Trim();
        return error.Length == 0 ? "no error output" : error;
    }

    private static string DecodeUtf8(byte[] bytes, string context) => DecodeUtf8(bytes.AsSpan(), context);

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes, string context)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException($"{context} was not valid UTF-8.", exception);
        }
    }

    private sealed record DrainedStream(byte[] Bytes, bool ExceededLimit);
}
