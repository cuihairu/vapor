using System.Text;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// FaultInjector unit tests: the service behind /v1/faults — budget and TTL
/// self-healing, selector matching per plane, kind isolation, and the Prometheus
/// render. The clock is injected, so expiry behavior is deterministic.
/// </summary>
public sealed class FaultInjectorTests
{
	private sealed class FrozenClock
	{
		public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		public Func<DateTimeOffset> Fn => () => Now;
	}

	private static FaultSpec Arm(FaultInjector injector, FaultKind kind, FaultMode mode, string? action = null, string? region = null, string? route = null, string? method = null, int? status = null, int? delay = null, long? budget = null, int? ttl = null) =>
		injector.Enable(kind, mode, action, region, route, method, status, delay, budget, ttl);

	// --- Enable: defaults, clamps, selector normalization ---

	[Fact]
	public void Enable_AppliesDocumentedDefaults()
	{
		var clock = new FrozenClock();
		var injector = new FaultInjector(clock.Fn);

		FaultSpec spec = Arm(injector, FaultKind.TaskDispatch, FaultMode.Error);

		Assert.NotNull(spec.Id);
		Assert.Equal(32, spec.Id.Length);
		Assert.Equal(FaultInjector.DefaultHttpStatusCode, spec.HttpStatusCode);
		Assert.Equal(FaultInjector.DefaultDelayMs, spec.DelayMs);
		Assert.Equal(FaultInjector.DefaultBudget, spec.Budget);
		Assert.Equal(0, spec.Fired);
		Assert.Equal(FaultInjector.DefaultBudget, spec.BudgetRemaining);
		Assert.Equal(clock.Now, spec.CreatedAt);
		Assert.Equal(clock.Now.AddSeconds(FaultInjector.DefaultTtlSeconds), spec.ExpiresAt);
	}

	[Fact]
	public void Enable_ClampsProvidedValuesToBounds()
	{
		var injector = new FaultInjector();

		FaultSpec tooLoose = Arm(injector, FaultKind.ApiRequest, FaultMode.Error, status: 350, delay: 99999, budget: 5000, ttl: 7200);
		FaultSpec tooTight = Arm(injector, FaultKind.ApiRequest, FaultMode.Delay, status: 700, delay: -5, budget: 0, ttl: 0);

		Assert.Equal(FaultInjector.MinHttpStatusCode, tooLoose.HttpStatusCode);
		Assert.Equal(FaultInjector.MaxDelayMs, tooLoose.DelayMs);
		Assert.Equal(FaultInjector.MaxBudget, tooLoose.Budget);
		Assert.Equal(FaultInjector.MaxTtlSeconds, (int)(tooLoose.ExpiresAt - tooLoose.CreatedAt).TotalSeconds);

		Assert.Equal(FaultInjector.MaxHttpStatusCode, tooTight.HttpStatusCode);
		Assert.Equal(FaultInjector.MinDelayMs, tooTight.DelayMs);
		Assert.Equal(FaultInjector.MinBudget, tooTight.Budget);
		Assert.Equal(FaultInjector.MinTtlSeconds, (int)(tooTight.ExpiresAt - tooTight.CreatedAt).TotalSeconds);
	}

	[Fact]
	public void Enable_NormalizesSelectors()
	{
		var injector = new FaultInjector();

		FaultSpec spec = Arm(injector, FaultKind.ApiRequest, FaultMode.Error, action: "  ", region: " ", route: " /v1/jobs ", method: " get ");

		Assert.Null(spec.Action);
		Assert.Null(spec.Region);
		Assert.Equal("/v1/jobs", spec.Route);
		Assert.Equal("GET", spec.Method);
	}

	// --- Dispatch-plane matching ---

