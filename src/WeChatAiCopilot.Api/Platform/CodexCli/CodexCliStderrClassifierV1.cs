namespace WeChatAiCopilot.Api.Platform.CodexCli;

/// <summary>
/// Maps a bounded stderr prefix to a fixed category. Raw stderr is never returned
/// from this boundary, logged, or persisted.
/// </summary>
internal static class CodexCliStderrClassifierV1
{
    private static readonly string[] AuthenticationPhrases =
    [
        "not logged in",
        "login required",
        "authentication required",
        "missing authentication",
        "unauthenticated"
    ];

    private static readonly string[] NetworkPhrases =
    [
        "connection refused",
        "failed to connect",
        "network is unreachable",
        "could not resolve host",
        "dns resolution failed",
        "socket hang up",
        "econnrefused",
        "econnreset",
        "etimedout",
        "enotfound",
        "eai_again"
    ];

    private static readonly string[] ConfigurationPhrases =
    [
        "unknown config key",
        "unknown config",
        "invalid configuration",
        "invalid config",
        "unknown feature",
        "unrecognized option",
        "unknown option",
        "unexpected argument"
    ];

    private static readonly string[] SchemaPhrases =
    [
        "invalid json schema",
        "schema validation failed",
        "schema is invalid",
        "failed to parse schema",
        "schema rejected",
        "structured output schema is invalid"
    ];

    public static CodexCliFailureCategoryV1 Classify(ReadOnlySpan<byte> stderr)
    {
        if (ContainsAny(stderr, AuthenticationPhrases))
        {
            return CodexCliFailureCategoryV1.AuthenticationMissing;
        }

        if (ContainsAny(stderr, NetworkPhrases))
        {
            return CodexCliFailureCategoryV1.NetworkConnection;
        }

        if (ContainsAny(stderr, ConfigurationPhrases))
        {
            return CodexCliFailureCategoryV1.CliConfiguration;
        }

        if (ContainsAny(stderr, SchemaPhrases))
        {
            return CodexCliFailureCategoryV1.SchemaRejected;
        }

        return CodexCliFailureCategoryV1.Unknown;
    }

    private static bool ContainsAny(ReadOnlySpan<byte> source, IReadOnlyList<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            if (ContainsAsciiIgnoreCase(source, phrase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> source, string phrase)
    {
        if (phrase.Length == 0 || phrase.Length > source.Length)
        {
            return false;
        }

        for (var start = 0; start <= source.Length - phrase.Length; start++)
        {
            var matched = true;
            for (var index = 0; index < phrase.Length; index++)
            {
                if (ToLowerAscii(source[start + index]) != phrase[index])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ('a' - 'A')) : value;
}
