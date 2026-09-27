using System.Globalization;
using System.Text;

namespace Vapor.ControlPlane;

public enum FaultKind
{
	TaskDispatch,
	ApiRequest,
}

public enum FaultMode
{
	Error,
	Delay,
}

/// <summary>The injection outcome handed to the enforcement point that consumed budget.</summary>
public sealed record FaultInjection(string Id, FaultKind Kind, FaultMode Mode, int HttpStatusCode, int DelayMs);

/// <summary>An armed fault: what it selects, what it injects, and its self-healing bounds.</summary>
public sealed record FaultSpec(
	string Id,
	FaultKind Kind,
	FaultMode Mode,
	string? Action,
	string? Region,
	string? Route,
	string? Method,
	int HttpStatusCode,
	int DelayMs,
	long Budget,
	long Fired,
	DateTimeOffset CreatedAt,
	DateTimeOffset ExpiresAt)
{
	/// <summary>Injections still armed (drops to zero right before the spec auto-removes).</summary>
	public long BudgetRemaining => Budget - Fired;
}

/// <summary>
/// Runtime fault injection for resilience drills (roadmap §8). Faults are armed
/// via the admin-only <c>/v1/faults</c> endpoints and consumed at the two
/// enforcement points — task dispatch (<see cref="TaskSchedulerService"/>, after
/// claim, before agent pick) and the API edge middleware (/v1 routes only; the
/// fault endpoints themselves, /healthz and /metrics are exempt so a drill can
/// always be observed and stopped). Every fault is bounded twice over: a
/// <see cref="FaultSpec.Budget"/> (injections left before it removes itself) and
/// an <see cref="FaultSpec.ExpiresAt"/> TTL, so a forgotten drill self-heals
/// instead of degrading the system indefinitely. State is in-memory: a control
/// plane restart disarms everything — deliberate, because outliving the operator's
/// attention is exactly what these bounds exist to prevent.
/// </summary>
public sealed class FaultInjector
{
	public const int MinTtlSeconds = 1;
	public const int MaxTtlSeconds = 3600;
	public const int DefaultTtlSeconds = 900;
	public const long MinBudget = 1;
	public const long MaxBudget = 1000;
	public const long DefaultBudget = 1;
	public const int MinDelayMs = 1;
	public const int MaxDelayMs = 60000;
	public const int DefaultDelayMs = 1000;
	public const int MinHttpStatusCode = 400;
	public const int MaxHttpStatusCode = 599;
	public const int DefaultHttpStatusCode = 500;

	private static readonly Func<DateTimeOffset> DefaultClock = () => DateTimeOffset.UtcNow;

	private readonly object _gate = new();
	private readonly List<FaultSpec> _faults = new();
	private readonly Dictionary<(FaultKind Kind, FaultMode Mode), long> _fired = new();
	private readonly Func<DateTimeOffset> _clock;

	public FaultInjector(Func<DateTimeOffset>? clock = null)
	{
		_clock = clock ?? DefaultClock;
	}

	/// <summary>Arms a fault. Inputs are clamped to the documented bounds (the API layer
	/// also rejects out-of-range values with a 400; clamping here is the belt under it).</summary>
	public FaultSpec Enable(FaultKind kind, FaultMode mode, string? action, string? region, string? route, string? method, int? httpStatusCode, int? delayMs, long? budget, int? ttlSeconds)
	{
		DateTimeOffset now = _clock();
		FaultSpec spec = new(
			Id.New(),
			kind,
			mode,
			NormalizeSelector(action),
			NormalizeSelector(region),
			NormalizeSelector(route),
			NormalizeSelector(method)?.ToUpperInvariant(),
			Math.Clamp(httpStatusCode ?? DefaultHttpStatusCode, MinHttpStatusCode, MaxHttpStatusCode),
			Math.Clamp(delayMs ?? DefaultDelayMs, MinDelayMs, MaxDelayMs),
			Math.Clamp(budget ?? DefaultBudget, MinBudget, MaxBudget),
			0,
			now,
			now.AddSeconds(Math.Clamp(ttlSeconds ?? DefaultTtlSeconds, MinTtlSeconds, MaxTtlSeconds)));

		lock (_gate)
		{
			_faults.Add(spec);
		}

		return spec;
	}

	/// <summary>Armed faults, expired ones swept first.</summary>
	public IReadOnlyList<FaultSpec> List()
	{
		DateTimeOffset now = _clock();
		lock (_gate)
		{
			SweepExpired(now);
			return _faults.ToArray();
		}
	}

