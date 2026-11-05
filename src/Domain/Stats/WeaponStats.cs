using System;
using System.Collections.Generic;

namespace statsCollector.Domain.Stats;

public sealed class WeaponStats
{
    private readonly Action _markDirty;
    private readonly Dictionary<string, int> _killsByWeapon = new();
    private readonly Dictionary<string, int> _headshotsByWeapon = new();
    private readonly Dictionary<string, int> _shotsByWeapon = new();
    private readonly Dictionary<string, int> _hitsByWeapon = new();
    private readonly Dictionary<string, int> _damageByWeapon = new();
    private readonly Dictionary<string, int> _deathsByWeapon = new();

    public WeaponStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public IReadOnlyDictionary<string, int> KillsByWeapon => _killsByWeapon;
    public IReadOnlyDictionary<string, int> HeadshotsByWeapon => _headshotsByWeapon;
    public IReadOnlyDictionary<string, int> ShotsByWeapon => _shotsByWeapon;
    public IReadOnlyDictionary<string, int> HitsByWeapon => _hitsByWeapon;
    public IReadOnlyDictionary<string, int> DamageByWeapon => _damageByWeapon;
    // The weapon this player was killed by.
    public IReadOnlyDictionary<string, int> DeathsByWeapon => _deathsByWeapon;

    public void RecordKill(string weapon, bool headshot)
    {
        Add(_killsByWeapon, weapon, 1);
        if (headshot) Add(_headshotsByWeapon, weapon, 1);
    }

    public void RecordShot(string weapon) => Add(_shotsByWeapon, weapon, 1);

    public void RecordHit(string weapon) => Add(_hitsByWeapon, weapon, 1);

    public void RecordDamage(string weapon, int damage) => Add(_damageByWeapon, weapon, damage);

    public void RecordDeath(string weapon) => Add(_deathsByWeapon, weapon, 1);

    private void Add(Dictionary<string, int> byWeapon, string weapon, int amount)
    {
        byWeapon[weapon] = byWeapon.GetValueOrDefault(weapon, 0) + amount;
        _markDirty();
    }

    public void Reset()
    {
        _killsByWeapon.Clear();
        _headshotsByWeapon.Clear();
        _shotsByWeapon.Clear();
        _hitsByWeapon.Clear();
        _damageByWeapon.Clear();
        _deathsByWeapon.Clear();
        _markDirty();
    }
}
