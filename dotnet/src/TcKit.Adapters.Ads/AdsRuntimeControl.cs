using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Ads;

/// <summary>
/// ADS <see cref="IRuntimeControl"/>: drives the system run/config transition. Thin shell over
/// <see cref="RuntimeOperations"/> (seam-driven, unit-tested against a fake); failures map to the
/// Result error contract.
/// </summary>
public sealed class AdsRuntimeControl : IRuntimeControl
{
    private readonly IAdsFactory _factory;

    public AdsRuntimeControl()
        : this(new AdsFactory())
    {
    }

    internal AdsRuntimeControl(IAdsFactory factory) => _factory = factory;

    public Task<Result> StartRuntimeAsync(string targetAmsId, CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                try
                {
                    return RuntimeOperations.StartRuntime(_factory, targetAmsId);
                }
#pragma warning disable CA1031 // The runtime boundary funnels every failure into the Result error contract.
                catch (Exception ex)
                {
                    return Result.Fail(ex.Message);
                }
#pragma warning restore CA1031
            },
            cancellationToken);
}
