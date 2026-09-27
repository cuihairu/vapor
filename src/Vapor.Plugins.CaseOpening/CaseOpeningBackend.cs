namespace Vapor.Plugins.CaseOpening;

/// <summary>
/// The seam between "decide what was unboxed" and "how it came to be". The
/// plugin ships exactly one implementation (<see cref="SimulationBackend"/>);
/// real case opening is a CS2 game-client transaction the public Steam Web API
/// cannot perform, and automating it would violate the Steam Subscriber
/// Agreement's automation clause — so no live backend exists or is planned
/// (docs/plugins.md, "ToS boundary").
/// </summary>
public interface ICaseOpeningBackend
{
	/// <summary>Backend mode name, reported in action/route output ("dry-run" for the simulator).</summary>
	string ModeName { get; }

	/// <summary>Opens one case.</summary>
	Task<OpenResult> OpenAsync(CaseDefinition caseDefinition, CancellationToken cancellationToken);

	/// <summary>Opens a case repeatedly (count already validated by the caller).</summary>
	Task<IReadOnlyList<OpenResult>> OpenManyAsync(CaseDefinition caseDefinition, int count, CancellationToken cancellationToken);
}

/// <summary>The dry-run simulator: pure local RNG via <see cref="CaseOpeningEngine"/>.</summary>
public sealed class SimulationBackend : ICaseOpeningBackend
{
	private readonly CaseOpeningEngine _engine;

	/// <summary>Creates a simulator over a fresh engine.</summary>
	public SimulationBackend()
		: this(new CaseOpeningEngine())
	{
	}

	/// <summary>Creates a simulator over a seeded engine (deterministic tests).</summary>
	public SimulationBackend(int seed)
		: this(new CaseOpeningEngine(seed))
	{
	}

	/// <summary>Creates a simulator over an injected engine.</summary>
	internal SimulationBackend(CaseOpeningEngine engine)
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
	}

	/// <inheritdoc/>
	public string ModeName => "dry-run";

	/// <inheritdoc/>
	public Task<OpenResult> OpenAsync(CaseDefinition caseDefinition, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(caseDefinition);
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(_engine.Open(caseDefinition));
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<OpenResult>> OpenManyAsync(
		CaseDefinition caseDefinition,
		int count,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(caseDefinition);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

		var results = new List<OpenResult>(count);
		for (int i = 0; i < count; i++)
		{
			results.Add(await OpenAsync(caseDefinition, cancellationToken).ConfigureAwait(false));
		}

		return results;
	}
}
