namespace TcKit.Core.Security;

/// <summary>
/// The permission tiers, in increasing power. A tool declares the minimum level it needs; the
/// configured mode must be at least that level for the call to proceed (the values are ordered, so
/// the check is a numeric comparison).
/// <list type="bullet">
///   <item><description><see cref="Read"/> — inspect only (project reads, docs, ADS/hardware reads).</description></item>
///   <item><description><see cref="Write"/> — author the project on disk (POU/GVL/DUT/I-O edits, build).</description></item>
///   <item><description><see cref="Execute"/> — act on a live target (deploy, start runtime, run tests, symbol writes, RPC).</description></item>
/// </list>
/// </summary>
public enum PermissionLevel
{
    Read = 0,
    Write = 1,
    Execute = 2,
}
