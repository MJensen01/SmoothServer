namespace SmoothServer
{
    /// <summary>
    /// Per-frame tick for the Modules/Server family.
    ///
    /// Through 0.2.x this file was ServerBootstrap.cs and carried a SECOND [BepInPlugin] class in
    /// the same DLL, because the Plugin.cs of the day registered modules from an explicit list and
    /// could not be edited by the agent that wrote these modules. 0.3.0's Plugin.cs discovers every
    /// FeatureModule subclass in the assembly by reflection, Configure()s it against the one
    /// ConfigFile (so ConfigWatcher hot-reload covers it), TryEnable()s it against the running side
    /// and lists it in the single module summary line - so the second plugin GUID is gone. One
    /// [BepInPlugin] per DLL: a second one shows up as a phantom mod in r2modman and the BepInEx log.
    ///
    /// All that is left is this tick, called once per frame from SmoothServerPlugin.Update().
    /// </summary>
    internal static class ServerModules
    {
        internal static void Tick(float dt)
        {
            PeerTelemetryModule.Tick(dt);
            AdaptiveBudgetModule.Tick(dt);
            VPOServerModule.Tick(dt);
            AsyncSaveModule.Tick(dt);
            SteamRatesModule.Tick(dt);
            SyncListCacheModule.TickStats(dt);
            StatsLogModule.Tick(dt);
        }
    }
}
