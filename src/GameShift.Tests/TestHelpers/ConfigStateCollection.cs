using Xunit;

namespace GameShift.Tests.TestHelpers;

/// <summary>
/// Test collection for tests that mutate global static state (SettingsManager, etc.)
/// to prevent xUnit from running them in parallel.
/// Apply [Collection("ConfigState")] to test classes that need it.
/// </summary>
[CollectionDefinition("ConfigState", DisableParallelization = true)]
public class ConfigStateCollection { }
