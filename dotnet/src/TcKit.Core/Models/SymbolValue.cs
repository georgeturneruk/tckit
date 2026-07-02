namespace TcKit.Core.Models;

/// <summary>
/// The value of a PLC symbol read over ADS, rendered as a string alongside its
/// declared type. Immutable DTO; the analogue of the Python <c>ports/types.py</c>
/// dataclasses.
/// </summary>
public sealed record SymbolValue(string InstancePath, string TypeName, string Value);
