namespace TcKit.Core.Authoring;

/// <summary>
/// Anchored single-occurrence text replacement, mirroring Claude Code's Edit semantics. Shared by
/// both writer backends so the three patch verbs behave byte-identically regardless of backend.
/// </summary>
public static class PatchText
{
    /// <summary>
    /// Replace the unique occurrence of <paramref name="oldString"/> with
    /// <paramref name="newString"/>. Throws when the anchor is empty, absent, or ambiguous;
    /// <paramref name="where"/> names the text being patched in the error message.
    /// </summary>
    public static string ApplyPatch(string text, string oldString, string newString, string where)
    {
        if (string.IsNullOrEmpty(oldString))
        {
            throw new ArgumentException("OldString required.");
        }

        var count = 0;
        for (var i = text.IndexOf(oldString, StringComparison.Ordinal); i >= 0;
            i = text.IndexOf(oldString, i + oldString.Length, StringComparison.Ordinal))
        {
            count++;
        }

        if (count == 0)
        {
            throw new InvalidOperationException($"OldString not found in {where}.");
        }

        if (count > 1)
        {
            throw new InvalidOperationException(
                $"OldString appears {count} times in {where}; anchor must be unique. Extend OldString with more surrounding context.");
        }

        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        return text[..index] + newString + text[(index + oldString.Length)..];
    }
}
