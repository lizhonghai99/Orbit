using Xunit;

namespace Orbit.Runtime.Tests;

/// <summary>
/// Process-wide <c>ORBIT_*</c> env is shared across tests in this assembly.
/// Classes that mutate it must share this collection so they never run in parallel.
/// </summary>
[CollectionDefinition("orbit-env")]
public sealed class OrbitEnvCollection;
