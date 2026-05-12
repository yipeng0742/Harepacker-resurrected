using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private void graphics_PreparingDeviceSettings(object sender, PreparingDeviceSettingsEventArgs e)
        {
            if (!IsAutoCaptureEnabled || e?.GraphicsDeviceInformation == null)
            {
                return;
            }

            e.GraphicsDeviceInformation.GraphicsProfile = GraphicsProfile.Reach;
            var pp = e.GraphicsDeviceInformation.PresentationParameters;
            if (pp != null)
            {
                pp.MultiSampleCount = 0;
                pp.DepthStencilFormat = DepthFormat.None;
                pp.IsFullScreen = false;
            }
        }

        private void ForceAutoCaptureCompatibleGraphicsSettings()
        {
            if (_DxDeviceManager == null)
            {
                return;
            }

            _DxDeviceManager.SynchronizeWithVerticalRetrace = false;
            _DxDeviceManager.HardwareModeSwitch = false;
            _DxDeviceManager.GraphicsProfile = GraphicsProfile.Reach;
            _DxDeviceManager.IsFullScreen = false;
            _DxDeviceManager.PreferMultiSampling = false;
            _DxDeviceManager.PreferredBackBufferFormat = SurfaceFormat.Color;
            _DxDeviceManager.PreferredDepthStencilFormat = DepthFormat.None;
            _DxDeviceManager.PreferredBackBufferWidth = Math.Max(_renderParams.RenderWidth, 1);
            _DxDeviceManager.PreferredBackBufferHeight = Math.Max(_renderParams.RenderHeight, 1);
        }
    }
}
