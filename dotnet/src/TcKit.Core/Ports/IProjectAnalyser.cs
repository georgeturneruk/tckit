using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Offline static analysis of a TwinCAT project (ADR-0018). Reads through
/// <see cref="IProjectReader"/> only, so it needs no XAE, no licence, and no running runtime.
/// </summary>
public interface IProjectAnalyser
{
    /// <summary>
    /// Analyse a project and return the findings that meet
    /// <see cref="AnalysisRequest.MinimumSeverity"/>, ordered most severe first.
    /// </summary>
    Task<AnalysisResult> AnalyseAsync(AnalysisRequest request, CancellationToken cancellationToken);
}