	/// <summary>Removes one fault; false when it never existed or already self-removed.</summary>
	public bool Disable(string id)
	{
		DateTimeOffset now = _clock();
		lock (_gate)
		{
			SweepExpired(now);
			return _faults.RemoveAll(f => f.Id == id) > 0;
		}
	}

	/// <summary>Panic button: disarms everything, returns how many were armed.</summary>
	public int ClearAll()
	{
		lock (_gate)
		{
			int removed = _faults.Count;
			_faults.Clear();
			return removed;
		}
	}

	/// <summary>
	/// Dispatch-plane acquisition: consumes the first matching unexpired fault
	/// (action/region selectors; null = any). Returns null when nothing matches —
	/// the common case, so the dispatch path stays untouched when no drill runs.
	/// </summary>
	public FaultInjection? TryInjectDispatch(string action, string? region)
	{
		return TryAcquire(FaultKind.TaskDispatch, f => (f.Action is null || string.Equals(f.Action, action, StringComparison.Ordinal)) && (f.Region is null || string.Equals(f.Region, region, StringComparison.Ordinal)));
	}

	/// <summary>
	/// API-edge acquisition for a /v1 route. Route matching is substring-based
	/// ("/v1/jobs" selects both /v1/jobs and /v1/jobs/{jobId}); method is
	/// ordinal-ignore-case. Callers must exempt the fault endpoints themselves.
	/// </summary>
	public FaultInjection? TryInjectApiRequest(string route, string method)
	{
		return TryAcquire(FaultKind.ApiRequest, f => (f.Route is null || route.Contains(f.Route, StringComparison.OrdinalIgnoreCase)) && (f.Method is null || string.Equals(f.Method, method, StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>Appends the fault metric families to a Prometheus exposition, sorted for stable output.</summary>
	public void Render(StringBuilder sb)
	{
		sb.Append("# HELP vapor_controlplane_fault_injections_total Fault injections fired, by plane and mode.\n");
		sb.Append("# TYPE vapor_controlplane_fault_injections_total counter\n");
		long armed;
		lock (_gate)
		{
			foreach (((FaultKind Kind, FaultMode Mode) key, long count) in _fired.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.Mode))
			{
				sb.Append("vapor_controlplane_fault_injections_total{kind=\"").Append(KindText(key.Kind))
					.Append("\",mode=\"").Append(ModeText(key.Mode))
					.Append("\"} ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
			}

			armed = _faults.Count;
		}

		sb.Append("# HELP vapor_controlplane_faults_armed Fault injections currently armed (budget and TTL not yet exhausted).\n");
		sb.Append("# TYPE vapor_controlplane_faults_armed gauge\n");
		sb.Append("vapor_controlplane_faults_armed ").Append(armed.ToString(CultureInfo.InvariantCulture)).Append('\n');
	}

	private FaultInjection? TryAcquire(FaultKind kind, Func<FaultSpec, bool> matches)
	{
		DateTimeOffset now = _clock();
		lock (_gate)
		{
			SweepExpired(now);
			for (int i = 0; i < _faults.Count; i++)
			{
				FaultSpec candidate = _faults[i];
				if (candidate.Kind != kind || !matches(candidate))
				{
					continue;
				}

				long fired = candidate.Fired + 1;
				if (fired >= candidate.Budget)
				{
					// Budget exhausted: this injection is the fault's last act.
					_faults.RemoveAt(i);
				}
				else
				{
					_faults[i] = candidate with { Fired = fired };
				}

				_fired[(candidate.Kind, candidate.Mode)] = _fired.GetValueOrDefault((candidate.Kind, candidate.Mode)) + 1;
				return new FaultInjection(candidate.Id, candidate.Kind, candidate.Mode, candidate.HttpStatusCode, candidate.DelayMs);
			}

			return null;
		}
	}

	private void SweepExpired(DateTimeOffset now)
	{
		_faults.RemoveAll(f => f.ExpiresAt <= now);
	}

	private static string? NormalizeSelector(string? raw)
	{
		string trimmed = (raw ?? "").Trim();
		return trimmed.Length == 0 ? null : trimmed;
	}

	private static string KindText(FaultKind kind) => kind == FaultKind.TaskDispatch ? "task-dispatch" : "api-request";

	private static string ModeText(FaultMode mode) => mode == FaultMode.Error ? "error" : "delay";
}
