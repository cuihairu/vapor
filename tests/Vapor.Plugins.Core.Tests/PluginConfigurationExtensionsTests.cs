using Vapor.Plugins.Core;
using Xunit;

namespace Vapor.Plugins.Core.Tests;

public sealed class PluginConfigurationExtensionsTests : IDisposable
{
	private readonly Dictionary<string, string> _config = new(StringComparer.OrdinalIgnoreCase);

	public void Dispose()
	{
		// Clear any environment variable a test may have set.
		foreach (var key in new[] { "VAPOR_TEST_PLUGIN_STR", "VAPOR_TEST_PLUGIN_INT", "VAPOR_TEST_PLUGIN_BOOL", "VAPOR_TEST_PLUGIN_DECIMAL" })
		{
			Environment.SetEnvironmentVariable(key, null);
		}
	}

	// --- GetString ---

	[Fact]
	public void GetString_ReturnsConfiguredValue()
	{
		_config["metrics.host"] = "0.0.0.0";

		Assert.Equal("0.0.0.0", _config.GetString("metrics.host", "127.0.0.1"));
	}

	[Fact]
	public void GetString_FallsBackWhenMissingOrBlank()
	{
		_config["blank"] = "  ";

		Assert.Equal("fallback", _config.GetString("missing", "fallback"));
		Assert.Equal("fallback", _config.GetString("blank", "fallback"));
	}

	[Fact]
	public void GetString_EnvironmentVariableOverridesConfig()
	{
		_config["key"] = "from-config";
		Environment.SetEnvironmentVariable("VAPOR_TEST_PLUGIN_STR", "from-env");

		Assert.Equal("from-env", _config.GetString("key", "fallback", "VAPOR_TEST_PLUGIN_STR"));
	}

	[Fact]
	public void GetString_BlankEnvironmentVariableFallsBackToConfig()
	{
		_config["key"] = "from-config";
		Environment.SetEnvironmentVariable("VAPOR_TEST_PLUGIN_STR", " ");

		Assert.Equal("from-config", _config.GetString("key", "fallback", "VAPOR_TEST_PLUGIN_STR"));
	}

	// --- GetInt32 ---

	[Fact]
	public void GetInt32_ParsesConfiguredValue()
	{
		_config["port"] = "9700";

		Assert.Equal(9700, _config.GetInt32("port", 80));
	}

	[Fact]
	public void GetInt32_FallsBackWhenUnparsableOrOutOfRange()
	{
		_config["bad"] = "abc";
		_config["huge"] = "99999";

		Assert.Equal(80, _config.GetInt32("missing", 80, min: 0, max: 65535));
		Assert.Equal(80, _config.GetInt32("bad", 80, min: 0, max: 65535));
		Assert.Equal(80, _config.GetInt32("huge", 80, min: 0, max: 65535));
	}

	[Fact]
	public void GetInt32_EnvironmentVariableOverridesConfig()
	{
		_config["port"] = "9700";
		Environment.SetEnvironmentVariable("VAPOR_TEST_PLUGIN_INT", "1234");

		Assert.Equal(1234, _config.GetInt32("port", 80, "VAPOR_TEST_PLUGIN_INT"));
	}

	// --- GetDecimal ---

	[Fact]
	public void GetDecimal_ParsesConfiguredValue()
	{
		_config["threshold"] = "33.3";

		Assert.Equal(33.3m, _config.GetDecimal("threshold", 10m));
	}

	[Fact]
	public void GetDecimal_FallsBackWhenUnparsableOrOutOfRange()
	{
		_config["bad"] = "abc";
		_config["huge"] = "20000";
		_config["tiny"] = "0.001";

		Assert.Equal(10m, _config.GetDecimal("missing", 10m, min: 0.01m, max: 10_000m));
		Assert.Equal(10m, _config.GetDecimal("bad", 10m, min: 0.01m, max: 10_000m));
		Assert.Equal(10m, _config.GetDecimal("huge", 10m, min: 0.01m, max: 10_000m));
		Assert.Equal(10m, _config.GetDecimal("tiny", 10m, min: 0.01m, max: 10_000m));
	}

	[Fact]
	public void GetDecimal_EnvironmentVariableOverridesConfig()
	{
		_config["threshold"] = "1";
		Environment.SetEnvironmentVariable("VAPOR_TEST_PLUGIN_DECIMAL", "42.5");

		Assert.Equal(42.5m, _config.GetDecimal("threshold", 10m, "VAPOR_TEST_PLUGIN_DECIMAL", min: 0.01m, max: 10_000m));
	}

	[Fact]
	public void GetDecimal_UnparsableEnvironmentVariableFallsBackToConfig()
	{
		_config["threshold"] = "33.3";
		Environment.SetEnvironmentVariable("VAPOR_TEST_PLUGIN_DECIMAL", "not-a-number");

		Assert.Equal(33.3m, _config.GetDecimal("threshold", 10m, "VAPOR_TEST_PLUGIN_DECIMAL"));
	}

	[Fact]
	public void GetDecimal_OutOfRangeEnvironmentVariableFallsBackToConfig()
	{
		_config["threshold"] = "33.3";
		Environment.SetEnvironmentVariable("VAPOR_TEST_PLUGIN_DECIMAL", "99999");

		Assert.Equal(33.3m, _config.GetDecimal("threshold", 10m, "VAPOR_TEST_PLUGIN_DECIMAL", min: 0.01m, max: 10_000m));
	}

	// --- GetBool ---

	[Theory]
	[InlineData("true", true)]
	[InlineData("YES", true)]
	[InlineData("1", true)]
	[InlineData("false", false)]
	[InlineData("No", false)]
	[InlineData("0", false)]
	public void GetBool_ParsesCommonTruthyAndFalsyForms(string configured, bool expected)
	{
		_config["enabled"] = configured;

		Assert.Equal(expected, _config.GetBool("enabled", !expected));
	}

	[Fact]
	public void GetBool_FallsBackWhenUnparsable()
	{
		_config["enabled"] = "sometimes";

		Assert.True(_config.GetBool("enabled", fallback: true));
	}

	[Fact]
	public void GetBool_NullConfiguredValue_FallsBack()
	{
		// A Dictionary<string, string> accepts null values, so TryGetValue can match
		// with a null value; TryParseBool must treat that like any other unparsable
		// entry and return the fallback.
		_config["enabled"] = null!;

		Assert.True(_config.GetBool("enabled", fallback: true));
		Assert.False(_config.GetBool("enabled", fallback: false));
	}

	[Fact]
	public void GetBool_EnvironmentVariableOverridesConfig()
	{
		_config["enabled"] = "true";
		Environment.SetEnvironmentVariable("VAPOR_TEST_PLUGIN_BOOL", "no");

		Assert.False(_config.GetBool("enabled", true, "VAPOR_TEST_PLUGIN_BOOL"));
	}
}
