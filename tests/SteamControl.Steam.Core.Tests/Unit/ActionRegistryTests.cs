using Microsoft.Extensions.Logging;
using Xunit;
using Moq;

namespace SteamControl.Steam.Core.Tests.Unit;

public class ActionRegistryTests
{
	private readonly Mock<ILogger<ActionRegistry>> _loggerMock;
	private readonly ActionRegistry _registry;

	public ActionRegistryTests()
	{
		_loggerMock = new Mock<ILogger<ActionRegistry>>(MockBehavior.Loose);
		_registry = new ActionRegistry(_loggerMock.Object);
	}

	[Fact]
	public void Register_AddsActionToRegistry()
	{
		// Arrange
		var action = new TestAction("test_action", "Test Description");

		// Act
		_registry.Register(action);

		// Assert
		var retrieved = _registry.Get("test_action");
		Assert.NotNull(retrieved);
		Assert.Equal("test_action", retrieved!.Name);
	}

	[Fact]
	public void Register_OverwritesExistingAction()
	{
		// Arrange
		var action1 = new TestAction("test_action", "Description 1");
		var action2 = new TestAction("test_action", "Description 2");

		// Act
		_registry.Register(action1);
		_registry.Register(action2);

		// Assert
		var retrieved = _registry.Get("test_action");
		Assert.NotNull(retrieved);
		Assert.Equal("Description 2", retrieved!.Metadata.Description);
	}

	[Theory]
	[InlineData("test_action", "test_action")]
	[InlineData("TEST_ACTION", "test_action")]
	[InlineData("TeSt_AcTiOn", "test_action")]
	[InlineData("test_action", "TEST_ACTION")]
	public void Get_IsCaseInsensitive(string queryName, string registeredName)
	{
		// Arrange
		var action = new TestAction(registeredName, "Description");
		_registry.Register(action);

		// Act
		var retrieved = _registry.Get(queryName);

		// Assert
		Assert.NotNull(retrieved);
		Assert.Equal(registeredName, retrieved!.Name, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Get_ReturnsNullForNonExistentAction()
	{
		// Act
		var result = _registry.Get("nonexistent");

		// Assert
		Assert.Null(result);
	}

	[Fact]
	public void Get_ReturnsNullWhenRegistryIsEmpty()
	{
		// Act
		var result = _registry.Get("anything");

		// Assert
		Assert.Null(result);
	}

	[Fact]
	public void ListNames_ReturnsEmptyListWhenRegistryIsEmpty()
	{
		// Act
		var names = _registry.ListNames();

		// Assert
		Assert.Empty(names);
	}

	[Fact]
	public void ListNames_ReturnsAllRegisteredActionNames()
	{
		// Arrange
		_registry.Register(new TestAction("zebra", "Z"));
		_registry.Register(new TestAction("apple", "A"));
		_registry.Register(new TestAction("banana", "B"));

		// Act
		var names = _registry.ListNames();

		// Assert
		Assert.Equal(3, names.Count);
	}

	[Fact]
	public void ListNames_ReturnsNamesInAlphabeticalOrder()
	{
		// Arrange
		_registry.Register(new TestAction("zebra", "Z"));
		_registry.Register(new TestAction("apple", "A"));
		_registry.Register(new TestAction("banana", "B"));

		// Act
		var names = _registry.ListNames();

		// Assert
		Assert.Collection(names,
			name => Assert.Equal("apple", name),
			name => Assert.Equal("banana", name),
			name => Assert.Equal("zebra", name)
		);
	}

	[Fact]
	public void ListNames_IsCaseInsensitiveForSorting()
	{
		// Arrange
		_registry.Register(new TestAction("ZEBRA", "Z"));
		_registry.Register(new TestAction("apple", "A"));
		_registry.Register(new TestAction("BANANA", "B"));

		// Act
		var names = _registry.ListNames();

		// Assert
		Assert.Collection(names,
			name => Assert.Equal("apple", name),
			name => Assert.Equal("BANANA", name),
			name => Assert.Equal("ZEBRA", name)
		);
	}

	[Fact]
	public void Register_MultipleActions_AllCanBeRetrieved()
	{
		// Arrange
		var actions = new[]
		{
			new TestAction("action1", "Desc1"),
			new TestAction("action2", "Desc2"),
			new TestAction("action3", "Desc3")
		};

		// Act
		foreach (var action in actions)
		{
			_registry.Register(action);
		}

		// Assert
		Assert.Equal(3, _registry.ListNames().Count);
		foreach (var action in actions)
		{
			var retrieved = _registry.Get(action.Name);
			Assert.NotNull(retrieved);
			Assert.Equal(action.Name, retrieved!.Name);
		}
	}

	[Fact]
	public void Register_ActionWithEmptyName_StillRegisters()
	{
		// Arrange & Act
		var action = new TestAction("", "Empty Name");
		_registry.Register(action);

		// Assert
		var retrieved = _registry.Get("");
		Assert.NotNull(retrieved);
	}

	[Fact]
	public void Register_ActionWithSpecialCharactersInName()
	{
		// Arrange & Act
		var action = new TestAction("test_action_123", "Special Chars");
		_registry.Register(action);

		// Assert
		var retrieved = _registry.Get("test_action_123");
		Assert.NotNull(retrieved);
	}

	[Fact]
	public void Register_DuplicateNames_OnlyLastOneIsReturned()
	{
		// Arrange
		var action1 = new TestAction("duplicate", "First");
		var action2 = new TestAction("duplicate", "Second");

		// Act
		_registry.Register(action1);
		_registry.Register(action2);

		// Assert
		var retrieved = _registry.Get("duplicate");
		Assert.Equal("Second", retrieved!.Metadata.Description);
		Assert.Single(_registry.ListNames()); // Only one entry
	}

	private sealed class TestAction : IAction
	{
		public string Name { get; }
		public ActionMetadata Metadata { get; }

		public TestAction(string name, string description)
		{
			Name = name;
			Metadata = new ActionMetadata(name, description, RequiresLogin: false, TimeoutSeconds: 30);
		}

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken)
		{
			return Task.FromResult<ActionResult>(new ActionResult(true, null, new Dictionary<string, object?>()));
		}
	}
}
