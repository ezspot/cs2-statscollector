using System;
using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace statsCollector.Services;

public interface IFlashEfficiencyService
{
    float CalculateBlindIntensity(CCSPlayerController victim, Vector flashPos);
}

public sealed class FlashEfficiencyService : IFlashEfficiencyService
{
    // private readonly IEngineTrace? _engineTrace;

    public FlashEfficiencyService()
    {
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
        
        var pitch = (float)(victimEyeAngles.X * Math.PI / 180.0);
        var yaw = (float)(victimEyeAngles.Y * Math.PI / 180.0);

        var forward = new Vector(
            (float)(Math.Cos(pitch) * Math.Cos(yaw)),
            (float)(Math.Cos(pitch) * Math.Sin(yaw)),
            (float)(-Math.Sin(pitch))
        );

        var dirToFlash = new Vector(
            flashPos.X - victimEyePos.X,
            flashPos.Y - victimEyePos.Y,
            flashPos.Z - victimEyePos.Z
        );

        // Normalize direction vector
        float length = (float)Math.Sqrt(dirToFlash.X * dirToFlash.X + dirToFlash.Y * dirToFlash.Y + dirToFlash.Z * dirToFlash.Z);
        if (length < 1.0f) return 1.0f; // Right on top of them

        var dirToFlashNorm = new Vector(dirToFlash.X / length, dirToFlash.Y / length, dirToFlash.Z / length);

        // Dot product to find alignment
        float dot = forward.X * dirToFlashNorm.X + forward.Y * dirToFlashNorm.Y + forward.Z * dirToFlashNorm.Z;

        // In CS2, a "full blind" is typically within a certain FOV
        // Intensity: 1.0 if looking directly at it, 0.0 if looking away
        // We use a non-linear mapping for better "blind feel"
        float intensity = Math.Max(0, dot); 
        
        // Account for distance falloff (Inverse Square Law simplified)
        float distanceFalloff = Math.Clamp(1.0f - (length / 1500.0f), 0.0f, 1.0f);
        
        return Math.Clamp(intensity * distanceFalloff, 0f, 1f);
    }
}
