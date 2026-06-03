using Microsoft.Xna.Framework.Graphics;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;

namespace HaCreator.MapSimulator.Automation
{
    internal static class SimGraphicsProbeCliRunner
    {
        internal static bool IsSimGraphicsProbeMode(string[] args)
        {
            if (args == null)
            {
                return false;
            }

            foreach (string arg in args)
            {
                if (string.Equals(arg, "--sim-graphics-probe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static int Run(string[] args)
        {
            try
            {
                WriteDxgiDiagnostics();
                RunSharpDxProbe();
                RunMonoGameDriverProbe();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SimGraphicsProbe] failed: " + ex);
                return 1;
            }
        }

        private static void WriteDxgiDiagnostics()
        {
            using var factory = new Factory1();
            var adapters = factory.Adapters1;
            Console.WriteLine($"[SimGraphicsProbe] dxgi_adapter_count={adapters.Length}");
            for (int i = 0; i < adapters.Length; i++)
            {
                using var adapter = adapters[i];
                var desc = adapter.Description1;
                Console.WriteLine(
                    $"[SimGraphicsProbe] adapter[{i}] desc={desc.Description} vendor={desc.VendorId} device={desc.DeviceId} flags={desc.Flags}");
                try
                {
                    using var output = adapter.GetOutput(0);
                    var outputDesc = output.Description;
                    Console.WriteLine(
                        $"[SimGraphicsProbe] adapter[{i}] output[0] device={outputDesc.DeviceName} attached={outputDesc.IsAttachedToDesktop} rotation={outputDesc.Rotation}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SimGraphicsProbe] adapter[{i}] output[0] status=fail error={ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        private static void RunSharpDxProbe()
        {
            foreach (DriverType driverType in new[] { DriverType.Hardware, DriverType.Warp, DriverType.Reference })
            {
                try
                {
                    using var device = new SharpDX.Direct3D11.Device(driverType, DeviceCreationFlags.None);
                    string featureLevel = device.FeatureLevel.ToString();
                    Console.WriteLine($"[SimGraphicsProbe] sharpdx driver={driverType} status=ok feature_level={featureLevel}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SimGraphicsProbe] sharpdx driver={driverType} status=fail error={ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        private static void RunMonoGameDriverProbe()
        {
            var originalDriverType = GraphicsAdapter.UseDriverType;
            var originalUseReferenceDevice = GraphicsAdapter.UseReferenceDevice;
            try
            {
                ProbeMonoGameDriver("hardware", GraphicsAdapter.DriverType.Hardware, false);
                ProbeMonoGameDriver("fast_software", GraphicsAdapter.DriverType.FastSoftware, false);
                ProbeMonoGameDriver("reference", GraphicsAdapter.DriverType.Reference, true);
            }
            finally
            {
                GraphicsAdapter.UseDriverType = originalDriverType;
                GraphicsAdapter.UseReferenceDevice = originalUseReferenceDevice;
            }
        }

        private static void ProbeMonoGameDriver(string label, GraphicsAdapter.DriverType driverType, bool useReferenceDevice)
        {
            try
            {
                GraphicsAdapter.UseDriverType = driverType;
                GraphicsAdapter.UseReferenceDevice = useReferenceDevice;

                PresentationParameters pp = new PresentationParameters
                {
                    BackBufferWidth = 16,
                    BackBufferHeight = 16,
                    BackBufferFormat = SurfaceFormat.Color,
                    DepthStencilFormat = DepthFormat.None,
                    DeviceWindowHandle = IntPtr.Zero,
                    IsFullScreen = false,
                };

                using var device = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, GraphicsProfile.Reach, pp);
                Console.WriteLine(
                    $"[SimGraphicsProbe] monogame driver={label} status=ok profile={device.GraphicsProfile} viewport={device.Viewport.Width}x{device.Viewport.Height}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimGraphicsProbe] monogame driver={label} status=fail error={ex.GetType().Name}:{ex.Message}");
            }
        }
    }
}
