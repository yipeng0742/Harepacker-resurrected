using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using HaCreator.MapSimulator.Effects;
using HaCreator.MapSimulator.Entities;
using HaCreator.MapSimulator.Core;

namespace HaCreator.MapSimulator.Automation
{
    public enum AutoCapturePhase
    {
        Idle,
        CameraMoving,
        SceneSetup,
        WaitRender,
        Capture,
        Next
    }

    public class AutoCaptureOrchestrator
    {
        private readonly MapSimulator _sim;
        private readonly AutoCapCameraController _camera;
        private readonly AutoCapSceneDirector _director;
        
        public AutoCapturePhase CurrentPhase { get; private set; } = AutoCapturePhase.Idle;
        private int _waitTicks = 0;

        public AutoCaptureOrchestrator(MapSimulator sim)
        {
            _sim = sim;
            _camera = new AutoCapCameraController(sim);
            _director = new AutoCapSceneDirector(sim);
        }

        public void Initialize()
        {
            _camera.Initialize();
            _director.Initialize();
            CurrentPhase = AutoCapturePhase.CameraMoving;
        }

        public void Update(GameTime gameTime)
        {
            if (CurrentPhase == AutoCapturePhase.Idle) return;

            switch (CurrentPhase)
            {
                case AutoCapturePhase.CameraMoving:
                    if (_camera.TickCamera())
                    {
                        CurrentPhase = AutoCapturePhase.SceneSetup;
                    }
                    break;
                case AutoCapturePhase.SceneSetup:
                    _director.PrepareScene();
                    // Wait a few frames for animations/effects to start playing before capture
                    _waitTicks = 5; 
                    CurrentPhase = AutoCapturePhase.WaitRender;
                    break;
                case AutoCapturePhase.WaitRender:
                    _waitTicks--;
                    if (_waitTicks <= 0)
                    {
                        CurrentPhase = AutoCapturePhase.Capture;
                    }
                    break;
                case AutoCapturePhase.Capture:
                    // Actual capture is triggered from Draw() using ShouldCaptureFrame flag
                    break;
                case AutoCapturePhase.Next:
                    _director.CleanupScene();
                    CurrentPhase = AutoCapturePhase.CameraMoving;
                    break;
            }
        }

        // Called from MapSimulator.Draw
        public bool ShouldCaptureThisFrame()
        {
            return CurrentPhase == AutoCapturePhase.Capture;
        }

        public void OnCaptureComplete(bool success)
        {
            if (CurrentPhase == AutoCapturePhase.Capture)
            {
                CurrentPhase = AutoCapturePhase.Next;
            }
        }
    }
}
