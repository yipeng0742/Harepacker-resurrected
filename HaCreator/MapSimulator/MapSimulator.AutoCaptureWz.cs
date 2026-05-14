using HaSharedLibrary;
using HaSharedLibrary.Render.DX;
using HaSharedLibrary.Util;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace HaCreator.MapSimulator
{
    public partial class MapSimulator
    {
        private int LoadAutoCaptureRealSkillEffects()
        {
            if (_autoCaptureRealSkillEffectControl == null || !_autoCaptureRealSkillEffectControl.Enabled)
            {
                System.Console.WriteLine("[AutoCap][real_skill_fx] skipped: disabled");
                return 0;
            }

            System.Console.WriteLine("[AutoCap] Loading real skill hit effects from Skill.wz...");

            var skillsToLoad = new Dictionary<int, (string imgName, string skillId)>
            {
                { 0, ("212", "2121003") },
                { 1, ("222", "2221006") },
                { 2, ("122", "1221011") },
                { 3, ("322", "3221007") },
                { 4, ("422", "4221001") }
            };

            int loadedCount = 0;
            foreach (var kv in skillsToLoad)
            {
                try
                {
                    var skillImg = Program.FindWzObject("Skill", kv.Value.imgName) as WzImage;
                    if (skillImg == null)
                    {
                        System.Console.WriteLine($"[AutoCap][璇婃柇] Skill image not found: Skill/{kv.Value.imgName}");
                        continue;
                    }

                    if (!skillImg.Parsed)
                    {
                        skillImg.ParseImage();
                    }

                    string relativeHitPath = $"{kv.Value.imgName}/skill/{kv.Value.skillId}/hit";
                    var hitNode = Program.FindWzObject("Skill", relativeHitPath) as WzImageProperty
                        ?? skillImg["skill"]?[kv.Value.skillId]?["hit"];

                    if (hitNode == null)
                    {
                        continue;
                    }

                    hitNode = hitNode.GetLinkedWzImageProperty();
                    var frames = new List<IDXObject>();
                    if (hitNode is WzCanvasProperty canvasNode)
                    {
                        LoadSingleFrame(canvasNode, frames);
                    }
                    else
                    {
                        LoadAnimationFrames(hitNode, frames);
                    }

                    if (frames.Count > 0)
                    {
                        _combatEffects.SetHitEffectFrames(kv.Key, frames);
                        System.Console.WriteLine($"[AutoCap][real_skill_fx] loaded skill_id={kv.Value.skillId} variation={kv.Key} frames={frames.Count}");
                        loadedCount++;
                    }
                    else
                    {
                        System.Console.WriteLine($"[AutoCap][real_skill_fx] empty_frames skill_id={kv.Value.skillId} variation={kv.Key}");
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[AutoCap][璇婃柇] Error loading skill effect {kv.Value.skillId}: {ex.GetType().Name} - {ex.Message}");
                }
            }

            System.Console.WriteLine($"[AutoCap][real_skill_fx] loaded_framesets={loadedCount}");
            return loadedCount;
        }

        private void LoadAnimationFrames(WzImageProperty container, List<IDXObject> frames)
        {
            if (container == null)
            {
                return;
            }

            var properties = container.WzProperties;
            int count = properties?.Count ?? 0;
            if (count == 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                var prop = properties[i];
                if (prop == null)
                {
                    continue;
                }

                if (int.TryParse(prop.Name, out _))
                {
                    if (prop is WzCanvasProperty canvas)
                    {
                        LoadSingleFrame(canvas, frames);
                    }
                    else if (prop is WzUOLProperty uol && uol.LinkValue is WzCanvasProperty target)
                    {
                        LoadSingleFrame(target, frames);
                    }
                    else if (prop is WzSubProperty sub)
                    {
                        LoadAnimationFrames(sub, frames);
                    }
                }
            }
        }

        private void LoadSingleFrame(WzCanvasProperty canvas, List<IDXObject> frames)
        {
            var bitmap = canvas.GetLinkedWzCanvasBitmap();
            if (bitmap == null)
            {
                return;
            }

            var texture = bitmap.ToTexture2D(GraphicsDevice);
            if (texture == null)
            {
                return;
            }

            var origin = canvas["origin"] as WzVectorProperty;
            int ox = origin?.X?.Value ?? texture.Width / 2;
            int oy = origin?.Y?.Value ?? texture.Height;
            int delay = (canvas["delay"] as WzIntProperty)?.Value ?? 100;

            frames.Add(new DXObject(-ox, -oy, texture, delay));
        }
    }
}
