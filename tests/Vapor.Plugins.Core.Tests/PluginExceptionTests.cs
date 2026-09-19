using Vapor.Plugins.Core;
using Xunit;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// PluginException is the host boundary's failure type for discovery, loading
/// and unloading; pin its full constructor surface.
/// </summary>
public class PluginExceptionTests
{
	[Fact]
	public void Ctors_PreserveMessageAndInner()
	{
		var bare = new PluginException();
		Assert.NotNull(bare.Message);
		Assert.Null(bare.InnerException);

		var withMessage = new PluginException("manifest missing");
		Assert.Equal("manifest missing", withMessage.Message);
		Assert.Null(withMessage.InnerException);

		var inner = new BadImageFormatException("not a managed dll");
		var wrapped = new PluginException("manifest missing", inner);
		Assert.Equal("manifest missing", wrapped.Message);
		Assert.Same(inner, wrapped.InnerException);
	}
}
