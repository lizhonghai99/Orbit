using Xunit;

namespace Orbit.Agents.Hosting.Tests;

/// <summary>
/// Process-wide <c>ORBIT_*</c> env is shared across tests in this assembly.
/// Classes that mutate it must share this collection so they never run in parallel.
/// <para>
/// 新增环境变量测试必须标注 <c>[Collection("orbit-env")]</c>，否则并行下会互相污染，flake 会悄悄回来。
/// </para>
/// </summary>
[CollectionDefinition("orbit-env")]
public sealed class OrbitEnvCollection;
