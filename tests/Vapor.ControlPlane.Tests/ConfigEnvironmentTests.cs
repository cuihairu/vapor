using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Drives <see cref="Config.LoadFromEnvironment"/> integer parsing arms:
/// garbage values (TryParse false), guard-violating values (0 / negative on
/// guarded keys), and valid overrides. Environment variables are process
/// globals — every case saves the previous value and restores it in finally.
/// The class rides the process-global collection (DisableParallelization):
/// Config.LoadFromEnvironment reads the whole variable surface, so a
/// concurrent boot in CompositionRootSmokeTests could observe a mutated key
/// (a negative ReconcileIntervalSeconds would kill its PeriodicTimer at
/// startup).
/// </summary>
[Collection(ProcessGlobalTracingCollection.Name)]
public sealed class ConfigEnvironmentTests
{
	/// <summary>(env var, raw value, Config property, expected).</summary>
	public static TheoryData<string, string, string, int> Rows => new()
	{
		// guarded > 0: garbage hits the parse-false arm, 0 hits the guard-false arm
		{ "Vapor_TASK_LEASE_SECONDS", "soon", nameof(Config.TaskLeaseSeconds), 300 },
		{ "Vapor_TASK_LEASE_SECONDS", "0", nameof(Config.TaskLeaseSeconds), 300 },
		{ "Vapor_TASK_LEASE_SECONDS", "45", nameof(Config.TaskLeaseSeconds), 45 },
		{ "Vapor_TASK_MAX_DISPATCH_ATTEMPTS", "soon", nameof(Config.TaskMaxDispatchAttempts), 10 },
		{ "Vapor_TASK_MAX_DISPATCH_ATTEMPTS", "-3", nameof(Config.TaskMaxDispatchAttempts), -3 },
		{ "Vapor_TASK_DISPATCH_RETRY_DELAY_MS", "soon", nameof(Config.TaskDispatchRetryDelayMs), 2000 },
		{ "Vapor_TASK_DISPATCH_RETRY_DELAY_MS", "-1", nameof(Config.TaskDispatchRetryDelayMs), 2000 },
		{ "Vapor_TASK_DISPATCH_RETRY_DELAY_MS", "0", nameof(Config.TaskDispatchRetryDelayMs), 0 },
		{ "Vapor_RECONCILE_INTERVAL_SECONDS", "soon", nameof(Config.ReconcileIntervalSeconds), 15 },
		{ "Vapor_RECONCILE_INTERVAL_SECONDS", "-7", nameof(Config.ReconcileIntervalSeconds), -7 },
		{ "Vapor_RECONCILE_MAX_ACCOUNTS_PER_AGENT", "soon", nameof(Config.ReconcileMaxAccountsPerAgent), 25 },
		{ "Vapor_RECONCILE_MAX_ACCOUNTS_PER_AGENT", "0", nameof(Config.ReconcileMaxAccountsPerAgent), 25 },
		{ "Vapor_RECONCILE_MAX_ACCOUNTS_PER_AGENT", "8", nameof(Config.ReconcileMaxAccountsPerAgent), 8 },
		{ "Vapor_RECONCILE_MAX_LOGIN_ATTEMPTS", "soon", nameof(Config.ReconcileMaxLoginAttempts), 3 },
		{ "Vapor_RECONCILE_MAX_LOGIN_ATTEMPTS", "0", nameof(Config.ReconcileMaxLoginAttempts), 3 },
		{ "Vapor_RECONCILE_MAX_LOGIN_ATTEMPTS", "5", nameof(Config.ReconcileMaxLoginAttempts), 5 },
		{ "Vapor_RECONCILE_LOGIN_COOLDOWN_SECONDS", "soon", nameof(Config.ReconcileLoginCooldownSeconds), 60 },
		{ "Vapor_RECONCILE_LOGIN_COOLDOWN_SECONDS", "-1", nameof(Config.ReconcileLoginCooldownSeconds), 60 },
		{ "Vapor_RECONCILE_LOGIN_COOLDOWN_SECONDS", "0", nameof(Config.ReconcileLoginCooldownSeconds), 0 },
		{ "Vapor_RECONCILE_SESSION_STALENESS_SECONDS", "soon", nameof(Config.ReconcileSessionStalenessSeconds), 120 },
		{ "Vapor_RECONCILE_SESSION_STALENESS_SECONDS", "0", nameof(Config.ReconcileSessionStalenessSeconds), 120 },
		{ "Vapor_RECONCILE_SESSION_STALENESS_SECONDS", "90", nameof(Config.ReconcileSessionStalenessSeconds), 90 },
		{ "Vapor_RECONCILE_FARM_REFRESH_SECONDS", "soon", nameof(Config.ReconcileFarmRefreshSeconds), 300 },
		{ "Vapor_RECONCILE_FARM_REFRESH_SECONDS", "0", nameof(Config.ReconcileFarmRefreshSeconds), 300 },
		{ "Vapor_RECONCILE_FARM_REFRESH_SECONDS", "120", nameof(Config.ReconcileFarmRefreshSeconds), 120 },
		{ "Vapor_RECONCILE_BOOST_REFRESH_SECONDS", "soon", nameof(Config.ReconcileBoostRefreshSeconds), 1800 },
		{ "Vapor_RECONCILE_BOOST_REFRESH_SECONDS", "0", nameof(Config.ReconcileBoostRefreshSeconds), 1800 },
		{ "Vapor_RECONCILE_BOOST_REFRESH_SECONDS", "60", nameof(Config.ReconcileBoostRefreshSeconds), 60 },
		{ "Vapor_RECONCILE_TRADE_REFRESH_SECONDS", "soon", nameof(Config.ReconcileTradeRefreshSeconds), 600 },
		{ "Vapor_RECONCILE_TRADE_REFRESH_SECONDS", "0", nameof(Config.ReconcileTradeRefreshSeconds), 600 },
		{ "Vapor_RECONCILE_TRADE_REFRESH_SECONDS", "30", nameof(Config.ReconcileTradeRefreshSeconds), 30 },
		{ "Vapor_RECONCILE_STANDING_REFRESH_SECONDS", "soon", nameof(Config.ReconcileStandingRefreshSeconds), 21600 },
		{ "Vapor_RECONCILE_STANDING_REFRESH_SECONDS", "0", nameof(Config.ReconcileStandingRefreshSeconds), 21600 },
		{ "Vapor_RECONCILE_STANDING_REFRESH_SECONDS", "3600", nameof(Config.ReconcileStandingRefreshSeconds), 3600 },
		// webhook retry keys stay out: CompositionRootSmokeTests mutates them on
		// the real process environment and classes run in parallel — the arms
		// are lit there already.
		{ "Vapor_CRAWL_WORKER_TICK_SECONDS", "soon", nameof(Config.CrawlWorkerTickSeconds), 5 },
		{ "Vapor_CRAWL_WORKER_TICK_SECONDS", "-2", nameof(Config.CrawlWorkerTickSeconds), -2 },
		{ "Vapor_CRAWL_KEEP_RUNS", "soon", nameof(Config.CrawlKeepRuns), 10 },
		{ "Vapor_CRAWL_KEEP_RUNS", "0", nameof(Config.CrawlKeepRuns), 10 },
		{ "Vapor_CRAWL_KEEP_RUNS", "3", nameof(Config.CrawlKeepRuns), 3 },
		{ "Vapor_CRAWL_MAX_APPS_PER_PLAN", "soon", nameof(Config.CrawlMaxAppsPerPlan), 500 },
		{ "Vapor_CRAWL_MAX_APPS_PER_PLAN", "0", nameof(Config.CrawlMaxAppsPerPlan), 500 },
		{ "Vapor_CRAWL_MAX_APPS_PER_PLAN", "42", nameof(Config.CrawlMaxAppsPerPlan), 42 },
		{ "Vapor_CRAWL_MAX_APPS_PER_TASK", "soon", nameof(Config.CrawlMaxAppsPerTask), 200 },
		{ "Vapor_CRAWL_MAX_APPS_PER_TASK", "0", nameof(Config.CrawlMaxAppsPerTask), 200 },
		{ "Vapor_CRAWL_MAX_APPS_PER_TASK", "17", nameof(Config.CrawlMaxAppsPerTask), 17 },
		{ "Vapor_CRAWL_RUN_TIMEOUT_SECONDS", "soon", nameof(Config.CrawlRunTimeoutSeconds), 1800 },
		{ "Vapor_CRAWL_RUN_TIMEOUT_SECONDS", "0", nameof(Config.CrawlRunTimeoutSeconds), 1800 },
		{ "Vapor_CRAWL_RUN_TIMEOUT_SECONDS", "600", nameof(Config.CrawlRunTimeoutSeconds), 600 },
		{ "Vapor_CRAWL_INTERVAL_MS", "soon", nameof(Config.CrawlIntervalMs), 500 },
		{ "Vapor_CRAWL_INTERVAL_MS", "-1", nameof(Config.CrawlIntervalMs), 500 },
		{ "Vapor_CRAWL_INTERVAL_MS", "0", nameof(Config.CrawlIntervalMs), 0 },
		{ "Vapor_API_RATE_LIMIT_PER_MINUTE", "soon", nameof(Config.ApiRateLimitPerMinute), 0 },
		{ "Vapor_API_RATE_LIMIT_PER_MINUTE", "0", nameof(Config.ApiRateLimitPerMinute), 0 },
		{ "Vapor_API_RATE_LIMIT_PER_MINUTE", "-5", nameof(Config.ApiRateLimitPerMinute), 0 },
		{ "Vapor_API_RATE_LIMIT_PER_MINUTE", "45", nameof(Config.ApiRateLimitPerMinute), 45 },
	};

