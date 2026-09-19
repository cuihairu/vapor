using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// NotFoundException is the store layer's public "missing" contract (thrown by
/// SqliteJobStore and mapped to REST 404s); pin its full constructor surface.
/// </summary>
public class ExceptionContractTests
{
	[Fact]
	public void NotFoundException_Ctors_PreserveMessageAndInner()
	{
		var bare = new NotFoundException();
		Assert.NotNull(bare.Message);
		Assert.Null(bare.InnerException);

		var withMessage = new NotFoundException("job not found");
		Assert.Equal("job not found", withMessage.Message);
		Assert.Null(withMessage.InnerException);

		var inner = new InvalidOperationException("store closed");
		var wrapped = new NotFoundException("job not found", inner);
		Assert.Equal("job not found", wrapped.Message);
		Assert.Same(inner, wrapped.InnerException);
	}
}
