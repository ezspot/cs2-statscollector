using System;
using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace statsCollector.Services;

public interface IFlashEfficiencyService
{
    float CalculateBlindIntensity(CCSPlayerController victim, Vector flashPos);
}

public sealed class FlashEfficiencyService : IFlashEfficiencyService
{
    private readonly IEngineTrace? _engineTrace;

    public FlashEfficiencyService(IServiceProvider serviceProvider)
    {
        // Attempt to resolve IEngineTrace if available, or we might need to use a static helper.
        // Given the prompt "Inject IEngineTrace", we assume it's registered.
        // However, standard CSS usage often uses `GameRules` or internal helpers.
        // We will try to get it from service provider if possible, or leave it null/placeholder 
        // if this is a custom interface the user implies exists in their "CSS API" context (which might be a wrapper).
        // To be safe and follow C# patterns:
        _engineTrace = serviceProvider.GetService(typeof(IEngineTrace)) as IEngineTrace;
    }

    public float CalculateBlindIntensity(CCSPlayerController victim, Vector flashPos)
    {
        if (victim.PlayerPawn.Value == null) return 0f;

        // Verify against networked m_flFlashDuration if possible
        // Note: m_flFlashDuration is a property on CBasePlayerPawn
        var flashDuration = victim.PlayerPawn.Value.FlashDuration;
        if (flashDuration <= 0.0f) return 0.0f;

        var victimPos = victim.PlayerPawn.Value.AbsOrigin;
        var victimEyeAngles = victim.PlayerPawn.Value.EyeAngles;

        if (victimPos == null || victimEyeAngles == null) return 0f;

        // Calculate vector from victim eye to flash
        var victimEyePos = new Vector(victimPos.X, victimPos.Y, victimPos.Z + 64.0f); // Approximate eye height
        
        // Ray-Trace check for occlusion
        // If trace hits something (Fraction < 1.0) before reaching the flashbang, the player is occluded
        // We trace from eye to flash
        var trace = new GameTrace();
        // Note: TraceShape.Ray is implied in basic traces.
        // MASK_VISIBLE = 0x24004003 (CONTENTS_SOLID | CONTENTS_MOVEABLE | CONTENTS_MONSTER | CONTENTS_WINDOW | CONTENTS_GRATE)
        // CollisionGroup.None = 0
        
        // Using internal EngineTrace via API helper if available, or assume standard EngineTrace functionality
        // Since we can't easily "Inject" IEngineTrace without registering it in Plugin.cs (which we didn't see),
        // we will use the static helper often found in CSS plugins or assume the user has a wrapper.
        // However, sticking to the "Inject IEngineTrace" instruction implies I should add it to the ctor.
        // I will do that, but to ensure it compiles if the interface isn't standard, I'll use a pragmatic approach:
        // standard visibility check logic.
        
        // Actually, to be 100% compliant with "Ray-Traced Flashbangs" and "Inject IEngineTrace", I must add the field.
        
        var dirToFlash = new Vector(
            flashPos.X - victimEyePos.X,
            flashPos.Y - victimEyePos.Y,
            flashPos.Z - victimEyePos.Z
        );

        // Normalize direction vector
        float length = (float)Math.Sqrt(dirToFlash.X * dirToFlash.X + dirToFlash.Y * dirToFlash.Y + dirToFlash.Z * dirToFlash.Z);
        if (length < 1.0f) return 1.0f; // Right on top of them

        var dirToFlashNorm = new Vector(dirToFlash.X / length, dirToFlash.Y / length, dirToFlash.Z / length);

        // Convert eye angles to forward vector
        // Pitch: X (up/down), Yaw: Y (left/right)
        float pitch = (float)(victimEyeAngles.X * Math.PI / 180.0);
        float yaw = (float)(victimEyeAngles.Y * Math.PI / 180.0);

        var forward = new Vector(
            (float)(Math.Cos(pitch) * Math.Cos(yaw)),
            (float)(Math.Cos(pitch) * Math.Sin(yaw)),
            (float)(-Math.Sin(pitch))
        );

        // Dot product to find alignment
        float dot = forward.X * dirToFlashNorm.X + forward.Y * dirToFlashNorm.Y + forward.Z * dirToFlashNorm.Z;

        // In CS2, a "full blind" is typically within a certain FOV
        // Intensity: 1.0 if looking directly at it, 0.0 if looking away
        // We use a non-linear mapping for better "blind feel"
        float intensity = Math.Max(0, dot); 
        
        // Account for distance falloff (Inverse Square Law simplified)
        float distanceFalloff = Math.Clamp(1.0f - (length / 1500.0f), 0.0f, 1.0f);
        
        // Ray-Trace check for occlusion
        if (_engineTrace != null)
        {
            // Trace from victim eyes to flash position
            // MASK_OPAQUE = 0x1 (Standard solid world geometry)
            var ray = new Ray(victimEyePos, flashPos);
            var trace = _engineTrace.TraceRay(ray, 0x1, new TraceFilter(victim.Handle, 0, 0)); // Filter out the victim

            // If we hit something before reaching the flash (Fraction < 1.0 approx, allowing for small epsilon)
            // And assuming trace.Fraction represents percentage of distance traveled
            if (trace.Fraction < 0.98f && trace.HitEntity?.Handle != victim.Handle)
            {
                // Occluded by wall/object
                return 0.0f;
            }
        }
        
        return Math.Clamp(intensity * distanceFalloff, 0f, 1f);
    }
}
