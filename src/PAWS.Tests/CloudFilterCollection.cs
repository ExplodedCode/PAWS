namespace PAWS.Tests;

/// <summary>
/// Tests that register/unregister real Windows Cloud Filter sync roots share this collection so xUnit
/// runs them sequentially — concurrent registration/unregistration against the shell (cldflt.sys) is not
/// something worth risking a race on just to save a few seconds of wall time.
/// </summary>
[CollectionDefinition("CloudFilter", DisableParallelization = true)]
public class CloudFilterCollection;
