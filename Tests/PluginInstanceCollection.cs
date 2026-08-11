using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Test classes that swap out the static <c>Plugin.Instance</c> (via
/// <see cref="PluginInstanceRestorer"/> or similar) must share this collection so
/// xUnit never runs them in parallel with each other - otherwise one test's fake
/// Plugin.Instance can be visible to another test's code running concurrently in the
/// same process, since it's a single static field.
/// </summary>
[CollectionDefinition("PluginInstance", DisableParallelization = true)]
public class PluginInstanceCollection
{
}
