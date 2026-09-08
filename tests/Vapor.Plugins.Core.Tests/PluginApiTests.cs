using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

public class PluginApiTests
{
	[Theory]
	[InlineData("1.0", 1, 0)]
	[InlineData("1.2.3", 1, 2)]
	[InlineData("2.0.0", 2, 0)]
	[InlineData("1.2.3-beta.1", 1, 2)]
	[InlineData("1.2.3+build.5", 1, 2)]
	[InlineData(" 1.0.0 ", 1, 0)]
	public void TryParseVersion_ParsesSemVerStrings(string input, int expectedMajor, int expectedMinor)
	{
		Assert.True(PluginApi.TryParseVersion(input, out var version));
		Assert.Equal(expectedMajor, version.Major);
		Assert.Equal(expectedMinor, version.Minor);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("abc")]
	[InlineData("0.1.0")]
	public void TryParseVersion_RejectsInvalidStrings(string? input)
	{
		Assert.False(PluginApi.TryParseVersion(input, out _));
	}

	[Fact]
	public void IsCompatible_AcceptsSameVersion()
	{
		Assert.True(PluginApi.IsCompatible(PluginApi.Current, out var reason));
		Assert.Null(reason);
	}

	[Fact]
	public void IsCompatible_AcceptsOlderMinor()
	{
		Assert.True(PluginApi.IsCompatible(new Version(PluginApi.Current.Major, 0), out _));
	}

	[Fact]
	public void IsCompatible_RejectsNewerMajor()
	{
		Assert.False(PluginApi.IsCompatible(new Version(PluginApi.Current.Major + 1, 0), out var reason));
		Assert.Contains("major", reason);
	}

	[Fact]
	public void IsCompatible_RejectsOlderMajor()
	{
		Assert.False(PluginApi.IsCompatible(new Version(0, 9), out var reason));
		Assert.Contains("major", reason);
	}

	[Fact]
	public void IsCompatible_RejectsNewerMinor()
	{
		Assert.False(PluginApi.IsCompatible(new Version(PluginApi.Current.Major, PluginApi.Current.Minor + 1), out var reason));
		Assert.Contains("host implements", reason);
	}

	[Fact]
	public void IsCompatible_IgnoresPatchAndPrerelease()
	{
		Assert.True(PluginApi.IsCompatible($"{PluginApi.Current.Major}.{PluginApi.Current.Minor}.99-rc.1", out _));
	}

	[Fact]
	public void IsCompatible_StringOverload_RejectsInvalidVersion()
	{
		Assert.False(PluginApi.IsCompatible("not-a-version", out var reason));
		Assert.Contains("invalid plugin API version", reason);
	}
}