	[Fact]
	public void TryInjectDispatch_ActionSelector_MatchesExactly()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Error, action: "login");

		Assert.NotNull(injector.TryInjectDispatch("login", "eu-west"));
		Assert.Null(injector.TryInjectDispatch("farm", "eu-west"));
	}

	[Fact]
	public void TryInjectDispatch_RegionSelector_RequiresRegionMatch()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Error, region: "eu-west");

		Assert.Null(injector.TryInjectDispatch("login", "us-east"));
		Assert.NotNull(injector.TryInjectDispatch("farm", "eu-west"));
	}

	[Fact]
	public void TryInjectDispatch_NullSelectors_MatchAnything()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Error);

		Assert.NotNull(injector.TryInjectDispatch("anything", null));
	}

	// --- Budget accounting and self-healing ---

	[Fact]
	public void TryInject_BudgetDecrementsAndAutoRemovesAtExhaustion()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Error, budget: 2);

		Assert.NotNull(injector.TryInjectDispatch("login", "eu"));
		FaultSpec remaining = Assert.Single(injector.List());
		Assert.Equal(1, remaining.Fired);
		Assert.Equal(1, remaining.BudgetRemaining);

		Assert.NotNull(injector.TryInjectDispatch("login", "eu"));
		Assert.Empty(injector.List());
		Assert.Null(injector.TryInjectDispatch("login", "eu"));
	}

	[Fact]
	public void TryInject_DefaultBudgetIsOneShot()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.ApiRequest, FaultMode.Error);

		Assert.NotNull(injector.TryInjectApiRequest("/v1/agents", "GET"));
		Assert.Null(injector.TryInjectApiRequest("/v1/agents", "GET"));
	}

	[Fact]
	public void TryInject_ExpiredFaultIsRefusedAndSwept()
	{
		var clock = new FrozenClock();
		var injector = new FaultInjector(clock.Fn);
		FaultSpec spec = Arm(injector, FaultKind.TaskDispatch, FaultMode.Error, ttl: 60);

		clock.Now = spec.ExpiresAt; // boundary: expired at the instant
		Assert.Null(injector.TryInjectDispatch("login", "eu"));
		Assert.Empty(injector.List());
		Assert.False(injector.Disable(spec.Id));
	}

	// --- API-plane matching ---

	[Fact]
	public void TryInjectApiRequest_RouteIsSubstringAndMethodIsCaseInsensitive()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.ApiRequest, FaultMode.Error, route: "/v1/jobs", budget: 2);

		// Substring: the concrete route pattern for GET /v1/jobs/{jobId} also matches.
		Assert.NotNull(injector.TryInjectApiRequest("/v1/jobs/{jobId}", "GET"));
		Assert.NotNull(injector.TryInjectApiRequest("/v1/jobs", "GET"));
		Assert.Null(injector.TryInjectApiRequest("/v1/agents", "GET"));
	}

	[Fact]
	public void TryInjectApiRequest_MethodSelector_MatchesAnyCase()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.ApiRequest, FaultMode.Delay, method: "post");

		Assert.Null(injector.TryInjectApiRequest("/v1/jobs", "GET"));
		Assert.NotNull(injector.TryInjectApiRequest("/v1/jobs", "POST"));
	}

	[Fact]
	public void TryInject_KindIsolation_DispatchProbeDoesNotConsumeApiFault()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.ApiRequest, FaultMode.Error);

		Assert.Null(injector.TryInjectDispatch("login", "eu"));
		Assert.NotNull(injector.TryInjectApiRequest("/v1/agents", "GET")); // budget untouched by the dispatch probe
	}

	[Fact]
	public void TryInject_KindIsolation_ApiProbeDoesNotConsumeDispatchFault()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Delay);

		Assert.Null(injector.TryInjectApiRequest("/v1/agents", "GET"));
		Assert.NotNull(injector.TryInjectDispatch("login", "eu"));
	}

	// --- Disable / ClearAll ---

	[Fact]
	public void Disable_RemovesOnlyTheTargetFault()
	{
		var injector = new FaultInjector();
		FaultSpec first = Arm(injector, FaultKind.ApiRequest, FaultMode.Error, route: "/v1/agents");
		FaultSpec second = Arm(injector, FaultKind.ApiRequest, FaultMode.Error, route: "/v1/jobs", budget: 2);

		Assert.True(injector.Disable(first.Id));
		Assert.False(injector.Disable(first.Id));
		Assert.NotNull(injector.TryInjectApiRequest("/v1/jobs", "GET"));
		Assert.Null(injector.TryInjectApiRequest("/v1/agents", "GET"));
		// second stays armed after one fire of its two-shot budget.
		Assert.Single(injector.List(), f => f.Id == second.Id);
	}

	[Fact]
	public void ClearAll_DisarmsEverythingAndReportsCount()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.ApiRequest, FaultMode.Error);
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Delay);
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Error);

		Assert.Equal(3, injector.ClearAll());
		Assert.Null(injector.TryInjectDispatch("login", "eu"));
		Assert.Null(injector.TryInjectApiRequest("/v1/agents", "GET"));
		Assert.Empty(injector.List());
	}

	// --- Metrics render ---

	[Fact]
	public void Render_EmptyState_ShowsFamilyHeadersAndZeroGauge()
	{
		var sb = new StringBuilder();

		new FaultInjector().Render(sb);

		Assert.Contains("vapor_controlplane_fault_injections_total counter", sb.ToString());
		Assert.Contains("vapor_controlplane_faults_armed 0", sb.ToString());
		Assert.DoesNotContain("fault_injections_total{", sb.ToString());
	}

	[Fact]
	public void Render_CountersByKeyAndMode_Sorted()
	{
		var injector = new FaultInjector();
		Arm(injector, FaultKind.ApiRequest, FaultMode.Error, budget: 10);
		injector.TryInjectApiRequest("/v1/x", "GET");
		injector.ClearAll(); // counters survive disarming; only armed faults reset
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Delay, budget: 10);
		injector.TryInjectDispatch("a", "r");
		injector.TryInjectDispatch("b", "r");
		Arm(injector, FaultKind.TaskDispatch, FaultMode.Error, budget: 5); // both dispatch faults stay armed → gauge 2

		var sb = new StringBuilder();
		injector.Render(sb);
		string rendered = sb.ToString();

		Assert.Contains("vapor_controlplane_fault_injections_total{kind=\"task-dispatch\",mode=\"delay\"} 2", rendered);
		Assert.Contains("vapor_controlplane_fault_injections_total{kind=\"api-request\",mode=\"error\"} 1", rendered);
		Assert.Contains("vapor_controlplane_faults_armed 2", rendered);
		// Enum order (TaskDispatch=0 < ApiRequest=1) drives the sort.
		Assert.True(rendered.IndexOf("task-dispatch\",mode=\"delay", StringComparison.Ordinal) < rendered.IndexOf("api-request\",mode=\"error", StringComparison.Ordinal));
	}

	[Fact]
	public void DefaultClock_ExpiryAnchorsToRealUtcNow()
	{
		var injector = new FaultInjector();

		FaultSpec spec = Arm(injector, FaultKind.TaskDispatch, FaultMode.Error, ttl: 60);

		Assert.True(spec.ExpiresAt > DateTimeOffset.UtcNow);
		Assert.True(spec.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(61));
	}
}
