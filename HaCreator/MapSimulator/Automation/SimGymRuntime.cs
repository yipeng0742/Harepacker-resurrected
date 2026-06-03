using System;

namespace HaCreator.MapSimulator.Automation
{
    internal sealed class SimGymRunOptions
    {
        public bool UseCompatibleGraphics { get; set; } = true;
        public bool EnableGraphicsDiagnostics { get; set; } = true;
        public bool MuteAudio { get; set; } = true;
        public bool DisableLocalHotkeys { get; set; } = true;
    }

    internal static class SimGymRuntime
    {
        [ThreadStatic]
        private static SimGymRunOptions _current;

        internal static SimGymRunOptions Current
        {
            get => _current;
            set => _current = value;
        }

        internal static bool UseCompatibleGraphics => _current?.UseCompatibleGraphics == true;

        internal static bool EnableGraphicsDiagnostics => _current?.EnableGraphicsDiagnostics == true;

        internal static bool MuteAudio => _current?.MuteAudio == true;

        internal static bool DisableLocalHotkeys => _current?.DisableLocalHotkeys == true;
    }
}