	[Theory, MemberData(nameof(Rows))]
	public void IntegerEnvironmentOverride_FollowsParseAndGuardArms(string name, string raw, string property, int expected)
	{
		string? original = Environment.GetEnvironmentVariable(name);
		try
		{
			Environment.SetEnvironmentVariable(name, raw);
			Config config = Config.LoadFromEnvironment();
			Assert.Equal(expected, IntegerProperty(config, property));
		}
		finally
		{
			Environment.SetEnvironmentVariable(name, original);
		}
	}

	[Fact]
	public void PluginIndexUrl_WhitespaceFallsBackToNull()
	{
		string? original = Environment.GetEnvironmentVariable("Vapor_PLUGIN_INDEX_URL");
		try
		{
			Environment.SetEnvironmentVariable("Vapor_PLUGIN_INDEX_URL", "   ");
			Config config = Config.LoadFromEnvironment();
			Assert.Null(config.PluginIndexUrl);
		}
		finally
		{
			Environment.SetEnvironmentVariable("Vapor_PLUGIN_INDEX_URL", original);
		}
	}

	private static int IntegerProperty(Config config, string property) => property switch
	{
		nameof(Config.TaskLeaseSeconds) => config.TaskLeaseSeconds,
		nameof(Config.TaskMaxDispatchAttempts) => config.TaskMaxDispatchAttempts,
		nameof(Config.TaskDispatchRetryDelayMs) => config.TaskDispatchRetryDelayMs,
		nameof(Config.ReconcileIntervalSeconds) => config.ReconcileIntervalSeconds,
		nameof(Config.ReconcileMaxAccountsPerAgent) => config.ReconcileMaxAccountsPerAgent,
		nameof(Config.ReconcileMaxLoginAttempts) => config.ReconcileMaxLoginAttempts,
		nameof(Config.ReconcileLoginCooldownSeconds) => config.ReconcileLoginCooldownSeconds,
		nameof(Config.ReconcileSessionStalenessSeconds) => config.ReconcileSessionStalenessSeconds,
		nameof(Config.ReconcileFarmRefreshSeconds) => config.ReconcileFarmRefreshSeconds,
		nameof(Config.ReconcileBoostRefreshSeconds) => config.ReconcileBoostRefreshSeconds,
		nameof(Config.ReconcileTradeRefreshSeconds) => config.ReconcileTradeRefreshSeconds,
		nameof(Config.ReconcileStandingRefreshSeconds) => config.ReconcileStandingRefreshSeconds,
		nameof(Config.CrawlWorkerTickSeconds) => config.CrawlWorkerTickSeconds,
		nameof(Config.CrawlKeepRuns) => config.CrawlKeepRuns,
		nameof(Config.CrawlMaxAppsPerPlan) => config.CrawlMaxAppsPerPlan,
		nameof(Config.CrawlMaxAppsPerTask) => config.CrawlMaxAppsPerTask,
		nameof(Config.CrawlRunTimeoutSeconds) => config.CrawlRunTimeoutSeconds,
		nameof(Config.CrawlIntervalMs) => config.CrawlIntervalMs,
		nameof(Config.ApiRateLimitPerMinute) => config.ApiRateLimitPerMinute,
		_ => throw new ArgumentException($"unknown property {property}", nameof(property)),
	};
}
