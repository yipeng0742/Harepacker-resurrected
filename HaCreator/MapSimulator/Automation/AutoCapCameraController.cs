using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace HaCreator.MapSimulator.Automation
{
    public class AutoCapCameraController
    {
        private readonly MapSimulator _sim;
        private List<Point> _scanPath = new List<Point>();
        private int _scanIndex = 0;

        public AutoCapCameraController(MapSimulator sim)
        {
            _sim = sim;
        }

        public void Initialize()
        {
            BuildScanPath();
        }

        private void BuildScanPath()
        {
            // Will port logic from MapSimulator.BuildAutoCaptureScanPath here
        }

        /// <summary>
        /// Ticks the camera movement. Returns true if camera has reached its destination and scene can be setup.
        /// </summary>
        public bool TickCamera()
        {
            // Will port logic from MapSimulator.TickAutoCaptureCamera here
            return true;
        }
    }
}
