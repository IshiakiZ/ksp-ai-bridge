namespace AIBridge.Api
{
    public static class Registry
    {
        public static void RegisterAll(BridgeHost host)
        {
            SceneWatch.Hook();
            GameApi.Register(host);
            DialogApi.Register(host);
            EditorApi.Register(host);
            FlightApi.Register(host);
            CameraApi.Register(host);
            NavApi.Register(host);
            PlannerApi.Register(host);
            PartApi.Register(host);
            ScienceApi.Register(host);
            SequenceApi.Register(host);
            SurfaceApi.Register(host);
            DockApi.Register(host);
            EvaApi.Register(host);
            VoiceApi.Register(host);
            ChatApi.Register(host);
            Company.Register(host);
            StructureApi.Register(host);
            AirApi.Register(host);
            WeatherApi.Register(host);
#if DEV
            DevRecorder.Register(host);
#endif
        }
    }
}
