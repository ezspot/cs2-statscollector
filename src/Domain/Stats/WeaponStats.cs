using System;
using System.Collections.Generic;

namespace statsCollector.Domain.Stats;

public sealed class WeaponStats
{
    private readonly Action _markDirty;
    private readonly Dictionary<string, int> _killsByWeapon = new();
    private readonly Dictionary<string, int> _deathsByWeapon = new();
    private readonly Dictionary<string, int> _shotsByWeapon = new();
    private readonly Dictionary<string, int> _hitsByWeapon = new();

    public WeaponStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public IReadOnlyDictionary<string, int> KillsByWeapon => _killsByWeapon;
    public IReadOnlyDictionary<string, int> DeathsByWeapon => _deathsByWeapon;
    public IReadOnlyDictionary<string, int> ShotsByWeapon => _shotsByWeapon;
    public IReadOnlyDictionary<string, int> HitsByWeapon => _hitsByWeapon;

    public void RecordKill(string weapon)
    {
        _killsByWeapon[weapon] = _killsByWeapon.GetValueOrDefault(weapon, 0) + 1;
        _markDirty();
    }

    public void RecordDeath(string weapon)
    {
        _deathsByWeapon[weapon] = _deathsByWeapon.GetValueOrDefault(weapon, 0) + 1;
        _markDirty();
    }

    public void RecordShot(string weapon)
    {
        _shotsByWeapon[weapon] = _shotsByWeapon.GetValueOrDefault(weapon, 0) + 1;
        _markDirty();
    }

    public void RecordHit(string weapon)
    {
        _hitsByWeapon[weapon] = _hitsByWeapon.GetValueOrDefault(weapon, 0) + 1;
        _markDirty();
    }

    public void Reset()
    {
        _killsByWeapon.Clear();
        _deathsByWeapon.Clear();
        _shotsByWeapon.Clear();
        _hitsByWeapon.Clear();
        _markDirty();
    }
}
